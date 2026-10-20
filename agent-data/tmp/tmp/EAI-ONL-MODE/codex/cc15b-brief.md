# CC-15b: Online status window (WPF) - Codex brief

Reply in English. You do NOT build, run tests or use git. Claude builds and reviews.

## Goal
Add a small, production-grade status window for Online mode in EasyLabel/src/EasyAI.Wpf, bound to the existing
`EasyAI.Desktop.Commands.OnlineControlAdapter` (already written, do not edit). It shows the live state of the Online runner
and lets the operator stop it and change thresholds while it runs.

## Allow-list (create ONLY these new files; edit nothing else)
- EasyLabel/src/EasyAI.Wpf/ViewModels/OnlineStatusViewModel.cs
- EasyLabel/src/EasyAI.Wpf/Views/OnlineStatusWindow.xaml
- EasyLabel/src/EasyAI.Wpf/Views/OnlineStatusWindow.xaml.cs
- EasyLabel/src/EasyAI.Wpf/Resources/OnlineStrings.resx  (English)
- EasyLabel/src/EasyAI.Wpf/Resources/OnlineStrings.vi.resx  (Vietnamese, full diacritics)
- EasyLabel/src/EasyAI.Wpf/Resources/OnlineStrings.cs  (hand-written static accessor; mirror how Resources/Strings.Designer.cs
  uses ResourceManager("EasyAI.Desktop.Resources.Strings") and `Strings.Culture`, but base name "EasyAI.Desktop.Resources.OnlineStrings";
  honour `Strings.Culture ?? CultureInfo.CurrentUICulture`)
Do NOT touch MainWindow*, Strings.*, App.xaml*, AppSettings, ShortcutTable, MainViewModel or any other file (many are dirty from other sessions).
Namespaces: EasyAI.Desktop.ViewModels, EasyAI.Desktop.Views, EasyAI.Desktop.Resources.

## Data contract
`OnlineControlAdapter` (EasyAI.Desktop.Commands) implements `EasyLabel.Commands.IOnlineControl`: `bool IsRunning`, `void Stop()`,
`string StatusJson()`, `string SetThreshold(string className, double score, double nms)` (returns null on success, else code
NOT_RUNNING / UNKNOWN_CLASS / INVALID_THRESHOLD). StatusJson shape (parse with System.Text.Json):
`{"running":bool,"recipeId":string,"accepted":long,"dropped":long,"completed":long,"failed":long,"totalP95Ms":number?}`
(`totalP95Ms` absent until the first frame completes; the idle adapter returns just `{"running":false}`).

## ViewModel (CommunityToolkit.Mvvm, same style as other VMs in ViewModels/)
`OnlineStatusViewModel(OnlineControlAdapter control, double budgetMs = 100)`; a DispatcherTimer polls every 500 ms (stop it on Dispose/unload).
Properties: IsRunning, RecipeId, Accepted, Dropped, Completed, Failed, TotalP95Ms, a `BudgetState` (Unknown when no p95, Ok when p95 <= budget,
Over otherwise), StatusText (localised), ScoreText + NmsText (editable strings), ThresholdMessage.
Commands: StopCommand (CanExecute = IsRunning; calls control.Stop()), ApplyThresholdCommand (CanExecute = IsRunning; parse invariant-culture
doubles; score and nms must be 0..1 else show a localised message and do not call; className null = all classes; show the returned
error code mapped to a localised message). No static state. Parsing logic must be a pure internal static method so it is unit-testable.

## View (real production controls only; match PromptWindow.xaml style: ui:FluentWindow, Mica, ui:TitleBar, DynamicResource brushes)
- Use existing custom/WPF-UI controls (ui:Button, ui:TextBox, ui:TextBlock, ui:Card); no stock CheckBox/ComboBox/NumericUpDown.
- Layout must adapt: MinWidth 360, SizeToContent=Height, content in a ScrollViewer (vertical auto, horizontal disabled), TextWrapping on all text,
  Grid with star columns, no fixed pixel widths on labels, nothing clipped at 100% and 150% DPI or with longer Vietnamese strings.
- Sections: status line with a coloured state indicator (use brush resources, not hard-coded colours); counters grid (Accepted, Completed,
  Dropped, Failed); p95 vs budget line; thresholds card (Score, NMS, Apply); Stop button (bottom, right aligned, wraps if narrow).
- Set AutomationProperties.AutomationId on every interactive control (OnlineStatusWindow, OnlineStopButton, OnlineApplyButton,
  OnlineScoreBox, OnlineNmsBox, OnlineStatusText) so UIA smoke can drive it.
- Code-behind: only InitializeComponent, DataContext wiring from a constructor taking the VM, and timer stop on Closed.

## Localisation
Every user-visible string comes from OnlineStrings (en + vi). No help/tip text, only labels and status messages.

## Deliverable
When done reply with: files created, public members of the VM, any assumption. Do not claim it compiles.
