using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EasyAI.Desktop.Services;

/// <summary>One CounterAI detection: strand count and centres in original image pixels.</summary>
public sealed record CounterAiDetection(int Count, IReadOnlyList<(int X, int Y)> Points, double Milliseconds);

/// <summary>
/// C# wrapper around the Python FastNet detector. Starts <c>counterai_service.py serve</c> once (model loaded and warmed up),
/// then <see cref="DetectAsync"/> sends one JSON request per image and returns a typed <see cref="CounterAiDetection"/>.
/// Calls are serialised; dispose to stop the Python process.
/// </summary>
public sealed class CounterAiEngine : IDisposable
{
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private Process? _process;
    private int _nextId;

    private CounterAiEngine()
    {
    }

    /// <summary>Strand diameter in pixels reported by the model (for drawing / label reach).</summary>
    public double Diameter { get; private set; } = 62;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Starts the detector and waits until the model is loaded. Throws <see cref="InvalidOperationException"/> on failure.</summary>
    public static async Task<CounterAiEngine> StartAsync(string weights, string device = "AUTO", double threshold = CounterAiPaths.DefaultThreshold, CancellationToken ct = default)
    {
        string script = CounterAiPaths.FindScript() ?? throw new InvalidOperationException("counterai_service.py not found");
        if (!File.Exists(weights))
        {
            throw new FileNotFoundException("CounterAI weights not found", weights);
        }

        ProcessStartInfo psi = CounterAiPaths.BuildStartInfo(script, new[] { "serve", "--weights", weights, "--device", device, "--threshold", threshold.ToString("0.###", CultureInfo.InvariantCulture) });
        psi.RedirectStandardInput = true;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        CounterAiEngine engine = new CounterAiEngine();
        try
        {
            engine._process = Process.Start(psi) ?? throw new InvalidOperationException("python did not start");
            engine._process.ErrorDataReceived += (_, _) => { };
            engine._process.BeginErrorReadLine();
            JsonElement ready = await engine.ReadEventAsync("ready", null, ct).ConfigureAwait(false);
            if (ready.TryGetProperty("diameter", out JsonElement d))
            {
                engine.Diameter = d.GetDouble();
            }

            return engine;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    /// <summary>Counts the strands of one image file. <paramref name="threshold"/> null keeps the value given at start.</summary>
    public async Task<CounterAiDetection> DetectAsync(string imagePath, double? threshold = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Process p = _process is { HasExited: false } ? _process : throw new InvalidOperationException("CounterAI engine is not running");
            int id = ++_nextId;
            Dictionary<string, object> req = new Dictionary<string, object> { ["id"] = id, ["image"] = imagePath };
            if (threshold.HasValue)
            {
                req["threshold"] = threshold.Value;
            }

            await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(req)).ConfigureAwait(false);
            await p.StandardInput.FlushAsync().ConfigureAwait(false);
            JsonElement r = await ReadEventAsync("result", id, ct).ConfigureAwait(false);
            return ParseResult(r);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Converts a "result" event to a detection; throws <see cref="InvalidOperationException"/> when the service reported an error.</summary>
    public static CounterAiDetection ParseResult(JsonElement r)
    {
        if (r.TryGetProperty("error", out JsonElement err))
        {
            throw new InvalidOperationException(err.GetString() ?? "CounterAI error");
        }

        List<(int X, int Y)> pts = new List<(int X, int Y)>();
        foreach (JsonElement p in r.GetProperty("points").EnumerateArray())
        {
            pts.Add((p[0].GetInt32(), p[1].GetInt32()));
        }

        return new CounterAiDetection(r.GetProperty("count").GetInt32(), pts, r.GetProperty("ms").GetDouble());
    }

    private async Task<JsonElement> ReadEventAsync(string kind, int? id, CancellationToken ct)
    {
        Process p = _process ?? throw new InvalidOperationException("CounterAI engine is not running");
        while (true)
        {
            string? line = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null)
            {
                throw new InvalidOperationException("CounterAI process ended");
            }

            JsonElement? ev = CounterAiRunner.ParseEvent(line);
            if (ev == null)
            {
                continue;
            }

            string k = ev.Value.TryGetProperty("event", out JsonElement kk) ? kk.GetString() ?? string.Empty : string.Empty;
            if (k == "error")
            {
                throw new InvalidOperationException(ev.Value.TryGetProperty("message", out JsonElement m) ? m.GetString() : "CounterAI error");
            }

            if (k != kind)
            {
                continue;
            }

            if (id.HasValue && ev.Value.TryGetProperty("id", out JsonElement rid) && rid.ValueKind == JsonValueKind.Number && rid.GetInt32() != id.Value)
            {
                continue;
            }

            return ev.Value;
        }
    }

    public void Dispose()
    {
        Process? p = _process;
        _process = null;
        if (p != null)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.StandardInput.WriteLine("{\"cmd\":\"quit\"}");
                    p.StandardInput.Flush();
                    if (!p.WaitForExit(2000))
                    {
                        p.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }

            p.Dispose();
        }

        _gate.Dispose();
    }
}
