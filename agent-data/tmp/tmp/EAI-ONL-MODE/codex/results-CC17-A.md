# CC17-A — OnlineEndpoint hardening

## Scope and files

Direct user authorization: execute `cc17-brief.md`, Package A, under HANDOFF sections 0 and 6. Architect plan CC17-r1; one source writer. No Work activation or new map ID was invented. No commit, tracking, package, model, wiring, or legacy application changes.

Changed files:
- `EasyLabel/src/EasyLabel.Online/OnlineEndpoint.cs`
- `EasyLabel/src/EasyLabel.Online/WireCodec.cs`
- `EasyLabel/tests/EasyLabel.Online.Tests/EndpointHardeningTests.cs` (preserved prior tests)

Preserved invariants: EOL1 valid request bytes, original five-argument endpoint constructor and original public codec overload, external runner ownership, C# 7.3. The constructor delegates to the new `maxConnections` overload with default 8. Admissions are capped before client-thread creation; mapped IPv6 addresses normalize before matching. Authentication reads the fixed header/token before pixel allocation. Disposal closes accepted clients, releases waiters, uses one monotonic 2000 ms join budget, and detaches results last. Disposal is terminal.

## Commands and numeric evidence

All executions use project-local TEMP/TMP. Build output is logged; each MSBuild invocation has a separate binlog. The final root invocation sets PowerShell `$ErrorActionPreference='Continue'` to retain native stderr diagnostics.

| Execution | Log | Result |
| --- | --- | --- |
| `dotnet test EasyLabel/tests/EasyLabel.Online.Tests -c Release -bl:<local binlog>` | `tmp/EAI-ONL-MODE/cc17-a/20261018T144422637Z/online-tests.log` | Restore blocked: access denied to user NuGet.Config; no tests |
| Same command with `--no-restore` | `tmp/EAI-ONL-MODE/cc17-a/20261018T144457048Z/online-tests.log` | CS0031: test length 512 assigned to byte; corrected |
| No-restore retry | `tmp/EAI-ONL-MODE/cc17-a/20261018T144604198Z/online-tests.log` | Incomplete failure output; superseded below |
| No-restore filtered cap test, `--filter FullyQualifiedName~ConnectionCapRejectsBeforeStartingAnotherClientAndReleasesItsSlot --logger "console;verbosity=normal"` | `tmp/EAI-ONL-MODE/cc17-a/root-20261018T144805698/cap-test.log` | 1 failed: test called GetStream after Shutdown; corrected by retaining the stream |
| `dotnet test EasyLabel/tests/EasyLabel.Online.Tests -c Release --no-restore --logger "console;verbosity=normal" -bl:artifacts/EAI-ONL-MODE/cc17-a/root-full-20261018T144909397/online-tests.binlog` | `tmp/EAI-ONL-MODE/cc17-a/root-full-20261018T144909397/online-tests.log` | **154 passed / 154 total; 0 failed; 0 compiler errors; 6 analyzer warnings; test time 2.9253 s** |

Final binlog exists: 438101 bytes. Earlier binlogs remain under the corresponding `artifacts/EAI-ONL-MODE/cc17-a/` directories. Six final analyzer diagnostics are existing xUnit1031 locations in EndpointHardeningTests, RunnerConcurrencyTests and SessionConcurrencyTests, plus xUnit2029 in SchemaAndBoundaryTests.

The original `DisposeClosesAcceptedClientConnection` assertion remains unchanged and passes. New stream evidence checks 40 bytes read for an invalid token and 8 bytes for declared length 1025 against cap 1024. New tests cover admission/rejection/slot reuse, first/last token mismatches, legacy/new codec reads, and mapped configured addresses.

## Audit and remaining limits

Task ID: CC17-A. Exact approved scope and files appear above. Scoped whitespace, conflict-marker, declaration/scope and C# 7.3 checks passed; both Online and Online.Tests compile with existing LangVersion 7.3. First distinct compiler root error was CS0031 in the new test; none remains in the final run. The old disposal regression was not weakened. Original dirty work and prior tests were preserved.

A test attempts IPv4-to-IPv6-listener coverage but returns when IPv6/dual-stack connection is unavailable, so its passing test count alone does not prove native mapped-remote coverage. Configured mapped-address normalization is exercised over loopback. No production load/latency or hostile-network throughput claim is made. An early verifier inference of a hung test process was not established; the root's diagnostic and full runs completed normally.

Independent review: pending. Final overall acceptance belongs to Claude.
