using EasyLabel.SegAI.Model;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>
/// Rules of the class table editor (UI-free, unit tested): the checks before OK and whether a draft differs from the saved table.
/// </summary>
public static class ClassTableEditRules
{
    /// <summary>
    /// Problem of a draft class list, or null when it can be saved. Runs the model rules first, then the project name rules that
    /// <c>SegAiSession.SaveClassTable</c> applies (no leading or trailing spaces, no duplicate names ignoring case).
    /// </summary>
    public static SegAiModelProblem? Validate(IReadOnlyList<SegAiClassEntry> classes)
    {
        SegAiModelProblem? problem = SegAiClassTable.Validate(classes);
        if (problem != null)
        {
            return problem;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < classes.Count; i++)
        {
            string name = classes[i].Name;
            if (name != name.Trim())
            {
                return new SegAiModelProblem(
                    "lớp " + (i + 1) + ": tên có khoảng trắng đầu hoặc cuối",
                    "class " + (i + 1) + ": the name has leading or trailing spaces");
            }

            if (!seen.Add(name))
            {
                return new SegAiModelProblem(
                    "lớp " + (i + 1) + ": tên trùng khi không phân biệt hoa thường",
                    "class " + (i + 1) + ": the name duplicates another class ignoring case");
            }
        }

        return null;
    }

    /// <summary>True when the draft differs from the saved table in count, names, kinds or enabled flags.</summary>
    public static bool HasChanges(IReadOnlyList<SegAiClassEntry> saved, IReadOnlyList<SegAiClassEntry> draft)
    {
        if (saved.Count != draft.Count)
        {
            return true;
        }

        for (int i = 0; i < saved.Count; i++)
        {
            if (!string.Equals(saved[i].Name, draft[i].Name, StringComparison.Ordinal)
                || saved[i].Kind != draft[i].Kind
                || saved[i].Enabled != draft[i].Enabled)
            {
                return true;
            }
        }

        return false;
    }
}
