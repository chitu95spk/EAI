# T5 — Online endpoint security review (read-only)

Date: 2026-10-18. No source or test code changed; no build or test run.

## Findings

1. **High — unauthenticated connection count and worker threads are unbounded.** `OnlineEndpoint.AcceptLoop` creates one dedicated background thread per allow-listed TCP connection (`EasyLabel/src/EasyLabel.Online/OnlineEndpoint.cs:70-80`; `IsBackground=true` at line 79). Each connection can retain its thread while it blocks in `ReadRequest` or waits for an inference result (`:84-105`, `:109-127`). `ReceiveTimeout=30000` applies to an individual blocking socket read (`:89`), not an overall request deadline. A client that trickles bytes before each read timeout can keep a request alive indefinitely. There is no semaphore, accepted-client queue, or connection limit. An allow-listed host can exhaust threads and memory with slow/incomplete requests. Recommended hardening: bounded concurrent-client admission, close/refuse when full, and an overall request deadline with cancellation.

2. **Medium — full request body is allocated and copied before authentication.** `WireCodec.ReadRequest` accepts the declared length below `maxBodyBytes`, reads it into `body`, then copies pixels into a second allocation (`EasyLabel/src/EasyLabel.Online/WireCodec.cs:54-77`); `OnlineEndpoint.Serve` compares the token only after that call returns (`OnlineEndpoint.cs:93-103`). Thus every unauthenticated request can consume approximately two full frame bodies (plus temporary buffers) per connection. The configured cap bounds one request but does not bound aggregate pressure, especially with finding 1. Recommended hardening: authenticate a fixed token segment before materializing pixels, impose an image-pixel/byte cap that is conservative for the deployment, and combine it with bounded clients.

3. **Medium — `Dispose` can return with serving client threads still live.** Disposal stops only the listener and joins only `acceptThread` for two seconds (`OnlineEndpoint.cs:56-62`); per-client background threads and their sockets are neither tracked nor closed. Disposal also unsubscribes `ResultReady`, so an in-flight submitted frame can lose its completion wake-up and wait until `resultTimeoutMs`. A client read can continue under the per-read timeout semantics described above. This weakens shutdown guarantees and can leave orphaned waits/work after a host requests stop. Recommended hardening: track active clients/cancellation, close them during disposal, and have `Handle` stop/cancel waits promptly.

4. **Informational — allow-list matching is exact IPAddress equality, so IPv4-mapped IPv6 endpoints do not normalize.** The remote address is matched directly against the hash set (`OnlineEndpoint.cs:77-78`), with no address-family or mapped-address normalization. This is a fail-closed compatibility risk for an IPv4 allow-list under mapped IPv6; no authorization bypass was found. Recommended hardening: normalize both configured and observed addresses deliberately, with tests for IPv4 and IPv4-mapped IPv6.

5. **Informational — authentication comparison is fixed-time for the fixed-length token.** `FixedTimeEquals` walks both byte arrays and folds differences (`OnlineEndpoint.cs:137-142`); callers always supply 32-byte values because codec copies `TokenLength` bytes (`WireCodec.cs:62-64`) and construction requires 32 bytes (`OnlineEndpoint.cs:31-35`). No direct timing-comparison defect found.

6. **Informational — protocol carries no filesystem path fields.** The request format contains only token, UTF-8 frame ID, dimensions and raw Mono8 pixels (`WireCodec.cs:21-25`, `:53-77`); responses are result/reason bytes (`:80-99`). No file-path ingestion/sink was found in this endpoint path.

## Verification

Commands run: read-only CodeGraph exploration and scoped `rg --files`; no build or test.

Known environment condition: `.git/index.lock` exists and is empty (0 bytes, observed 2026-10-18 13:56:46); it was not changed or retried.

## Residual scope

This is a source review only. Network behavior, IPv4-mapped connection behavior, resource exhaustion thresholds, and shutdown behavior remain runtime-unverified.
