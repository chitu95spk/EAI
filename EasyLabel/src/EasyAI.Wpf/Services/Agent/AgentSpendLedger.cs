using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>What a ledger line is: a reservation before a run, its settlement after it, or the user's acknowledgement of a damaged ledger.</summary>
public enum AgentLedgerKind
{
    Reserve = 0,
    Settle,
    Ack,
}

/// <summary>The state of the ledger. <see cref="Unknown"/> = not loaded or not readable: nothing may be spent.</summary>
public enum AgentLedgerIntegrity
{
    Unknown = 0,
    Ok,
    Tampered,
}

/// <summary>One line of the ledger. <see cref="Mac"/> is an HMAC-SHA256 over every other field and the previous record's MAC.</summary>
public sealed record AgentLedgerRecord
{
    /// <summary>1-based position in the whole ledger (continues across an acknowledgement).</summary>
    public long Seq { get; init; }

    /// <summary>When the record was written (UTC, milliseconds). Never earlier than the record before it.</summary>
    public DateTime Utc { get; init; }

    public string InstanceId { get; init; } = string.Empty;

    public AgentLedgerKind Kind { get; init; }

    /// <summary>The reservation id: a Reserve and its Settle share it.</summary>
    public string Reference { get; init; } = string.Empty;

    public string Tool { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    /// <summary>USD: the reserved amount (Reserve) or the amount actually spent (Settle).</summary>
    public double Usd { get; init; }

    /// <summary>True when the amount is an estimate (cancelled, timed-out, crashed or unpriced runs).</summary>
    public bool Estimated { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public string PrevHash { get; init; } = string.Empty;

    public string Mac { get; init; } = string.Empty;
}

/// <summary>
/// The agent spend ledger: an append-only, HMAC-chained record of every reservation and settlement, read by the day cap. Deleting, editing,
/// truncating or re-ordering lines is detected on load; the day cap then reads 0 until the user acknowledges (the damaged file is kept as
/// evidence). Writes take a named mutex, so two EasyAI processes on one PC share one chain. This is tamper-evident, not tamper-proof: the key
/// lives next to the anchor in the user's own profile.
/// Call <see cref="Load"/> / <see cref="LoadAsync"/> and every write off the UI thread: all of them read or write files.
/// </summary>
public sealed class AgentSpendLedger
{
    /// <summary>The previous hash of the first record of a new ledger.</summary>
    public const string Genesis = "GENESIS";

    /// <summary>How long one operation waits for the named mutex before it gives up (the operation then fails closed).</summary>
    public const int LockWaitMs = 5000;

    private const string MutexName = @"Local\EasyAI.AgentLedger.v1";
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly object _gate = new object();
    private readonly IAgentLedgerStore _store;
    private readonly IAgentLedgerAnchor _anchor;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<DateTime, DateTime> _toLocal;
    private readonly string _instanceId;
    private readonly List<AgentLedgerRecord> _records = new List<AgentLedgerRecord>();

    private AgentLedgerIntegrity _integrity = AgentLedgerIntegrity.Unknown;
    private bool _loaded;
    private bool _tornPending;
    private bool _anchorStale;
    private byte[]? _key;
    private AgentLedgerAnchorSnapshot? _state;
    private AgentLedgerAnchorSnapshot? _anchorSeen;
    private DateTime _lastUtc = DateTime.MinValue;

    /// <param name="store">The ledger text.</param>
    /// <param name="anchor">The trusted state outside the ledger text.</param>
    /// <param name="utcNow">The clock (UTC); null = the system clock.</param>
    /// <param name="toLocal">UTC to local time; null = the time zone of the PC.</param>
    /// <param name="instanceId">Written into every record; null = a new GUID.</param>
    public AgentSpendLedger(IAgentLedgerStore store, IAgentLedgerAnchor anchor, Func<DateTime>? utcNow = null, Func<DateTime, DateTime>? toLocal = null, string? instanceId = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _toLocal = toLocal ?? (utc => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local));
        _instanceId = string.IsNullOrEmpty(instanceId) ? Guid.NewGuid().ToString("N") : instanceId!;
    }

    /// <summary>The state as last determined (no I/O).</summary>
    public AgentLedgerIntegrity Integrity
    {
        get
        {
            lock (_gate)
            {
                return _integrity;
            }
        }
    }

    /// <summary>Reads and verifies the ledger now. Blocking I/O: not on the UI thread.</summary>
    public AgentLedgerIntegrity Load()
    {
        return RunLocked(() =>
        {
            LoadLocked();
            return _integrity;
        }, AgentLedgerIntegrity.Unknown);
    }

    /// <summary>Runs <see cref="Load"/> on a worker thread.</summary>
    public Task<AgentLedgerIntegrity> LoadAsync()
    {
        return Task.Run(Load);
    }

    /// <summary>The records held in memory (a copy).</summary>
    public IReadOnlyList<AgentLedgerRecord> Records()
    {
        lock (_gate)
        {
            return _records.ToArray();
        }
    }

    /// <summary>Appends a reservation before a run starts. Null when the ledger is not usable or the write failed: the run must not start.</summary>
    public AgentLedgerRecord? Reserve(string reservationId, string tool, string model, double usd)
    {
        return RunLocked(() => AppendLocked(AgentLedgerKind.Reserve, Text(reservationId), Text(tool), Text(model), Usd(usd), true, 0, 0), null);
    }

    /// <summary>Appends the amount actually spent by a run that had a reservation. Null when the write failed.</summary>
    public AgentLedgerRecord? Settle(string reservationId, string tool, string model, double usd, bool estimated, long inputTokens = 0, long outputTokens = 0)
    {
        return RunLocked(() => AppendLocked(AgentLedgerKind.Settle, Text(reservationId), Text(tool), Text(model), Usd(usd), estimated, Math.Max(0, inputTokens), Math.Max(0, outputTokens)), null);
    }

    /// <summary>
    /// The user acknowledges a damaged ledger: the file is kept as evidence and a new chain starts from the trusted anchor. Returns true when
    /// the ledger is usable afterwards. Spend recorded in the damaged file is not carried over (the evidence file keeps it).
    /// </summary>
    public bool Acknowledge()
    {
        return RunLocked(AcknowledgeLocked, false);
    }

    /// <summary>
    /// What is left of the day cap today (local day; the clock is never allowed to move the day back). A tampered or unreadable ledger gives 0.
    /// A cap at or below 0 means "no cap" and gives positive infinity.
    /// </summary>
    public double DayRemainingUsd(double dayCapUsd)
    {
        return RunLocked(() =>
        {
            if (!SyncLocked())
            {
                return 0d;
            }

            if (double.IsNaN(dayCapUsd))
            {
                return 0d;
            }

            if (dayCapUsd <= 0 || double.IsPositiveInfinity(dayCapUsd))
            {
                return double.PositiveInfinity;
            }

            return Math.Max(0d, dayCapUsd - DaySpentLocked());
        }, 0d);
    }

    /// <summary>Runs the body under the in-process lock and the named mutex. Returns <paramref name="fallback"/> when the mutex is not obtained.</summary>
    private TResult RunLocked<TResult>(Func<TResult> body, TResult fallback)
    {
        lock (_gate)
        {
            Mutex mutex;
            try
            {
                mutex = new Mutex(false, MutexName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                return fallback;
            }

            using (mutex)
            {
                bool owned = false;
                try
                {
                    try
                    {
                        owned = mutex.WaitOne(LockWaitMs);
                    }
                    catch (AbandonedMutexException)
                    {
                        // The previous owner died holding the mutex; this process owns it now and the chain is re-verified on load.
                        owned = true;
                    }

                    return owned ? body() : fallback;
                }
                finally
                {
                    if (owned)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }
    }

    // Caller holds the locks.
    private void LoadLocked()
    {
        _loaded = false;
        _tornPending = false;
        _anchorStale = false;
        _records.Clear();
        _state = null;
        _anchorSeen = null;
        _key = null;
        _lastUtc = DateTime.MinValue;
        _integrity = AgentLedgerIntegrity.Unknown;

        string text;
        try
        {
            text = _store.ReadAll();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return;
        }

        AgentLedgerAnchorRead read = _anchor.TryRead(out AgentLedgerAnchorSnapshot? snap);
        if (read == AgentLedgerAnchorRead.Unavailable)
        {
            return;
        }

        if (read == AgentLedgerAnchorRead.Corrupt)
        {
            Tampered();
            return;
        }

        if (read == AgentLedgerAnchorRead.Missing || snap == null)
        {
            if (text.Trim().Length > 0)
            {
                // Records without any anchor: the anchor was deleted or the file was replaced.
                Tampered();
                return;
            }

            _integrity = AgentLedgerIntegrity.Ok;
            _loaded = true;
            return;
        }

        byte[] key = Convert.FromBase64String(snap.KeyBase64);
        _key = key;
        _anchorSeen = snap;
        if (!Verify(text, snap, key, out List<AgentLedgerRecord> parsed, out int validLength, out bool torn))
        {
            Tampered();
            return;
        }

        int count = parsed.Count;
        if (count < snap.Count || count > snap.Count + 1)
        {
            // Fewer records than the anchor covers: a cut head or tail. More than one unanchored record: records were added.
            Tampered();
            return;
        }

        string anchorHere = snap.Count == 0 ? snap.BaseHash : parsed[(int)snap.Count - 1].Mac;
        if (!string.Equals(anchorHere, snap.Hash, StringComparison.Ordinal))
        {
            Tampered();
            return;
        }

        _records.AddRange(parsed);
        _lastUtc = count > 0 ? parsed[count - 1].Utc : DateTime.MinValue;
        _state = new AgentLedgerAnchorSnapshot(
            snap.KeyBase64,
            snap.Seq - snap.Count + count,
            count,
            count > 0 ? parsed[count - 1].Mac : snap.BaseHash,
            snap.BaseHash);
        _anchorStale = count == snap.Count + 1;
        _integrity = AgentLedgerIntegrity.Ok;
        _loaded = true;

        if (torn)
        {
            string repaired = text.Substring(0, validLength);
            if (repaired.Length > 0 && repaired[repaired.Length - 1] != '\n')
            {
                repaired += "\n";
            }

            try
            {
                _store.ReplaceAll(repaired);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _tornPending = true;
            }
        }
    }

    private void Tampered()
    {
        _integrity = AgentLedgerIntegrity.Tampered;
        _loaded = true;
    }

    // Caller holds the locks. True when the ledger is loaded, usable and matches the anchor.
    private bool SyncLocked()
    {
        if (!_loaded || _tornPending)
        {
            LoadLocked();
        }
        else
        {
            AgentLedgerAnchorRead read = _anchor.TryRead(out AgentLedgerAnchorSnapshot? current);
            bool same = read == AgentLedgerAnchorRead.Found
                ? current != null && current == _anchorSeen
                : read == AgentLedgerAnchorRead.Missing && _anchorSeen == null;
            if (!same)
            {
                // Another process wrote the ledger, or the anchor changed: verify again before the next write.
                LoadLocked();
            }
        }

        return _integrity == AgentLedgerIntegrity.Ok && _loaded && !_tornPending;
    }

    // Caller holds the locks.
    private AgentLedgerRecord? AppendLocked(AgentLedgerKind kind, string reference, string tool, string model, double usd, bool estimated, long input, long output)
    {
        if (!SyncLocked())
        {
            return null;
        }

        if (_key == null)
        {
            // First use: the key and an empty anchor are written before the first record.
            _key = RandomNumberGenerator.GetBytes(32);
            _state = new AgentLedgerAnchorSnapshot(Convert.ToBase64String(_key), 0, 0, Genesis, Genesis);
            _anchorStale = true;
        }

        if (_state == null)
        {
            return null;
        }

        // A previous append reached the file but not the anchor: the anchor is brought up to date before the next record.
        if (_anchorStale && !WriteAnchorLocked())
        {
            return null;
        }

        byte[] key = _key!;
        AgentLedgerAnchorSnapshot state = _state!;
        AgentLedgerRecord draft = new AgentLedgerRecord
        {
            Seq = state.Seq + 1,
            Utc = Stamp(),
            InstanceId = _instanceId,
            Kind = kind,
            Reference = reference,
            Tool = tool,
            Model = model,
            Usd = usd,
            Estimated = estimated,
            InputTokens = input,
            OutputTokens = output,
            PrevHash = state.Hash,
            Mac = string.Empty,
        };
        AgentLedgerRecord record = draft with { Mac = ComputeMac(key, draft) };

        try
        {
            _store.Append(Serialize(record) + "\n");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // The file may hold a partial line: the next operation reloads and repairs it first.
            _loaded = false;
            return null;
        }

        _records.Add(record);
        _lastUtc = record.Utc;
        _state = new AgentLedgerAnchorSnapshot(state.KeyBase64, record.Seq, state.Count + 1, record.Mac, state.BaseHash);
        _anchorStale = true;
        WriteAnchorLocked();
        return record;
    }

    // Caller holds the locks.
    private bool WriteAnchorLocked()
    {
        if (_state == null || !_anchor.TryWrite(_state))
        {
            return false;
        }

        _anchorSeen = _state;
        _anchorStale = false;
        return true;
    }

    // Caller holds the locks.
    private bool AcknowledgeLocked()
    {
        if (!_loaded || _tornPending)
        {
            LoadLocked();
        }

        if (_integrity == AgentLedgerIntegrity.Ok && _loaded && !_tornPending)
        {
            return true;
        }

        if (_integrity != AgentLedgerIntegrity.Tampered)
        {
            return false;
        }

        string text;
        try
        {
            text = _store.ReadAll();
            if (text.Length > 0)
            {
                _store.SaveEvidence(text);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }

        AgentLedgerAnchorRead read = _anchor.TryRead(out AgentLedgerAnchorSnapshot? trusted);
        if (read == AgentLedgerAnchorRead.Unavailable)
        {
            return false;
        }

        bool hasTrusted = read == AgentLedgerAnchorRead.Found && trusted != null;
        byte[] key = hasTrusted ? Convert.FromBase64String(trusted!.KeyBase64) : RandomNumberGenerator.GetBytes(32);
        long seq = hasTrusted ? trusted!.Seq : 0;
        string baseHash = hasTrusted ? trusted!.Hash : Genesis;

        AgentLedgerRecord draft = new AgentLedgerRecord
        {
            Seq = seq + 1,
            Utc = Stamp(),
            InstanceId = _instanceId,
            Kind = AgentLedgerKind.Ack,
            Reference = string.Empty,
            Tool = string.Empty,
            Model = string.Empty,
            PrevHash = baseHash,
            Mac = string.Empty,
        };
        AgentLedgerRecord record = draft with { Mac = ComputeMac(key, draft) };

        try
        {
            _store.ReplaceAll(Serialize(record) + "\n");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }

        _records.Clear();
        _records.Add(record);
        _lastUtc = record.Utc;
        _key = key;
        _state = new AgentLedgerAnchorSnapshot(Convert.ToBase64String(key), seq + 1, 1, record.Mac, baseHash);
        _anchorSeen = hasTrusted ? trusted : null;
        _anchorStale = true;
        _tornPending = false;
        _integrity = AgentLedgerIntegrity.Ok;
        _loaded = true;
        if (!WriteAnchorLocked())
        {
            // Not stored: the next load sees a file that does not match the old anchor and asks for acknowledgement again.
            _integrity = AgentLedgerIntegrity.Unknown;
            _loaded = false;
            return false;
        }

        return true;
    }

    // Caller holds the locks. Spend of the local day of "now" (never earlier than the last record): settled amounts, plus reservations
    // that were never settled (a crashed run counts as fully spent).
    private double DaySpentLocked()
    {
        DateTime now = Stamp();
        DateTime day = LocalDay(now);
        HashSet<string> settled = new HashSet<string>(StringComparer.Ordinal);
        foreach (AgentLedgerRecord record in _records)
        {
            if (record.Kind == AgentLedgerKind.Settle)
            {
                settled.Add(record.Reference);
            }
        }

        double total = 0;
        foreach (AgentLedgerRecord record in _records)
        {
            if (LocalDay(record.Utc) != day)
            {
                continue;
            }

            if (record.Kind == AgentLedgerKind.Settle)
            {
                total += record.Usd;
            }
            else if (record.Kind == AgentLedgerKind.Reserve && !settled.Contains(record.Reference))
            {
                total += record.Usd;
            }
        }

        return total;
    }

    private DateTime LocalDay(DateTime utc)
    {
        return _toLocal(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).Date;
    }

    // The clock, but never earlier than the last record: a clock set back does not reopen a spent day, and records stay in order.
    private DateTime Stamp()
    {
        DateTime now = DateTime.SpecifyKind(_utcNow(), DateTimeKind.Utc);
        if (now < _lastUtc)
        {
            now = _lastUtc;
        }

        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    // Checks every line from the anchor's base hash. An unfinished last line that fails is reported as torn (not tampering).
    private static bool Verify(string text, AgentLedgerAnchorSnapshot snap, byte[] key, out List<AgentLedgerRecord> parsed, out int validLength, out bool torn)
    {
        parsed = new List<AgentLedgerRecord>();
        validLength = 0;
        torn = false;
        long firstSeq = snap.Seq - snap.Count + 1;
        string prev = snap.BaseHash;
        int pos = 0;
        while (pos < text.Length)
        {
            int newline = text.IndexOf('\n', pos);
            bool terminated = newline >= 0;
            int lineEnd = terminated ? newline : text.Length;
            int next = terminated ? newline + 1 : text.Length;
            string line = text.Substring(pos, lineEnd - pos).TrimEnd('\r');
            pos = next;
            if (line.Trim().Length == 0)
            {
                validLength = next;
                if (!terminated)
                {
                    torn = true;
                }

                continue;
            }

            AgentLedgerRecord? record = TryParse(line);
            bool valid = record != null && record.Seq == firstSeq + parsed.Count && record.PrevHash == prev && MacMatches(key, record);
            if (!valid)
            {
                if (!terminated)
                {
                    torn = true;
                    break;
                }

                return false;
            }

            parsed.Add(record!);
            prev = record!.Mac;
            validLength = next;
            if (!terminated)
            {
                torn = true;
                break;
            }
        }

        return true;
    }

    private static bool MacMatches(byte[] key, AgentLedgerRecord record)
    {
        byte[] expected = Encoding.ASCII.GetBytes(ComputeMac(key, record));
        byte[] actual = Encoding.ASCII.GetBytes(record.Mac);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string ComputeMac(byte[] key, AgentLedgerRecord record)
    {
        byte[] data = Encoding.UTF8.GetBytes(Canonical(record));
        using HMACSHA256 hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(data));
    }

    // Every field, length-prefixed, so no value can be moved into a neighbouring field.
    private static string Canonical(AgentLedgerRecord r)
    {
        StringBuilder builder = new StringBuilder();
        Field(builder, r.Seq.ToString(Inv));
        Field(builder, r.Utc.ToString(TimeFormat, Inv));
        Field(builder, r.InstanceId);
        Field(builder, r.Kind.ToString());
        Field(builder, r.Reference);
        Field(builder, r.Tool);
        Field(builder, r.Model);
        Field(builder, r.Usd.ToString("R", Inv));
        Field(builder, r.Estimated ? "1" : "0");
        Field(builder, r.InputTokens.ToString(Inv));
        Field(builder, r.OutputTokens.ToString(Inv));
        Field(builder, r.PrevHash);
        return builder.ToString();
    }

    private static void Field(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(Inv)).Append(':').Append(value).Append(';');
    }

    private static string Serialize(AgentLedgerRecord r)
    {
        JObject line = new JObject
        {
            ["seq"] = r.Seq,
            ["utc"] = r.Utc.ToString(TimeFormat, Inv),
            ["inst"] = r.InstanceId,
            ["kind"] = r.Kind.ToString(),
            ["ref"] = r.Reference,
            ["tool"] = r.Tool,
            ["model"] = r.Model,
            ["usd"] = r.Usd,
            ["est"] = r.Estimated,
            ["in"] = r.InputTokens,
            ["out"] = r.OutputTokens,
            ["prev"] = r.PrevHash,
            ["mac"] = r.Mac,
        };
        return line.ToString(Formatting.None);
    }

    private static AgentLedgerRecord? TryParse(string line)
    {
        JObject o;
        try
        {
            // Strings stay strings: the default date handling would turn the "utc" field into a Date token and reject the record.
            o = JObject.Load(new JsonTextReader(new System.IO.StringReader(line)) { DateParseHandling = DateParseHandling.None });
        }
        catch (JsonException)
        {
            return null;
        }

        if (!TryLong(o, "seq", out long seq) || seq < 1
            || !TryString(o, "utc", out string utcText)
            || !DateTime.TryParseExact(utcText, TimeFormat, Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime utc)
            || !TryString(o, "inst", out string instance)
            || !TryString(o, "kind", out string kindText)
            || !Enum.TryParse<AgentLedgerKind>(kindText, false, out AgentLedgerKind kind)
            || kind.ToString() != kindText
            || !TryString(o, "ref", out string reference)
            || !TryString(o, "tool", out string tool)
            || !TryString(o, "model", out string model)
            || !TryDouble(o, "usd", out double usd)
            || double.IsNaN(usd) || double.IsInfinity(usd) || usd < 0
            || !TryBool(o, "est", out bool estimated)
            || !TryLong(o, "in", out long input) || input < 0
            || !TryLong(o, "out", out long output) || output < 0
            || !TryString(o, "prev", out string prev)
            || !TryString(o, "mac", out string mac))
        {
            return null;
        }

        return new AgentLedgerRecord
        {
            Seq = seq,
            Utc = utc,
            InstanceId = instance,
            Kind = kind,
            Reference = reference,
            Tool = tool,
            Model = model,
            Usd = usd,
            Estimated = estimated,
            InputTokens = input,
            OutputTokens = output,
            PrevHash = prev,
            Mac = mac,
        };
    }

    private static bool TryString(JObject o, string name, out string value)
    {
        JToken? token = o[name];
        value = string.Empty;
        if (token == null || token.Type != JTokenType.String)
        {
            return false;
        }

        value = token.Value<string>() ?? string.Empty;
        return true;
    }

    private static bool TryLong(JObject o, string name, out long value)
    {
        JToken? token = o[name];
        value = 0;
        if (token == null || token.Type != JTokenType.Integer)
        {
            return false;
        }

        value = token.Value<long>();
        return true;
    }

    private static bool TryDouble(JObject o, string name, out double value)
    {
        JToken? token = o[name];
        value = 0;
        if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer))
        {
            return false;
        }

        value = token.Value<double>();
        return true;
    }

    private static bool TryBool(JObject o, string name, out bool value)
    {
        JToken? token = o[name];
        value = false;
        if (token == null || token.Type != JTokenType.Boolean)
        {
            return false;
        }

        value = token.Value<bool>();
        return true;
    }

    private static string Text(string? value)
    {
        return value ?? string.Empty;
    }

    private static double Usd(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;
    }
}
