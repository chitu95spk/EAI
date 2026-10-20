using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Core.Editing;

namespace EasyAI.Desktop.Views;

public partial class CanvasView : UserControl
{
    public CanvasView()
    {
        InitializeComponent();
    }

    // A tool the project kind refuses is not selected (the key is still consumed, so it does not reach a text field).
    private static bool PickTool(CanvasViewModel model, CanvasTool tool)
    {
        if (ProjectKindToolRules.IsToolAllowed(model.Kind, tool))
        {
            model.Tool = tool;
        }

        return true;
    }

    /// <summary>
    /// Canvas hotkeys of <see cref="ShortcutTable"/> (called from the window's PreviewKeyDown after the window-level ones). Returns true
    /// when the key was consumed. Never consumes keys while a text box, combo box or slider has the focus.
    /// </summary>
    public bool HandleKey(KeyEventArgs e)
    {
        CanvasViewModel? model = DataContext as CanvasViewModel;
        if (model == null || model.InputBlocked || ShortcutInput.BlocksShortcuts(Keyboard.FocusedElement))
        {
            return false;
        }

        ShortcutEntry? entry = ShortcutInput.Match(e);
        if (entry == null)
        {
            return HandleClassHotkey(model, e);
        }

        // Lists keep Ctrl+arrows and the plain editing keys (Delete, Enter, Escape, Backspace, T) while they have the focus.
        bool listFocused = Keyboard.FocusedElement is Selector;
        CanvasEditSession? session = model.Session;
        switch (entry.Id)
        {
            case ShortcutId.ToolPolygon: return PickTool(model, CanvasTool.Polygon);
            case ShortcutId.ToolRect: return PickTool(model, CanvasTool.Rect);
            case ShortcutId.ToolRot: return PickTool(model, CanvasTool.Rot);
            case ShortcutId.ToolCircle: return PickTool(model, CanvasTool.Circle);
            case ShortcutId.ToolPoint: return PickTool(model, CanvasTool.Point);
            case ShortcutId.ToolLine: return PickTool(model, CanvasTool.Line);
            case ShortcutId.ToolLineStrip: return PickTool(model, CanvasTool.LineStrip);
            case ShortcutId.ToolHand: return PickTool(model, CanvasTool.Hand);
            case ShortcutId.Undo:
                // Labelme: Ctrl+Z while drawing removes the last point.
                return session != null && session.HasDraft ? Run(model.UndoLastPointCommand) : Run(model.UndoCommand);
            case ShortcutId.Copy: return Run(model.CopyCommand);
            case ShortcutId.Paste: return Run(model.PasteCommand);
            case ShortcutId.Duplicate: return Run(model.DuplicateCommand);
            case ShortcutId.EditLabel: return Run(model.EditLabelCommand);
            case ShortcutId.DeleteVertex:
                // Labelme "Delete selected vertex" is Meta+H (the Windows key; Windows itself may claim Win+H).
                return Run(model.DeleteVertexCommand);
            case ShortcutId.SelectUp: return !listFocused && Run(model.SelectNeighbourCommand, SelectDirection.Up);
            case ShortcutId.SelectDown: return !listFocused && Run(model.SelectNeighbourCommand, SelectDirection.Down);
            case ShortcutId.SelectLeft: return !listFocused && Run(model.SelectNeighbourCommand, SelectDirection.Left);
            case ShortcutId.SelectRight: return !listFocused && Run(model.SelectNeighbourCommand, SelectDirection.Right);
            case ShortcutId.KeepPrevious:
                model.KeepPreviousAnnotation = !model.KeepPreviousAnnotation;
                return true;
            case ShortcutId.ZoomIn: return Run(model.ZoomInCommand);
            case ShortcutId.ZoomOut: return Run(model.ZoomOutCommand);
            case ShortcutId.ActualSize: return Run(model.ActualSizeCommand);
            case ShortcutId.Fit: return Run(model.FitCommand);
            case ShortcutId.FitSpace:
                // Space keeps its own job on a focused button, check box or toggle (it presses it).
                return !(Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase) && Run(model.FitCommand);
            case ShortcutId.FitWidth: return Run(model.FitWidthCommand);
            case ShortcutId.ToggleShapes: return !listFocused && Run(model.ToggleShapesCommand);
            case ShortcutId.DeleteShape:
                if (listFocused) return false;
                if (model.HasSelectedSuggestions && !model.HasSelection)
                {
                    model.RejectSuggestions(true);
                    return true;
                }

                return Run(model.DeleteCommand);
            case ShortcutId.CancelDraft:
                if (listFocused) return false;
                // Esc drops the AI prompt (clicks / box) and cancels an AI run in flight before anything else.
                if (model.AiCancelPrompt()) return true;
                if (model.HasSelectedSuggestions && (session == null || (!session.IsDragging && !session.HasDraft)))
                {
                    model.ClearSuggestionSelection();
                    return true;
                }

                if (session == null || (!session.IsDragging && !session.HasDraft && session.SelectedIndices.Count == 0)) return false;
                model.Cancel();
                return true;
            case ShortcutId.FinishDraft:
                if (listFocused) return false;
                // Enter with the AI-Points tool: the clicks are final, the next click starts a new object.
                if (model.AiCommitPrompt()) return true;
                if (model.HasSelectedSuggestions && (session == null || !session.HasDraft))
                {
                    return model.AcceptSuggestions(true) > 0;
                }

                if (session == null || !session.HasDraft) return false;
                model.Finish();
                return true;
            case ShortcutId.RemoveDraftVertex:
                if (listFocused) return false;
                if (session == null || !session.HasDraft) return false;
                model.RemoveDraftVertex();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Moves keyboard focus to the annotation surface so Space/hotkeys are not captured by a just-clicked button.</summary>
    public void FocusSurface()
    {
        if (Surface.Focusable)
        {
            Keyboard.Focus(Surface);
        }
    }

    /// <summary>Repaints the surface (the colour theme changed: the area around the image uses the palette's surround colour).</summary>
    public void Redraw()
    {
        Surface.InvalidateVisual();
    }

    /// <summary>
    /// Digit keys 1-9 pick the class at that table position in a project with more than two classes. Plain digits only (no Ctrl,
    /// Alt, Shift or Windows key), not while a list has the focus, and a disabled class is not selected.
    /// </summary>
    private static bool HandleClassHotkey(CanvasViewModel model, KeyEventArgs e)
    {
        if (ShortcutInput.CurrentModifiers() != ShortcutModifiers.None || Keyboard.FocusedElement is Selector)
        {
            return false;
        }

        int index = ClassTableRules.HotkeyIndex(e.Key.ToString(), model.ClassCount);
        if (index < 0 || !ClassTableRules.IsSelectable(model.ClassEnabled, index))
        {
            return false;
        }

        model.SetActiveClass(index);
        return true;
    }

    private static bool Run(ICommand command, object? parameter = null)
    {
        if (!command.CanExecute(parameter))
        {
            return false;
        }

        command.Execute(parameter);
        return true;
    }
}
