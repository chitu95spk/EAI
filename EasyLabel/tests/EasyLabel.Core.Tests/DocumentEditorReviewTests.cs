using System;
using System.Collections.Generic;
using EasyLabel.Core.Commands;
using EasyLabel.Core.Model;
using Newtonsoft.Json;
using Xunit;

namespace EasyLabel.Core.Tests
{
    public sealed class DocumentEditorReviewTests
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

        private static ShapeProvenance Model()
        {
            return new ShapeProvenance { Source = "model", Model = "Agent | test" };
        }

        [Fact]
        public void MixedReviewIsOneUndoUnitAndRestoresOriginalIndexes()
        {
            var (doc, sidecar, editor) = Create(5);
            string before = State(doc, sidecar);

            editor.ApplyReview(
                new[] { Rect("n", 300) },
                new[] { Model() },
                new Dictionary<int, LabelShape> { { 1, Rect("r1", 100) } },
                new Dictionary<int, ShapeProvenance> { { 1, Model() } },
                new[] { 3, 0 },
                "Accept review");

            // Indexes are those of the document before the call: 0 and 3 are gone, 1 is replaced, the add is last.
            Assert.Equal(new[] { "r1", "s2", "s4", "n" }, doc.Shapes.ConvertAll(s => s.Label));
            Assert.Equal(4, sidecar.Shapes.Count);
            Assert.Equal("model", sidecar.Shapes[0].Source);
            Assert.Equal("manual", sidecar.Shapes[1].Source);
            Assert.Equal("model", sidecar.Shapes[3].Source);
            string after = State(doc, sidecar);

            Assert.True(editor.History.Undo());
            Assert.Equal(before, State(doc, sidecar));
            Assert.Equal(new[] { "s0", "s1", "s2", "s3", "s4" }, doc.Shapes.ConvertAll(s => s.Label));
            Assert.False(editor.History.Undo());

            Assert.True(editor.History.Redo());
            Assert.Equal(after, State(doc, sidecar));
            Assert.False(editor.History.Redo());
        }

        [Fact]
        public void DeletionsOnlyRestoreAtOriginalIndexes()
        {
            var (doc, sidecar, editor) = Create(4);
            string before = State(doc, sidecar);

            editor.ApplyReview(null, null, null, null, new[] { 1, 2 }, "Accept review");

            Assert.Equal(new[] { "s0", "s3" }, doc.Shapes.ConvertAll(s => s.Label));
            Assert.True(editor.History.Undo());
            Assert.Equal(before, State(doc, sidecar));
        }

        [Fact]
        public void ReplacementKeepsProvenanceWhenNoneIsGiven()
        {
            var (doc, sidecar, editor) = Create(2);
            sidecar.Shapes[1] = new ShapeProvenance { Source = "manual", Model = "keep" };

            editor.ApplyReview(null, null, new Dictionary<int, LabelShape> { { 1, Rect("r", 50) } }, null, null, "Accept review");

            Assert.Equal("r", doc.Shapes[1].Label);
            Assert.Equal("keep", sidecar.Shapes[1].Model);
        }

        [Fact]
        public void SameIndexReplacedAndDeletedIsRejectedWithoutChange()
        {
            var (doc, sidecar, editor) = Create(3);
            string before = State(doc, sidecar);

            Assert.Throws<ArgumentException>(() => editor.ApplyReview(
                null, null, new Dictionary<int, LabelShape> { { 1, Rect("r", 50) } }, null, new[] { 1 }, "Accept review"));

            Assert.Equal(before, State(doc, sidecar));
            Assert.False(editor.History.CanUndo);
        }

        [Fact]
        public void OutOfRangeTargetThrowsAndLeavesDocumentAndHistoryUntouched()
        {
            var (doc, sidecar, editor) = Create(3);
            string before = State(doc, sidecar);

            Assert.Throws<ArgumentOutOfRangeException>(() => editor.ApplyReview(
                new[] { Rect("n", 300) }, new[] { Model() }, null, null, new[] { 7 }, "Accept review"));

            Assert.Equal(before, State(doc, sidecar));
            Assert.False(editor.History.CanUndo);
        }

        [Fact]
        public void MisalignedAddProvenanceThrows()
        {
            var (_, _, editor) = Create(1);
            Assert.Throws<ArgumentException>(() => editor.ApplyReview(
                new[] { Rect("n", 300) }, new ShapeProvenance[0], null, null, null, "Accept review"));
        }

        [Fact]
        public void EmptyReviewDoesNotCreateAnUndoStep()
        {
            var (_, _, editor) = Create(2);
            editor.ApplyReview(null, null, null, null, null, "Accept review");
            Assert.False(editor.History.CanUndo);
        }
    }
}
