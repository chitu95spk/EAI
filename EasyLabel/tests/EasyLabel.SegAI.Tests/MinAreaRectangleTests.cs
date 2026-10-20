using EasyLabel.Core.Convert;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

/// <summary>Cross-check of the pure C# minimum-area rectangle against <c>cv2.minAreaRect</c> + <c>boxPoints</c> (OpenCvSharp).</summary>
public sealed class MinAreaRectangleTests
{
    private static IReadOnlyList<(double X, double Y)> Pts(params double[] xy)
    {
        List<(double X, double Y)> list = new List<(double X, double Y)>();
        for (int i = 0; i < xy.Length; i += 2)
        {
            list.Add((xy[i], xy[i + 1]));
        }

        return list;
    }

    [Fact]
    public void AxisAlignedRectangleKeepsClockwiseOrderFromTopLeft()
    {
        double[]? corners = MinAreaRectangle.Compute(Pts(0, 0, 4, 0, 4, 2, 0, 2, 2, 1), out double area);

        Assert.NotNull(corners);
        Assert.Equal(8d, area, 9);
        double[] expected = { 0, 0, 4, 0, 4, 2, 0, 2 };
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(expected[i], corners![i], 9);
        }
    }

    [Fact]
    public void DegenerateInputsGiveZeroAreaRectangles()
    {
        double[]? single = MinAreaRectangle.Compute(Pts(3, 4, 3, 4, 3, 4), out double singleArea);
        Assert.NotNull(single);
        Assert.Equal(0d, singleArea);
        Assert.Equal(new double[] { 3, 4, 3, 4, 3, 4, 3, 4 }, single);

        double[]? line = MinAreaRectangle.Compute(Pts(0, 0, 2, 2, 4, 4), out double lineArea);
        Assert.NotNull(line);
        Assert.Equal(0d, lineArea, 9);

        Assert.Null(MinAreaRectangle.Compute(Pts(), out _));
    }

    [Fact]
    public void MatchesOpenCvMinAreaRectOnRandomPolygons()
    {
        Random random = new Random(20261008);
        for (int round = 0; round < 150; round++)
        {
            int count = random.Next(3, 13);
            List<(double X, double Y)> points = new List<(double X, double Y)>();
            Point2f[] cvPoints = new Point2f[count];
            for (int i = 0; i < count; i++)
            {
                int x = random.Next(0, 1000);
                int y = random.Next(0, 1000);
                points.Add((x, y));
                cvPoints[i] = new Point2f(x, y);
            }

            RotatedRect expected = Cv2.MinAreaRect(cvPoints);
            double expectedArea = (double)expected.Size.Width * expected.Size.Height;
            if (expectedArea < 1d)
            {
                // Collinear or nearly so: corner sets are not unique.
                continue;
            }

            double[]? corners = MinAreaRectangle.Compute(points, out double area);

            Assert.NotNull(corners);
            Assert.True(Math.Abs(area - expectedArea) <= 1e-3 * expectedArea, "round " + round + ": area " + area + " vs " + expectedArea);

            Point2f[] expectedCorners = expected.Points();
            bool[] used = new bool[4];
            bool sameCorners = true;
            for (int c = 0; c < 4 && sameCorners; c++)
            {
                int match = -1;
                for (int e = 0; e < 4; e++)
                {
                    if (!used[e] && Math.Abs(corners![c * 2] - expectedCorners[e].X) < 0.2 && Math.Abs(corners[(c * 2) + 1] - expectedCorners[e].Y) < 0.2)
                    {
                        match = e;
                        break;
                    }
                }

                if (match < 0) sameCorners = false;
                else used[match] = true;
            }

            if (!sameCorners)
            {
                // Equal-area ties (every triangle has three minimum rectangles of the same area): the corner set is not unique,
                // so require instead that our rectangle encloses every point.
                Assert.True(Encloses(corners!, points), "round " + round + ": the corners differ from OpenCV and do not enclose all points");
            }
        }
    }

    private static bool Encloses(double[] corners, List<(double X, double Y)> points)
    {
        for (int i = 0; i < 4; i++)
        {
            double ax = corners[i * 2], ay = corners[(i * 2) + 1];
            double bx = corners[((i + 1) % 4) * 2], by = corners[(((i + 1) % 4) * 2) + 1];
            double ex = bx - ax, ey = by - ay;
            double length = Math.Sqrt((ex * ex) + (ey * ey));
            if (length < 1e-9) continue;
            double side = 0;
            // The rectangle's interior lies on one side of every edge: use the centre to find which.
            double cx = (corners[0] + corners[2] + corners[4] + corners[6]) / 4, cy = (corners[1] + corners[3] + corners[5] + corners[7]) / 4;
            double centreSide = ((ex * (cy - ay)) - (ey * (cx - ax))) / length;
            foreach ((double X, double Y) p in points)
            {
                side = ((ex * (p.Y - ay)) - (ey * (p.X - ax))) / length;
                if (side * centreSide < -1e-6) return false;
            }
        }

        return true;
    }
}
