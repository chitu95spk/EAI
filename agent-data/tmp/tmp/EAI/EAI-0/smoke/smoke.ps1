# EAI-0 UI smoke. Run from repo root: powershell -NoProfile -File tmp\EAI\EAI-0\smoke\smoke.ps1
# Everything is written under artifacts\EAI\EAI-0\r1\smoke. Uses a generated fixture project (no user data).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
$root = (Resolve-Path '.').Path
$out = Join-Path $root 'artifacts\EAI\EAI-0\r1\smoke'
$fixture = Join-Path $root 'tmp\EAI\EAI-0\fixture-project'
$exe = Join-Path $root 'EasyLabel\src\EasyAI.Wpf\bin\Release\net9.0-windows\EasyAI.exe'
New-Item -ItemType Directory -Force $out, $fixture | Out-Null
$env:TEMP = Join-Path $root 'tmp\EAI\EAI-0\temp'; $env:TMP = $env:TEMP
$results = New-Object System.Collections.Generic.List[string]
function Note($m) { Write-Host $m; $script:results.Add($m) }

# fixture images
$sizes = @(@(640,480), @(1280,720), @(800,800), @(3000,2000))
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $p = Join-Path $fixture ("img{0}.png" -f ($i + 1))
  if (-not (Test-Path $p)) {
    $bmp = New-Object System.Drawing.Bitmap $sizes[$i][0], $sizes[$i][1]
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0,0)), ([System.Drawing.Point]::new($bmp.Width,$bmp.Height)), ([System.Drawing.Color]::FromArgb(40+$i*50,90,200)), ([System.Drawing.Color]::FromArgb(230,160-$i*30,60))
    $g.FillRectangle($br, 0, 0, $bmp.Width, $bmp.Height)
    $g.DrawString("fixture " + ($i+1), (New-Object System.Drawing.Font 'Segoe UI', 48), [System.Drawing.Brushes]::White, 30, 30)
    $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
  }
}

function Start-App($settings, $culture) {
  New-Item -ItemType Directory -Force $settings | Out-Null
  # One pre-quoted string: Start-Process does NOT quote array items, and our paths contain spaces.
  $a = '--settings-dir "{0}" --project "{1}"' -f $settings, $fixture
  if ($culture) { $a += ' --culture ' + $culture }
  $p = Start-Process -FilePath $exe -ArgumentList $a -PassThru
  $deadline = (Get-Date).AddSeconds(25)
  do { Start-Sleep -Milliseconds 400; $p.Refresh() } while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
  if ($p.MainWindowHandle -eq 0) { throw 'main window did not appear' }
  Start-Sleep -Seconds 3
  # Safety guard: the app must show the fixture project, otherwise stop before it touches anything else.
  $w0 = Get-Win $p
  $pp = Find-Id $w0 'ProjectPath'
  $txt = if ($pp) { $pp.Current.Name } else { '' }
  if ($txt -notlike '*fixture-project*') { $p.Kill(); throw "app opened '$txt' instead of the fixture; aborted" }
  return $p
}
function Get-Win($p) { [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle) }
function Find-Id($win, $id) {
  $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), $id
  $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Shot($p, $name) {
  # PrintWindow renders only this app window (never other windows that overlap it).
  Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public struct RC{public int L,T,R,B;}public static class W{[DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h,out RC r);[DllImport("user32.dll")]public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint f);[DllImport("user32.dll")]public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int ht,bool rp);}' -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 500
  $rc = New-Object RC; [W]::GetWindowRect($p.MainWindowHandle, [ref]$rc) | Out-Null
  $w = $rc.R - $rc.L; $h = $rc.B - $rc.T
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  $ok = [W]::PrintWindow($p.MainWindowHandle, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
  $f = Join-Path $out ($name + '.png'); $bmp.Save($f); $bmp.Dispose(); Note ("shot {0} {1}x{2} printwindow={3}" -f $name, $w, $h, $ok)
}
function Stop-App($p) {
  $win = Get-Win $p
  try { $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
  if (-not $p.WaitForExit(8000)) { $p.Kill(); Note 'WARN app did not close via WindowPattern; killed' } else { Note 'closed via WindowPattern' }
}
function Ids($win, $ids) { foreach ($i in $ids) { $e = Find-Id $win $i; Note ("{0,-22} {1}" -f $i, $(if ($e) { 'found' } else { 'MISSING' })) } }

$ids = 'OpenFolderButton','ResetLayoutButton','ImageList','PreviewImage','LeftSplitter','RightSplitter','SegAiStatus','MvsStatus'
$dpi = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -ErrorAction SilentlyContinue).AppliedDPI
Note "AppliedDPI=$dpi (150% DPI is owed unless this is 144)"

# Run A: en default
$sA = Join-Path $out 'settings-A'; Remove-Item $sA -Recurse -Force -ErrorAction SilentlyContinue
$p = Start-App $sA $null
$win = Get-Win $p
Ids $win $ids
Shot $p 'A1-default-en'
$list = Find-Id $win 'ImageList'
if ($list) {
  $items = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
  Note ("list items: {0} (fixture has 4)" -f $items.Count)
  if ($items.Count -ge 2) { $items[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 2 }
}
Shot $p 'A2-selected-en'
[W]::MoveWindow($p.MainWindowHandle, 40, 40, 900, 600, $true) | Out-Null; Start-Sleep -Seconds 2
Shot $p 'A3-compact-900x600'
Ids (Get-Win $p) @('CompactTabs','ImageList','ResetLayoutButton')
Stop-App $p
Note ("layout.json after close: " + (Test-Path (Join-Path $sA 'layout.json')))

# Run B: seeded non-default layout -> applied -> Reset -> defaults
$sB = Join-Path $out 'settings-B'; Remove-Item $sB -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $sB | Out-Null
if (Test-Path (Join-Path $sA 'layout.json')) {
  $j = Get-Content (Join-Path $sA 'layout.json') -Raw | ConvertFrom-Json
  Note ("A layout.json: " + (Get-Content (Join-Path $sA 'layout.json') -Raw).Replace("`n",' ').Substring(0, [Math]::Min(300,(Get-Content (Join-Path $sA 'layout.json') -Raw).Length)))
}
'{"SchemaVersion":1,"LeftWidth":330,"RightWidth":420}' | Set-Content (Join-Path $sB 'layout.json') -Encoding UTF8
$p = Start-App $sB $null; $win = Get-Win $p
Shot $p 'B1-seeded-layout'
$lw = (Find-Id $win 'ImageList').Current.BoundingRectangle.Width
Note ("B seeded: image list width px = $lw (seed LeftWidth 330 DIP)")
$btn = Find-Id $win 'ResetLayoutButton'
if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Seconds 2 }
Shot $p 'B2-after-reset'
$lw2 = (Find-Id (Get-Win $p) 'ImageList').Current.BoundingRectangle.Width
Note ("B after reset: image list width px = $lw2")
Stop-App $p
Note ("B layout.json: " + (Get-Content (Join-Path $sB 'layout.json') -Raw).Replace("`n",' '))

# Run C: vi
$sC = Join-Path $out 'settings-C'; Remove-Item $sC -Recurse -Force -ErrorAction SilentlyContinue
$p = Start-App $sC 'vi'; Shot $p 'C1-default-vi'; Stop-App $p

$results | Set-Content (Join-Path $out 'smoke-result.txt') -Encoding UTF8
