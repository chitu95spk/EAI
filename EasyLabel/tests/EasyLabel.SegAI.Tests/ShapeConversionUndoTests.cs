using EasyLabel.Core.Commands;
using EasyLabel.Core.Editing;
using EasyLabel.Core.Model;
using Newtonsoft.Json;

namespace EasyLabel.SegAI.Tests;

public sealed class ShapeConversionUndoTests
{
    private static LabelShape Rect(double x) => new LabelShape
    {
        Label = "a",
        ShapeType = "rectangle",
        Points = new List<double[]> { new[] { x, 10.0 }, new[] { x + 40.0, 50.0 } }
    };

    private static (LabelMeDocument doc, DocumentEditor editor, CanvasEditSession session) Create()
    {
        var doc = new LabelMeDocument();
        var sidecar = new EasyLabelSidecar();
        doc.Shapes.Add(Rect(0));
        doc.Shapes.Add(Rect(100));
        doc.Shapes.Add(new LabelShape { Label = "p", ShapeType = "point", Points = new List<double[]> { new[] { 5.0, 5.0 } } });
        for (int i = 0; i < 3; i++) sidecar.Shapes.Add(new ShapeProvenance());
        var editor = new DocumentEditor(doc, sidecar);
        var session = new CanvasEditSession(editor, doc, 400, 300);
        session.SelectAll();
        return (doc, editor, session);
    }

    private static string State(LabelMeDocument doc) => JsonConvert.SerializeObject(doc.Shapes);

    [Fact]
    public void Converting_the_selection_is_one_undo_step_and_redo_reapplies()
    {
        var (doc, editor, session) = Create();
        string before = State(doc);

        ShapeConversionPlan plan = session.PlanConversion("polygon");
        Assert.Equal(2, plan.Replacements.Count);
        Assert.Equal(1, plan.Skipped); // the point is not convertible
        Assert.True(session.ApplyConversion(plan));
        Assert.Equal("polygon", doc.Shapes[0].ShapeType);
        Assert.Equal("polygon", doc.Shapes[1].ShapeType);
        Assert.Equal("point", doc.Shapes[2].ShapeType);
        Assert.Equal(3, session.SelectedIndices.Count);
        string after = State(doc);

        Assert.True(editor.History.Undo());
        Assert.Equal(before, State(doc));
        Assert.False(editor.History.Undo());
        Assert.True(editor.History.Redo());
        Assert.Equal(after, State(doc));
    }

    [Fact]
    public void Nothing_convertible_records_no_history()
    {
        var (_, editor, session) = Create();
        session.ClearSelection();
        session.Select(2, false);
        Assert.False(session.CanConvertSelection("polygon"));
        ShapeConversionPlan plan = session.PlanConversion("polygon");
        Assert.Empty(plan.Replacements);
        Assert.False(session.ApplyConversion(plan));
        Assert.False(editor.History.CanUndo);
    }
}
