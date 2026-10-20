using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// T2: every Train-tab block reason has a human message and an optional fix, the fix helpers never throw, and the English and
/// Vietnamese resource files stay in step (same keys, no empty value, same {n} placeholders, every key the Train step uses exists).
/// </summary>
public sealed class ActionableErrorTests
{
    private static readonly string[] TrainSourceFiles =
    {
        "ViewModels/TrainStepRules.cs",
        "ViewModels/TrainStepViewModel.cs",
        "Views/TrainStepView.xaml",
        "Views/TrainStepView.xaml.cs",
    };

    private static readonly string[] ExportSendKeys =
    {
        "ExpSendOkFormat", "ExpSendFolderNotSet", "ExpSendFolderMissingFormat", "ExpSendFolderNotWritableFormat",
        "ExpSendModelMissingFormat", "ExpSendUnsupportedFormat", "ExpSendModelInvalidFormat", "ExpSendFailedFormat",
    };

    private static string? FindWpfFolder(string callerFile)
    {
        string? fromSource = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(callerFile) ?? string.Empty, "..", "..", "src", "EasyAI.Wpf"));
        if (System.IO.Directory.Exists(System.IO.Path.Combine(fromSource, "Resources")))
        {
            return fromSource;
        }

        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            foreach (string relative in new[] { "EasyLabel/src/EasyAI.Wpf", "src/EasyAI.Wpf" })
            {
                string candidate = System.IO.Path.Combine(dir, relative);
                if (System.IO.Directory.Exists(System.IO.Path.Combine(candidate, "Resources")))
                {
                    return candidate;
                }
            }

            dir = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        }

        Console.Error.WriteLine("ActionableErrorTests skipped: EasyLabel/src/EasyAI.Wpf/Resources was not found from the test source path or the test output folder.");
        return null;
    }

    private static string? Wpf([CallerFilePath] string callerFile = "") => FindWpfFolder(callerFile);

    private static Dictionary<string, string> LoadResx(string wpfFolder, string fileName)
    {
        XDocument document = XDocument.Load(System.IO.Path.Combine(wpfFolder, "Resources", fileName));
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement data in document.Root!.Elements("data"))
        {
            string? name = (string?)data.Attribute("name");
            Assert.False(string.IsNullOrEmpty(name), fileName + ": a data element has no name");
            Assert.False(map.ContainsKey(name!), fileName + ": duplicate key " + name);
            map[name!] = (string?)data.Element("value") ?? string.Empty;
        }

        return map;
    }

    private static string Placeholders(string value)
    {
        SortedSet<int> indexes = new SortedSet<int>();
        foreach (Match match in Regex.Matches(value, @"\{(\d+)(?:[,:][^}]*)?\}"))
        {
            indexes.Add(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        return string.Join(",", indexes);
    }

    [Fact]
    public void Resx_EnglishAndVietnamese_HaveSameKeysNoEmptyValuesAndSamePlaceholders()
    {
        string? wpf = Wpf();
        if (wpf == null)
        {
            return;
        }

        Dictionary<string, string> en = LoadResx(wpf, "Strings.resx");
        Dictionary<string, string> vi = LoadResx(wpf, "Strings.vi.resx");

        List<string> problems = new List<string>();
        foreach (string key in en.Keys.Where(k => !vi.ContainsKey(k)))
        {
            problems.Add("missing in Strings.vi.resx: " + key);
        }

        foreach (string key in vi.Keys.Where(k => !en.ContainsKey(k)))
        {
            problems.Add("missing in Strings.resx: " + key);
        }

        foreach (KeyValuePair<string, string> pair in en)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                problems.Add("empty value in Strings.resx: " + pair.Key);
            }

            if (vi.TryGetValue(pair.Key, out string? other))
            {
                if (string.IsNullOrWhiteSpace(other))
                {
                    problems.Add("empty value in Strings.vi.resx: " + pair.Key);
                }
                else if (Placeholders(pair.Value) != Placeholders(other))
                {
                    problems.Add("placeholders differ for " + pair.Key + ": [" + Placeholders(pair.Value) + "] vs [" + Placeholders(other) + "]");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TrainStepFiles_OnlyReferenceKeysThatExistInBothLanguages()
    {
        string? wpf = Wpf();
        if (wpf == null)
        {
            return;
        }

        HashSet<string> en = new HashSet<string>(LoadResx(wpf, "Strings.resx").Keys, StringComparer.Ordinal);
        HashSet<string> vi = new HashSet<string>(LoadResx(wpf, "Strings.vi.resx").Keys, StringComparer.Ordinal);
        string[] notKeys = { "ByName", "Culture", "ResourceManager", "Get" };
        // String literals in the Train sources that are AutomationIds or type names, not resource keys.
        HashSet<string> NotResourceKeys = new HashSet<string>(StringComparer.Ordinal) { "TrainChartsView", "TrainFixButton", "TrainFixText", "TrainFixKind" };

        SortedSet<string> used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string relative in TrainSourceFiles)
        {
            string path = System.IO.Path.Combine(wpf, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Assert.True(System.IO.File.Exists(path), "Train step source not found: " + path);
            string text = System.IO.File.ReadAllText(path);
            foreach (Match match in Regex.Matches(text, "ByName\\(\"([A-Za-z0-9_]+)\"\\)"))
            {
                used.Add(match.Groups[1].Value);
            }

            foreach (Match match in Regex.Matches(text, @"\bStrings\.([A-Za-z][A-Za-z0-9_]*)"))
            {
                if (!notKeys.Contains(match.Groups[1].Value))
                {
                    used.Add(match.Groups[1].Value);
                }
            }

            // Keys returned by the rules as string literals ("TrainBlockBusy", "UltraBlockNoCuda", ...).
            foreach (Match match in Regex.Matches(text, "\"((?:TrainBlock|UltraBlock|UltraError|UltraDataset|TrainAuto|TrainStop|Metric|Verdict)[A-Za-z0-9]*)\""))
            {
                if (!NotResourceKeys.Contains(match.Groups[1].Value))
                {
                    used.Add(match.Groups[1].Value);
                }
            }
        }

        Assert.NotEmpty(used);
        List<string> problems = new List<string>();
        foreach (string key in used)
        {
            if (!en.Contains(key))
            {
                problems.Add("not in Strings.resx: " + key);
            }

            if (!vi.Contains(key))
            {
                problems.Add("not in Strings.vi.resx: " + key);
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryBlockReason_HasMessageKeyInBothLanguages_AndFixCaptionWhenItHasAFix()
    {
        string? wpf = Wpf();
        if (wpf == null)
        {
            return;
        }

        Dictionary<string, string> en = LoadResx(wpf, "Strings.resx");
        Dictionary<string, string> vi = LoadResx(wpf, "Strings.vi.resx");
        List<string> problems = new List<string>();

        void Check(string what, string? key, bool keyRequired)
        {
            if (key == null)
            {
                if (keyRequired)
                {
                    problems.Add(what + ": no message key");
                }

                return;
            }

            if (!en.ContainsKey(key) || !vi.ContainsKey(key))
            {
                problems.Add(what + ": key " + key + " missing in a language");
            }
        }

        foreach (EasyLabel.SegAI.Training.SegAiTrainBlock block in Enum.GetValues<EasyLabel.SegAI.Training.SegAiTrainBlock>())
        {
            foreach (int labelled in new[] { 0, 1, 2, 10 })
            {
                Check("Segment " + block + "/" + labelled, EasyAI.Desktop.ViewModels.TrainStepRules.SegmentBlockKey(block, labelled), block != EasyLabel.SegAI.Training.SegAiTrainBlock.None);
                Check("Segment fix " + block + "/" + labelled, EasyAI.Desktop.ViewModels.TrainStepRules.FixButtonKey(EasyAI.Desktop.ViewModels.TrainStepRules.FixKindOf(block, labelled)), false);
            }
        }

        foreach (EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock block in Enum.GetValues<EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock>())
        {
            Check("Ultra " + block, EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlockKey(block), block != EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock.None);
            Check("Ultra fix " + block, EasyAI.Desktop.ViewModels.TrainStepRules.FixButtonKey(EasyAI.Desktop.ViewModels.TrainStepRules.UltraFixKindOf(block)), false);
        }

        foreach (EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind kind in Enum.GetValues<EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind>())
        {
            Check("Fix kind " + kind, EasyAI.Desktop.ViewModels.TrainStepRules.FixButtonKey(kind), kind != EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.None);
        }

        foreach (EasyLabel.SegAI.Training.Ultralytics.UltraDatasetProblem problem in Enum.GetValues<EasyLabel.SegAI.Training.Ultralytics.UltraDatasetProblem>())
        {
            Check("Dataset problem " + problem, EasyAI.Desktop.ViewModels.TrainStepRules.UltraDatasetProblemKey(problem), true);
        }

        foreach (string key in ExportSendKeys)
        {
            Check("Export " + key, key, true);
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [InlineData(EasyLabel.SegAI.Training.SegAiTrainBlock.None, 10, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.None)]
    [InlineData(EasyLabel.SegAI.Training.SegAiTrainBlock.Busy, 10, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.None)]
    [InlineData(EasyLabel.SegAI.Training.SegAiTrainBlock.NativeMissing, 10, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.None)]
    [InlineData(EasyLabel.SegAI.Training.SegAiTrainBlock.BackboneMissing, 10, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.OpenModelFolder)]
    [InlineData(EasyLabel.SegAI.Training.SegAiTrainBlock.NoTrainImages, 0, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.GoToLabel)]
    [InlineData(EasyLabel.SegAI.Training.SegAiTrainBlock.NoValImages, 1, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.GoToLabel)]
    [InlineData(EasyLabel.SegAI.Training.SegAiTrainBlock.NoValImages, 2, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.AutoAssignVal)]
    public void SegmentBlock_MapsToFixKind(EasyLabel.SegAI.Training.SegAiTrainBlock block, int labelled, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind expected)
    {
        Assert.Equal(expected, EasyAI.Desktop.ViewModels.TrainStepRules.FixKindOf(block, labelled));
    }

    [Theory]
    [InlineData(EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock.None, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.None)]
    [InlineData(EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock.NoSession, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.None)]
    [InlineData(EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock.Busy, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.None)]
    [InlineData(EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock.Checking, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.None)]
    [InlineData(EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock.NoTrainerFolder, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.UseSegmentAi)]
    [InlineData(EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock.NoVenv, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.UseSegmentAi)]
    [InlineData(EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock.NoCuda, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.UseSegmentAi)]
    [InlineData(EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock.Failed, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.UseSegmentAi)]
    [InlineData(EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock.NoFamily, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.UseSegmentAi)]
    [InlineData(EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock.NoSamples, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.GoToLabel)]
    [InlineData(EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock.TooFewImages, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind.GoToLabel)]
    public void UltraBlock_MapsToFixKind(EasyAI.Desktop.ViewModels.TrainStepRules.UltraBlock block, EasyAI.Desktop.ViewModels.TrainStepRules.TrainFixKind expected)
    {
        Assert.Equal(expected, EasyAI.Desktop.ViewModels.TrainStepRules.UltraFixKindOf(block));
    }

    [Fact]
    public void NearestExistingFolder_NeverThrows_AndFindsTheExistingParent()
    {
        string baseDir = System.IO.Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        string missing = System.IO.Path.Combine(baseDir, "no-such-folder-t2", "Models", "DefectV2");

        Assert.Equal(baseDir, EasyAI.Desktop.ViewModels.TrainStepRules.NearestExistingFolder(missing));
        Assert.Equal(baseDir, EasyAI.Desktop.ViewModels.TrainStepRules.NearestExistingFolder(baseDir));
        Assert.Null(EasyAI.Desktop.ViewModels.TrainStepRules.NearestExistingFolder(null));
        Assert.Null(EasyAI.Desktop.ViewModels.TrainStepRules.NearestExistingFolder(string.Empty));
        Assert.Null(EasyAI.Desktop.ViewModels.TrainStepRules.NearestExistingFolder("   "));
        Assert.Null(EasyAI.Desktop.ViewModels.TrainStepRules.NearestExistingFolder("bad\0path"));
    }
}
