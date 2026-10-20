import re
src = open(r'E:\Code\EasyVision_Unisen - NoService\.claude\task-contract.EAI-3.toml', encoding='utf-8').read()
s = src.replace('EAI-3', 'EAI-4')


def sub(pattern, new, flags=0):
    global s
    m = re.search(pattern, s, flags)
    assert m, pattern
    s = s[:m.start()] + new + s[m.end():]


sub(r'notes = ".*"', 'notes = "EasyAI milestone 4. Master plan: tmp/EAI/plan.md (r4). Plan revision tmp/EAI/EAI-4/plan-r1.md (DRAFT, not approved). Defaults if unanswered: Q1 only Train new, Q2 require >=1 Train and >=1 Val, Q3 ScottPlot.WPF 5 chart, Q4 compare embedded reports with a different-validation-set warning, Q5 ask to stop on close."')
sub(r'title = ".*"', 'title = "EasyAI Step 3 Train: train, registry candidates, candidate vs active comparison, Use this model"')
sub(r'goal = ".*"', 'goal = "Step 3 panel in EasyAI.Wpf backed by a training service over SegAiTrainingPipeline: start/stop with live loss/IoU chart, registry candidate list, candidate-vs-active comparison with warnings, activation through SetActiveModel."')
sub(r'current_behavior = ".*"', 'current_behavior = "Only Steps 1-2 exist; the training pipeline has no service wrapper or UI, there is no comparison type and no chart control."')
sub(r'expected_behavior = ".*"', 'expected_behavior = "Commit A: SegAiTrainingService, SegAiModelComparison, registry/run read helpers, tests. Commit B: Step 3 panel, chart, candidate list, comparison, Use this model, vi/en strings, UI smoke."')
sub(r'out_of_scope = \[.*\]', 'out_of_scope = ["Any EasyVision project or file (read-only reference)", "EasyLabel.App WinForms (unchanged)", "Advanced options drawer (EAI-8)", "Inspection list, camera, statistics (EAI-5..7)", "INT8 / export parity beyond the registry file"]')
sub(r'dependencies = \[.*\]', 'dependencies = ["EAI-3 closed (f03e0cd4c, 417ff55d8)", "tmp/EAI/plan.md r4", "tmp/EAI/EAI-4/plan-r1.md"]')
sub(r'normalized_request = ".*"', 'normalized_request = "Implement EAI-4 per plan-r1 after user approval: training service + comparison + read helpers, then Step 3 panel with chart; two commits."')
sub(r'build_reason = ".*"', 'build_reason = "Training service, comparison math and native training need unit and opt-in native tests; WPF panel, chart, DPI and language behaviour need a real app run."')
s = s.replace('estimated_tokens = 1000000', 'estimated_tokens = 1200000')
sub(r'behavior = \[.*?\n\]', '''behavior = [
  "Start is disabled with a typed reason unless >=1 Train and >=1 Val image exist; Stop cancels through the CancellationToken and leaves no half-registered candidate",
  "A finished run registers exactly one TRAINED candidate, never activates it; Use this model activates only through SetActiveModel",
  "Comparison shows deltas for mAP50, mAP50-95, pixel IoU and per-class AP50, flags DifferentValidationSet and BoxOverextension, and never hides a worse result",
  "While a run is active auto-label, accept and import are disabled with real CanExecute; every Step 3 control is backed by real code",
]''', re.S)
s = s.replace('existing 86 Core + 106 SegAI tests still pass', 'existing 86 Core + 142 SegAI tests still pass')
s = s.replace('SegAiResult does not expose enough to rebuild holes exactly (revise the plan, do not improvise).', 'ScottPlot.WPF 5 cannot be restored or does not meet the chart need (request a scope decision, no hand-drawn substitute).')
open(r'E:\Code\EasyVision_Unisen - NoService\.claude\task-contract.EAI-4.toml', 'w', encoding='utf-8').write(s)
print('ok')
