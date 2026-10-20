using System.Collections.Generic;
using EasyLabel.Core.Commands;
using EasyLabel.Core.Model;
using Newtonsoft.Json;
using Xunit;

namespace EasyLabel.Core.Tests
{
    public sealed class DocumentEditorBatchTests
    {
        private static LabelShape Rect(string label, double x)
        {
            return new LabelShape
            {
                Label = label,
                ShapeType = "rectangle",
                Points = new List<double[]> { new[] { x, 10.0 }, new[] { x + 10.0, 20.0 } }
            };
        }

        private static (LabelMeDocument doc, EasyLabelSidecar sidecar, DocumentEditor editor) Create(int count)
        {
            var doc = new LabelMeDocument();
            var sidecar = new EasyLabelSidecar();
            for (int i = 0; i < count; i++)
            {
                doc.Shapes.Add(Rect("s" + i, i * 20));
                sidecar.Shapes.Add(new ShapeProvenance());
            }
            return (doc, sidecar, new DocumentEditor(doc, sidecar));
        }

        private static string State(LabelMeDocument doc, EasyLabelSidecar sidecar)
        {
            return JsonConvert.SerializeObject(doc.Shapes) + JsonConvert.SerializeObject(sidecar.Shapes);
        }

        [Fact]
        public void DeleteManyIsOneUndoUnit()
        {
            var (doc, sidecar, editor) = Create(5);
            string before = State(doc, sidecar);
            editor.DeleteMany(new[] { 1, 3, 3 });
            Assert.Equal(new[] { "s0", "s2", "s4" }, doc.Shapes.ConvertAll(s => s.Label));
            Assert.Equal(3, sidecar.Shapes.Count);
            string after = State(doc, sidecar);

            Assert.True(editor.History.Undo());
            Assert.Equal(before, State(doc, sidecar));
            Assert.False(editor.History.Undo());
            Assert.True(editor.History.Redo());
            Assert.Equal(after, State(doc, sidecar));
        }

        [Fact]
        public void ReplaceManyIsOneUndoUnit()
        {
            var (doc, sidecar, editor) = Create(3);
            string before = State(doc, sidecar);
            var replacements = new Dictionary<int, LabelShape> { { 0, Rect("a", 100) }, { 2, Rect("c", 150) } };
            editor.ReplaceMany(replacements, "Move");
            Assert.Equal(new[] { "a", "s1", "c" }, doc.Shapes.ConvertAll(s => s.Label));
            string after = State(doc, sidecar);

            Assert.True(editor.History.Undo());
            Assert.Equal(before, State(doc, sidecar));
            Assert.False(editor.History.Undo());
            Assert.True(editor.History.Redo());
            Assert.Equal(after, State(doc, sidecar));
        }

        [Fact]
        public void NoOpBatchRecordsNothing()
        {
            var (doc, _, editor) = Create(2);
            editor.DeleteMany(new int[0]);
            editor.ReplaceMany(new Dictionary<int, LabelShape> { { 1, doc.Shapes[1] } }, "same");
            Assert.False(editor.History.CanUndo);
        }

        [Fact]
        public void OutOfRangeIndexThrowsAndLeavesDocumentUntouched()
        {
            var (doc, sidecar, editor) = Create(2);
            string before = State(doc, sidecar);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => editor.DeleteMany(new[] { 0, 5 }));
            Assert.Equal(before, State(doc, sidecar));
            Assert.False(editor.History.CanUndo);
        }
    }
}
