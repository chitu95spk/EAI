using System.Globalization;
using System.IO;
using System.Text;
using EasyLabel.Ai.Onnx;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// SAM2 works first: it outlines every object it finds on the image (<see cref="Sam2Candidates"/>), the app writes the outlines to the run folder
/// (<c>candidates.json</c>) and a picture with the numbered outlines; the model then only says which numbers make up the objects of the task.
/// </summary>
public static class Sam2Preview
{
    public const string PreviewFileName = "preview.png";
    public const string CandidatesFileName = "candidates.json";
    public const int MaxSide = 900;

    private static readonly object Gate = new object();
    private static Sam2Backend? _backend;
    private static string? _backendFolder;

    /// <summary>Loads SAM2 in the background so the first run does not pay for it (the model folder may be null).</summary>
    public static void WarmUp(string? modelsFolder)
    {
        if (string.IsNullOrWhiteSpace(modelsFolder)) return;
        _ = Task.Run(() =>
        {
            try
            {
                Backend(modelsFolder);
            }
            catch (Exception)
            {
            }
        });
    }

    private static Sam2Backend Backend(string modelsFolder)
    {
        lock (Gate)
        {
            if (_backend != null && string.Equals(_backendFolder, modelsFolder, StringComparison.OrdinalIgnoreCase)) return _backend;
            _backend?.Dispose();
            _backend = Sam2Backend.Load(AiModelFiles.Resolve(AiModelFiles.Sam2Id, Path.Combine(modelsFolder, AiModelFiles.Sam2Id)));
            _backendFolder = modelsFolder;
            return _backend;
        }
    }

    /// <summary>Null (and an empty note) when SAM2 is not available or finds nothing: the caller then falls back to the plain preview.</summary>
    public static string? TryWrite(string imagePath, string runFolder, string? modelsFolder, out string note)
    {
        note = string.Empty;
        if (string.IsNullOrWhiteSpace(modelsFolder)) return null;
        try
        {
            using Mat mat = Cv2.ImDecode(File.ReadAllBytes(imagePath), ImreadModes.Color | ImreadModes.IgnoreOrientation);
            if (mat.Empty()) return null;
            byte[] pixels = new byte[(long)mat.Width * mat.Height * 3];
            using (Mat continuous = mat.Clone())
            {
                System.Runtime.InteropServices.Marshal.Copy(continuous.Data, pixels, 0, pixels.Length);
            }

            IReadOnlyList<Sam2Candidate> candidates = Sam2Candidates.Generate(Backend(modelsFolder), new AiImage(pixels, mat.Width, mat.Height, imagePath), CancellationToken.None);
            if (candidates.Count == 0) return null;

            WriteJson(Path.Combine(runFolder, CandidatesFileName), candidates);
            double factor = Math.Min(1.0, (double)MaxSide / Math.Max(mat.Width, mat.Height));
            string path = Path.Combine(runFolder, PreviewFileName);
            Draw(mat, candidates, factor, path);
            note = BuildNote(candidates, mat.Width, mat.Height, factor);
            return path;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is AiModelException || ex is OpenCVException || ex is InvalidOperationException || ex is ArgumentException)
        {
            note = string.Empty;
            return null;
        }
    }

    private static void WriteJson(string path, IReadOnlyList<Sam2Candidate> candidates)
    {
        JArray items = new JArray();
        foreach (Sam2Candidate c in candidates)
        {
            items.Add(new JObject
            {
                ["id"] = c.Id,
                ["score"] = Math.Round(c.Score, 3),
                ["points"] = new JArray(c.Outer.Select(p => new JArray(Math.Round(p.X, 1), Math.Round(p.Y, 1)))),
            });
        }

        File.WriteAllText(path, new JObject { ["candidates"] = items }.ToString(Newtonsoft.Json.Formatting.None));
    }

    private static void Draw(Mat source, IReadOnlyList<Sam2Candidate> candidates, double factor, string path)
    {
        using Mat canvas = new Mat();
        if (factor < 1.0) Cv2.Resize(source, canvas, new Size((int)Math.Round(source.Width * factor), (int)Math.Round(source.Height * factor)), 0, 0, InterpolationFlags.Area);
        else source.CopyTo(canvas);
        foreach (Sam2Candidate c in candidates)
        {
            Scalar colour = Color(c.Id);
            Point[] pts = c.Outer.Select(p => new Point((int)Math.Round(p.X * factor), (int)Math.Round(p.Y * factor))).ToArray();
            Cv2.Polylines(canvas, new[] { pts }, true, colour, 1, LineTypes.AntiAlias);
            string text = c.Id.ToString(CultureInfo.InvariantCulture);
            Point at = new Point((int)Math.Round(c.Centre.X * factor) - 6, (int)Math.Round(c.Centre.Y * factor) + 4);
            Cv2.PutText(canvas, text, at, HersheyFonts.HersheySimplex, 0.45, Scalar.All(0), 3, LineTypes.AntiAlias);
            Cv2.PutText(canvas, text, at, HersheyFonts.HersheySimplex, 0.45, colour, 1, LineTypes.AntiAlias);
        }

        Cv2.ImWrite(path, canvas);
    }

    // Bright, well separated colours on a grey picture.
    private static Scalar Color(int id)
    {
        Scalar[] palette = { new Scalar(0, 255, 255), new Scalar(255, 128, 0), new Scalar(0, 255, 0), new Scalar(255, 0, 255), new Scalar(0, 128, 255), new Scalar(255, 255, 0) };
        return palette[id % palette.Length];
    }

    private static string BuildNote(IReadOnlyList<Sam2Candidate> candidates, int width, int height, double factor)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        StringBuilder sb = new StringBuilder();
        sb.Append("SAM2 has already outlined ").Append(candidates.Count.ToString(ci)).Append(" candidate parts of this ").Append(width.ToString(ci)).Append('x').Append(height.ToString(ci))
          .Append(" image. They are drawn and numbered on the attached picture (a ").Append((factor).ToString("0.###", ci)).Append(" scale preview). ")
          .Append("A real object is often split into several candidates (for example a screw = its head + its shaft), and a candidate may be a part of the background or of a neighbour.\n");
        sb.Append("Candidates (id: centre x,y; size w x h in original pixels): ");
        sb.Append(string.Join("; ", candidates.Select(c => c.Id.ToString(ci) + ": " + c.Centre.X.ToString("0", ci) + "," + c.Centre.Y.ToString("0", ci) + "; " + c.Box.Width.ToString("0", ci) + "x" + c.Box.Height.ToString("0", ci))));
        sb.Append(".\nDo ONE call of pick_candidates: objects = [{ids:[...]}, ...], each entry the candidate numbers that together form ONE object the task asks for (leave out the ones it does not ask for), with the label. It joins the outlines and fits the box for you. Then finish with one short line. Only an object that has no candidate at all needs detect_points (original pixel x,y). Do not call propose_shapes, do not zoom, do not re-check.");
        return sb.ToString();
    }
}
