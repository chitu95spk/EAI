using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using EasyLabel.Core.Commands;
using EasyLabel.Core.Editing;
using EasyLabel.Core.Geometry;
using EasyLabel.Core.Model;
using Xunit;

namespace EasyLabel.Core.Tests
{
    public sealed class CanvasEditSessionTests
    {
        private const float S = 1f;
        private const EditModifiers N = EditModifiers.None;

        private sealed class Fixture
        {
            public LabelMeDocument Doc = new LabelMeDocument { ImageWidth = 200, ImageHeight = 100 };
            public EasyLabelSidecar Sidecar = new EasyLabelSidecar();
            public DocumentEditor Editor;
            public CanvasEditSession Session;

            public Fixture(params LabelShape[] shapes)
            {
                Doc.Shapes.AddRange(shapes);
                Editor = new DocumentEditor(Doc, Sidecar);
                Session = new CanvasEditSession(Editor, Doc, 200, 100);
            }

            public int Units()
            {
                int n = 0;
                while (Editor.History.Undo()) n++;
                return n;
            }

            public void Drag(float x0, float y0, float x1, float y1, EditModifiers m = N)
            {
                Session.PointerDown(x0, y0, S, m);
                Session.PointerMove((x0 + x1) / 2, (y0 + y1) / 2, S, m);
                Session.PointerMove(x1, y1, S, m);
                Session.PointerUp(x1, y1, S, m);
            }
        }

        [Fact]
        public void Select_sweep_from_empty_space_selects_every_touched_shape()
        {
            var f = new Fixture(Rect(10, 10, 30, 30), Rect(50, 10, 70, 30), Rect(150, 60, 190, 90));
            Assert.True(f.Session.PointerDown(5, 5, S, N));
            Assert.True(f.Session.MarqueeRect.HasValue);
            f.Session.PointerMove(40, 40, S, N);
            f.Session.PointerMove(60, 40, S, N);
            Assert.False(f.Session.PointerUp(60, 40, S, N));
            Assert.Null(f.Session.MarqueeRect);
            Assert.Equal(new[] { 0, 1 }, f.Session.SelectedIndices.ToArray());
            Assert.Equal(0, f.Units());
        }

        [Fact]
        public void Select_click_on_empty_space_deselects_without_selecting()
        {
            var f = new Fixture(Rect(10, 10, 30, 30));
            f.Session.SelectAll();
            f.Session.PointerDown(120, 80, S, N);
            f.Session.PointerUp(120, 80, S, N);
            Assert.Empty(f.Session.SelectedIndices);
        }

        [Fact]
        public void Select_sweep_with_ctrl_adds_to_the_selection()
        {
            var f = new Fixture(Rect(10, 10, 30, 30), Rect(150, 60, 190, 90));
            f.Session.PointerDown(20, 20, S, N);
            f.Session.PointerUp(20, 20, S, N);
            Assert.Equal(new[] { 0 }, f.Session.SelectedIndices.ToArray());
            f.Drag(140, 50, 195, 95, EditModifiers.Ctrl);
            Assert.Equal(new[] { 0, 1 }, f.Session.SelectedIndices.ToArray());
        }

        private static LabelShape Rect(double l, double t, double r, double b)
        {
            return new LabelShape
            {
                Label = "NG",
                ShapeType = "rectangle",
                Points = new List<double[]> { new[] { l, t }, new[] { r, b } }
            };
        }

        private static LabelShape Poly(params double[] xy)
        {
            var s = new LabelShape { Label = "NG", ShapeType = "polygon" };
            for (int i = 0; i < xy.Length; i += 2) s.Points.Add(new[] { xy[i], xy[i + 1] });
            return s;
        }

        private static LabelShape Rot(double l, double t, double r, double b)
        {
            return new LabelShape
            {
                Label = "NG",
                ShapeType = "oriented_rectangle",
                Points = new List<double[]> { new[] { l, t }, new[] { r, t }, new[] { r, b }, new[] { l, b } }
            };
        }

        [Fact]
        public void RectDragIsOneUndoUnit()
        {
            var f = new Fixture();
            f.Session.Tool = CanvasTool.Rect;
            f.Drag(10, 10, 50, 40);
            Assert.Single(f.Doc.Shapes);
            Assert.Equal(new[] { 10.0, 10.0 }, f.Doc.Shapes[0].Points[0]);
            Assert.Equal(new[] { 50.0, 40.0 }, f.Doc.Shapes[0].Points[1]);
            Assert.Equal(1, f.Units());
        }

        [Fact]
        public void RectIsClampedToImageAndTinyBoxDiscarded()
        {
            var f = new Fixture();
            f.Session.Tool = CanvasTool.Rect;
            f.Drag(150, 50, 400, 300);
            Assert.Equal(new[] { 200.0, 100.0 }, f.Doc.Shapes[0].Points[1]);
            f.Drag(20, 20, 21, 60);
            Assert.Single(f.Doc.Shapes);
            Assert.Equal(1, f.Units());
        }

        [Fact]
        public void EscDuringCreationOrMoveRecordsNothing()
        {
            var f = new Fixture(Rect(10, 10, 60, 40));
            f.Session.Tool = CanvasTool.Rect;
            f.Session.PointerDown(100, 10, S, N);
            f.Session.PointerMove(150, 50, S, N);
            Assert.True(f.Session.Cancel());
            Assert.False(f.Session.PointerUp(150, 50, S, N));
            Assert.Single(f.Doc.Shapes);

            f.Session.Tool = CanvasTool.Select;
            f.Session.Select(0, false);
            f.Session.PointerDown(35, 25, S, N);
            f.Session.PointerMove(80, 60, S, N);
            Assert.True(f.Session.Cancel());
            Assert.False(f.Session.PointerUp(80, 60, S, N));
            Assert.Equal(new[] { 10.0, 10.0 }, f.Doc.Shapes[0].Points[0]);
            Assert.Equal(0, f.Units());
        }

        [Fact]
        public void RightClickRecordsNothing()
        {
            var f = new Fixture(Rect(10, 10, 60, 40));
            Assert.False(f.Session.PointerDown(35, 25, S, N, EditPointerButton.Right));
            Assert.False(f.Session.PointerUp(35, 25, S, N, EditPointerButton.Right));
            f.Session.Tool = CanvasTool.Rect;
            Assert.False(f.Session.PointerDown(100, 10, S, N, EditPointerButton.Right));
            Assert.False(f.Session.PointerUp(150, 50, S, N, EditPointerButton.Right));
            Assert.Single(f.Doc.Shapes);
            Assert.Empty(f.Session.SelectedIndices);
            Assert.Equal(0, f.Units());
        }

        [Fact]
        public void AiToolsIgnorePointerInputAndNeverSelectOrEdit()
        {
            var f = new Fixture(Rect(10, 10, 60, 40));
            foreach (CanvasTool tool in new[] { CanvasTool.AiPoints, CanvasTool.AiBox })
            {
                f.Session.Tool = tool;
                Assert.False(f.Session.PointerDown(35, 25, S, N));
                f.Session.PointerMove(80, 60, S, N);
                Assert.False(f.Session.PointerUp(80, 60, S, N));
                Assert.False(f.Session.IsDragging);
                Assert.False(f.Session.HasDraft);
                Assert.Empty(f.Session.SelectedIndices);
                Assert.Single(f.Doc.Shapes);
            }

            Assert.Equal(0, f.Units());
        }

        [Fact]
        public void ClickWithoutMovementRecordsNothing()
        {
            var f = new Fixture(Rect(10, 10, 60, 40));
            f.Drag(35, 25, 35, 25);
            Assert.Equal(new[] { 0 }, f.Session.SelectedIndices.ToArray());
            Assert.Equal(0, f.Units());
        }

        [Fact]
        public void PolygonIsOneUndoUnitForEnterAndForFirstPointClose()
        {
            var f = new Fixture();
            f.Session.Tool = CanvasTool.Polygon;
            foreach (var p in new[] { (20f, 20f), (80f, 20f), (80f, 60f), (20f, 60f) })
                Assert.True(f.Session.PointerDown(p.Item1, p.Item2, S, N));
            Assert.False(f.Session.PointerUp(20, 60, S, N));
            Assert.True(f.Session.RemoveDraftVertex());
            Assert.Equal(3, f.Session.DraftPoints.Count);
            Assert.True(f.Session.Finish());
            Assert.Equal(3, f.Doc.Shapes[0].Points.Count);

            foreach (var p in new[] { (100f, 20f), (150f, 20f), (150f, 60f) })
                f.Session.PointerDown(p.Item1, p.Item2, S, N);
            Assert.True(f.Session.PointerDown(101, 21, S, N)); // click on first point closes
            Assert.Equal(2, f.Doc.Shapes.Count);
            Assert.False(f.Session.HasDraft);
            Assert.Equal(2, f.Units());
        }

        [Fact]
        public void PolygonEscCancelsDraftAndFinishNeedsThreePoints()
        {
            var f = new Fixture();
            f.Session.Tool = CanvasTool.Polygon;
            f.Session.PointerDown(20, 20, S, N);
            f.Session.PointerDown(80, 20, S, N);
            Assert.False(f.Session.Finish());
            Assert.True(f.Session.Cancel());
            Assert.False(f.Session.HasDraft);
            Assert.Empty(f.Doc.Shapes);
            Assert.Equal(0, f.Units());
        }

        [Fact]
        public void RotationCreationOnBothSidesPassesTryCreate()
        {
            foreach (float thirdY in new[] { 80f, 20f })
            {
                var f = new Fixture();
                f.Session.Tool = CanvasTool.Rot;
                f.Session.PointerDown(50, 50, S, N);
                f.Session.PointerDown(100, 50, S, N);
                Assert.True(f.Session.PointerDown(75, thirdY, S, N));
                Assert.Single(f.Doc.Shapes);
                PointF[] pts = f.Doc.Shapes[0].Points.Select(p => new PointF((float)p[0], (float)p[1])).ToArray();
                Assert.True(RotatedBoxGeometry.TryCreate(pts, out RotatedBoxGeometry box));
                Assert.Equal(50.0, (double)box.Width, 2);
                Assert.Equal(30.0, (double)box.Height, 2);
                Assert.Equal(1, f.Units());
            }
        }

        [Fact]
        public void RotationCreationDiagonalEdgeBothSides()
        {
            foreach (var third in new[] { (55.2f, 66.4f), (72f, 44f) })
            {
                var f = new Fixture();
                f.Session.Tool = CanvasTool.Rot;
                f.Session.PointerDown(60, 60, S, N);
                f.Session.PointerDown(100, 90, S, N);
                f.Session.PointerDown(third.Item1, third.Item2, S, N);
                Assert.Single(f.Doc.Shapes);
                PointF[] pts = f.Doc.Shapes[0].Points.Select(p => new PointF((float)p[0], (float)p[1])).ToArray();
                Assert.True(RotatedBoxGeometry.TryCreate(pts, out _));
            }
        }

        [Fact]
        public void MoveIsPushedBackInsideImage()
        {
            var f = new Fixture(Rect(10, 10, 60, 40));
            f.Session.Select(0, false);
            f.Drag(35, 25, -500, 25);
            Assert.Equal(new[] { 0.0, 10.0 }, f.Doc.Shapes[0].Points[0]);
            Assert.Equal(new[] { 50.0, 40.0 }, f.Doc.Shapes[0].Points[1]);
            f.Drag(25, 25, 900, 900);
            Assert.Equal(new[] { 150.0, 70.0 }, f.Doc.Shapes[0].Points[0]);
            Assert.Equal(new[] { 200.0, 100.0 }, f.Doc.Shapes[0].Points[1]);
            Assert.Equal(2, f.Units());
        }

        [Fact]
        public void ResizeAndRotateLeavingImageAreRejected()
        {
            var f = new Fixture(Rect(10, 10, 60, 40), Rot(5, 5, 35, 15));
            f.Session.Select(0, false);
            f.Drag(60, 40, 300, 300); // BR corner outside image
            Assert.Equal(new[] { 60.0, 40.0 }, f.Doc.Shapes[0].Points[1]);

            f.Session.Select(1, false);
            f.Drag(20, -19, 50, 10, EditModifiers.None); // rotation handle, 90 deg would leave the image
            Assert.Equal(5.0, f.Doc.Shapes[1].Points[0][0], 6);
            Assert.Equal(5.0, f.Doc.Shapes[1].Points[0][1], 6);
            Assert.Equal(0, f.Units());
        }

        [Fact]
        public void ResizeEnforcesMinimumSizeAndIsOneUnit()
        {
            var f = new Fixture(Rect(10, 10, 60, 40));
            f.Session.Select(0, false);
            f.Drag(60, 40, 5, 5); // drag BR beyond TL -> collapses to min 2 px
            double[] a = f.Doc.Shapes[0].Points[0], b = f.Doc.Shapes[0].Points[1];
            Assert.True(b[0] - a[0] >= 2.0 - 1e-6 && b[1] - a[1] >= 2.0 - 1e-6);
            Assert.Equal(1, f.Units());
        }

        [Fact]
        public void RotateInsideImageIsOneUnitAndCarriesHoles()
        {
            var f = new Fixture(Rot(80, 40, 120, 60));
            var store = new ShapeHoleStore<string>((h, map) => h + "r");
            f.Session.Holes = store;
            LabelShape original = DocumentEditor.Clone(f.Doc.Shapes[0]);
            store.Set(original, "H");
            f.Session.Select(0, false);
            f.Drag(100, 16, 144, 50); // handle at (100, 40 - 24); +90 degrees
            Assert.NotEqual(original.Points[0][0], f.Doc.Shapes[0].Points[0][0]);
            Assert.True(store.TryGet(f.Doc.Shapes[0], out string moved));
            Assert.Equal("Hr", moved);
            Assert.Equal(1, f.Units());
        }

        [Fact]
        public void HoleStoreSurvivesUndoAndRedo()
        {
            var f = new Fixture(Rect(10, 10, 60, 40));
            var store = new ShapeHoleStore<string>((h, map) => h + "m");
            f.Session.Holes = store;
            LabelShape original = DocumentEditor.Clone(f.Doc.Shapes[0]);
            store.Set(original, "H");
            f.Session.Select(0, false);
            f.Drag(35, 25, 55, 45);
            LabelShape moved = DocumentEditor.Clone(f.Doc.Shapes[0]);
            Assert.True(store.TryGet(moved, out string h1));
            Assert.Equal("Hm", h1);

            Assert.True(f.Editor.History.Undo());
            f.Session.NotifyDocumentChanged();
            Assert.True(store.TryGet(f.Doc.Shapes[0], out string h0));
            Assert.Equal("H", h0);
            Assert.True(f.Editor.History.Redo());
            Assert.True(store.TryGet(f.Doc.Shapes[0], out string h2));
            Assert.Equal("Hm", h2);
        }

        [Fact]
        public void PolygonKeepsAtLeastThreeVertices()
        {
            var f = new Fixture(Poly(20, 20, 80, 20, 80, 60));
            Assert.False(f.Session.DeleteVertex(0, 1));
            Assert.Equal(3, f.Doc.Shapes[0].Points.Count);

            var g = new Fixture(Poly(20, 20, 80, 20, 80, 60, 20, 60));
            Assert.True(g.Session.DeleteVertex(0, 2));
            Assert.Equal(3, g.Doc.Shapes[0].Points.Count);
            Assert.False(g.Session.DeleteVertex(0, 1));
            Assert.Equal(1, g.Units());
        }

        [Fact]
        public void DoubleClickOnEdgeInsertsVertexAsOneUnit()
        {
            var f = new Fixture(Poly(20, 20, 80, 20, 80, 60));
            f.Session.Select(0, false);
            Assert.True(f.Session.DoubleClick(50, 21, S, N));
            Assert.Equal(4, f.Doc.Shapes[0].Points.Count);
            Assert.Equal(50.0, f.Doc.Shapes[0].Points[1][0], 3);
            Assert.Equal(1, f.Units());
        }

        [Fact]
        public void VertexDragIsOneUnit()
        {
            var f = new Fixture(Poly(20, 20, 80, 20, 80, 60));
            f.Session.Select(0, false);
            f.Drag(80, 60, 120, 90);
            Assert.Equal(new[] { 120.0, 90.0 }, f.Doc.Shapes[0].Points[2]);
            Assert.Equal(1, f.Units());
        }

        [Fact]
        public void PasteIsClampedInsideImage()
        {
            var f = new Fixture(Rect(180, 10, 195, 30));
            f.Session.Select(0, false);
            Assert.True(f.Session.Copy());
            Assert.True(f.Session.Paste());
            Assert.Equal(2, f.Doc.Shapes.Count);
            Assert.Equal(new[] { 185.0, 20.0 }, f.Doc.Shapes[1].Points[0]);
            Assert.Equal(new[] { 200.0, 40.0 }, f.Doc.Shapes[1].Points[1]);
            Assert.Equal(new[] { 1 }, f.Session.SelectedIndices.ToArray());
            Assert.Equal(1, f.Units());
        }

        [Fact]
        public void DuplicateDeleteNudgeAreSingleUnitsAndUndoable()
        {
            var f = new Fixture(Rect(10, 10, 30, 30), Rect(50, 10, 70, 30));
            f.Session.SelectAll();
            Assert.True(f.Session.Duplicate());
            Assert.Equal(4, f.Doc.Shapes.Count);
            Assert.True(f.Session.Nudge(1, 0, true));
            Assert.Equal(30.0, f.Doc.Shapes[2].Points[0][0], 6);
            Assert.True(f.Session.Delete());
            Assert.Equal(2, f.Doc.Shapes.Count);
            Assert.Empty(f.Session.SelectedIndices);
            Assert.Equal(3, f.Units());
            Assert.Equal(2, f.Doc.Shapes.Count);
        }

        [Fact]
        public void NonSegAiShapesAreNotSelectedOrEdited()
        {
            // "mask" is a LabelMe kind the editor does not draw or edit (circle/point/line/linestrip are editable since EAI-UI C).
            var circle = new LabelShape
            {
                Label = "x",
                ShapeType = "mask",
                Points = new List<double[]> { new[] { 50.0, 50.0 }, new[] { 60.0, 50.0 } }
            };
            var f = new Fixture(circle);
            f.Session.SelectAll();
            Assert.Empty(f.Session.SelectedIndices);
            f.Drag(50, 50, 80, 80);
            Assert.Empty(f.Session.SelectedIndices);
            Assert.Equal(0, f.Units());
        }
    }
}
