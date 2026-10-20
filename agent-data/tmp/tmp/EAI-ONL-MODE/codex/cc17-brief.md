# CC-17 follow-up brief for the Codex lead (2026-10-18)

Read `tmp/EAI-ONL-MODE/codex/HANDOFF.md` section 0 and 6 first; those rules still bind you. Reply in English. You may split the
packages below among your own Codex subagents, but only as the dependency order allows (see "Order"). You do not run git commit.
Claude reviews and commits. Results go to `tmp/EAI-ONL-MODE/codex/results-CC17-<pkg>.md` in the T-report format (numbers from logs).

Inputs reviewed by Claude (accepted as evidence): results-T1..T7.md. T7 stays blocked (user still relabelling).

## Package A - OnlineEndpoint hardening (security, single writer, Claude reviews every line)
Allow-list: `EasyLabel/src/EasyLabel.Online/OnlineEndpoint.cs`, `EasyLabel/src/EasyLabel.Online/WireCodec.cs`,
`EasyLabel/tests/EasyLabel.Online.Tests/EndpointHardeningTests.cs` and NEW test files in that folder. Nothing else.
Fix the defects found in T4/T5, C# 7.3 syntax, no new packages, wire format version stays "EOL1" and valid requests stay byte-compatible:
1. (T5.1 High) Cap concurrent connections (constructor option `maxConnections`, default 8, over-limit connections are closed immediately and counted in `Rejected`).
   No unbounded thread creation. Keep one thread per accepted client (fine at this cap).
2. (T5.2 Medium) Authenticate before allocating the big body: read the fixed header and the 32-byte token first, compare, and only then allocate/read the rest.
   Before auth cap the declared body at a small constant (e.g. token + id + 8 bytes + 0 pixels is NOT enough; use: reject unauthenticated when declared length
   exceeds `maxBodyBytes`, but never allocate until the token matched). Keep `ReadRequest` public API usable by existing tests (add a new overload rather than break callers).
3. (T5.3 / T4 reproducer) `Dispose` must close accepted client sockets, join client threads with a bounded timeout, and not unsubscribe `ResultReady` before in-flight waiters are released.
   `DisposeClosesAcceptedClientConnection` must pass without being weakened.
4. (T5.4) Normalise IPv4-mapped IPv6 remote addresses (`MapToIPv4`) before the allow-list check.
Add tests for 1, 2 (unauthenticated oversize declared length allocates nothing: assert via a stream that records bytes read), 4. Run `dotnet test EasyLabel/tests/EasyLabel.Online.Tests -c Release` (log to file). Target: all existing tests plus new ones pass.

## Package B - WPF unit tests (scope decision made: option 1 approved)
Allow-list (new files only): `EasyLabel/src/EasyAI.Wpf/Properties/InternalsVisibleToTests.cs` containing only
`[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("EasyAI.Wpf.Tests")]`, and a new project `EasyLabel/tests/EasyAI.Wpf.Tests/` (net9.0-windows, UseWPF, C# 7.3,
xunit like the other test projects, project reference to `EasyAI.Wpf.csproj`, no new NuGet packages beyond what Directory.Packages.props already lists).
Tests: `OnlineStatusViewModel.TryParseStatus` (idle JSON, full JSON, missing totalP95Ms, negative counter, malformed JSON), `TryParseThresholds` (0, 1, 0.5, 1.01, -0.1, NaN, "abc", comma decimal),
BudgetState (Unknown/Ok/Over, boundary p95 == budget), command CanExecute follows IsRunning, and `OnlineControlAdapter` Attach/Detach/NOT_RUNNING with a real `OnlineSession`
built from `Fixtures`-style data (copy the minimal recipe construction; do not link test files). VM needs a Dispatcher: create it on an STA thread inside the test. If EasyAI.Wpf's
post-build copying of Agent MCP output makes the test project unreasonable, report exactly why and stop.

## Package C - OnlineStatusWindow follow-ups (after B finished building; same Wpf build output)
Allow-list: `EasyLabel/src/EasyAI.Wpf/Views/OnlineStatusWindow.xaml`, `tmp/EAI-ONL-MODE/smoke/*`.
1. Add AutomationIds to RecipeId, Accepted, Completed, Dropped, Failed, P95BudgetText, ThresholdMessage and the running indicator (T3 low finding).
2. Fix the T2 harness `ValidatePixels` alpha issue so blank detection is reliable; re-run the harness at 96 DPI (en/vi, idle/running) and save PNGs to `artifacts/EAI-ONL-MODE/ui-smoke/<new run>/`.
3. Try a real 144-DPI run (per-monitor DPI via the harness manifest or a DPI-aware app setting). If the environment cannot produce a true 144 DPI window, say so; do not fake it by scaling bitmaps.
4. Only if the 360 DIP Vietnamese capture shows a real problem, switch the two-column grids to a wrap-friendly layout; otherwise leave the layout alone.

## Order
A and B may run in parallel only if they never build at the same time (both compile `EasyLabel.Online`); simplest: A first, then B, then C. Do not run two `dotnet build` of the same project concurrently.

## Not in scope
Wiring the window/adapter into MainWindow/MainViewModel/WpfCommandHost, CC-3..CC-5, anything touching OnlineBench, T7/model work.

## Codex execution plan CC17-A-r1 (2026-10-18)

Task reference: task-mvdidyw5-6zr0qz. Direct user request and this brief authorize sequential A, B, C. Deep route for A; conditional easyvision_architect approved this exact A-only revision after discovery. No separate task was created; no ChatGPT Work activation/approval is fabricated. Existing root runtime is retained.

A-only file scope: OnlineEndpoint.cs, WireCodec.cs, existing EndpointHardeningTests.cs and new Online.Tests files. Preserve EOL1 valid bytes and original public APIs. Preserve the existing five-parameter constructor exactly, forwarding to a new explicit six-parameter maxConnections constructor (default behavior8). Under lifecycle gate coordinate Start/stopping, capacity, registration and Thread.Start atomically, rollback startup failure, unregister in Serve finally; reject/close/count over-limit before new threads. Close sockets outside locks.

Authenticated ReadRequest overload: validate expected32-byte token before reading. Read8-byte EOL1/header and validate cap/minimum/overflow boundaries, then32-byte token fixed-time compare. UnauthorizedAccessException on mismatch; endpoint countsRejected and sendsUnauthorized. No large allocation/read before auth. After auth parse bounded1..256-byte ID, dimensions and remaining pixels directly; validate positive dimensions and long width*height against remaining bytes before allocating pixels. Preserve old public overload/token return and leave trailing bytes untouched.

Dedicated waiter gate: registration/result lookup+assignment+Set/removal+worker-owned handleDispose use the same lock; registration checks volatile stopping. Duplicate registration disposes only its own new handle. No waiter/lifecycle lock nesting or locks across Submit, I/O, Wait, Close or Join. Dispose snapshots under lifecycle gate, stops listener/closes accepted sockets outside gates, signals waiters without disposing worker-owned handles, joins accept/client threads with one total monotonic timeout budget (skip current thread), then unsubscribes ResultReady. Repeated dispose and late handlers remain safe after deadline.

Normalize both configured and remote IPv4-mapped IPv6 addresses only; preserve nativeIPv6. Tests: keep DisposeClosesAcceptedClientConnection unchanged or stricter; capacity rejection/count/re-admission; recording stream overcap8bytes and bad-tokenwithin-cap40bytes/no tail; valid authenticated+legacydecoder parity; mappedIPv6allowlistactualIPv4admission; pendingdisposal/latecallback/duplicatecleanup races. Bytecount proves consumption boundaries; no-large-allocation claim needs source inspection, not assumed allocation measurement.

One production-code writer applies A and self-reviews. Authorized read-only verifier runs focused dotnet test EasyLabel/tests/EasyLabel.Online.Tests -c Release with unique log/binlog and project-local TEMP/TMP. Independent reviewer checks actual diff/evidence; at most two bounded fix cycles. B starts only after A acceptance, C only after B build. No commit/push/branch/worktree, tracking, model/training/bench/wiring/hardware or legacy build. C#7.3 authored syntax throughout. Stop only affected package if out-of-scope compiler/dependency/source changes are necessary. Reports results-CC17-A/B/C.md include required audit and exact evidence. Baseline tmp/EAI-ONL-MODE/cc17/20261018T073547Z.
