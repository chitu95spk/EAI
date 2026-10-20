# EAI-2 extended UI smoke (Rot, Polygon, zoom, pan, Space, quick switching). Run: powershell -NoProfile -File tmpEAIEAI-2smokesmoke2.ps1 -Culture en
# Output: artifactsEAIEAI-2smoke3.
param([string]$Culture = 'en')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
$root = (Resolve-Path '.').Path
$out = Join-Path $root 'artifacts\EAI\EAI-2\smoke3'
$fixture = Join-Path $root ('tmp\EAI\EAI-2\fixture2-' + $Culture)
$exe = Join-Path $root 'EasyLabel\src\EasyAI.Wpf\bin\x64\Release\net9.0-windows\EasyAI.exe'
New-Item -ItemType Directory -Force $out | Out-Null
if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
Copy-Item (Join-Path $root 'tmp\EAI\EAI-0\fixture-project') $fixture -Recurse
$env:TEMP = Join-Path $root 'tmp\EAI\EAI-2\temp'; $env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
$hwnd = [IntPtr]::Zero
$log = New-Object System.Collections.Generic.List[string]
function Note($m) { Write-Host $m; $script:log.Add($m) }
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public struct RC{public int L,T,R,B;}
public static class W{
 [DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h, out RC r);
 [DllImport("user32.dll")]public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
 [DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")]public static extern bool IsIconic(IntPtr h);
 [DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr h,int c);
 [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")]public static extern void mouse_event(uint f,int x,int y,uint d,UIntPtr e);
}
'@
function Ensure($p) {
  # Never send input unless our window is restored and in the foreground (else clicks hit other apps).
  for ($i = 0; $i -lt 5; $i++) {
    if ([W]::IsIconic($hwnd)) { [W]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 400 }
    (New-Object -ComObject WScript.Shell).AppActivate($p.Id) | Out-Null; [W]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 300
    if (-not [W]::IsIconic($hwnd) -and [W]::GetForegroundWindow() -eq $hwnd) { return $true }
  }
  Note 'ENSURE FAILED: window not foreground, input skipped'; return $false
}
function Keys($p, $k) { if (Ensure $p) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 300 } }
function Shot($p, $name) {
  if ([W]::IsIconic($hwnd)) { Note "WINDOW MINIMIZED before shot $name (restoring)"; [W]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 500 }
  Start-Sleep -Milliseconds 600
  $rc = New-Object RC; [W]::GetWindowRect($hwnd, [ref]$rc) | Out-Null
  $w = $rc.R - $rc.L; $h = $rc.B - $rc.T
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  $ok = [W]::PrintWindow($hwnd, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
  $bmp.Save((Join-Path $out ("$Culture-$name.png"))); $bmp.Dispose(); Note ("shot $Culture-$name ${w}x${h} printwindow=$ok")
}
function Drag($p, $fx1, $fy1, $fx2, $fy2) {
  if (-not (Ensure $p)) { return }
  $rc = New-Object RC; [W]::GetWindowRect($hwnd, [ref]$rc) | Out-Null
  $w = $rc.R - $rc.L; $h = $rc.B - $rc.T
  $x1 = $rc.L + [int]($w*$fx1); $y1 = $rc.T + [int]($h*$fy1); $x2 = $rc.L + [int]($w*$fx2); $y2 = $rc.T + [int]($h*$fy2)
  [W]::SetCursorPos($x1,$y1) | Out-Null; Start-Sleep -Milliseconds 150
  [W]::mouse_event(0x2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 150   # left down
  [W]::SetCursorPos([int](($x1+$x2)/2),[int](($y1+$y2)/2)) | Out-Null; Start-Sleep -Milliseconds 150
  [W]::SetCursorPos($x2,$y2) | Out-Null; Start-Sleep -Milliseconds 150
  [W]::mouse_event(0x4,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 400   # left up
}
$sd = Join-Path $env:TEMP "settings-$Culture"; if (Test-Path $sd) { Remove-Item $sd -Recurse -Force }
$a = '--settings-dir "{0}" --project "{1}" --culture {2}' -f (Join-Path $env:TEMP "settings-$Culture"), $fixture, $Culture
$p = Start-Process -FilePath $exe -ArgumentList $a -PassThru
try {
  $deadline = (Get-Date).AddSeconds(25)
  do { Start-Sleep -Milliseconds 400; $p.Refresh() } while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
  if ($p.MainWindowHandle -eq 0) { throw 'main window did not appear' }
  $hwnd = $p.MainWindowHandle
  Start-Sleep -Seconds 4
  [W]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 500
  (New-Object -ComObject WScript.Shell).AppActivate($p.Id) | Out-Null; [W]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 600
