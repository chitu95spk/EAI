using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace EasyLabel.Online
{
    public sealed class BoundingBox
    {
        public BoundingBox(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public double Width { get; private set; }
        public double Height { get; private set; }
    }

    public sealed class Centroid
    {
        public Centroid(double x, double y) { X = x; Y = y; }
        public double X { get; private set; }
        public double Y { get; private set; }
    }

    public sealed class Detection
    {
        public Detection(string @class, double score, BoundingBox bbox, double areaPx, Centroid centroid)
        { Class = @class; Score = score; Bbox = bbox; AreaPx = areaPx; Centroid = centroid; }
        public string Class { get; private set; }
        public double Score { get; private set; }
        public BoundingBox Bbox { get; private set; }
        public double AreaPx { get; private set; }
        public Centroid Centroid { get; private set; }
    }

    public sealed class ResultRow
    {
        internal ResultRow(string frameId, string recipeId, string verdict, IList<Detection> detections, IDictionary<string, double> stageMs)
        {
            FrameId = frameId; RecipeId = recipeId; Verdict = verdict;
            Detections = new ReadOnlyCollection<Detection>(detections.ToArray());
            PerStageMs = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(stageMs, StringComparer.Ordinal));
        }
        public string FrameId { get; private set; }
        public string RecipeId { get; private set; }
        public string Verdict { get; private set; }
        public IReadOnlyList<Detection> Detections { get; private set; }
        public IReadOnlyDictionary<string, double> PerStageMs { get; private set; }
        public byte[] Serialize() { return RecipeJson.Encode(this); }
    }

    /// <summary>
    /// Evaluates already postprocessed detections; no inference, NMS, preprocessing or unit conversion.
    /// any/count/area operate on detections passing per-class score, area and centroid-in-ROI filters.
    /// area is the summed areaPx; ROI is half-open in pixel coordinates. maxCount is an additional NG gate.
    /// Caller-supplied timings are copied, never measured here, so repeated inputs serialize identically.
    /// </summary>
    public sealed class RecipeEvaluator
    {
        private readonly Recipe recipe;
        private readonly RuleNode rule;
        private readonly Dictionary<string, RecipeClass> classes;

        public RecipeEvaluator(Recipe recipe)
        {
            this.recipe = RecipeJson.Parse(System.Text.Encoding.UTF8.GetString(RecipeJson.Serialize(recipe)));
            classes = this.recipe.Classes.ToDictionary(c => c.Name, StringComparer.Ordinal);
            rule = VerdictParser.Parse(this.recipe.Verdict.NgIf, classes.Keys);
        }

        public ResultRow Evaluate(string frameId, string recipeId, IEnumerable<Detection> detections, IDictionary<string, double> perStageMs = null)
        {
            if (!RecipeValidation.Text(frameId, 256) || !RecipeValidation.Text(recipeId, 256)) throw new ArgumentException("frameId and recipeId are required (max 256).");
            if (detections == null) throw new ArgumentNullException(nameof(detections));
            Dictionary<string, double> timings = perStageMs == null ? new Dictionary<string, double>(StringComparer.Ordinal)
                : new Dictionary<string, double>(perStageMs, StringComparer.Ordinal);
            if (timings.Any(p => !RecipeValidation.Identifier(p.Key) || !RecipeValidation.Range(p.Value, 0, 1e12)))
                throw new ArgumentException("Stage timings require identifier keys and finite nonnegative milliseconds.");
            Dictionary<string, ClassStatistics> stats = classes.Keys.ToDictionary(k => k, k => new ClassStatistics(), StringComparer.Ordinal);
            List<Detection> accepted = new List<Detection>();
            foreach (Detection detection in detections)
            {
                ValidateDetection(detection);
                RecipeClass item;
                if (!classes.TryGetValue(detection.Class, out item)) throw new ArgumentException("Unknown detection class: " + detection.Class);
                if (detection.Score < item.MinScore || detection.AreaPx < item.MinAreaPx) continue;
                double[] roi = item.Roi;
                if (roi != null && (detection.Centroid.X < roi[0] || detection.Centroid.Y < roi[1]
                    || detection.Centroid.X >= roi[0] + roi[2] || detection.Centroid.Y >= roi[1] + roi[3])) continue;
                ClassStatistics value = stats[detection.Class];
                value.Count = checked(value.Count + 1);
                value.Area += detection.AreaPx;
                accepted.Add(detection);
            }
            bool ng = rule.Evaluate(stats) || classes.Any(p => stats[p.Key].Count > p.Value.MaxCount);
            return new ResultRow(frameId, recipeId, ng ? "NG" : "OK", accepted, timings);
        }

        private static void ValidateDetection(Detection detection)
        {
            if (detection == null || !RecipeValidation.Identifier(detection.Class) || !RecipeValidation.Range(detection.Score, 0, 1)
                || !RecipeValidation.Range(detection.AreaPx, 0, 1e12) || detection.Bbox == null || detection.Centroid == null)
                throw new ArgumentException("Invalid detection class, score, area or missing geometry.");
            BoundingBox box = detection.Bbox;
            if (!RecipeValidation.Range(box.X, 0, 1e7) || !RecipeValidation.Range(box.Y, 0, 1e7)
                || !RecipeValidation.Range(box.Width, double.Epsilon, 1e7) || !RecipeValidation.Range(box.Height, double.Epsilon, 1e7)
                || !RecipeValidation.Range(detection.Centroid.X, box.X, box.X + box.Width)
                || !RecipeValidation.Range(detection.Centroid.Y, box.Y, box.Y + box.Height))
                throw new ArgumentException("Detection geometry must be finite, positive and contain its centroid.");
        }
    }
}
