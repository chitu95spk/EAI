param([string]$Out = "E:\Code\EasyVision_Unisen - NoService\artifacts\EAI\EAI-UI\g1")
$ErrorActionPreference = "Stop"
$exe = "E:\Code\EasyVision_Unisen - NoService\EasyLabel\src\EasyAI.Wpf\bin\x64\Release\net9.0-windows\EasyAI.exe"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W { [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; } }
"@
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8
if ($p.HasExited) { "CRASHED exit=$($p.ExitCode)" | Out-File "$Out\smokeg.txt" -Encoding utf8; exit 1 }
$p.Refresh()
$h = $p.MainWindowHandle
$r = New-Object W+RECT; [void][W]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L; $ht = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
[void][W]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
$bmp.Save("$Out\main.png"); $bmp.Dispose()
"RUNNING title=$($p.MainWindowTitle) size=${w}x${ht}" | Out-File "$Out\smokeg.txt" -Encoding utf8
[void]$p.CloseMainWindow(); Start-Sleep -Seconds 2
if (-not $p.HasExited) { $p.Kill() }
