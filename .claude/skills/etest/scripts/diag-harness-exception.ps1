param(
    [Parameter(Mandatory = $true)][string]$HarnessExe,   # isolated harness exe (artifacts\verification\Release\x64\bin\<Name>\<exe>)
    [string]$ExpectMessage = ''                             # message printed by the harness after "FAIL:"; first exception with this message is reported
)
# Diagnostic for a FAILED .NET Framework harness that prints only ex.Message. Runs the harness in-process (same CLR as
# Windows PowerShell 5.1) and prints the stack of the first matching exception. Read-only; output stays on the console.
$ErrorActionPreference = 'Stop'
$dir = [IO.Path]::GetDirectoryName($HarnessExe)
Set-Location $dir
$env:PATH = "$dir;$env:PATH"
Add-Type -TypeDefinition @'
using System;
public static class HarnessFirstChance {
    public static string Wanted = "";
    public static string First = null;
    public static void Install() {
        AppDomain.CurrentDomain.FirstChanceException += (s, e) => {
            if (First != null) return;
            if (Wanted.Length == 0 || e.Exception.Message == Wanted)
                First = e.Exception.GetType().FullName + ": " + e.Exception.Message + "\n" + e.Exception.StackTrace;
        };
    }
}
'@
[HarnessFirstChance]::Wanted = $ExpectMessage
[HarnessFirstChance]::Install()
$asm = [Reflection.Assembly]::LoadFrom($HarnessExe)
$n = $asm.EntryPoint.GetParameters().Count
$rc = $asm.EntryPoint.Invoke($null, $(if ($n -eq 0) { $null } else { , @(, [string[]]@()) }))
"harness return code: $rc"
if ([HarnessFirstChance]::First) { 'first matching exception:'; [HarnessFirstChance]::First } else { 'no matching exception observed' }
