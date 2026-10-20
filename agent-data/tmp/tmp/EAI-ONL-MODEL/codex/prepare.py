import os, re, json, hashlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
BASE = ROOT / 'tmp/EAI-ONL-MODEL'
OUT = BASE / 'codex'
os.environ['TEMP'] = os.environ['TMP'] = str(BASE / 'temp')

def cs73(text):
    text = 'using System;\nusing System.IO;\nusing System.Linq;\nusing System.Collections.Generic;\n' + text
    text = text.replace('namespace ThresholdSweep;', 'namespace ThresholdSweep\n{') + '\n}\n'
    text = re.sub(r'\b(byte\[\]|string)\?', r'\1', text)
    text = re.sub(r'!(?=[.;,)\[])', '', text)
    # Preserve lexical disposal by turning each using declaration into a nested using scope.
    lines = text.splitlines()
    result = []
    depth = 0
    scopes = []
    for line in lines:
        stripped = line.strip()
        if stripped.startswith('}'):
            while scopes and scopes[-1] == depth:
                result.append('    ' * max(0, depth - 1) + '}')
                scopes.pop()
        match = re.match(r'(\s*)using (var|Mat) (.*);$', line)
        if match:
            result.append(match[1] + 'using (' + match[2] + ' ' + match[3] + ')')
            result.append(match[1] + '{')
            scopes.append(depth)
        else:
            result.append(line)
        # Original braces alone determine original lexical depth.
        depth += line.count('{') - line.count('}')
    return '\n'.join(result) + '\n'

def project(name):
    directory = OUT / name
    directory.mkdir(exist_ok=True)
    reference = ROOT / 'artifacts/EAI-ONL-MODEL/sweep-build/bin'
    references = ''.join('<Reference Include="%s"><HintPath>%s</HintPath></Reference>' % (dll.stem, dll) for dll in reference.glob('*.dll') if dll.stem != 'ThresholdSweep')
    (directory / (name + '.csproj')).write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0-windows</TargetFramework><LangVersion>7.3</LangVersion><Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings><PlatformTarget>x64</PlatformTarget><EnableNETAnalyzers>false</EnableNETAnalyzers><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>''' + references + '''</ItemGroup><Target Name="CopyNativeRuntime" AfterTargets="Build"><ItemGroup><RuntimeFile Include="''' + str(reference / 'runtimes/**/*') + '''" /></ItemGroup><Copy SourceFiles="@(RuntimeFile)" DestinationFiles="@(RuntimeFile->'$(OutDir)runtimes/%(RecursiveDir)%(Filename)%(Extension)')" /></Target></Project>''', encoding='utf-8')
    return directory

source = (BASE / 'ThresholdSweep/Program.cs').read_text(encoding='utf-8-sig')
source = source.replace('private const int Limit = 40;', 'private static int Limit = int.MaxValue;\n    private static string Split = "val";')
source = source.replace('string scope = Path.Combine(root, "tmp", "EAI-ONL-MODEL");', 'string scope = Path.Combine(root, "tmp", "EAI-ONL-MODEL", "codex");')
start = source.index('            string temp = Path.Combine(run, "temp");')
end = source.index('            Directory.SetCurrentDirectory(root);', start)
source = source[:start] + '            Directory.CreateDirectory(run);\n' + source[end:]
source = source.replace('if (!File.Exists(json)) { missingJson++; continue; }', '''if (!File.Exists(json)) { missingJson++; continue; }
                using (var splitDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json)))
                {
                    System.Text.Json.JsonElement flags, flag;
                    if (!splitDoc.RootElement.TryGetProperty("flags", out flags) ||
                        !flags.TryGetProperty(Split, out flag) || flag.ValueKind != System.Text.Json.JsonValueKind.True) continue;
                }''')
source = source.replace('var options = Parse(args);', '''var filtered = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--split") { Split = args[++i]; continue; }
                filtered.Add(args[i]);
            }
            if (Split != "val" && Split != "train") throw new ArgumentException("--split must be val or train");
            Limit = Split == "train" ? 40 : int.MaxValue;
            Console.WriteLine("Split: " + Split + "; cap=" + Limit);
            var options = Parse(filtered.ToArray());''')
directory = project('ThresholdSweepVal')
(directory / 'Program.cs').write_text(cs73(source), encoding='utf-8')
(OUT / 'source-identities.json').write_text(json.dumps({str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in [BASE / 'ThresholdSweep/Program.cs', BASE / 'make_tiles.py', BASE / 'train-tiles512/v512.segai', ROOT / 'tmp/EAI-ONL3/native-stage/BeeNativeSegAI.dll'] if p.exists()}, indent=2))
