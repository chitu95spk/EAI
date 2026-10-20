using EasyLabel.Ai.Onnx;
using EasyLabel.Core.Editing;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// Tightens the oriented_rectangle proposals of a finished agent run with a local SAM2 (no LLM, no cost): the axis-aligned box of the
/// proposal is the SAM2 prompt, the best mask is polygonised by the segmenter and the minimum-area rotated rectangle of that outline
/// replaces the four corners. Everything else (label, score, note, other shape types) is kept; a proposal also stays as it is when SAM2 is
/// missing, the call fails, the mask is empty or the new box is degenerate. SAM2 calls are serialised, so several batch workers share one backend.
/// </summary>
public sealed class AgentSam2Refiner : IDisposable
{
    /// <summary>The refined box may not be smaller than this share of the proposal's area, nor larger than <see cref="MaxAreaRatio"/> times it.</summary>
    public const double MinAreaRatio = 0.5;

    public const double MaxAreaRatio = 1.8;

    /// <summary>Outline of the best SAM2 mask for a box prompt (x1, y1, x2, y2 in image pixels); null or fewer than 3 points = no mask.</summary>
    public delegate Task<IReadOnlyList<double[]>?> SegmentBox(AgentRunInput input, double[] box, CancellationToken ct);

    /// <summary>Outline of the best SAM2 mask for a <see cref="RefinePrompt"/>; null or fewer than 3 points = no mask.</summary>
    public delegate Task<IReadOnlyList<double[]>?> SegmentPrompt(AgentRunInput input, RefinePrompt prompt, CancellationToken ct);

    /// <summary>What one SAM2 call gets: the box (x1, y1, x2, y2) plus optional positive and negative click points (x, y), all inside the image.</summary>
    public sealed record RefinePrompt(double[] Box, IReadOnlyList<double[]> Positive, IReadOnlyList<double[]> Negative);

    /// <summary>How the SAM2 prompt of one proposal is built from its rotated box.</summary>
    public enum PromptMode
    {
        /// <summary>The axis-aligned bounds of the rotated box (about 1.4 times the box area at 45 degrees).</summary>
        Bounds,

        /// <summary>Bounds plus a positive click on the box centre.</summary>
        BoundsCentre,

        /// <summary>Bounds, a positive centre click, and a negative click in every bounds corner that lies clearly outside the rotated box (neighbours).</summary>
        BoundsCentreNegatives,

        /// <summary>The bounds of the box shrunk to <see cref="RefineOptions.ShrinkScale"/> about its centre, plus a positive centre click.</summary>
        ShrunkCentre,
    }

    /// <summary>Tuning of the refinement; the defaults are the measured best (see the EAI-LABEL box-fit evidence).</summary>
    public sealed class RefineOptions
    {
        public PromptMode Prompt { get; init; } = PromptMode.Bounds;

        /// <summary>Scale of the prompt box for <see cref="PromptMode.ShrunkCentre"/>.</summary>
        public double ShrinkScale { get; init; } = 0.85;

        /// <summary>The SAM2 outline is first clipped to the agent box scaled by this factor about its centre (a mask that bled into a neighbour or stretched along a tip is cut); 0 = no clip.</summary>
        public double ClipScale { get; init; }

        /// <summary>The refined box may not be smaller than this share of the proposal's area.</summary>
        public double MinAreaRatio { get; init; } = AgentSam2Refiner.MinAreaRatio;

        /// <summary>The refined box may not be larger than this many times the proposal's area.</summary>
        public double MaxAreaRatio { get; init; } = AgentSam2Refiner.MaxAreaRatio;
    }

    private readonly SegmentPrompt _segment;
    private readonly IDisposable? _owner;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private int _calls;

    public AgentSam2Refiner(SegmentBox segment, IDisposable? owner = null)
        : this(WrapBox(segment ?? throw new ArgumentNullException(nameof(segment))), owner, null)
    {
    }

    public AgentSam2Refiner(SegmentPrompt segment, IDisposable? owner, RefineOptions? options)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        _owner = owner;
        Options = options ?? new RefineOptions();
    }

    public RefineOptions Options { get; }

    private static SegmentPrompt WrapBox(SegmentBox segment) => (input, prompt, ct) => segment(input, prompt.Box, ct);

    /// <summary>How many SAM2 prompts were sent so far.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>A refiner on the SAM2 pair in <paramref name="sam2Folder"/>; the models load lazily on the first proposal.</summary>
    /// <param name="loadImage">Decodes an image file to the BGR layout the backend takes (the Label step passes its AiImageLoader; kept out of this file so the tests link it without WPF).</param>
    public static AgentSam2Refiner CreateLocal(string sam2Folder, Func<string, CancellationToken, AiImage> loadImage, RefineOptions? options = null)
    {
        Sam2Segmenter segmenter = new Sam2Segmenter(sam2Folder, loadImage);
        return new AgentSam2Refiner(segmenter.SegmentAsync, segmenter, options);
    }

    /// <summary>The run delegate with the refinement after a successful run; cancel or any refine failure returns the result unrefined.</summary>
    public Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>> Wrap(
        Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return async (input, ct, progress) =>
        {
            AgentRunResult result = await run(input, ct, progress).ConfigureAwait(false);
            if (result.Kind != AgentOutcomeKind.Success || ct.IsCancellationRequested)
            {
                return result;
            }

            return await RefineAsync(input, result, ct).ConfigureAwait(false);
        };
    }

    public async Task<AgentRunResult> RefineAsync(AgentRunInput input, AgentRunResult result, CancellationToken ct)
    {
        if (result.Proposals.Count == 0)
        {
            return result;
        }

        List<AgentProposal> refined = new List<AgentProposal>(result.Proposals.Count);
        bool changed = false;
        // The gate is held once per image, so the decoded image and its embedding are reused for every shape and workers never interleave images.
        bool gateHeld = false;
        try
        {
            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                gateHeld = true;
            }
            catch (OperationCanceledException)
            {
                // Cancelled while waiting: the paid agent result is returned unrefined.
                return result;
            }

            bool abandonImage = false;
            foreach (AgentProposal proposal in result.Proposals)
            {
                AgentProposal next = proposal;
                if (!abandonImage && proposal.Source != "sam2" && proposal.ShapeType == EasyAI.Desktop.ViewModels.AiSuggestionMapper.OrientedRectangleShape && proposal.Points.Count == 4 && !ct.IsCancellationRequested)
                {
                    RefineOutcome outcome = await TryRefineAsync(input, proposal.Points, ct).ConfigureAwait(false);
                    if (outcome.Corners != null)
                    {
                        next = new AgentProposal(
                            proposal.Label, proposal.ShapeType, ToPoints(outcome.Corners), proposal.Holes, proposal.Score, proposal.Note,
                            proposal.Action, proposal.TargetIndex, proposal.TargetHash);
                        changed = true;
                    }

                    // A failed first call (models missing, NaN embedding, cancel) would fail again for the rest of this image.
                    if (outcome.Failed && !changed)
                    {
                        abandonImage = true;
                    }
                }

                refined.Add(next);
            }
        }
        finally
        {
            if (gateHeld)
            {
                _gate.Release();
            }
        }

        return changed ? result with { Proposals = refined } : result;
    }

    private readonly struct RefineOutcome
    {
        public RefineOutcome(double[]? corners, bool failed)
        {
            Corners = corners;
            Failed = failed;
        }

        public double[]? Corners { get; }

        /// <summary>The segmenter threw (as opposed to returning no usable mask).</summary>
        public bool Failed { get; }
    }

    private async Task<RefineOutcome> TryRefineAsync(AgentRunInput input, IReadOnlyList<double[]> points, CancellationToken ct)
    {
        if (input.ImageWidth <= 0 || input.ImageHeight <= 0 || points.Any(p => p == null || p.Length < 2))
        {
            return new RefineOutcome(null, false);
        }

        // A box that leaves the image (an object cut by the border): the visible outline cannot tell where the hidden part ends and pinning a
        // cut corner to the border distorts the box, so the agent's own box is kept.
        if (input.AllowOutOfBounds && LeavesImage(points, input.ImageWidth, input.ImageHeight))
        {
            return new RefineOutcome(null, false);
        }

        RefinePrompt? prompt = BuildPrompt(points, input.ImageWidth, input.ImageHeight, Options);
        if (prompt == null)
        {
            return new RefineOutcome(null, false);
        }

        IReadOnlyList<double[]>? outline;
        try
        {
            Interlocked.Increment(ref _calls);
            outline = await _segment(input, prompt, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled, models missing, NaN embedding, ...: the agent's own box stays.
            return new RefineOutcome(null, true);
        }

        return new RefineOutcome(outline == null ? null : FitBox(points, outline, input.ImageWidth, input.ImageHeight, Options, !input.AllowOutOfBounds), false);
    }

    /// <summary>True when a corner lies more than 1 pixel outside the image.</summary>
    public static bool LeavesImage(IReadOnlyList<double[]> corners, int width, int height)
    {
        return corners.Any(p => p[0] < -1 || p[1] < -1 || p[0] > width + 1 || p[1] > height + 1);
    }

    /// <summary>The SAM2 prompt of a rotated box (corners inside the image, up to 1 pixel tolerance), or null when the bounds are under 1 pixel.</summary>
    public static RefinePrompt? BuildPrompt(IReadOnlyList<double[]> corners, int width, int height, RefineOptions options)
    {
        double cx = corners.Average(p => p[0]), cy = corners.Average(p => p[1]);
        double scale = options.Prompt == PromptMode.ShrunkCentre ? options.ShrinkScale : 1.0;
        double[] box =
        {
            Math.Max(0, cx + scale * (corners.Min(p => p[0]) - cx)), Math.Max(0, cy + scale * (corners.Min(p => p[1]) - cy)),
            Math.Min(width, cx + scale * (corners.Max(p => p[0]) - cx)), Math.Min(height, cy + scale * (corners.Max(p => p[1]) - cy)),
        };
        if (box[2] - box[0] < 1 || box[3] - box[1] < 1)
        {
            return null;
        }

        List<double[]> positive = new List<double[]>();
        List<double[]> negative = new List<double[]>();
        if (options.Prompt != PromptMode.Bounds)
        {
            positive.Add(new[] { Math.Min(width - 1.0, Math.Max(0, cx)), Math.Min(height - 1.0, Math.Max(0, cy)) });
        }

        if (options.Prompt == PromptMode.BoundsCentreNegatives)
        {
            double shortSide = Math.Min(Distance(corners[0], corners[1]), Distance(corners[1], corners[2]));
            foreach (double[] corner in new[]
            {
                new[] { box[0], box[1] }, new[] { box[2], box[1] }, new[] { box[2], box[3] }, new[] { box[0], box[3] },
            })
            {
                if (!Inside(corners, corner) && DistanceToPolygon(corners, corner) >= 0.08 * shortSide)
                {
                    negative.Add(new[] { Math.Min(width - 1.0, Math.Max(0, corner[0])), Math.Min(height - 1.0, Math.Max(0, corner[1])) });
                }
            }
        }

        return new RefinePrompt(box, positive, negative);
    }

    private static double Distance(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));

    private static bool Inside(IReadOnlyList<double[]> ring, double[] p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if ((ring[i][1] > p[1]) != (ring[j][1] > p[1])
                && p[0] < (ring[j][0] - ring[i][0]) * (p[1] - ring[i][1]) / (ring[j][1] - ring[i][1]) + ring[i][0])
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double DistanceToPolygon(IReadOnlyList<double[]> ring, double[] p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
        {
            double[] a = ring[i];
            double[] b = ring[(i + 1) % ring.Count];
            double dx = b[0] - a[0], dy = b[1] - a[1];
            double len2 = dx * dx + dy * dy;
            double t = len2 <= 0 ? 0 : Math.Max(0, Math.Min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / len2));
            best = Math.Min(best, Distance(p, new[] { a[0] + t * dx, a[1] + t * dy }));
        }

        return best;
    }

    /// <summary>
    /// The minimum-area rotated rectangle of <paramref name="outline"/> as the flat corner list [x0,y0,...,x3,y3] (clamped to the image when
    /// <paramref name="clamp"/>), or null when the outline is too small or the rectangle's area is outside the ratios of
    /// <paramref name="options"/> (default 50% to 1.8 times) of the <paramref name="original"/> area. With <see cref="RefineOptions.ClipScale"/> the
    /// outline is first clipped to the original box scaled about its centre.
    /// </summary>
    public static double[]? FitBox(IReadOnlyList<double[]> original, IReadOnlyList<double[]> outline, int width, int height, RefineOptions? options = null, bool clamp = true)
    {
        options ??= new RefineOptions();
        if (outline.Count < 3)
        {
            return null;
        }

        IReadOnlyList<double[]> ring = outline;
        if (options.ClipScale > 0 && original.Count == 4)
        {
            double cx = original.Average(p => p[0]), cy = original.Average(p => p[1]);
            List<double[]> grown = original.Select(p => new[] { cx + options.ClipScale * (p[0] - cx), cy + options.ClipScale * (p[1] - cy) }).ToList();
            ring = ClipConvex(outline, grown);
            if (ring.Count < 3)
            {
                return null;
            }
        }

        List<double[]> clamped = ring.Select(p => new[] { Math.Min(width, Math.Max(0, p[0])), Math.Min(height, Math.Max(0, p[1])) }).ToList();
        List<double[]>? fitted = ShapeConverter.FitOrientedRectangle(clamped);
        if (fitted == null || fitted.Count != 4)
        {
            return null;
        }

        double oldArea = Area(original);
        double newArea = Area(fitted);
        if (oldArea <= 0 || newArea < oldArea * options.MinAreaRatio || newArea > oldArea * options.MaxAreaRatio)
        {
            return null;
        }

        double[] flat = new double[8];
        for (int i = 0; i < 4; i++)
        {
            flat[2 * i] = clamp ? Math.Min(width, Math.Max(0, fitted[i][0])) : fitted[i][0];
            flat[2 * i + 1] = clamp ? Math.Min(height, Math.Max(0, fitted[i][1])) : fitted[i][1];
        }

        return flat.All(double.IsFinite) ? flat : null;
    }

    /// <summary>The part of <paramref name="subject"/> inside the convex polygon <paramref name="clip"/> (Sutherland-Hodgman); fewer than 3 points when nothing is left.</summary>
    public static List<double[]> ClipConvex(IReadOnlyList<double[]> subject, IReadOnlyList<double[]> clip)
    {
        List<double[]> output = subject.Select(p => new[] { p[0], p[1] }).ToList();
        double sign = SignedArea(clip) >= 0 ? 1 : -1;
        for (int i = 0; i < clip.Count && output.Count > 0; i++)
        {
            double[] a = clip[i];
            double[] b = clip[(i + 1) % clip.Count];
            List<double[]> input = output;
            output = new List<double[]>();
            for (int j = 0; j < input.Count; j++)
            {
                double[] cur = input[j];
                double[] prev = input[(j + input.Count - 1) % input.Count];
                double sc = sign * Side(a, b, cur), sp = sign * Side(a, b, prev);
                if ((sc >= 0) != (sp >= 0))
                {
                    double t = sp / (sp - sc);
                    output.Add(new[] { prev[0] + t * (cur[0] - prev[0]), prev[1] + t * (cur[1] - prev[1]) });
                }

                if (sc >= 0)
                {
                    output.Add(cur);
                }
            }
        }

        return output;
    }

    private static double Side(double[] a, double[] b, double[] p) => (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0]);

    private static double SignedArea(IReadOnlyList<double[]> ring)
    {
        double sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            double[] a = ring[i];
            double[] b = ring[(i + 1) % ring.Count];
            sum += a[0] * b[1] - b[0] * a[1];
        }

        return sum / 2.0;
    }

    private static IReadOnlyList<double[]> ToPoints(double[] flat)
    {
        List<double[]> points = new List<double[]>(4);
        for (int i = 0; i < 4; i++)
        {
            points.Add(new[] { flat[2 * i], flat[2 * i + 1] });
        }

        return points;
    }

    private static double Area(IReadOnlyList<double[]> ring)
    {
        double sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            double[] a = ring[i];
            double[] b = ring[(i + 1) % ring.Count];
            sum += a[0] * b[1] - b[0] * a[1];
        }

        return Math.Abs(sum) / 2.0;
    }

    public void Dispose()
    {
        // A cancelled call may still be inside the segmenter: give it a moment to leave before the models are released.
        _gate.Wait(TimeSpan.FromSeconds(5));
        _owner?.Dispose();
    }

    /// <summary>The production segmenter: one shared <see cref="Sam2Backend"/> (loaded on first use) and the last decoded image.</summary>
    private sealed class Sam2Segmenter : IDisposable
    {
        private readonly string _folder;
        private Sam2Backend? _backend;
        private AiImage? _image;
        private string? _imagePath;

        private readonly Func<string, CancellationToken, AiImage> _loadImage;

        public Sam2Segmenter(string folder, Func<string, CancellationToken, AiImage> loadImage)
        {
            _folder = folder;
            _loadImage = loadImage;
        }

        public async Task<IReadOnlyList<double[]>?> SegmentAsync(AgentRunInput input, RefinePrompt refinePrompt, CancellationToken ct)
        {
            // Called under the refiner's gate, so the fields need no lock.
            if (_backend == null)
            {
                AiModelResolution files = AiModelFiles.Resolve(AiModelFiles.Sam2Id, _folder);
                _backend = await Task.Run(() => Sam2Backend.Load(files), CancellationToken.None).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            if (_image == null || !string.Equals(_imagePath, input.ImagePath, StringComparison.OrdinalIgnoreCase))
            {
                string path = input.ImagePath;
                _image = await Task.Run(() => _loadImage(path, ct), ct).ConfigureAwait(false);
                _imagePath = path;
            }

            double[] box = refinePrompt.Box;
            List<AiPoint> clicks = new List<AiPoint>();
            clicks.AddRange(refinePrompt.Positive.Select(p => new AiPoint((float)p[0], (float)p[1], true)));
            clicks.AddRange(refinePrompt.Negative.Select(p => new AiPoint((float)p[0], (float)p[1], false)));
            AiPrompt prompt = new AiPrompt(clicks, new AiBox((float)box[0], (float)box[1], (float)box[2], (float)box[3]));
            IReadOnlyList<AiProposal> found = await _backend.RunAsync(_image, prompt, ct).ConfigureAwait(false);
            if (found.Count == 0)
            {
                return null;
            }

            return found[0].Outer.Select(p => new[] { (double)p.X, (double)p.Y }).ToList();
        }

        public void Dispose()
        {
            _backend?.Dispose();
            _backend = null;
            _image = null;
        }
    }
}
