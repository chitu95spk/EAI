import subprocess, re, os
REPO = r"E:\Code\EasyVision_Unisen - NoService"
IDX = os.path.join(REPO, "tmp", "EAI-COST", "idx3")
env = dict(os.environ, GIT_INDEX_FILE=IDX, TEMP=os.path.join(REPO,"tmp","EAI-COST"), TMP=os.path.join(REPO,"tmp","EAI-COST"))
W = "EasyLabel/src/EasyAI.Wpf/"; C = "EasyLabel/src/EasyLabel.Core/Projects/"
T = "EasyLabel/tests/EasyLabel.SegAI.Tests/"
def git(args, inp=None):
    r = subprocess.run(["git"] + args, cwd=REPO, input=inp, capture_output=True, env=env)
    if r.returncode != 0:
        raise SystemExit("git %s failed: %s" % (args, r.stderr.decode("utf-8", "replace")))
    return r.stdout
def parse(diff):
    hunks = []; cur = None
    for line in diff.split("\n"):
        m = re.match(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", line)
        if m:
            cur = {"os": int(m.group(1)), "oc": 1 if m.group(2) is None else int(m.group(2)), "old": [], "new": []}
            hunks.append(cur)
        elif cur is not None:
            if line.startswith("-"): cur["old"].append(line[1:].rstrip("\r"))
            elif line.startswith("+"): cur["new"].append(line[1:].rstrip("\r"))
    return hunks
def drop_group(new, marker, n):
    i = next(k for k, l in enumerate(new) if marker in l)
    del new[i:i+n]
def tx_agents_vm(i, h):
    if i == 12: h["new"] = [h["new"][-1]]
    if i == 15:
        h["new"] = [l for l in h["new"] if not l.strip().startswith(("Prices = new", "PromptSubAgent = new", "Prices.RefreshOfflineState"))]
    if i == 16:
        drop_group(h["new"], 'The "Refresh price" block', 3)
        drop_group(h["new"], 'The prompt sub-agent block', 3)
def tx_appsettings(i, h):
    h["new"] = h["new"][3:6]  # offline doc + prop + blank
    assert "AgentOfflineOnly" in h["new"][1], h["new"]
def tx_budget(i, h):
    h["new"] = [l for l in h["new"] if "RefinePromptAsync" not in l]
def tx_csproj(i, h):
    h["new"] = [l for l in h["new"] if "AgentCliCapabilities" in l]
    h["oc"] = 0; h["old"] = []; h["os"] = 49 + 1  # placeholder, fixed below
SPEC = {
 W+"Infrastructure/AppSettings.cs": ([1], tx_appsettings),
 W+"Infrastructure/SettingsAccessor.cs": (None, None),
 W+"Infrastructure/SettingsModel.cs": (None, None),
 W+"Services/Agent/AgentPrices.cs": (None, None),
 W+"ViewModels/SettingsViewModel.cs": (None, None),
 W+"ViewModels/AgentsSettingsViewModel.cs": (list(range(1,19)), tx_agents_vm),
 W+"Views/AgentsSettingsView.xaml": ([1,3], None),
 W+"ViewModels/LabelStepViewModel.cs": ([i for i in range(1,24) if i != 10], None),
 W+"ViewModels/LabelStepViewModel.AgentChat.cs": ([3,6], None),
 W+"Resources/Strings.Designer.cs": ([1,3], None),
 W+"Resources/Strings.resx": ([1,5], None),
 W+"Resources/Strings.vi.resx": ([1,5], None),
 C+"ProjectManifest.cs": (None, None),
 C+"ProjectManifestStore.cs": (None, None),
 T+"AgentRunnerBudgetTests.cs": (None, tx_budget),
 T+"EditorShellRulesTests.cs": (None, None),
}
for path, (sel, tx) in SPEC.items():
    hunks = parse(git(["diff", "-U0", "--", path]).decode("utf-8"))
    lines = git(["show", "HEAD:" + path]).decode("utf-8").split("\n")
    chosen = list(range(1, len(hunks)+1)) if sel is None else sel
    if tx is not None:
        for i in chosen:
            if tx in (tx_agents_vm,): tx(i, hunks[i-1])
            elif tx is tx_appsettings: tx(i, hunks[i-1])
            elif tx is tx_budget: tx(i, hunks[i-1])
    for i in sorted(chosen, reverse=True):
        h = hunks[i-1]
        if h["oc"] == 0: lines[h["os"]:h["os"]] = h["new"]
        else:
            s = h["os"]-1
            assert lines[s:s+h["oc"]] == h["old"], (path, i, lines[s:s+h["oc"]], h["old"])
            lines[s:s+h["oc"]] = h["new"]
    sha = git(["hash-object", "-w", "--stdin"], inp="\n".join(lines).encode("utf-8")).decode().strip()
    git(["update-index", "--cacheinfo", "100644,%s,%s" % (sha, path)])
    print(path, "included", len(chosen), "/", len(hunks))
# csproj: insert CliCapabilities after HEAD's AgentPrices line
p = T+"EasyLabel.SegAI.Tests.csproj"
lines = git(["show", "HEAD:"+p]).decode("utf-8").split("\n")
k = next(i for i,l in enumerate(lines) if "Services\Agent\AgentPrices.cs" in l)
lines.insert(k+1, '    <Compile Include="..\..\src\EasyAI.Wpf\Services\Agent\AgentCliCapabilities.cs" Link="Linked\AgentCliCapabilities.cs" />')
sha = git(["hash-object","-w","--stdin"], inp="\n".join(lines).encode()).decode().strip()
git(["update-index","--cacheinfo","100644,%s,%s"%(sha,p)])
for n in [C+"ProjectAgentConsent.cs", "EasyLabel/tests/EasyLabel.Core.Tests/ProjectAgentConsentTests.cs", W+"Services/Agent/AgentConsentGate.cs", W+"Services/Agent/AgentCliCapabilities.cs", T+"AgentOfflineAndCapabilityTests.cs"]:
    git(["add","--",n])
