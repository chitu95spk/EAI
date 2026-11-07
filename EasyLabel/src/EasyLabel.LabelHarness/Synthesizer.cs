using OpenCvSharp;

namespace EasyLabel.LabelHarness;

/// <summary>One generated training image and its exact labels. Owns <see cref="Image"/>.</summary>
public sealed class SyntheticSample : IDisposable
{
    public SyntheticSample(Mat image, IReadOnlyList<SeedObject> objects)
    {
        Image = image;
        Objects = objects;
    }

    public Mat Image { get; }

    public IReadOnlyList<SeedObject> Objects { get; }

    public void Dispose() => Image.Dispose();
}

/// <summary>
/// Makes many similar labelled images from ONE confirmed reference: the object is cut out with its outline, the hole is filled
/// (inpaint), and the object is pasted back at a random place with random scale, rotation, brightness gain and sensor noise.
/// Labels are exact because they are the same transform applied to the confirmed outline.
/// Background variation: with probability <see cref="Options.BackgroundChance"/> the cleaned reference background is replaced by a
/// different one (user photos from <see cref="Options.BackgroundFolder"/>, a flat colour, a gradient, a blotchy texture, or the
/// mirrored reference with a colour shift) so a model trained on these does not learn "object = this one background". On those
/// images the object edge is feathered, otherwise the model learns the hard paste seam instead of the object.
/// shortcut: one object per image; upgrade to several objects per image when a model trained on these generalises poorly.
/// </summary>
public sealed class Synthesizer
{
    private static readonly string[] BackgroundExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    private enum BackgroundKind
    {
        Folder,
        Solid,
        Gradient,
        Texture,
        Mirror,
    }

    public sealed class Options
    {
        public double MinScale { get; init; } = 0.8;

        public double MaxScale { get; init; } = 1.25;

        public double MaxRotateDeg { get; init; } = 30;

        /// <summary>Brightness gain is 1 +- this.</summary>
        public double GainJitter { get; init; } = 0.15;

        public double NoiseSigma { get; init; } = 2.0;

        /// <summary>0 = always the reference's own cleaned background (default, old behaviour); 1 = always a different background.</summary>
        public double BackgroundChance { get; init; }

        /// <summary>Optional folder of object-free photos (png/jpg/bmp/tif) used as backgrounds. Without it only generated backgrounds are used.</summary>
        public string? BackgroundFolder { get; init; }
    }

    public Synthesizer(Options? options = null)
    {
        Opt = options ?? new Options();
        if (Opt.BackgroundChance < 0 || Opt.BackgroundChance > 1) throw new ArgumentOutOfRangeException(nameof(options), "BackgroundChance must be 0..1.");
        if (Opt.BackgroundFolder != null)
        {
            if (!Directory.Exists(Opt.BackgroundFolder)) throw new ArgumentException("The background folder does not exist: " + Opt.BackgroundFolder);
            _backgroundFiles = Directory.EnumerateFiles(Opt.BackgroundFolder)
                .Where(f => BackgroundExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            _backgroundFiles = Array.Empty<string>();
        }
    }

    public Options Opt { get; }

    private readonly string[] _backgroundFiles;

    /// <summary>Number of background photos found in <see cref="Options.BackgroundFolder"/>.</summary>
    public int BackgroundPhotoCount => _backgroundFiles.Length;

    public IEnumerable<SyntheticSample> Generate(Mat reference, SeedObject seed, int count, int rngSeed)
    {
        LabelGeometry.Validate(seed.Polygon);
        if (reference.Empty() || reference.Type() != MatType.CV_8UC3) throw new ArgumentException("The reference must be a non-empty 8-bit BGR image.");
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

        double[] b = LabelGeometry.Bounds(seed.Polygon);
        int x0 = Math.Max(0, (int)Math.Floor(b[0]));
        int y0 = Math.Max(0, (int)Math.Floor(b[1]));
        int x1 = Math.Min(reference.Width, (int)Math.Ceiling(b[2]) + 1);
        int y1 = Math.Min(reference.Height, (int)Math.Ceiling(b[3]) + 1);
        if (x1 - x0 < 4 || y1 - y0 < 4) throw new ArgumentException("The seed object is smaller than 4 px or outside the reference image.");

        using Mat mask = LabelGeometry.Raster(seed.Polygon, reference.Width, reference.Height);
        using Mat grown = new Mat();
        Cv2.Dilate(mask, grown, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7)));
        using Mat background = new Mat();
        Cv2.Inpaint(reference, grown, background, 3, InpaintTypes.Telea);

        Rect rect = new Rect(x0, y0, x1 - x0, y1 - y0);
        using Mat objectCrop = new Mat(reference, rect).Clone();
        using Mat maskCrop = new Mat(mask, rect).Clone();
        var rng = new Random(rngSeed);

        for (int i = 0; i < count; i++)
        {
            using Mat? varied = rng.NextDouble() < Opt.BackgroundChance ? MakeBackground(background, rng) : null;
            SyntheticSample? sample = TryMakeOne(varied ?? background, varied != null, objectCrop, maskCrop, rect, seed, rng);
            if (sample != null) yield return sample;
        }
    }

    private SyntheticSample? TryMakeOne(Mat background, bool feather, Mat objectCrop, Mat maskCrop, Rect rect, SeedObject seed, Random rng)
    {
        double scale = Lerp(rng, Opt.MinScale, Opt.MaxScale);
        double angle = Lerp(rng, -Opt.MaxRotateDeg, Opt.MaxRotateDeg);
        double gain = 1.0 + Lerp(rng, -Opt.GainJitter, Opt.GainJitter);

        using Mat m = Cv2.GetRotationMatrix2D(new Point2f(objectCrop.Width / 2f, objectCrop.Height / 2f), angle, scale);
        double[] xs = new double[4];
        double[] ys = new double[4];
        int c = 0;
        foreach ((double cx, double cy) in new[] { (0.0, 0.0), (objectCrop.Width, 0.0), (objectCrop.Width, objectCrop.Height), (0.0, objectCrop.Height) })
        {
            xs[c] = m.At<double>(0, 0) * cx + m.At<double>(0, 1) * cy + m.At<double>(0, 2);
            ys[c] = m.At<double>(1, 0) * cx + m.At<double>(1, 1) * cy + m.At<double>(1, 2);
            c++;
        }

        double minX = xs.Min();
        double minY = ys.Min();
        int bw = (int)Math.Ceiling(xs.Max() - minX);
        int bh = (int)Math.Ceiling(ys.Max() - minY);
        if (bw >= background.Width || bh >= background.Height) return null; // too large for this image at this scale/angle: skip, never clip a label
        m.Set(0, 2, m.At<double>(0, 2) - minX);
        m.Set(1, 2, m.At<double>(1, 2) - minY);

        using Mat warped = new Mat();
        using Mat warpedMask = new Mat();
        Cv2.WarpAffine(objectCrop, warped, m, new Size(bw, bh), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        Cv2.WarpAffine(maskCrop, warpedMask, m, new Size(bw, bh), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
        using Mat lit = new Mat();
        warped.ConvertTo(lit, MatType.CV_8UC3, gain);

        int px = rng.Next(0, background.Width - bw + 1);
        int py = rng.Next(0, background.Height - bh + 1);
        Mat image = background.Clone();
        using (Mat roi = new Mat(image, new Rect(px, py, bw, bh)))
        {
            if (feather) BlendFeathered(roi, lit, warpedMask);
            else lit.CopyTo(roi, warpedMask);
        }

        if (Opt.NoiseSigma > 0) AddNoise(image, Opt.NoiseSigma, rng);

        var polygon = seed.Polygon.Select(p =>
        {
            double rx = p[0] - rect.X;
            double ry = p[1] - rect.Y;
            return new[]
            {
                m.At<double>(0, 0) * rx + m.At<double>(0, 1) * ry + m.At<double>(0, 2) + px,
                m.At<double>(1, 0) * rx + m.At<double>(1, 1) * ry + m.At<double>(1, 2) + py,
            };
        }).ToList();

        return new SyntheticSample(image, new[] { new SeedObject(seed.Label, polygon) });
    }

    /// <summary>Alpha-blends <paramref name="lit"/> over <paramref name="roi"/> with a softened mask edge (the label polygon is unchanged).</summary>
    private static void BlendFeathered(Mat roi, Mat lit, Mat objectMask)
    {
        using Mat inner = new Mat();
        Cv2.Erode(objectMask, inner, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3))); // keeps the black warp border out of the blend
        using Mat alpha = new Mat();
        inner.ConvertTo(alpha, MatType.CV_32FC1, 1.0 / 255.0);
        Cv2.GaussianBlur(alpha, alpha, new Size(0, 0), 0.8);
        using Mat alpha3 = new Mat();
        Cv2.Merge(new[] { alpha, alpha, alpha }, alpha3);
        using Mat invAlpha = new Mat();
        alpha.ConvertTo(invAlpha, MatType.CV_32FC1, -1.0, 1.0); // 1 - alpha
        using Mat inv = new Mat();
        Cv2.Merge(new[] { invAlpha, invAlpha, invAlpha }, inv);
        using Mat fg = new Mat();
        using Mat bg = new Mat();
        lit.ConvertTo(fg, MatType.CV_32FC3);
        roi.ConvertTo(bg, MatType.CV_32FC3);
        using Mat blended = new Mat();
        using Mat part = new Mat();
        Cv2.Multiply(fg, alpha3, blended);
        Cv2.Multiply(bg, inv, part);
        Cv2.Add(blended, part, blended);
        blended.ConvertTo(roi, MatType.CV_8UC3);
    }

    /// <summary>A same-size replacement background. Caller disposes it.</summary>
    private Mat MakeBackground(Mat cleaned, Random rng)
    {
        BackgroundKind kind;
        double r = rng.NextDouble();
        if (_backgroundFiles.Length > 0 && r < 0.5) kind = BackgroundKind.Folder;
        else kind = (BackgroundKind)(1 + rng.Next(4)); // Solid..Mirror

        if (kind == BackgroundKind.Folder)
        {
            Mat? photo = FromPhoto(_backgroundFiles[rng.Next(_backgroundFiles.Length)], cleaned.Width, cleaned.Height, rng);
            if (photo != null) return photo;
            kind = BackgroundKind.Texture; // unreadable file: fall back instead of failing the whole batch
        }

        Size size = cleaned.Size();
        switch (kind)
        {
            case BackgroundKind.Solid:
                return new Mat(size, MatType.CV_8UC3, RandomColour(rng));
            case BackgroundKind.Gradient:
                return Gradient(size, RandomColour(rng), RandomColour(rng), Lerp(rng, 0, Math.PI));
            case BackgroundKind.Texture:
                return Texture(size, rng);
            default:
                return Mirror(cleaned, rng);
        }
    }

    // Mostly dull grey/brown-ish tones like real tables and screens, sometimes any colour.
    private static Scalar RandomColour(Random rng)
    {
        if (rng.NextDouble() < 0.6)
        {
            double g = Lerp(rng, 20, 235);
            return new Scalar(Clamp(g + Lerp(rng, -18, 18)), Clamp(g + Lerp(rng, -18, 18)), Clamp(g + Lerp(rng, -18, 18)));
        }

        return new Scalar(rng.Next(256), rng.Next(256), rng.Next(256));
    }

    private static Mat Gradient(Size size, Scalar a, Scalar b, double angle)
    {
        const int N = 32;
        using Mat small = new Mat(N, N, MatType.CV_8UC3);
        double ca = Math.Cos(angle);
        double sa = Math.Sin(angle);
        double span = Math.Abs(ca) + Math.Abs(sa);
        for (int y = 0; y < N; y++)
        {
            for (int x = 0; x < N; x++)
            {
                double t = (((x + 0.5) / N - 0.5) * ca + ((y + 0.5) / N - 0.5) * sa) / span + 0.5;
                small.Set(y, x, new Vec3b((byte)Clamp(a.Val0 + (b.Val0 - a.Val0) * t), (byte)Clamp(a.Val1 + (b.Val1 - a.Val1) * t), (byte)Clamp(a.Val2 + (b.Val2 - a.Val2) * t)));
            }
        }

        var big = new Mat();
        Cv2.Resize(small, big, size, 0, 0, InterpolationFlags.Linear);
        return big;
    }

    // Random low-resolution colour cells smoothed up to full size: blotchy, surface-like, no edges that look like objects.
    private static Mat Texture(Size size, Random rng)
    {
        int cells = rng.Next(4, 24);
        Scalar baseColour = RandomColour(rng);
        double spread = Lerp(rng, 10, 70);
        using Mat small = new Mat(cells, cells, MatType.CV_8UC3);
        for (int y = 0; y < cells; y++)
        {
            for (int x = 0; x < cells; x++)
            {
                double d = Lerp(rng, -spread, spread); // one shared offset keeps it tonal; per-channel tint stays small
                small.Set(y, x, new Vec3b((byte)Clamp(baseColour.Val0 + d + Lerp(rng, -8, 8)), (byte)Clamp(baseColour.Val1 + d + Lerp(rng, -8, 8)), (byte)Clamp(baseColour.Val2 + d + Lerp(rng, -8, 8))));
            }
        }

        var big = new Mat();
        Cv2.Resize(small, big, size, 0, 0, InterpolationFlags.Cubic);
        return big;
    }

    private static Mat Mirror(Mat cleaned, Random rng)
    {
        using Mat flipped = new Mat();
        Cv2.Flip(cleaned, flipped, (FlipMode)rng.Next(-1, 2)); // -1 both, 0 vertical, 1 horizontal
        var shifted = new Mat();
        flipped.ConvertTo(shifted, MatType.CV_8UC3, Lerp(rng, 0.6, 1.4), Lerp(rng, -50, 50));
        return shifted;
    }

    private static Mat? FromPhoto(string path, int width, int height, Random rng)
    {
        using Mat photo = Cv2.ImRead(path, ImreadModes.Color);
        if (photo.Empty() || photo.Width < 8 || photo.Height < 8) return null;

        double aspect = (double)width / height;
        double cw = photo.Width;
        double ch = cw / aspect;
        if (ch > photo.Height)
        {
            ch = photo.Height;
            cw = ch * aspect;
        }

        double f = Lerp(rng, 0.4, 1.0);
        int w = Math.Max(8, (int)Math.Round(cw * f));
        int h = Math.Max(8, (int)Math.Round(ch * f));
        w = Math.Min(w, photo.Width);
        h = Math.Min(h, photo.Height);
        var crop = new Rect(rng.Next(0, photo.Width - w + 1), rng.Next(0, photo.Height - h + 1), w, h);
        using Mat piece = new Mat(photo, crop);
        var result = new Mat();
        Cv2.Resize(piece, result, new Size(width, height), 0, 0, InterpolationFlags.Area);
        if (rng.NextDouble() < 0.5) Cv2.Flip(result, result, FlipMode.Y);
        return result;
    }

    private static void AddNoise(Mat image, double sigma, Random rng)
    {
        using Mat f = new Mat();
        image.ConvertTo(f, MatType.CV_32FC3);
        using Mat noise = new Mat(image.Size(), MatType.CV_32FC3);
        // Own Gaussian (Box-Muller) so one rngSeed reproduces the samples; OpenCV's RNG is process-global.
        float[] values = new float[image.Rows * image.Cols * 3];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (float)(sigma * Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble()));
        }

        System.Runtime.InteropServices.Marshal.Copy(values, 0, noise.Data, values.Length);
        Cv2.Add(f, noise, f);
        f.ConvertTo(image, MatType.CV_8UC3);
    }

    private static double Clamp(double v) => v < 0 ? 0 : v > 255 ? 255 : v;

    private static double Lerp(Random rng, double lo, double hi) => lo + (hi - lo) * rng.NextDouble();
}
