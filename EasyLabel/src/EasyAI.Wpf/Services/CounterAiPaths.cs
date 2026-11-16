using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace EasyAI.Desktop.Services;

/// <summary>Shared CounterAI backend helpers: python / script discovery, process start info, <c>*_points.csv</c> I/O, default threshold and weights.</summary>
public static class CounterAiPaths
{
    public const string ScriptName = "counterai_service.py";

    /// <summary>Default detection threshold.</summary>
    public const double DefaultThreshold = 0.8;

    /// <summary>Finds counterai_service.py: EASYAI_COUNTERAI_SCRIPT, else tools\counterai\python up from the app folder.</summary>
    public static string? FindScript()
    {
        string? env = Environment.GetEnvironmentVariable("EASYAI_COUNTERAI_SCRIPT");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            return env;
        }

        for (DirectoryInfo? d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "tools", "counterai", "python", ScriptName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>EASYAI_COUNTERAI_PYTHON (torch build matching the GPU, e.g. cu130 for RTX 50xx), else EASYAI_PYTHON, else "python".</summary>
    public static string PythonExe() =>
        ReadEnv("EASYAI_COUNTERAI_PYTHON")
        ?? ReadEnv("EASYAI_PYTHON")
        ?? "python";

    /// <summary>Process env first, then the persisted User/Machine value: an app started from Explorer before the variable was set does not inherit it.</summary>
    private static string? ReadEnv(string name)
    {
        foreach (EnvironmentVariableTarget target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? v = Environment.GetEnvironmentVariable(name, target);
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
        }

        return null;
    }

    public static ProcessStartInfo BuildStartInfo(string script, IEnumerable<string> args)
    {
        ProcessStartInfo psi = new ProcessStartInfo(PythonExe())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(script);
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        return psi;
    }

    /// <summary>Default weights file of a project: &lt;project&gt;\counterai\fastnet.pt (empty without a project).</summary>
    public static string DefaultWeightsPath(string? projectRoot) =>
        string.IsNullOrEmpty(projectRoot) ? string.Empty : Path.Combine(projectRoot, "counterai", "fastnet.pt");

    /// <summary>Parses a threshold text (',' or '.' decimal); values outside (0,1) or unparsable give <see cref="DefaultThreshold"/>.</summary>
    public static double ParseThreshold(string? text)
    {
        if (!double.TryParse((text ?? string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double th) || th <= 0 || th >= 1)
        {
            return DefaultThreshold;
        }

        return th;
    }

    /// <summary>Path of the points CSV beside an image: &lt;dir&gt;\&lt;name&gt;_points.csv.</summary>
    public static string PointsCsvPath(string imagePath) =>
        Path.Combine(Path.GetDirectoryName(imagePath) ?? string.Empty, Path.GetFileNameWithoutExtension(imagePath) + "_points.csv");

    public static List<(int X, int Y)> ReadPointsCsv(string path)
    {
        List<(int X, int Y)> pts = new List<(int X, int Y)>();
        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0) return pts;
        string[] head = lines[0].Split(',');
        int ix = Array.FindIndex(head, h => h.Trim().Trim('"').Equals("x", StringComparison.OrdinalIgnoreCase));
        int iy = Array.FindIndex(head, h => h.Trim().Trim('"').Equals("y", StringComparison.OrdinalIgnoreCase));
        if (ix < 0 || iy < 0) return pts;
        for (int i = 1; i < lines.Length; i++)
        {
            string[] c = lines[i].Split(',');
            if (c.Length <= Math.Max(ix, iy)) continue;
            if (double.TryParse(c[ix], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                double.TryParse(c[iy], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                pts.Add(((int)x, (int)y));
            }
        }

        return pts;
    }

    public static void WritePointsCsv(string path, IReadOnlyList<(int X, int Y)> points)
    {
        StringBuilder sb = new StringBuilder("id,x,y\r\n");
        for (int i = 0; i < points.Count; i++)
        {
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(',').Append(points[i].X.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(points[i].Y.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
