namespace EasyLabel.SegAI.Tests;

public sealed class AgentSpendLedgerTests
{
    private static readonly DateTime Day1 = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Clock
    {
        public DateTime Utc { get; set; }
    }

    private sealed class MemoryLedgerStore : EasyAI.Desktop.Services.Agent.IAgentLedgerStore
    {
        public string Text { get; set; } = string.Empty;

        public List<string> Evidence { get; } = new List<string>();

        public string ReadAll()
        {
            return Text;
        }

        public void Append(string text)
        {
            Text += text;
        }

        public void ReplaceAll(string text)
        {
            Text = text;
        }

        public void SaveEvidence(string text)
        {
            Evidence.Add(text);
        }
    }

    private sealed class MemoryLedgerAnchor : EasyAI.Desktop.Services.Agent.IAgentLedgerAnchor
    {
        public EasyAI.Desktop.Services.Agent.AgentLedgerAnchorSnapshot? Current { get; set; }

        public bool FailWrites { get; set; }

        public bool ReadUnavailable { get; set; }

        public EasyAI.Desktop.Services.Agent.AgentLedgerAnchorRead TryRead(out EasyAI.Desktop.Services.Agent.AgentLedgerAnchorSnapshot? snapshot)
        {
            snapshot = Current;
            if (ReadUnavailable)
            {
                return EasyAI.Desktop.Services.Agent.AgentLedgerAnchorRead.Unavailable;
            }

            return Current == null ? EasyAI.Desktop.Services.Agent.AgentLedgerAnchorRead.Missing : EasyAI.Desktop.Services.Agent.AgentLedgerAnchorRead.Found;
        }

        public bool TryWrite(EasyAI.Desktop.Services.Agent.AgentLedgerAnchorSnapshot snapshot)
        {
            if (FailWrites)
            {
                return false;
            }

            Current = snapshot;
            return true;
        }
    }

    private static EasyAI.Desktop.Services.Agent.AgentSpendLedger NewLedger(MemoryLedgerStore store, MemoryLedgerAnchor anchor, Clock clock)
    {
        return new EasyAI.Desktop.Services.Agent.AgentSpendLedger(store, anchor, () => clock.Utc, utc => utc, "test-instance");
    }

    private static Clock NewClock()
    {
        return new Clock { Utc = Day1 };
    }

    // ---------- reading the day cap ----------

    [Fact]
    public void ReserveThenSettleCountsOnlyTheSettledAmount()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger ledger = NewLedger(store, anchor, clock);

        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Ok, ledger.Load());
        Assert.NotNull(ledger.Reserve("r1", "label", "sonnet", 0.5));
        Assert.NotNull(ledger.Settle("r1", "label", "sonnet", 0.2, false));

        Assert.Equal(0.8, ledger.DayRemainingUsd(1.0), 6);
        Assert.True(double.IsPositiveInfinity(ledger.DayRemainingUsd(0)));
    }

    [Fact]
    public void CrashedReservationCountsAsFullySpent()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger first = NewLedger(store, anchor, clock);
        first.Load();
        first.Reserve("r1", "label", "sonnet", 0.5);

        EasyAI.Desktop.Services.Agent.AgentSpendLedger second = NewLedger(store, anchor, clock);
        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Ok, second.Load());
        Assert.Equal(0.5, second.DayRemainingUsd(1.0), 6);
    }

    [Fact]
    public void ChainSurvivesReloadWithOrderedRecords()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger ledger = NewLedger(store, anchor, clock);
        ledger.Load();
        for (int i = 1; i <= 3; i++)
        {
            ledger.Reserve("r" + i, "suggest", "sonnet", 0.1);
            clock.Utc = clock.Utc.AddSeconds(5);
            ledger.Settle("r" + i, "suggest", "sonnet", 0.1, true, 10, 20);
        }

        EasyAI.Desktop.Services.Agent.AgentSpendLedger reloaded = NewLedger(store, anchor, clock);
        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Ok, reloaded.Load());
        IReadOnlyList<EasyAI.Desktop.Services.Agent.AgentLedgerRecord> records = reloaded.Records();
        Assert.Equal(6, records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal((long)(i + 1), records[i].Seq);
        }

        Assert.Equal(0.7, reloaded.DayRemainingUsd(1.0), 6);
    }

    [Fact]
    public void NextLocalDayStartsWithTheFullCap()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger ledger = NewLedger(store, anchor, clock);
        ledger.Load();
        ledger.Reserve("r1", "label", "sonnet", 0.9);
        ledger.Settle("r1", "label", "sonnet", 0.9, false);

        Assert.Equal(0.1, ledger.DayRemainingUsd(1.0), 6);

        clock.Utc = Day1.AddDays(1);
        Assert.Equal(1.0, ledger.DayRemainingUsd(1.0), 6);
    }

    [Fact]
    public void ClockSetBackDoesNotReopenTheSpentDay()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger ledger = NewLedger(store, anchor, clock);
        ledger.Load();
        ledger.Reserve("r1", "label", "sonnet", 0.6);
        ledger.Settle("r1", "label", "sonnet", 0.6, false);

        clock.Utc = Day1.AddDays(-1);

        Assert.Equal(0.4, ledger.DayRemainingUsd(1.0), 6);
        Assert.NotNull(ledger.Reserve("r2", "label", "sonnet", 0.1));
        IReadOnlyList<EasyAI.Desktop.Services.Agent.AgentLedgerRecord> records = ledger.Records();
        Assert.True(records[records.Count - 1].Utc >= records[records.Count - 2].Utc);
    }

    // ---------- tamper detection ----------

    [Fact]
    public void EditedAmountIsDetectedAndAcknowledgementKeepsEvidence()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger ledger = NewLedger(store, anchor, clock);
        ledger.Load();
        ledger.Reserve("r1", "label", "sonnet", 0.5);
        ledger.Settle("r1", "label", "sonnet", 0.2, false);

        store.Text = store.Text.Replace("\"usd\":0.2,", "\"usd\":0.01,");

        EasyAI.Desktop.Services.Agent.AgentSpendLedger reloaded = NewLedger(store, anchor, clock);
        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Tampered, reloaded.Load());
        Assert.Equal(0.0, reloaded.DayRemainingUsd(1.0), 6);
        Assert.Null(reloaded.Reserve("r2", "label", "sonnet", 0.1));

        Assert.True(reloaded.Acknowledge());
        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Ok, reloaded.Integrity);
        Assert.Single(store.Evidence);
        Assert.Contains("\"usd\":0.01,", store.Evidence[0]);
        Assert.Equal(1.0, reloaded.DayRemainingUsd(1.0), 6);
    }

    [Fact]
    public void DeletedLedgerTextIsDetected()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger ledger = NewLedger(store, anchor, clock);
        ledger.Load();
        ledger.Reserve("r1", "label", "sonnet", 0.5);
        ledger.Settle("r1", "label", "sonnet", 0.2, false);

        store.Text = string.Empty;

        EasyAI.Desktop.Services.Agent.AgentSpendLedger reloaded = NewLedger(store, anchor, clock);
        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Tampered, reloaded.Load());
        Assert.Equal(0.0, reloaded.DayRemainingUsd(1.0), 6);
    }

    [Fact]
    public void RemovedLastLineIsDetected()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger ledger = NewLedger(store, anchor, clock);
        ledger.Load();
        ledger.Reserve("r1", "label", "sonnet", 0.5);
        ledger.Settle("r1", "label", "sonnet", 0.2, false);

        store.Text = store.Text.Substring(0, store.Text.LastIndexOf('\n', store.Text.Length - 2) + 1);

        EasyAI.Desktop.Services.Agent.AgentSpendLedger reloaded = NewLedger(store, anchor, clock);
        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Tampered, reloaded.Load());
    }

    [Fact]
    public void TornLastLineThatTheAnchorCoversIsDetected()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger ledger = NewLedger(store, anchor, clock);
        ledger.Load();
        ledger.Reserve("r1", "label", "sonnet", 0.5);
        ledger.Settle("r1", "label", "sonnet", 0.2, false);

        store.Text = store.Text.Substring(0, store.Text.Length - 10);

        EasyAI.Desktop.Services.Agent.AgentSpendLedger reloaded = NewLedger(store, anchor, clock);
        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Tampered, reloaded.Load());
    }

    [Fact]
    public void MissingAnchorWithRecordsIsDetected()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger ledger = NewLedger(store, anchor, clock);
        ledger.Load();
        ledger.Reserve("r1", "label", "sonnet", 0.5);

        anchor.Current = null;

        EasyAI.Desktop.Services.Agent.AgentSpendLedger reloaded = NewLedger(store, anchor, clock);
        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Tampered, reloaded.Load());
    }

    [Fact]
    public void UnreadableAnchorFailsClosed()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor { ReadUnavailable = true };
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger ledger = NewLedger(store, anchor, clock);

        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Unknown, ledger.Load());
        Assert.Equal(0.0, ledger.DayRemainingUsd(1.0), 6);
        Assert.Null(ledger.Reserve("r1", "label", "sonnet", 0.1));
        Assert.False(ledger.Acknowledge());
        Assert.Empty(store.Evidence);
    }

    [Fact]
    public void AcknowledgingAHealthyLedgerKeepsEvidenceEmpty()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger ledger = NewLedger(store, anchor, clock);
        ledger.Load();

        Assert.True(ledger.Acknowledge());
        Assert.Empty(store.Evidence);
    }

    // ---------- crash and concurrency ----------

    [Fact]
    public void RecordWrittenBeforeAnchorUpdateIsAdopted()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger ledger = NewLedger(store, anchor, clock);
        ledger.Load();
        ledger.Reserve("r1", "label", "sonnet", 0.3);
        EasyAI.Desktop.Services.Agent.AgentLedgerAnchorSnapshot? before = anchor.Current;
        ledger.Reserve("r2", "label", "sonnet", 0.4);

        anchor.Current = before;

        EasyAI.Desktop.Services.Agent.AgentSpendLedger reloaded = NewLedger(store, anchor, clock);
        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Ok, reloaded.Load());
        Assert.Equal(0.3, reloaded.DayRemainingUsd(1.0), 6);
    }

    [Fact]
    public void TornLineFromAnInterruptedWriteIsRepairedBeforeTheNextRecord()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger first = NewLedger(store, anchor, clock);
        first.Load();
        first.Reserve("r1", "label", "sonnet", 0.3);
        store.Text += "{\"seq\":2,\"utc\":\"2026-";

        EasyAI.Desktop.Services.Agent.AgentSpendLedger second = NewLedger(store, anchor, clock);
        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Ok, second.Load());
        Assert.NotNull(second.Reserve("r2", "label", "sonnet", 0.4));

        EasyAI.Desktop.Services.Agent.AgentSpendLedger third = NewLedger(store, anchor, clock);
        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Ok, third.Load());
        Assert.Equal(2, third.Records().Count);
        Assert.Equal(0.3, third.DayRemainingUsd(1.0), 6);
    }

    [Fact]
    public void TwoInstancesShareOneChain()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor();
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger a = NewLedger(store, anchor, clock);
        EasyAI.Desktop.Services.Agent.AgentSpendLedger b = NewLedger(store, anchor, clock);
        a.Load();
        b.Load();

        Assert.NotNull(a.Reserve("r1", "label", "sonnet", 0.5));
        Assert.NotNull(b.Reserve("r2", "label", "sonnet", 0.3));

        Assert.Equal(0.2, a.DayRemainingUsd(1.0), 6);
        Assert.Equal(0.2, b.DayRemainingUsd(1.0), 6);
    }

    [Fact]
    public void FailedAnchorWriteStopsTheRunWithoutCorruptingTheLedger()
    {
        MemoryLedgerStore store = new MemoryLedgerStore();
        MemoryLedgerAnchor anchor = new MemoryLedgerAnchor { FailWrites = true };
        Clock clock = NewClock();
        EasyAI.Desktop.Services.Agent.AgentSpendLedger ledger = NewLedger(store, anchor, clock);
        ledger.Load();

        Assert.Null(ledger.Reserve("r1", "label", "sonnet", 0.3));
        Assert.Equal(string.Empty, store.Text);

        anchor.FailWrites = false;
        Assert.NotNull(ledger.Reserve("r1", "label", "sonnet", 0.3));

        EasyAI.Desktop.Services.Agent.AgentSpendLedger reloaded = NewLedger(store, anchor, clock);
        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentLedgerIntegrity.Ok, reloaded.Load());
        Assert.Equal(0.7, reloaded.DayRemainingUsd(1.0), 6);
    }
}
