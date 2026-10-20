namespace EasyLabel.SegAI.Tests
{
    public sealed class AgentChatWindowBoundsTests
    {
        private static readonly EasyAI.Desktop.Infrastructure.ChatRect Full = new EasyAI.Desktop.Infrastructure.ChatRect(0, 0, 1920, 1040);

        private static EasyAI.Desktop.Infrastructure.ChatRect Button(double left, double top)
        {
            return new EasyAI.Desktop.Infrastructure.ChatRect(left, top, 44, 44);
        }

        [Xunit.Fact]
        public void TheWindowOpensRightOfTheButtonWithTheBottomEdgesAligned()
        {
            EasyAI.Desktop.Infrastructure.ChatRect button = Button(10, 900);
            EasyAI.Desktop.Infrastructure.ChatRect r = EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.Anchor(button, 400, 520, Full);
            Xunit.Assert.Equal(button.Right + 8, r.Left);
            Xunit.Assert.Equal(button.Bottom, r.Bottom);
            Xunit.Assert.Equal(400, r.Width);
            Xunit.Assert.Equal(520, r.Height);
        }

        [Xunit.Fact]
        public void ABottomEdgeBelowTheWorkAreaIsPulledUp()
        {
            EasyAI.Desktop.Infrastructure.ChatRect r = EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.Anchor(Button(10, 1020), 400, 520, Full);
            Xunit.Assert.Equal(10 + 44 + 8, r.Left);
            Xunit.Assert.Equal(1040, r.Bottom);
        }

        [Xunit.Fact]
        public void AButtonNearTheTopPushesTheWindowDown()
        {
            EasyAI.Desktop.Infrastructure.ChatRect r = EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.Anchor(Button(10, 100), 400, 520, Full);
            Xunit.Assert.Equal(0, r.Top);
            Xunit.Assert.Equal(520, r.Bottom);
        }

        [Xunit.Fact]
        public void AButtonNearTheRightEdgePullsTheWindowLeft()
        {
            EasyAI.Desktop.Infrastructure.ChatRect r = EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.Anchor(Button(1850, 900), 400, 520, Full);
            Xunit.Assert.Equal(1920 - 400, r.Left);
            Xunit.Assert.Equal(1920, r.Right);
        }

        [Xunit.Fact]
        public void AButtonLeftOfTheWorkAreaPushesTheWindowRight()
        {
            EasyAI.Desktop.Infrastructure.ChatRect work = new EasyAI.Desktop.Infrastructure.ChatRect(100, 0, 1000, 800);
            EasyAI.Desktop.Infrastructure.ChatRect r = EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.Anchor(Button(-200, 700), 400, 520, work);
            Xunit.Assert.Equal(100, r.Left);
        }

        [Xunit.Fact]
        public void AWorkAreaSmallerThanTheWindowShrinksItAndKeepsTheTopLeftReachable()
        {
            // 900x600 pixels at 150% is a 600x400 DIP area.
            EasyAI.Desktop.Infrastructure.ChatRect small = new EasyAI.Desktop.Infrastructure.ChatRect(0, 0, 600, 400);
            EasyAI.Desktop.Infrastructure.ChatRect r = EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.Anchor(Button(10, 380), 400, 520, small);
            Xunit.Assert.Equal(400, r.Width);
            Xunit.Assert.Equal(400, r.Height);
            Xunit.Assert.True(r.Left >= 0 && r.Right <= 600);
            Xunit.Assert.Equal(0, r.Top);
            Xunit.Assert.Equal(400, r.Bottom);

            EasyAI.Desktop.Infrastructure.ChatRect tiny = new EasyAI.Desktop.Infrastructure.ChatRect(0, 0, 300, 300);
            EasyAI.Desktop.Infrastructure.ChatRect t = EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.Anchor(Button(10, 250), 400, 520, tiny);
            Xunit.Assert.Equal(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.MinWidth, t.Width);
            Xunit.Assert.Equal(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.MinHeight, t.Height);
            Xunit.Assert.Equal(0, t.Left);
            Xunit.Assert.Equal(0, t.Top);
        }

        [Xunit.Fact]
        public void ASecondMonitorWithANegativeOriginIsAValidWorkArea()
        {
            EasyAI.Desktop.Infrastructure.ChatRect left = new EasyAI.Desktop.Infrastructure.ChatRect(-1920, 0, 1920, 1040);
            EasyAI.Desktop.Infrastructure.ChatRect r = EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.Anchor(Button(-1900, 800), 400, 520, left);
            Xunit.Assert.Equal(-1900 + 44 + 8, r.Left);
            Xunit.Assert.Equal(844, r.Bottom);

            EasyAI.Desktop.Infrastructure.ChatRect edge = EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.Anchor(Button(-60, 800), 400, 520, left);
            Xunit.Assert.Equal(0, edge.Right);
            Xunit.Assert.Equal(-400, edge.Left);
        }

        [Xunit.Fact]
        public void AnInvalidWorkAreaStillGivesTheAnchoredRectangle()
        {
            EasyAI.Desktop.Infrastructure.ChatRect button = Button(10, 900);
            EasyAI.Desktop.Infrastructure.ChatRect r = EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.Anchor(
                button, 400, 520, new EasyAI.Desktop.Infrastructure.ChatRect(0, 0, 0, 0));
            Xunit.Assert.Equal(button.Right + 8, r.Left);
            Xunit.Assert.Equal(button.Bottom, r.Bottom);
        }

        [Xunit.Fact]
        public void BadSizesBecomeTheDefaultOrTheLimits()
        {
            Xunit.Assert.Equal(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.DefaultWidth, EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.ClampWidth(double.NaN));
            Xunit.Assert.Equal(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.DefaultHeight, EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.ClampHeight(-5));
            Xunit.Assert.Equal(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.MinWidth, EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.ClampWidth(10));
            Xunit.Assert.Equal(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.MaxSize, EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.ClampHeight(1e9));
            Xunit.Assert.Null(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.SanitizeCoordinate(double.NaN));
            Xunit.Assert.Null(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.SanitizeCoordinate(double.PositiveInfinity));
            Xunit.Assert.Null(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.SanitizeCoordinate(null));
            Xunit.Assert.Equal(-40.0, EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.SanitizeCoordinate(-40));
        }

        [Xunit.Fact]
        public void TheSettingsReadBackClampedValues()
        {
            EasyAI.Desktop.Infrastructure.AppSettings settings = new EasyAI.Desktop.Infrastructure.AppSettings();
            Xunit.Assert.Null(settings.AgentChatLeft);
            Xunit.Assert.Null(settings.AgentChatTop);
            Xunit.Assert.Equal(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.DefaultWidth, settings.AgentChatWidth);
            Xunit.Assert.Equal(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.DefaultHeight, settings.AgentChatHeight);

            settings.AgentChatWidth = 12;
            settings.AgentChatHeight = double.NaN;
            settings.AgentChatLeft = double.NaN;
            settings.AgentChatTop = 250;
            Xunit.Assert.Equal(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.MinWidth, settings.AgentChatWidth);
            Xunit.Assert.Equal(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.DefaultHeight, settings.AgentChatHeight);
            Xunit.Assert.Null(settings.AgentChatLeft);
            Xunit.Assert.Equal(250.0, settings.AgentChatTop);

            settings.AgentChatWidth = 99999;
            Xunit.Assert.Equal(EasyAI.Desktop.Infrastructure.AgentChatWindowBounds.MaxSize, settings.AgentChatWidth);
        }
    }
}
