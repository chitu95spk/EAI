import sys
p = sys.argv[1]
par = open(sys.argv[2], encoding='utf-8').read().replace('\r\n', '\n')
raw = open(p, 'rb').read()
bom = raw.startswith(b'\xef\xbb\xbf')
crlf = b'\r\n' in raw
s = raw.decode('utf-8-sig').replace('\r\n', '\n')

def rep(old, new):
    global s
    assert old in s, old
    s = s.replace(old, new, 1)

rep("/// Runs the agent over a list of images one after the other (never in parallel). The agent call",
    "/// Runs the agent over a list of images, one after the other (one run delegate) or with several runs at the same time (one delegate per worker,\n/// each worker its own <c>AgentRunner</c>: a runner holds a single live process). The agent call")
rep("delegates <c>loadInput</c>, <c>apply</c> and <c>report</c> are called on the caller's context (the UI thread when started from it).",
    "delegates <c>loadInput</c>, <c>apply</c> and <c>report</c> are always called on the caller's context (the UI thread when started from it), also\n/// with several workers: only the agent calls overlap. With several workers the experience block is not built (it comes from the previous image).")
rep("    private readonly Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>> _run;\n",
    "    /// <summary>Cost reserved for an image in flight before any image gave a real cost, so a cost cap is not overshot by the whole worker pool.</summary>\n    private const double FirstReserveUsd = 0.10;\n\n    private readonly IReadOnlyList<Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>>> _runs;\n")
rep("        _run = (input, ct, progress) => run(input, ct);\n    }",
    "        _runs = new[] { (Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>>)((input, ct, progress) => run(input, ct)) };\n    }")
rep("        _run = run;\n    }\n",
    "        _runs = new[] { run };\n    }\n\n    /// <param name=\"workers\">One agent run per worker (1..n); with more than one, images are labelled at the same time and each worker has its own runner.</param>\n    public AgentBatchRunner(IReadOnlyList<Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>>> workers)\n    {\n        if (workers == null || workers.Count == 0)\n        {\n            throw new ArgumentException(\"At least one worker is required.\", nameof(workers));\n        }\n\n        _runs = workers;\n    }\n")
rep("        IProgress<AgentStreamEvent>? liveProgress = null)\n    {\n        AgentBatchProgress progress",
    "        IProgress<AgentStreamEvent>? liveProgress = null)\n    {\n        if (_runs.Count > 1)\n        {\n            return await RunParallelAsync(rels, caps, loadInput, apply, report, ct, liveProgress);\n        }\n\n        AgentBatchProgress progress")
rep("result = await _run(input, ct, liveProgress);", "result = await _runs[0](input, ct, liveProgress);")
i = s.rstrip().rfind('}')
s = s[:i].rstrip('\n') + '\n' + par
if crlf:
    s = s.replace('\n', '\r\n')
open(p, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))
