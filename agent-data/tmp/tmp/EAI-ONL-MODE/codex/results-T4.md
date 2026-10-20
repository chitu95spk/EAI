# T4 — Online backend hardening tests

## Files created

- `EasyLabel/tests/EasyLabel.Online.Tests/EndpointHardeningTests.cs`
- `EasyLabel/tests/EasyLabel.Online.Tests/RunnerConcurrencyTests.cs`
- `EasyLabel/tests/EasyLabel.Online.Tests/SessionConcurrencyTests.cs`

## Results

| Command | Log / artifact | Result |
| --- | --- | --- |
| Initial `dotnet test EasyLabel/tests/EasyLabel.Online.Tests/EasyLabel.Online.Tests.csproj -c Release -bl:artifacts/EAI-ONL-MODE/t4/online-tests.binlog` | `tmp/EAI-ONL-MODE/t4/online-tests.log`; `artifacts/EAI-ONL-MODE/t4/online-tests.binlog` | Superseded by hardening review: Passed 148; Failed 0 |
| Reviewer-cycle `dotnet test EasyLabel/tests/EasyLabel.Online.Tests/EasyLabel.Online.Tests.csproj -c Release -bl:artifacts/EAI-ONL-MODE/t4-fix1b/online-tests.binlog` | `tmp/EAI-ONL-MODE/t4-fix1b/online-tests.log`; `artifacts/EAI-ONL-MODE/t4-fix1b/online-tests.binlog` | Passed: 147; Failed: 1; Skipped: 0; duration: 926 ms. The single failure is the intentional production-defect reproducer below. |
| scoped trailing-whitespace and conflict-marker scan of the three untracked files | console | PASS — none found. `git diff --check` is n/a because it does not inspect untracked files. |

## Audit

- **Task ID:** T4.
- **Approved scope and preserved invariants:** new test files only under `EasyLabel/tests/EasyLabel.Online.Tests`; no `src` or project-file changes; loopback-only, finite eight-client maximum, strict 250 ms/5 s/10 s test deadlines, bounded 16-byte frames, and no random sleep stress or induced unbounded allocation/thread creation.
- **Exact changed files:** the three files listed above.
- **C# compiler assumption:** project declares `LangVersion` 7.3; all authored syntax remains C# 7.3-compatible.
- **First distinct compiler/root errors:** none found.
- **Defect found:** `OnlineEndpoint.Dispose` stops the listener and removes `ResultReady`, but it does not close already accepted client sockets. `DisposeClosesAcceptedClientConnection` intentionally fails with `Expected: -1; Actual: 69` (`E` from a delayed EOL1 `Failed` response). It is a deterministic active-client ownership reproducer, retained as required; no production fix was made.
- **Other covered behavior:** duplicate in-flight IDs are rejected; valid-length truncated bodies and slow partial clients close without blocking a subsequent loopback request; oversize requests receive `BadRequest`; concurrent Stop/Submit and threshold swap/frames/Stop finish with bounded synchronization.
- **Residual risks:** the test uses a finite eight-client loopback gate and cannot measure production connection-rate limits or the 30-second receive-timeout behavior. `OnlineEndpoint` still creates one background thread per accepted client by design. Shutdown behavior remains unverified beyond the documented failing reproducer.

Independent reviewer: ACCEPT for the bounded diagnostic deliverable. Backend readiness remains blocked on the retained endpoint disposal failure.
