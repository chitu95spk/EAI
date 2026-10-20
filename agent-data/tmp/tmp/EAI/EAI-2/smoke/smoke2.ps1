# EAI-2 extended UI smoke (Rot, Polygon, zoom, pan, Space, quick switching). Run: powershell -NoProfile -File tmpEAIEAI-2smokesmoke2.ps1 -Culture en
# Output: artifactsEAIEAI-2smoke-b.
param([string]$Culture = 'en')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
$root = (Resolve-Path '.').Path
$out = Join-Path $root 'artifacts\EAI\EAI-2\smoke-b'
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
  Shot $p 'b1-open'
  Keys $p 'o'
  Drag $p 0.30 0.30 0.30 0.30
  Drag $p 0.42 0.30 0.42 0.30
  Drag $p 0.42 0.46 0.42 0.46
  Start-Sleep -Milliseconds 800
  Shot $p 'b2-rot-drawn'
  Keys $p 'p'
  Drag $p 0.50 0.30 0.50 0.30
  Drag $p 0.62 0.30 0.62 0.30
  Drag $p 0.62 0.45 0.62 0.45
  Drag $p 0.52 0.50 0.52 0.50
  Keys $p '{ENTER}'
  Shot $p 'b3-polygon-done'
  Keys $p 'v'
  # wheel zoom in at canvas centre, then middle-button pan
  $rc = New-Object RC; [W]::GetWindowRect($hwnd, [ref]$rc) | Out-Null
  $cx = $rc.L + [int](($rc.R-$rc.L)*0.47); $cy = $rc.T + [int](($rc.B-$rc.T)*0.55)
  [W]::SetCursorPos($cx,$cy) | Out-Null; Start-Sleep -Milliseconds 200
  if (Ensure $p) { for ($i=0;$i -lt 3;$i++) { [W]::mouse_event(0x800,0,0,120,[UIntPtr]::Zero); Start-Sleep -Milliseconds 200 } }
  Shot $p 'b4-zoomed'
  if (Ensure $p) {
    [W]::mouse_event(0x20,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 150
    [W]::SetCursorPos($cx+80,$cy+40) | Out-Null; Start-Sleep -Milliseconds 200
    [W]::SetCursorPos($cx+160,$cy+80) | Out-Null; Start-Sleep -Milliseconds 200
    [W]::mouse_event(0x40,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 300
  }
  Shot $p 'b5-panned'
  Keys $p 'f'
  # Space toggles Train/Val of the current image; key repeat must toggle only once per press
  Keys $p ' '
  Shot $p 'b6-space-toggled'
  Keys $p ' '
  Shot $p 'b7-space-back'
  Keys $p '{ESC}'
  # arrows with nothing selected step images: Right,Right then Left
  Keys $p '{RIGHT}{RIGHT}'
  Shot $p 'b9-arrows-right'
  Keys $p '{LEFT}'
  Shot $p 'b10-arrows-left'
  # quick image switching with the canvas dirty: unselect, then arrows rapidly
  Keys $p '{ESC}'
  Drag $p 0.04 0.18 0.04 0.18
  Drag $p 0.04 0.22 0.04 0.22
  Drag $p 0.04 0.14 0.04 0.14
  Drag $p 0.04 0.18 0.04 0.18
  Drag $p 0.04 0.12 0.04 0.12
  Shot $p 'b8-switched'
  Keys $p '{ESC}'
  Start-Sleep -Seconds 2   # let the save queue write
  $p.Refresh(); Note ("process alive: " + (-not $p.HasExited))
}
finally {
  if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; if (-not $p.WaitForExit(20000)) { $p.Kill() } }
  Note ("exit code: " + $p.ExitCode)
}
Get-ChildItem $fixture -Recurse -Include *.json | ForEach-Object { Note ("json: " + $_.FullName.Substring($root.Length) + " " + $_.Length + " bytes") }
Get-ChildItem (Join-Path $fixture '.easyai') -Recurse -ErrorAction SilentlyContinue | ForEach-Object { Note ("easyai: " + $_.FullName.Substring($root.Length)) }
$log | Set-Content (Join-Path $out "$Culture-smoke.log")
