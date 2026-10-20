p = r'E:\Code\EasyVision_Unisen - NoService\EasyLabel\src\EasyAI.Wpf\Services\Agent\AgentResults.cs'
raw = open(p, 'rb').read()
bom = raw.startswith(b'\xef\xbb\xbf')
crlf = b'\r\n' in raw
s = raw.decode('utf-8-sig').replace('\r\n', '\n')

old = "        string error = (stderr ?? string.Empty).Trim();\n        string raw = (stdout ?? string.Empty).Trim();\n        bool signedOut = AgentLoginDetector.LooksSignedOutShort(error)"
new = "        string error = StripCodexBanner(stderr);\n        string raw = (stdout ?? string.Empty).Trim();\n        bool signedOut = AgentLoginDetector.LooksSignedOutShort(error)"
assert old in s
s = s.replace(old, new, 1)

old2 = "    /// <summary>The first <see cref=\"RawMessageLimit\"/> characters, white space collapsed to single spaces.</summary>"
helper = '''    /// <summary>
    /// The stderr of a failed Codex run without its start banner ("Reading additional input from stdin...", version, workdir, model, session id) and
    /// without the echoed prompt, so the 300 character message shows the real error. An ERROR line wins when there is one.
    /// </summary>
    public static string StripCodexBanner(string? stderr)
    {
        string text = (stderr ?? string.Empty).Replace("\\r\\n", "\\n").Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        string[] lines = text.Split('\\n');
        List<string> errors = new List<string>();
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("stream error", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(trimmed);
            }
        }

        if (errors.Count > 0)
        {
            return string.Join(" ", errors);
        }

        int lastSession = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("session id:", StringComparison.OrdinalIgnoreCase))
            {
                lastSession = i;
            }
        }

        if (lastSession < 0)
        {
            return text;
        }

        // After "session id:" come the closing "--------" and, in exec mode, "user" and the prompt; keep only what follows the prompt echo's section.
        int start = lastSession + 1;
        while (start < lines.Length && (lines[start].Trim().Length == 0 || lines[start].Trim().All(c => c == '-')))
        {
            start++;
        }

        string rest = string.Join("\\n", lines.Skip(start)).Trim();
        return rest.Length > 0 ? rest : text;
    }

'''
assert old2 in s
s = s.replace(old2, helper + old2, 1)
if crlf:
    s = s.replace('\n', '\r\n')
open(p, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))
