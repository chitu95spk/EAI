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
    /// <summary>EAI-UI commit C: circle / point / line / linestrip kinds and the new edit commands.</summary>
    public sealed class ShapeKindsEditTests
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

            public void Click(float x, float y) => Session.PointerDown(x, y, S, N);
        }

        private static LabelShape Shape(string type, string label, params double[] xy)
        {
            var s = new LabelShape { Label = label, ShapeType = type };
            for (int i = 0; i < xy.Length; i += 2) s.Points.Add(new[] { xy[i], xy[i + 1] });
            return s;
        }

        private static double Area(LabelShape s) => System.Math.Abs(ShapeGeometryOps.SignedArea(s.Points));

        // ---------- creation ----------

        [Fact]
        public void CircleToolTwoClicksCreateOneUndoUnit()
        {
            var f = new Fixture();
            f.Session.Tool = CanvasTool.Circle;
            f.Click(50, 50);
            Assert.True(f.Session.HasDraft);
            f.Session.PointerMove(60, 50, S, N);
            Assert.Equal("circle", f.Session.CreationPreview!.ShapeType);
            f.Click(60, 50);
            Assert.False(f.Session.HasDraft);
            LabelShape s = Assert.Single(f.Doc.Shapes);
            Assert.Equal("circle", s.ShapeType);
            Assert.Equal(2, s.Points.Count);
            Assert.Equal(0, f.Session.TakeCreatedIndex());
            Assert.Equal(-1, f.Session.TakeCreatedIndex());
            Assert.Equal(1, f.Units());
        }

        [Fact]
        public void CircleWithZeroRadiusIsNotCreated()
        {
            var f = new Fixture();
            f.Session.Tool = CanvasTool.Circle;
            f.Click(50, 50);
            f.Click(50, 50);
            Assert.Empty(f.Doc.Shapes);
        }

        [Fact]
        public void PointToolOneClickCreatesPoint()
        {
            var f = new Fixture();
            f.Session.Tool = CanvasTool.Point;
            f.Click(30, 40);
            LabelShape s = Assert.Single(f.Doc.Shapes);
            Assert.Equal("point", s.ShapeType);
            Assert.Equal(new[] { 30.0, 40.0 }, s.Points[0]);
            Assert.Equal(1, f.Units());
        }

        [Fact]
        public void LineToolTwoClicksCreateLine()
        {
            var f = new Fixture();
            f.Session.Tool = CanvasTool.Line;
            f.Click(10, 10);
            f.Click(90, 40);
            Assert.Equal("line", Assert.Single(f.Doc.Shapes).ShapeType);
            Assert.Equal(1, f.Units());
        }

        [Fact]
        public void LineStripFinishesWithEnterOrDoubleClick()
        {
            var f = new Fixture();
            f.Session.Tool = CanvasTool.LineStrip;
            f.Click(10, 10);
            f.Click(50, 10);
            f.Click(50, 50);
            Assert.True(f.Session.Finish());
            Assert.Equal(3, f.Doc.Shapes[0].Points.Count);

            f.Click(100, 10);
            f.Click(150, 40);
            Assert.True(f.Session.DoubleClick(150, 40, S, N));
            Assert.Equal("linestrip", f.Doc.Shapes[1].ShapeType);
            Assert.Equal(2, f.Doc.Shapes[1].Points.Count);
            Assert.Equal(2, f.Units());
        }

        [Fact]
        public void LineStripNeedsTwoPoints()
        {
            var f = new Fixture();
            f.Session.Tool = CanvasTool.LineStrip;
            f.Click(10, 10);
            Assert.False(f.Session.Finish());
            Assert.True(f.Session.RemoveDraftVertex());
            Assert.False(f.Session.HasDraft);
        }

        // ---------- hit tests ----------

        [Fact]
        public void CircleHitTestsHandlesDiscAndOutside()
        {
            // Handles hit within half of the 48 px touch size (24 px at scale 1), so the circle is large enough to have a free disc.
            LabelShape c = Shape("circle", "NG", 50, 50, 150, 50);
            Assert.Equal(0, ShapeHitTester.HitTest(c, new PointF(50, 50), S).Index);
            Assert.Equal(ShapeHitKind.Vertex, ShapeHitTester.HitTest(c, new PointF(50, 50), S).Kind);
            Assert.Equal(ShapeHitKind.Vertex, ShapeHitTester.HitTest(c, new PointF(150, 50), S).Kind);
            Assert.Equal(1, ShapeHitTester.HitTest(c, new PointF(150, 50), S).Index);
            Assert.Equal(ShapeHitKind.Move, ShapeHitTester.HitTest(c, new PointF(100, 50), S).Kind);
            Assert.Equal(ShapeHitKind.Move, ShapeHitTester.HitTest(c, new PointF(50, 140), S).Kind);
            Assert.Equal(ShapeHitKind.None, ShapeHitTester.HitTest(c, new PointF(50, 200), S).Kind);
        }

        [Fact]
        public void PointHitUsesScreenRadius()
        {
            LabelShape p = Shape("point", "NG", 40, 40);
            Assert.Equal(ShapeHitKind.Move, ShapeHitTester.HitTest(p, new PointF(44, 40), S).Kind);
            Assert.Equal(ShapeHitKind.None, ShapeHitTester.HitTest(p, new PointF(80, 40), S).Kind);
            // 10 image px at 4x zoom is 40 screen px: outside the 24 px hit radius, while at 1x it is inside
            Assert.Equal(ShapeHitKind.Move, ShapeHitTester.HitTest(p, new PointF(50, 40), S).Kind);
            Assert.Equal(ShapeHitKind.None, ShapeHitTester.HitTest(p, new PointF(50, 40), 4f).Kind);
        }

        [Fact]
        public void LineAndStripHitSegmentsAndVertices()
        {
            LabelShape line = Shape("line", "NG", 10, 10, 110, 10);
            Assert.Equal(ShapeHitKind.Move, ShapeHitTester.HitTest(line, new PointF(60, 13), S).Kind);
            Assert.Equal(ShapeHitKind.None, ShapeHitTester.HitTest(line, new PointF(60, 30), S).Kind);
            Assert.Equal(ShapeHitKind.Vertex, ShapeHitTester.HitTest(line, new PointF(110, 10), S).Kind);

            LabelShape strip = Shape("linestrip", "NG", 10, 10, 60, 10, 60, 60);
            Assert.Equal(ShapeHitKind.Move, ShapeHitTester.HitTest(strip, new PointF(62, 35), S).Kind);
            Assert.Equal(2, ShapeHitTester.HitTest(strip, new PointF(60, 60), S).Index);
            Assert.Equal(ShapeHitKind.None, ShapeHitTester.HitTest(strip, new PointF(20, 40), S).Kind);
        }

        // ---------- editing ----------

        [Fact]
        public void SelectToolMovesAndEditsNewKinds()
        {
            var f = new Fixture(Shape("circle", "NG", 50, 50, 70, 50));
            f.Click(55, 52);
            Assert.Equal(new[] { 0 }, f.Session.SelectedIndices.ToArray());
            f.Session.PointerMove(75, 52, S, N);
            Assert.True(f.Session.PointerUp(75, 52, S, N));
            Assert.Equal(70.0, f.Doc.Shapes[0].Points[0][0]);
            Assert.Equal(90.0, f.Doc.Shapes[0].Points[1][0]);

            // edge handle changes the radius only
            f.Click(90, 50);
            f.Session.PointerMove(100, 50, S, N);
            Assert.True(f.Session.PointerUp(100, 50, S, N));
            Assert.Equal(70.0, f.Doc.Shapes[0].Points[0][0]);
            Assert.Equal(100.0, f.Doc.Shapes[0].Points[1][0]);

            // centre handle moves the whole circle
            f.Click(70, 50);
            f.Session.PointerMove(60, 40, S, N);
            Assert.True(f.Session.PointerUp(60, 40, S, N));
            Assert.Equal(60.0, f.Doc.Shapes[0].Points[0][0]);
            Assert.Equal(90.0, f.Doc.Shapes[0].Points[1][0]);
            Assert.Equal(40.0, f.Doc.Shapes[0].Points[1][1]);
            Assert.Equal(3, f.Units());
        }

        [Fact]
        public void PointCanBeDraggedAndNudged()
        {
            var f = new Fixture(Shape("point", "NG", 40, 40));
            f.Click(40, 40);
            f.Session.PointerMove(60, 50, S, N);
            Assert.True(f.Session.PointerUp(60, 50, S, N));
            Assert.Equal(new[] { 60.0, 50.0 }, f.Doc.Shapes[0].Points[0]);
            Assert.True(f.Session.Nudge(1, 0, false));
            Assert.Equal(61.0, f.Doc.Shapes[0].Points[0][0]);
            Assert.Equal(2, f.Units());
        }

        [Fact]
        public void CopyPasteDuplicateAndDeleteWorkForNewKinds()
        {
            var f = new Fixture(Shape("line", "NG", 10, 10, 50, 10), Shape("circle", "OK", 100, 50, 120, 50));
            f.Session.SelectAll();
            Assert.True(f.Session.Duplicate());
            Assert.Equal(4, f.Doc.Shapes.Count);
            Assert.True(f.Session.Copy());
            Assert.True(f.Session.Paste());
            Assert.Equal(6, f.Doc.Shapes.Count);
            Assert.True(f.Session.Delete());
            Assert.Equal(4, f.Doc.Shapes.Count);
            Assert.Equal(3, f.Units());
        }

        [Fact]
        public void LineStripVertexCanBeDeletedDownToTwoPoints()
        {
            var f = new Fixture(Shape("linestrip", "NG", 10, 10, 60, 10, 60, 60));
            f.Session.Select(0, false);
            f.Click(60, 10); // press the middle vertex
            f.Session.PointerUp(60, 10, S, N);
            Assert.True(f.Session.CanDeleteSelectedVertex);
            Assert.True(f.Session.DeleteSelectedVertex());
            Assert.Equal(2, f.Doc.Shapes[0].Points.Count);
            Assert.False(f.Session.CanDeleteSelectedVertex);
            Assert.False(f.Session.DeleteVertex(0, 0));
            Assert.Equal(1, f.Units());
        }

        [Fact]
        public void PolygonSelectedVertexDeleteKeepsThreeVertices()
        {
            var f = new Fixture(Shape("polygon", "NG", 10, 10, 90, 10, 90, 90, 10, 90));
            f.Session.Select(0, false);
            f.Click(90, 90);
            f.Session.PointerUp(90, 90, S, N);
            Assert.True(f.Session.DeleteSelectedVertex());
            Assert.Equal(3, f.Doc.Shapes[0].Points.Count);
            f.Session.Select(0, false);
            f.Click(10, 10);
            f.Session.PointerUp(10, 10, S, N);
            Assert.False(f.Session.DeleteSelectedVertex());
        }

        [Fact]
        public void AddPointOnEdgeWorksForPolygonAndLineStrip()
        {
            var f = new Fixture(Shape("polygon", "NG", 10, 10, 90, 10, 90, 90, 10, 90), Shape("linestrip", "OK", 100, 10, 150, 10, 150, 60));
            f.Session.Select(0, false);
            Assert.True(f.Session.CanAddPointOnEdge(50, 12, S));
            Assert.False(f.Session.CanAddPointOnEdge(50, 50, S));
            Assert.True(f.Session.AddPointOnEdge(50, 12, S));
            Assert.Equal(5, f.Doc.Shapes[0].Points.Count);
            Assert.Equal(50.0, f.Doc.Shapes[0].Points[1][0]);

            f.Session.Select(1, false);
            Assert.True(f.Session.AddPointOnEdge(125, 11, S));
            Assert.Equal(4, f.Doc.Shapes[1].Points.Count);
            Assert.Equal(125.0, f.Doc.Shapes[1].Points[1][0]);
            Assert.Equal(2, f.Units());
        }

        [Fact]
        public void LineStripEdgeIsNotClosed()
        {
            var f = new Fixture(Shape("linestrip", "NG", 10, 10, 60, 10, 60, 60));
            f.Session.Select(0, false);
            // the segment from the last to the first vertex does not exist for a strip
            Assert.False(f.Session.CanAddPointOnEdge(35, 35, S));
        }

        [Fact]
        public void HiddenShapesAreNotHitOrSelected()
        {
            var f = new Fixture(Shape("rectangle", "NG", 10, 10, 50, 50));
            f.Session.VisibilityFilter = _ => false;
            f.Click(30, 30);
            Assert.Empty(f.Session.SelectedIndices);
            f.Session.SelectAll();
            Assert.Empty(f.Session.SelectedIndices);
            f.Session.VisibilityFilter = null;
            f.Session.SelectAll();
            Assert.Single(f.Session.SelectedIndices);
        }

        [Fact]
        public void OutOfBoundsPointsAreAllowedOnlyWhenEnabled()
        {
            var f = new Fixture();
            f.Session.Tool = CanvasTool.Point;
            f.Click(-20, 500);
            Assert.Equal(new[] { 0.0, 100.0 }, f.Doc.Shapes[0].Points[0]);
            f.Session.AllowOutOfBounds = true;
            f.Click(-20, 500);
            Assert.Equal(new[] { -20.0, 500.0 }, f.Doc.Shapes[1].Points[0]);
        }

        // ---------- label / direction / merge ----------

        [Fact]
        public void EditLabelIsOneUndoUnitAndKeepsUntouchedFields()
        {
            var a = Shape("rectangle", "NG", 10, 10, 30, 30);
            a.Description = "keep";
            var f = new Fixture(a, Shape("circle", "OK", 100, 50, 120, 50));
            f.Session.SelectAll();
            ShapeLabelInfo initial = f.Session.DescribeSelection();
            Assert.Null(initial.Label);
            Assert.Null(initial.GroupId);
            Assert.True(f.Session.ApplyLabelInfo(initial, new ShapeLabelInfo { Label = "scratch", GroupId = 7, Description = string.Empty }));
            Assert.All(f.Doc.Shapes, s => Assert.Equal("scratch", s.Label));
            Assert.All(f.Doc.Shapes, s => Assert.Equal(7, s.GroupId));
            Assert.Equal("keep", f.Doc.Shapes[0].Description);
            Assert.Equal(1, f.Units());
            Assert.Equal("NG", f.Doc.Shapes[0].Label);
        }

        [Fact]
        public void EditLabelWithoutChangeRecordsNothing()
        {
            var f = new Fixture(Shape("point", "NG", 5, 5));
            f.Session.SelectAll();
            ShapeLabelInfo info = f.Session.DescribeSelection();
            Assert.False(f.Session.ApplyLabelInfo(info, new ShapeLabelInfo { Label = "NG", GroupId = null, Description = string.Empty }));
            Assert.Equal(0, f.Units());
        }

        [Fact]
        public void SelectInDirectionPicksNearestInThatDirection()
        {
            var f = new Fixture(
                Shape("point", "NG", 100, 50),
                Shape("point", "NG", 100, 10),
                Shape("point", "NG", 100, 90),
                Shape("point", "NG", 20, 52),
                Shape("point", "NG", 180, 48));
            f.Session.Select(0, false);
            Assert.True(f.Session.SelectInDirection(SelectDirection.Up));
            Assert.Equal(new[] { 1 }, f.Session.SelectedIndices.ToArray());
            Assert.False(f.Session.SelectInDirection(SelectDirection.Up));
            f.Session.Select(0, false);
            Assert.True(f.Session.SelectInDirection(SelectDirection.Down));
            Assert.Equal(2, f.Session.SelectedIndices[0]);
            f.Session.Select(0, false);
            f.Session.SelectInDirection(SelectDirection.Left);
            Assert.Equal(3, f.Session.SelectedIndices[0]);
            f.Session.Select(0, false);
            f.Session.SelectInDirection(SelectDirection.Right);
            Assert.Equal(4, f.Session.SelectedIndices[0]);
        }

        [Fact]
        public void MergeOverlappingRectanglesIntoOnePolygonAsOneUndoUnit()
        {
            var f = new Fixture(Shape("rectangle", "NG", 0, 0, 10, 10), Shape("rectangle", "NG", 5, 5, 15, 15), Shape("point", "OK", 90, 90));
            f.Session.Select(0, false);
            f.Session.Select(1, true);
            Assert.True(f.Session.CanMerge());
            Assert.True(f.Session.Merge());
            Assert.Equal(2, f.Doc.Shapes.Count);
            Assert.Equal("polygon", f.Doc.Shapes[0].ShapeType);
            Assert.Equal(8, f.Doc.Shapes[0].Points.Count);
            Assert.Equal(175.0, Area(f.Doc.Shapes[0]), 6);
            Assert.Equal(new[] { 0 }, f.Session.SelectedIndices.ToArray());
            Assert.Equal(1, f.Units());
            Assert.Equal(3, f.Doc.Shapes.Count);
        }

        [Fact]
        public void MergeIsRefusedWhenASelectedShapeHasSegmentAiHoles()
        {
            var f = new Fixture(Shape("rectangle", "NG", 0, 0, 10, 10), Shape("rectangle", "NG", 5, 5, 15, 15));
            var store = new ShapeHoleStore<string>((h, map) => h);
            f.Session.Holes = store;
            f.Session.Select(0, false);
            f.Session.Select(1, true);
            Assert.True(f.Session.CanMerge());
            store.Set(f.Doc.Shapes[0], "H"); // the union polygon would get a new geometry key and lose these holes
            Assert.False(f.Session.CanMerge());
            Assert.False(f.Session.Merge());
            Assert.Equal(2, f.Doc.Shapes.Count);
        }

        [Fact]
        public void MergeIsRefusedForDisjointDifferentLabelOrOpenShapes()
        {
            var f = new Fixture(Shape("rectangle", "NG", 0, 0, 10, 10), Shape("rectangle", "NG", 50, 50, 60, 60),
                Shape("rectangle", "OK", 5, 5, 15, 15), Shape("circle", "NG", 5, 5, 8, 5));
            f.Session.Select(0, false);
            f.Session.Select(1, true);
            Assert.False(f.Session.CanMerge()); // disjoint
            f.Session.Select(0, false);
            f.Session.Select(2, true);
            Assert.False(f.Session.CanMerge()); // different labels
            f.Session.Select(0, false);
            f.Session.Select(3, true);
            Assert.False(f.Session.CanMerge()); // circle is not a closed polygon kind
            f.Session.Select(0, false);
            Assert.False(f.Session.CanMerge()); // single shape
            Assert.False(f.Session.Merge());
            Assert.Equal(0, f.Units());
        }

        [Fact]
        public void UnionHandlesContainmentAndRefusesHoles()
        {
            var outer = Shape("polygon", "NG", 0, 0, 100, 0, 100, 100, 0, 100);
            var inner = Shape("polygon", "NG", 20, 20, 40, 20, 40, 40, 20, 40);
            Assert.True(ShapeGeometryOps.TryUnion(outer.Points, inner.Points, out List<double[]> contained));
            Assert.Equal(4, contained.Count);

            // a "C" closed by a bar encloses a hole
            var c = Shape("polygon", "NG", 0, 0, 100, 0, 100, 20, 20, 20, 20, 80, 100, 80, 100, 100, 0, 100);
            var bar = Shape("polygon", "NG", 80, 10, 95, 10, 95, 90, 80, 90);
            Assert.False(ShapeGeometryOps.TryUnion(c.Points, bar.Points, out _));
        }
    }
}
