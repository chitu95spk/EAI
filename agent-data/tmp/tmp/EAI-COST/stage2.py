import subprocess, re, sys, os

REPO = r"E:\Code\EasyVision_Unisen - NoService"
IDX = os.path.join(REPO, "tmp", "EAI-COST", "idx2")
env = dict(os.environ, GIT_INDEX_FILE=IDX)
W = "EasyLabel/src/EasyAI.Wpf/"
T = "EasyLabel/tests/EasyLabel.SegAI.Tests/"

def git(args, inp=None, e=env):
    r = subprocess.run(["git"] + args, cwd=REPO, input=inp, capture_output=True, env=e)
    if r.returncode != 0:
        raise SystemExit("git %s failed: %s" % (args, r.stderr.decode("utf-8", "replace")))
    return r.stdout

# spec: path -> None (all hunks) | list of 1-based hunk indices | dict overrides
SPEC = {
    W + "Services/Agent/AgentBatch.cs": None,
    W + "Services/Agent/AgentBatchRunner.cs": None,
    W + "Services/Agent/AgentCacheAndLog.cs": None,
    W + "Services/Agent/AgentCommandLine.cs": None,
    W + "Services/Agent/AgentResults.cs": None,
    W + "Services/Agent/AgentStreamParser.cs": None,
    W + "Services/AgentRunner.cs": None,   # with truncation below
    W + "ViewModels/LabelStepViewModel.cs": None,
    W + "Resources/Strings.Designer.cs": [1],
    W + "Resources/Strings.resx": [1],
    W + "Resources/Strings.vi.resx": [1],
    T + "AgentCacheLogTests.cs": None,
    T + "AgentRunnerProcessTests.cs": None,
    T + "EasyLabel.SegAI.Tests.csproj": [3],
}

def parse(diff):
    hunks = []
    cur = None
    for line in diff.split("\n"):
        m = re.match(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", line)
        if m:
            cur = {"os": int(m.group(1)), "oc": 1 if m.group(2) is None else int(m.group(2)), "old": [], "new": []}
            hunks.append(cur)
        elif cur is not None:
            if line.startswith("-"):
                cur["old"].append(line[1:])
            elif line.startswith("+"):
                cur["new"].append(line[1:])
            elif line.startswith("\\"):
                print("WARN no-newline marker")
    return hunks

total = {}
for path, sel in SPEC.items():
    diff = git(["diff", "-U0", "--", path]).decode("utf-8")
    hunks = parse(diff)
    head = git(["show", "HEAD:" + path]).decode("utf-8")
    lines = head.split("\n")  # LF; last element after final newline
    chosen = list(range(1, len(hunks) + 1)) if sel is None else sel
    for i in chosen:
        h = hunks[i - 1]
        if path.endswith("Services/AgentRunner.cs") and h["os"] == 374:
            k = h["new"].index("        return result;")
            h["new"] = h["new"][:k + 1]
            print("truncated suggest hunk to", len(h["new"]), "lines")
    for i in sorted(chosen, reverse=True):
        h = hunks[i - 1]
        if h["oc"] == 0:
            at = h["os"]  # insert after old line os (1-based) -> index os
            lines[at:at] = h["new"]
        else:
            s = h["os"] - 1
            assert lines[s:s + h["oc"]] == h["old"], (path, i)
            lines[s:s + h["oc"]] = h["new"]
    out = "\n".join(lines).encode("utf-8")
    sha = git(["hash-object", "-w", "--stdin"], inp=out).decode().strip()
    mode = "100644"
    git(["update-index", "--cacheinfo", "%s,%s,%s" % (mode, sha, path)])
    total[path] = (len(chosen), len(hunks))

for new in [W + "Services/Agent/AgentRunSpend.cs", T + "AgentRunnerBudgetTests.cs", T + "AgentBatchBudgetTests.cs"]:
    git(["add", "--", new])
for p, (c, n) in total.items():
    print("%s included %d / %d" % (p, c, n))
