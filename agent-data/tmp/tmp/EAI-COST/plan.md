# EAI-COST plan rev 1 (EAI-COST-plan-r1)

Source: easyvision-architect report, saved by the coordinator. Static reading only; nothing built or run. Plan is a candidate until the scope revision below is approved.

## Scope revision needed before S4, S6, S8
Files outside contract in_scope:
- EasyLabel/src/EasyAI.Wpf/Services/AgentRunner.cs (single place every agent process starts; not under Services/Agent/**)
- EasyLabel/src/EasyAI.Wpf/ViewModels/LabelStepViewModel.cs (day-cap reads, batch setup, suggest, outcome switches; lines ~1061, 1168-1228, 1808, 1859, 1999, 2127)
- EasyLabel/src/EasyAI.Wpf/Infrastructure/AppSettings.cs, SettingsAccessor.cs, SettingsModel.cs (settings rows live here)
Contract mismatches: EasyLabel.Ai and Ai.Mcp launch no runs (no change needed). No model-download/update-check C# in EasyAI; only HttpClient is price refresh (AgentsSettingsViewModel.cs:522-563, AgentPrices.cs:260).

## Findings
- BE-15 NOT a bug: _killed reset under _liveLock before each start (AgentRunner.cs:436-439). Tiny gap: CancelAndKill between 438 and 457 ignored (token still works). AgentJob never disposed (low).
- BE-01 confirmed: no budget flag (AgentCommandLine.cs:36-55,158-178); parallel reserve is an average guess (AgentBatchRunner.cs:318-325). --max-budget-usd support in installed CLI UNVERIFIED.
- BE-02 confirmed, worse: Cancelled/TimedOut cost 0 (AgentRunner.cs:506,511); batch loops drop Cancelled cost (AgentBatchRunner.cs:229-233,397-406); no mid-run usage parsing (AgentResults.cs:216-228).
- BE-03 confirmed: SuggestAsync/RefinePromptAsync not logged (AgentRunner.cs:323-431), use --output-format json.
- BE-04 confirmed: day cap from log.csv with sync disk I/O on UI thread (LabelStepViewModel.cs:1061,1168); AgentSpendTracker.cs:81-143 skips damaged lines, missing file = zero; Codex has no cost.
- Test seam: EasyLabel.SegAI.Tests.csproj:37-67 compiles agent sources directly; new files must be added there. PowerShell fake-agent harness exists (AgentRunnerProcessTests.cs:55-65, AgentRunner.cs:89-119).
- Language: EasyLabel is net9, LangVersion 12, nullable on (Directory.Build.props:3-4). No C# 13+. C# 7.3 rule applies to EasyVision .NET Framework projects only.
- No ProtectedData package: DPAPI would be a new NuGet (stop-and-ask). Use HMACSHA256.

## Invariants
1. No agent process starts unless Offline-only is off, project has consent, and a reservation is granted.
2. Sum of granted reservations <= min(batch cap left, day cap left). Overshoot <= one model turn per active worker (documented granularity unit).
3. Every started process ends in a Settle record, cost > 0 or flagged estimated. Crash leaves open Reserve = fully spent.
4. No ledger/consent/probe I/O on the UI thread.
5. vi/en resx key sets identical; no raw exception text to users.
6. Unknown manifest keys survive save; old manifests open.
7. New properties null = today's behaviour.

## Slices (each must compile alone)
- S1 Metering: AgentStreamParser.cs, AgentStreamEvent.cs, new AgentUsageMeter.cs. Per-message last usage, priced; estimate = max(token price, turns x conservative USD/turn); fallback price in AgentPrices.cs.
- S2 Ledger: new AgentSpendLedger.cs behind IAgentLedgerStore / IAgentLedgerAnchor. Append-only JSONL %LOCALAPPDATA%\EasyAI\agent-ledger\ledger-yyyy-MM.jsonl; records Reserve/Settle/Ack with seq, utc, instanceId, kind, tool, model, usd, estimated, tokens, prevHash, HMAC-SHA256. Key + anchor (lastSeq,lastHash) in HKCU\Software\EasyAI\AgentLedger with file fallback. Named mutex, flush to disk. Broken chain / missing / behind anchor => Tampered, day remaining 0 until Ack; torn last line covered by anchor = crash artifact. Day = max(now, last record utc). Load once async, keep in memory.
- S3 Budget: new AgentSpendBudget.cs, one thread-safe object per batch shared by all workers/retries. TryReserve = remaining unreserved / free slots, refuse < 0.02 USD. Settle returns unused. Suggest/refine ceiling ~0.15 USD.
- S4 Runner wiring (needs scope revision): AgentRunner.cs, AgentCommandLine.cs, AgentResults.cs, AgentCacheAndLog.cs. Props Budget, Ledger, Prices, IsOffline, RequireConsent, MaxBudgetFlagSupported. One guarded path: offline/consent -> async reserve -> optional --max-budget-usd -> run with watchdog -> Settle. Watchdog in ReadStdoutAsync (AgentRunner.cs:565-573) kills tree at reservation, new AgentOutcomeKind.BudgetLimit appended last. Cancel/Timeout/BudgetLimit cost = max(estimate, floor), estimated flag. Codex: fixed conservative reserve, estimate from tokens line. AgentRunResult.IsCostEstimated; log.csv trailing estimated column (parser reads cols 0-11 by index).
- S5 Batch: AgentBatch.cs, AgentBatchRunner.cs. AgentBatchProgress.AddSpend counts Cancelled cost; BudgetLimit -> Stop CostCap/DayCap; keep old reserve as second defence.
- S6 View model (needs scope revision): LabelStepViewModel.cs, .AgentChat.cs:502,819. Awaited ledger snapshot; one budget for runners at 1186-1226; BudgetLimit messages at 1859/1999; process-wide AgentSpendLedger.Shared.
- S7 Capability probe: new AgentCliCapabilities.cs; claude --help once in background, 10 s timeout, skipped offline, cached by exe path+mtime; flag missing => watchdog only.
- S8 Offline-only (needs scope revision): Infrastructure settings files, AgentsSettingsViewModel.cs:563 and login, AgentPriceStore.RefreshAsync offline guard. Agent commands disabled with plain explanation. Test seam: start-info factory + HttpMessageHandler failing on any call.
- S9 Consent: EasyLabel.Core/Projects/ProjectManifest.cs, ProjectManifestStore.cs:72-111. Nullable agentConsent {version, granted, utc, tools}, written only when set. DialogService.Confirm (DialogService.cs:437) before any agent action; decline sends nothing; manifest write off UI thread; Revoke button in settings.
- S10 Strings: Strings.resx / Strings.vi.resx (Consent*, OfflineOnly*, AgentBudgetStopped, AgentLedgerTampered/Acknowledge, AgentCostEstimated) + key-parity script in tmp/EAI-COST/.
- S11 (optional) Disposal: AgentRunner/AgentJob IDisposable, dispose batch runners in finally.

## Verification (advisory; build/test need authorization)
Linked Compile items in EasyLabel.SegAI.Tests.csproj; fake scripts in tmp/EAI-COST/fakes/ (stream spend, hang, codex tokens, json suggest). Tests: meter dedupe; ledger chain/delete/edit/torn line; 8-worker reservation stress; budget kill; cancel cost > 0; offline = zero launches; old manifests open and unknown keys survive (Core.Tests). Build WPF, Core, SegAI.Tests, Core.Tests; logs to artifacts/EAI-COST/<run>/ with TEMP/TMP inside project.

## Risks
--max-budget-usd support unknown; Codex cannot be bounded mid-run; ledger tamper-evident not tamper-proof; clock forward starts day early; stale prices while offline (fallback limits); new enum value affects all outcome switches; RunCoreAsync sync section before first await does disk I/O on UI thread (AgentRunner.cs:238-302); Ultralytics weight download out of scope.

## Assumptions
1. stream-json assistant lines carry message.usage and message.id (else turn floor still gives a watchdog). 2. HKCU writable on customer PCs (file fallback). 3. Fair-share ceiling acceptable UX.

## Needs from user
- Scope revision for the 3 file groups above (before S4, S6, S8).
- Approval to run `claude --help` (local, no spend); explicit consent for any paid real-agent run.
- build / run_tests / commit authorization.
