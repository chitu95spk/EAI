# T3 — OnlineStatusWindow UI rule 0.2a review (read-only)

Reviewed: 2026-10-18. No source changes, build, or runtime UI smoke were performed.

## Findings

| Severity | Evidence | Finding |
| --- | --- | --- |
| Unverified layout risk | `EasyLabel/src/EasyAI.Wpf/Views/OnlineStatusWindow.xaml:55-84,100-141` | The two equal-width columns have no narrow-width fallback. At the required 360 DIP width, longer Vietnamese labels/messages wrap inside each half-width column. Vertical scrolling and wrapping provide containment; a rendered 360-DIP Vietnamese smoke is required before claiming poor readability, clipping, or overlap as a defect. |
| Low UIA lookup gap | `EasyLabel/src/EasyAI.Wpf/Views/OnlineStatusWindow.xaml:37-54,64-99,100-142` | The window, status text, two input boxes, and two buttons expose AutomationIds. The running indicator, RecipeId, Accepted/Completed/Dropped/Failed values, P95BudgetText and ThresholdMessage do not expose stable IDs for direct UIA lookup. Missing IDs on static labels or card containers alone are not treated as defects. |

## Pass evidence

- The window is the real production `ui:FluentWindow` path (`OnlineStatusWindow.xaml:1`), uses WPF-UI controls (`:24,49,100,116,132,147`), and contains none of the prohibited stock `CheckBox`, `ComboBox`, or `NumericUpDown` controls.
- Colour is supplied through dynamic theme resources (`:40,43,53,88,91,94`); no hard-coded colour brush appears.
- `MinWidth=360`, `SizeToContent=Height`, work-area constrained maximum height, text wrapping, and a vertical `ScrollViewer` (`:8-11,28-31,49-54,65-85,112-140`) provide an adaptive layout path. Runtime rendering remains unverified.

Post-review evidence: T2 current 20261018T072500Z captures were manually reviewed at 360 DIP/native96 DPI. No clipping or overlap was observed in en/vi idle/running, including the long Vietnamese recipe. Native144 DPI and OS UIA remain unverified; source risk is not a demonstrated clipping defect.
