using System.Text;

namespace EasyLabel.SegAI.Training.Ultralytics;

/// <summary>The answer of a one-shot trainer query (preflight, list families).</summary>
public sealed record UltraQueryResult<T>(T? Value, string ErrorCode, string ErrorMessage)
    where T : class
{
    public bool Success => Value != null;
}

/// <summary>
/// Runs <c>train_ultra.py</c> as a hidden subprocess: one-shot queries (<see cref="Preflight"/>, <see cref="ListFamilies"/>) and a
/// cancellable training run that reports <see cref="UltraEvent"/> progress. stdout is the JSON-lines protocol; stderr is kept as a
/// 4 KB tail for error messages. The process tree is killed on cancel and on <see cref="Dispose"/>.
/// </summary>
public sealed class UltraTrainService : IDisposable
{
    public const int StderrTailChars = 4096;

    public static readonly TimeSpan DefaultOneShotTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(15);

    private readonly UltraTrainerLocation _location;
    private readonly UltraProcessFactory _factory;
    private readonly TimeSpan _oneShotTimeout;
    private readonly object _liveLock = new object();
    private readonly List<IUltraProcess> _live = new List<IUltraProcess>();
    private bool _disposed;

    /// <param name="location">A <see cref="UltraTrainerStatus.Ready"/> location from <see cref="UltraTrainerLocator"/>.</param>
    /// <param name="factory">Replaces the real process start (tests); null = <see cref="SystemUltraProcess.Start"/>.</param>
    /// <param name="oneShotTimeout">Replaces the 60 s limit of preflight and list-families (tests).</param>
    public UltraTrainService(UltraTrainerLocation location, UltraProcessFactory? factory = null, TimeSpan? oneShotTimeout = null)
    {
        _location = location;
        _factory = factory ?? SystemUltraProcess.Start;
        _oneShotTimeout = oneShotTimeout ?? DefaultOneShotTimeout;
    }

    /// <summary>Blocking <see cref="PreflightAsync"/>; call from a background thread.</summary>
    public UltraQueryResult<UltraPreflight> Preflight()
    {
        return Task.Run(() => PreflightAsync(CancellationToken.None)).GetAwaiter().GetResult();
    }

    /// <summary>Blocking <see cref="ListFamiliesAsync"/>; call from a background thread.</summary>
    public UltraQueryResult<IReadOnlyList<UltraFamily>> ListFamilies()
    {
        return Task.Run(() => ListFamiliesAsync(CancellationToken.None)).GetAwaiter().GetResult();
    }

    public async Task<UltraQueryResult<UltraPreflight>> PreflightAsync(CancellationToken ct)
    {
        (UltraEvent? ev, string code, string message) = await RunOneShotAsync("--preflight", UltraEventKind.Preflight, ct).ConfigureAwait(false);
        return new UltraQueryResult<UltraPreflight>(ev?.Preflight, code, message);
    }

    public async Task<UltraQueryResult<IReadOnlyList<UltraFamily>>> ListFamiliesAsync(CancellationToken ct)
    {
        (UltraEvent? ev, string code, string message) = await RunOneShotAsync("--list-families", UltraEventKind.Families, ct).ConfigureAwait(false);
        return new UltraQueryResult<IReadOnlyList<UltraFamily>>(ev?.Families, code, message);
    }

    /// <summary>
    /// Trains. Expected problems (missing environment, CUDA, memory, bad data, cancel) come back as the result; only a bug throws.
    /// <paramref name="progress"/> gets every parsed event (start, epoch, done, error) in order.
    /// </summary>
    public async Task<UltraTrainResult> RunAsync(UltraTrainRequest request, IProgress<UltraEvent>? progress, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (ct.IsCancellationRequested)
        {
            return Cancelled();
        }

        if (_location.Status != UltraTrainerStatus.Ready)
        {
            return Failed("env_missing", "Ultralytics trainer is not available: " + _location.Status);
        }

        IUltraProcess process;
        try
        {
            process = Start(BuildTrainArguments(request));
        }
        catch (ObjectDisposedException)
        {
            return Cancelled();
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            return Failed("env_missing", ex.Message);
        }

        try
        {
            StderrTail tail = new StderrTail(StderrTailChars);
            Task stderrTask = Task.Run(() => PumpStderrAsync(process, tail));
            UltraEvent? doneEvent = null;
            UltraEvent? errorEvent = null;
            bool cancelled = false;
            bool[] doneSeen = new bool[1];

            // A cancel that arrives after the done event must not kill the finishing process.
            using (ct.Register(() =>
            {
                if (!Volatile.Read(ref doneSeen[0]))
                {
                    process.Kill();
                }
            }))
            {
                try
                {
                    string? line;
                    while ((line = await process.ReadOutputLineAsync(ct).ConfigureAwait(false)) != null)
                    {
                        UltraEvent? ev = UltraJson.ParseLine(line);
                        if (ev == null)
                        {
                            continue;
                        }

                        if (ev.Kind == UltraEventKind.Done)
                        {
                            doneEvent = ev;
                            Volatile.Write(ref doneSeen[0], true);
                        }
                        else if (ev.Kind == UltraEventKind.Error)
                        {
                            errorEvent = ev;
                        }

                        progress?.Report(ev);
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    // The pipe broke (killed process); the exit code below decides.
                }
            }

            // Cancelled (or disposed) counts only while no done event arrived.
            cancelled = doneEvent == null && (cancelled || ct.IsCancellationRequested || IsDisposed);
            if (cancelled)
            {
                process.Kill();
            }

            int exitCode = await WaitExitAsync(process, cancelled).ConfigureAwait(false);
            await Task.WhenAny(stderrTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

            if (cancelled || (doneEvent == null && errorEvent != null && errorEvent.ErrorCode == "cancelled"))
            {
                return Cancelled();
            }

            if (doneEvent != null && exitCode == 0)
            {
                return new UltraTrainResult
                {
                    Success = true,
                    BestPt = doneEvent.BestPt,
                    Onnx = doneEvent.Onnx,
                    Classes = doneEvent.Classes,
                    Map50 = doneEvent.Map50,
                    Map5095 = doneEvent.Map5095,
                    ElapsedSeconds = doneEvent.ElapsedSeconds,
                };
            }

            string code = errorEvent != null && errorEvent.ErrorCode.Length > 0 ? errorEvent.ErrorCode : CodeFromExit(exitCode);
            string message = errorEvent != null && errorEvent.Message.Length > 0
                ? errorEvent.Message
                : "Trainer ended with exit code " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + " without a result.";
            string text = tail.Text;
            if (text.Length > 0)
            {
                message += "\n" + text;
            }

            return Failed(code, message);
        }
        finally
        {
            Forget(process);
            process.Dispose();
        }
    }

    /// <summary>Kills every running trainer process tree.</summary>
    public void Dispose()
    {
        IUltraProcess[] processes;
        lock (_liveLock)
        {
            _disposed = true;
            processes = _live.ToArray();
            _live.Clear();
        }

        foreach (IUltraProcess process in processes)
        {
            process.Kill();
        }
    }

    /// <summary>The trainer command line for one run (after the script path).</summary>
    public static IReadOnlyList<string> BuildTrainArgumentList(UltraTrainRequest r)
    {
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        // String options use the single-token "--opt=value" form so a value starting with '-' is never parsed as a flag.
        List<string> args = new List<string>
        {
            "--data=" + r.DataYaml,
            "--family=" + r.FamilyId,
            "--epochs", r.Epochs.ToString(inv),
            "--imgsz", r.ImgSize.ToString(inv),
        };
        if (r.Batch.HasValue)
        {
            args.Add("--batch");
            args.Add(r.Batch.Value.ToString(inv));
        }

        args.Add("--project=" + r.RunsDir);
        args.Add("--name=" + r.RunName);
        args.Add("--workers");
        args.Add(r.Workers.ToString(inv));
        args.Add("--patience");
        args.Add(r.Patience.ToString(inv));
        args.Add("--seed");
        args.Add(r.Seed.ToString(inv));
        if (r.AllowCpu)
        {
            args.Add("--device=cpu");
            args.Add("--allow-cpu");
        }

        return args;
    }

    /// <summary>The trainer's exit-code contract: 2 user error, 3 environment missing, 4 no CUDA or out of memory.</summary>
    public static string CodeFromExit(int exitCode)
    {
        switch (exitCode)
        {
            case 2:
                return "data";
            case 3:
                return "env_missing";
            case 4:
                return "no_cuda";
            default:
                return "unknown";
        }
    }

    private string[] BuildTrainArguments(UltraTrainRequest request)
    {
        List<string> args = new List<string> { _location.ScriptPath };
        args.AddRange(BuildTrainArgumentList(request));
        return args.ToArray();
    }

    private IUltraProcess Start(IReadOnlyList<string> arguments)
    {
        Dictionary<string, string> env = new Dictionary<string, string>
        {
            ["PYTHONUTF8"] = "1",
            ["PYTHONUNBUFFERED"] = "1",
        };
        lock (_liveLock)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(UltraTrainService));
            }
        }

        IUltraProcess process = _factory(new UltraProcessSpec(_location.PythonPath, arguments, _location.TrainerDirectory, env));
        bool dispose;
        lock (_liveLock)
        {
            dispose = _disposed;
            if (!dispose)
            {
                _live.Add(process);
            }
        }

        if (dispose)
        {
            process.Dispose();
            throw new ObjectDisposedException(nameof(UltraTrainService));
        }

        return process;
    }

    private void Forget(IUltraProcess process)
    {
        lock (_liveLock)
        {
            _live.Remove(process);
        }
    }

    private async Task<(UltraEvent? Event, string Code, string Message)> RunOneShotAsync(string flag, UltraEventKind wanted, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_location.Status != UltraTrainerStatus.Ready)
        {
            return (null, "env_missing", "Ultralytics trainer is not available: " + _location.Status);
        }

        IUltraProcess process;
        try
        {
            process = Start(new[] { _location.ScriptPath, flag });
        }
        catch (ObjectDisposedException)
        {
            return (null, "cancelled", "cancelled");
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            return (null, "env_missing", ex.Message);
        }

        try
        {
            StderrTail tail = new StderrTail(StderrTailChars);
            Task stderrTask = Task.Run(() => PumpStderrAsync(process, tail));
            using CancellationTokenSource timeout = new CancellationTokenSource(_oneShotTimeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            UltraEvent? found = null;
            UltraEvent? error = null;
            try
            {
                string? line;
                while ((line = await process.ReadOutputLineAsync(linked.Token).ConfigureAwait(false)) != null)
                {
                    UltraEvent? ev = UltraJson.ParseLine(line);
                    if (ev != null && ev.Kind == wanted)
                    {
                        found = ev;
                    }
                    else if (ev != null && ev.Kind == UltraEventKind.Error)
                    {
                        error = ev;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                return ct.IsCancellationRequested
                    ? (null, "cancelled", "cancelled")
                    : (null, "unknown", "Trainer did not answer within " + ((int)_oneShotTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + " s.");
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // Broken pipe: fall through to the exit code.
            }

            int exitCode = await WaitExitAsync(process, false).ConfigureAwait(false);
            await Task.WhenAny(stderrTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            if (found != null)
            {
                return (found, string.Empty, string.Empty);
            }

            if (IsDisposed)
            {
                return (null, "cancelled", "cancelled");
            }

            string message = error != null && error.Message.Length > 0
                ? error.Message
                : "Trainer ended with exit code " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + " without an answer.";
            string text = tail.Text;
            if (text.Length > 0)
            {
                message += "\n" + text;
            }

            return (null, error != null && error.ErrorCode.Length > 0 ? error.ErrorCode : CodeFromExit(exitCode), message);
        }
        finally
        {
            Forget(process);
            process.Dispose();
        }
    }

    private static async Task<int> WaitExitAsync(IUltraProcess process, bool killed)
    {
        try
        {
            using CancellationTokenSource limit = new CancellationTokenSource(killed ? TimeSpan.FromSeconds(5) : ExitGrace);
            return await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            return -1;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
        {
            return -1;
        }
    }

    private static async Task PumpStderrAsync(IUltraProcess process, StderrTail tail)
    {
        try
        {
            string? line;
            while ((line = await process.ReadErrorLineAsync(CancellationToken.None).ConfigureAwait(false)) != null)
            {
                tail.Append(line);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
        {
        }
    }

    private static bool IsStartFailure(Exception ex)
    {
        return ex is System.ComponentModel.Win32Exception || ex is IOException || ex is InvalidOperationException || ex is UnauthorizedAccessException;
    }

    private bool IsDisposed
    {
        get
        {
            lock (_liveLock)
            {
                return _disposed;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(UltraTrainService));
        }
    }

    private static UltraTrainResult Cancelled()
    {
        return new UltraTrainResult { Cancelled = true, ErrorCode = "cancelled", ErrorMessage = "Training cancelled." };
    }

    private static UltraTrainResult Failed(string code, string message)
    {
        return new UltraTrainResult { ErrorCode = code, ErrorMessage = message };
    }

    /// <summary>The last <c>capacity</c> characters of stderr, thread-safe.</summary>
    private sealed class StderrTail
    {
        private readonly int _capacity;
        private readonly StringBuilder _text = new StringBuilder();

        public StderrTail(int capacity)
        {
            _capacity = capacity;
        }

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString().Trim();
                }
            }
        }

        public void Append(string line)
        {
            lock (_text)
            {
                _text.Append(line).Append('\n');
                if (_text.Length > _capacity)
                {
                    _text.Remove(0, _text.Length - _capacity);
                }
            }
        }
    }
}
