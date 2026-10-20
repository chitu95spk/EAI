using EasyLabel.Core.Io;

namespace EasyLabel.Core.Tests
{
    public sealed class ShapeFingerprintTests
    {
        private static readonly List<double[]> Triangle = new List<double[]> { new[] { 1.0, 2.0 }, new[] { 30.5, 2.0 }, new[] { 30.5, 40.25 } };

        [Fact]
        public void FingerprintIs16LowerCaseHexCharactersAndDeterministic()
        {
            string a = ShapeFingerprint.Compute("OK", "polygon", Triangle);
            string b = ShapeFingerprint.Compute("OK", "polygon", Triangle);

            Assert.Equal(a, b);
            Assert.Equal(ShapeFingerprint.Length, a.Length);
            Assert.Matches("^[0-9a-f]{16}$", a);
        }

        [Fact]
        public void PointsAreRoundedToTwoDecimalsAndNegativeZeroEqualsZero()
        {
            string exact = ShapeFingerprint.Compute("OK", "polygon", new List<double[]> { new[] { 1.0, 0.0 }, new[] { 2.0, 3.0 } });
            string noisy = ShapeFingerprint.Compute("OK", "polygon", new List<double[]> { new[] { 1.0004, -0.0004 }, new[] { 1.9996, 3.0003 } });

            Assert.Equal(exact, noisy);
        }

        [Fact]
        public void LabelTypeAndPointsEachChangeTheFingerprint()
        {
            string baseline = ShapeFingerprint.Compute("OK", "polygon", Triangle);

            Assert.NotEqual(baseline, ShapeFingerprint.Compute("NG", "polygon", Triangle));
            Assert.NotEqual(baseline, ShapeFingerprint.Compute("OK", "rectangle", Triangle));
            Assert.NotEqual(baseline, ShapeFingerprint.Compute("OK", "polygon", new List<double[]> { new[] { 1.0, 2.0 }, new[] { 30.5, 2.0 }, new[] { 30.5, 40.5 } }));
            Assert.NotEqual(baseline, ShapeFingerprint.Compute("OK", "polygon", Triangle.Take(2)));
        }

        [Fact]
        public void LabelIsTrimmedAndTypeIsCaseInsensitiveButLabelIsNot()
        {
            string baseline = ShapeFingerprint.Compute("OK", "polygon", Triangle);

            Assert.Equal(baseline, ShapeFingerprint.Compute("  OK ", " Polygon", Triangle));
            Assert.NotEqual(baseline, ShapeFingerprint.Compute("ok", "polygon", Triangle));
        }

        [Fact]
        public void NullInputsCountAsEmpty()
        {
            Assert.Equal(ShapeFingerprint.Compute(string.Empty, string.Empty, new List<double[]>()), ShapeFingerprint.Compute(null, null, null));
        }
    }
}
