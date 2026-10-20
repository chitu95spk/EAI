using System;
using System.Collections.Generic;
using EasyLabel.Core.Commands;
using EasyLabel.Core.Model;
using Newtonsoft.Json;
using Xunit;

namespace EasyLabel.Core.Tests
{
    public sealed class DocumentEditorTests
    {
        [Fact]
        public void TwentyMixedOperationsUndoAndRedoWithProvenance()
        {
            LabelMeDocument document = new LabelMeDocument();
            EasyLabelSidecar sidecar = new EasyLabelSidecar();
            DocumentEditor editor = new DocumentEditor(document, sidecar);
            List<string> states = new List<string> { State(document, sidecar) };
            for (int index = 0; index < 20; index++)
            {
                switch (index % 5)
                {
                    case 0:
                        editor.Add(new[] { Rectangle("manual") }, new[] { new ShapeProvenance() }, "Draw");
                        break;
                    case 1:
                        LabelShape moved = DocumentEditor.Clone(document.Shapes[0]);
                        moved.Points[0][0] += 5;
                        editor.Replace(0, moved, "Move or resize");
                        break;
                    case 2:
                        editor.ChangeClass(0, "class" + index);
                        break;
                    case 3:
                        editor.Add(new[] { Rectangle("detected"), Rectangle("detected2") },
                            new[] { new ShapeProvenance { Source = "model", Model = "test.pt", Confidence = .9 },
                                new ShapeProvenance { Source = "model", Model = "test.pt", Confidence = .8 } }, "Apply detections");
                        break;
                    case 4:
                        string nextProvenance = JsonConvert.SerializeObject(sidecar.Shapes[1]);
                        editor.Delete(0);
                        Assert.Equal(nextProvenance, JsonConvert.SerializeObject(sidecar.Shapes[0]));
                        break;
                }
                states.Add(State(document, sidecar));
            }
            for (int index = 19; index >= 0; index--)
            {
                Assert.True(editor.History.Undo());
                Assert.Equal(states[index], State(document, sidecar));
            }
            Assert.False(editor.History.IsDirty);
            Assert.False(editor.History.Undo());
            for (int index = 1; index <= 20; index++)
            {
                Assert.True(editor.History.Redo());
                Assert.Equal(states[index], State(document, sidecar));
            }
            Assert.False(editor.History.Redo());
        }

        [Fact]
        public void BranchAfterUndoDoesNotReuseSavedCheckpointOrInputReferences()
        {
            LabelMeDocument document = new LabelMeDocument();
            EasyLabelSidecar sidecar = new EasyLabelSidecar();
            DocumentEditor editor = new DocumentEditor(document, sidecar);
            LabelShape shape = Rectangle("original");
            editor.Add(new[] { shape }, new[] { new ShapeProvenance() }, "Draw");
            shape.Points[0][0] = 999;
            Assert.Equal(1, document.Shapes[0].Points[0][0]);
            editor.History.MarkSaved();
            editor.History.Undo();
            editor.Add(new[] { Rectangle("replacement") }, new[] { new ShapeProvenance() }, "Draw");
            Assert.True(editor.History.IsDirty);
            Assert.False(editor.History.CanRedo);
            editor.History.Undo();
            editor.History.Redo();
            Assert.Equal("replacement", document.Shapes[0].Label);
        }

        [Fact]
        public void NoOpAndFailedChangesDoNotDestroyRedo()
        {
            LabelMeDocument document = new LabelMeDocument();
            DocumentEditor editor = new DocumentEditor(document, new EasyLabelSidecar());
            editor.Add(new[] { Rectangle("original") }, new[] { new ShapeProvenance() }, "Draw");
            editor.ChangeClass(0, "changed");
            editor.History.Undo();
            editor.Replace(0, DocumentEditor.Clone(document.Shapes[0]), "Click without dragging");
            Assert.Throws<ArgumentOutOfRangeException>(() => editor.Delete(10));
            Assert.True(editor.History.CanRedo);
            editor.History.Redo();
            Assert.Equal("changed", document.Shapes[0].Label);
        }

        [Fact]
        public void DuplicateKeepsProvenanceAndIsOneUndoableMutation()
        {
            LabelMeDocument document = new LabelMeDocument();
            EasyLabelSidecar sidecar = new EasyLabelSidecar();
            DocumentEditor editor = new DocumentEditor(document, sidecar);
            editor.Add(new[] { Rectangle("original") }, new[] { new ShapeProvenance { Source = "model", Model = "unit", Confidence = 0.9 } }, "Draw");
            editor.History.MarkSaved();

            editor.Duplicate(0);

            Assert.Equal(2, document.Shapes.Count);
            Assert.Equal("original", document.Shapes[1].Label);
            Assert.Equal("model", sidecar.Shapes[1].Source);
            Assert.Equal(11.0, document.Shapes[1].Points[0][0]);
            Assert.True(editor.History.IsDirty);
            Assert.True(editor.History.Undo());
            Assert.Single(document.Shapes);
            Assert.Single(sidecar.Shapes);
            Assert.False(editor.History.IsDirty);
        }

        [Fact]
        public void PolygonVertexMutationsRemainUndoableAndDoNotChangeProvenance()
        {
            LabelMeDocument document = new LabelMeDocument();
            EasyLabelSidecar sidecar = new EasyLabelSidecar();
            DocumentEditor editor = new DocumentEditor(document, sidecar);
            LabelShape polygon = new LabelShape
            {
                Label = "part",
                ShapeType = "polygon",
                Points = new List<double[]> { new[] { 0.0, 0.0 }, new[] { 20.0, 0.0 }, new[] { 20.0, 20.0 } }
            };
            editor.Add(new[] { polygon }, new[] { new ShapeProvenance { Source = "manual" } }, "Draw polygon");
            editor.History.MarkSaved();

            editor.InsertVertex(0, 0, new[] { 10.0, 0.0 });
            Assert.Equal(4, document.Shapes[0].Points.Count);
            Assert.Equal("manual", sidecar.Shapes[0].Source);
            editor.RemoveVertex(0, 1);
            Assert.Equal(3, document.Shapes[0].Points.Count);
            Assert.True(editor.History.Undo());
            Assert.Equal(4, document.Shapes[0].Points.Count);
            Assert.True(editor.History.Undo());
            Assert.Equal(3, document.Shapes[0].Points.Count);
            Assert.False(editor.History.IsDirty);
        }

        private static LabelShape Rectangle(string label)
        {
            return new LabelShape { Label = label, ShapeType = "rectangle",
                Points = new List<double[]> { new double[] { 1, 2 }, new double[] { 10, 20 } } };
        }

        private static string State(LabelMeDocument document, EasyLabelSidecar sidecar)
        {
            return JsonConvert.SerializeObject(new { document, sidecar });
        }
    }
}
