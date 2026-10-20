import re
root = r'E:\Code\EasyVision_Unisen - NoService'
s = open(root + r'\.claude\task-contract.EAI-4.toml', encoding='utf-8').read().replace('EAI-4', 'EAI-UI')


def sub(pattern, new, flags=0):
    global s
    m = re.search(pattern, s, flags)
    assert m, pattern
    s = s[:m.start()] + new + s[m.end():]


sub(r'notes = ".*"', 'notes = "EasyAI main-window re-layout like Labelme (menu bar, toolbar, step bar, tool strip, right panels Labels/Shapes/Files). Plan tmp/EAI/EAI-UI/plan-r1.md (DRAFT, not approved). Runs before EAI-5. Defaults if unanswered: Q1 files checkbox read-only annotated indicator, Q2 no Flags panel, Q3 Engines card moves to About + status bar, Q4 Edit label toggles NG/OK."')
sub(r'title = ".*"', 'title = "EasyAI Labelme-style main window: menus, toolbar, step bar, tool strip, Labels/Shapes/Files panels"')
sub(r'goal = ".*"', 'goal = "Re-layout EasyAI.Wpf like Labelme using only real commands: menu bar, toolbar, step bar, vertical tool strip, right-hand Labels/Shapes/Files panels, context menu, View-menu panel toggles and persisted layout."')
sub(r'current_behavior = ".*"', 'current_behavior = "Three fixed columns (image list, canvas with wrap toolbar, step panel); no menus, no shapes list, no context menu."')
sub(r'expected_behavior = ".*"', 'expected_behavior = "Commit A: menu bar, toolbar, step bar, tool strip, start screen, About, layout persistence. Commit B: Labels/Shapes/Files panels, view-only shape visibility, context menu, Edit label, View toggles. Existing smoke flows still pass unchanged."')
sub(r'out_of_scope = \[.*\]', 'out_of_scope = ["Any EasyVision project or file (read-only reference)", "EasyLabel.App WinForms (unchanged)", "Docking/floating panels (AvalonDock = new NuGet, not approved)", "New drawing tools, flags, save-model changes", "EAI-5 inspection list"]')
sub(r'dependencies = \[.*\]', 'dependencies = ["EAI-4 closed (ab206ffea, 1a1109299)", "tmp/EAI/plan.md r4", "tmp/EAI/EAI-UI/plan-r1.md"]')
sub(r'normalized_request = ".*"', 'normalized_request = "Implement EAI-UI per plan-r1 after user approval: shell (commit A) then right-hand panels (commit B)."')
sub(r'build_reason = ".*"', 'build_reason = "Large XAML restructuring; AutomationId compatibility, hotkeys, DPI and language behaviour need a real app run."')
s = s.replace('estimated_tokens = 1200000', 'estimated_tokens = 900000')
sub(r'behavior = \[.*?\n\]', '''behavior = [
  "Every menu item, toolbar button and context-menu entry is backed by a real command; nothing is shown that cannot act (no Save, Flags, extra tools)",
  "Shape visibility is view-only: never saved, never in the undo stack, hidden shapes are not drawn or hit-tested",
  "Existing smoke3/smoke4 flows run unchanged (AutomationIds preserved) and pass; layout persists, View toggles hide/show panels, Reset layout restores the default",
  "Files list shows a read-only annotated indicator and badges as plain text; Labels list shows NG/OK counts for the current image",
]''', re.S)
s = s.replace('existing 86 Core + 142 SegAI tests still pass', 'existing 86 Core + 187 SegAI tests still pass')
s = s.replace('comparison unit-test log, real tiny-run duration', 'smoke3/smoke4 re-run logs, layout smoke screenshots')
s = s.replace('ScottPlot.WPF 5 cannot be restored or does not meet the chart need (request a scope decision, no hand-drawn substitute).', 'A WPF-UI control cannot provide a menu/panel behaviour the plan needs (stop and report; no stock substitute, no docking library).')
# reset approval and authorization
s = s.replace('plan_approved = true', 'plan_approved = false').replace('plan_approved_by = "user"', 'plan_approved_by = ""')
s = re.sub(r'approved_plan_revision = ".*"', 'approved_plan_revision = ""', s)
s = re.sub(r'approved_at_utc = ".*"', 'approved_at_utc = ""', s)
s = s.replace('granted_by = "user"', 'granted_by = ""')
s = re.sub(r'granted_at_utc = ".*"', 'granted_at_utc = ""', s)
for k in ['edit_code', 'build', 'run_tests', 'commit']:
    s = re.sub(r'^%s = true' % k, '%s = false' % k, s, flags=re.M)
open(root + r'\.claude\task-contract.EAI-UI.toml', 'w', encoding='utf-8').write(s)
print('ok')
