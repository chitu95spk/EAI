using System;
using System.Text;

namespace EasyLabel.Online
{
    /// <summary>
    /// Operator-facing controls over a running <see cref="OnlineRunner"/> (backs ONL.STOP / ONL.STATUS / ONL.THRESHOLD.SET).
    /// Threshold changes build a new validated recipe copy and swap the evaluator; the frozen recipe on disk is never touched.
    /// </summary>
    public sealed class OnlineSession
    {
        private readonly OnlineRunner runner;
        private readonly string recipeId;
        private readonly object gate = new object();
        private Recipe recipe;

        public OnlineSession(OnlineRunner runner, Recipe recipe, string recipeId)
        {
            if (runner == null) throw new ArgumentNullException("runner");
            if (recipe == null) throw new ArgumentNullException("recipe");
            this.runner = runner; this.recipe = Copy(recipe); this.recipeId = recipeId;
        }

        public bool IsRunning { get { return runner.IsRunning; } }

        public void Stop() { runner.Stop(); }

        /// <summary>Returns null on success, otherwise a short error code.</summary>
        public string SetThreshold(string className, double score, double nms)
        {
            if (!runner.IsRunning) return "NOT_RUNNING";
            lock (gate)
            {
                Recipe next = Copy(recipe);
                bool matched = false;
                foreach (RecipeClass c in next.Classes)
                {
                    if (className != null && !string.Equals(c.Name, className, StringComparison.Ordinal)) continue;
                    c.MinScore = score;
                    matched = true;
                }
                if (!matched) return "UNKNOWN_CLASS";
                next.Nms = nms;
                RecipeEvaluator evaluator;
                try { evaluator = new RecipeEvaluator(next); }
                catch (RecipeInvalid) { return "INVALID_THRESHOLD"; }
                runner.SwapEvaluator(evaluator);
                recipe = next;
                return null;
            }
        }

        public string StatusJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"running\":").Append(runner.IsRunning ? "true" : "false");
            sb.Append(",\"recipeId\":\"").Append(recipeId.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            sb.Append(",\"accepted\":").Append(runner.Metrics.Accepted);
            sb.Append(",\"dropped\":").Append(runner.Metrics.Dropped);
            sb.Append(",\"completed\":").Append(runner.Metrics.Completed);
            sb.Append(",\"failed\":").Append(runner.Metrics.Failed);
            foreach (StageStats s in runner.Metrics.Snapshot())
            {
                if (s.Stage != OnlineMetrics.TotalStage) continue;
                sb.Append(",\"totalP95Ms\":").Append(s.P95Ms.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.Append('}').ToString();
        }

        private static Recipe Copy(Recipe source)
        {
            return RecipeJson.Parse(Encoding.UTF8.GetString(RecipeJson.Serialize(source)));
        }
    }
}
