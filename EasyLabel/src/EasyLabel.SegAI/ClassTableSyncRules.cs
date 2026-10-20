using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using EasyLabel.SegAI.Model;

namespace EasyLabel.SegAI
{
    /// <summary>Appends valid annotation names without changing existing class ids or settings.</summary>
    public static class ClassTableSyncRules
    {
        [return: MaybeNull]
        public static SegAiClassTable Extend(SegAiClassTable current, IEnumerable<string> labels,
            out IReadOnlyList<string> skipped)
        {
            var rejected = new List<string>();
            skipped = rejected;
            var entries = current.Classes.ToList();
            var seen = new HashSet<string>(entries.Select(c => c.Name.Trim()), StringComparer.OrdinalIgnoreCase);
            // A legacy override can contain names that do not satisfy the project table rules.
            var initialProblem = SegAiClassTable.Validate(entries);
            if (initialProblem != null || seen.Count != entries.Count || entries.Any(c => c.Name != c.Name.Trim()))
            {
                rejected.Add(initialProblem == null ? "The current class names are not valid project names." : initialProblem.En);
                return null;
            }

            foreach (string raw in labels)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string name = raw.Trim();
                if (!seen.Add(name)) continue;
                bool good = string.Equals(name, "OK", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "GOOD", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Tot", StringComparison.OrdinalIgnoreCase);
                entries.Add(new SegAiClassEntry(name, good ? SegAiClassKind.Good : SegAiClassKind.Defect, true));
                var problem = SegAiClassTable.Validate(entries);
                if (problem != null)
                {
                    entries.RemoveAt(entries.Count - 1);
                    rejected.Add(name + ": " + problem.En);
                }
            }

            return entries.Count == current.Classes.Count ? null : new SegAiClassTable(entries);
        }
    }
}
