using System.Linq;
using EasyLabel.Core.Commands;
using EasyLabel.Core.Editing;
using EasyLabel.Core.Model;
using Xunit;

namespace EasyLabel.Core.Tests
{
    /// <summary>CounterAI stamp mode: left click pastes a fixed-size circle/rectangle, right click deletes the shape under the pointer.</summary>
    public sealed class StampModeTests
    {
        private static CanvasEditSession Make(out LabelMeDocument doc, out DocumentEditor editor)
        {
            doc = new LabelMeDocument { ImageWidth = 400, ImageHeight = 300 };
            editor = new DocumentEditor(doc, new EasyLabelSidecar());
            return new CanvasEditSession(editor, doc, 400, 300) { StampMode = true, StampWidth = 60f, StampHeight = 40f, ActiveLabel = "wire" };
        }

        [Fact]
        public void LeftClickStampsCircleCentredOnPointer_NoSelectionNoDialog()
        {
            CanvasEditSession s = Make(out LabelMeDocument doc, out DocumentEditor editor);
            s.Tool = CanvasTool.Count;
            Assert.True(s.PointerDown(100, 100, 1f, EditModifiers.None));
            LabelShape c = Assert.Single(doc.Shapes);
            Assert.Equal("circle", c.ShapeType);
            Assert.Equal("wire", c.Label);
            Assert.Equal(100.0, c.Points[0][0]);
            Assert.Equal(130.0, c.Points[1][0]);   // radius = StampWidth / 2
            Assert.Empty(s.SelectedIndices);
            Assert.Equal(-1, s.TakeCreatedIndex());
            Assert.True(editor.History.Undo());
            Assert.Empty(doc.Shapes);
        }

        [Fact]
        public void RectangleStampUsesWidthAndHeight_AndHoverPreviewMatches()
        {
            CanvasEditSession s = Make(out LabelMeDocument doc, out DocumentEditor editor);
            s.Tool = CanvasTool.Count;
            s.StampCircle = false;
            s.PointerMove(200, 150, 1f, EditModifiers.None);
            Assert.Equal("rectangle", s.CreationPreview.ShapeType);
            s.PointerDown(200, 150, 1f, EditModifiers.None);
            LabelShape r = Assert.Single(doc.Shapes);
            Assert.Equal(170.0, r.Points[0][0]);
            Assert.Equal(130.0, r.Points[0][1]);
            Assert.Equal(230.0, r.Points[1][0]);
            Assert.Equal(170.0, r.Points[1][1]);
        }

        [Fact]
        public void RightClickDeletesShapeUnderPointer_NearestCentreWins_AndMissDoesNothing()
        {
            CanvasEditSession s = Make(out LabelMeDocument doc, out DocumentEditor editor);
            s.Tool = CanvasTool.Count;
            s.PointerDown(100, 100, 1f, EditModifiers.None);
            s.PointerDown(140, 100, 1f, EditModifiers.None);   // within 0.7 x size of the first
            Assert.Equal(2, doc.Shapes.Count);

            Assert.True(s.PointerDown(133, 100, 1f, EditModifiers.None, EditPointerButton.Right));
            Assert.Equal(1, doc.Shapes.Count);
            Assert.Equal(100.0, doc.Shapes[0].Points[0][0]);   // the one centred at 140 was nearest

            Assert.False(s.PointerDown(300, 250, 1f, EditModifiers.None, EditPointerButton.Right));
            Assert.Equal(1, doc.Shapes.Count);
            Assert.True(editor.History.Undo());
            Assert.Equal(2, doc.Shapes.Count);
        }

        [Fact]
        public void CircleToolIsNotAffectedByCount_StillNeedsTwoClicks()
        {
            CanvasEditSession s = Make(out LabelMeDocument doc, out DocumentEditor editor);
            s.Tool = CanvasTool.Circle;
            s.PointerDown(100, 100, 1f, EditModifiers.None);
            Assert.Empty(doc.Shapes);
            Assert.True(s.HasDraft);
            Assert.False(s.PointerDown(100, 100, 1f, EditModifiers.None, EditPointerButton.Right));
        }

        [Fact]
        public void StampNearEdgeIsClampedCentre()
        {
            CanvasEditSession s = Make(out LabelMeDocument doc, out DocumentEditor editor);
            s.Tool = CanvasTool.Count;
            s.PointerDown(-20, 500, 1f, EditModifiers.None);
            LabelShape c = Assert.Single(doc.Shapes);
            Assert.Equal(0.0, c.Points[0][0]);
            Assert.Equal(300.0, c.Points[0][1]);
            Assert.True(doc.Shapes.All(x => x.Points.Count == 2));
        }
    }
}
