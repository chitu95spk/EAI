param([string]$Out = 'E:\Code\EasyVision_Unisen - NoService\artifacts\EAI-LABEL\ui-smoke1', [string]$Tag = 'a')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class Pw { [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 public struct RECT { public int L, T, R, B; } }
'@
[void][Pw]::SetProcessDPIAware()
New-Item -ItemType Directory -Force $Out | Out-Null
$exe = 'E:\Code\EasyVision_Unisen - NoService\tmp\EAI-LABEL\iso-out\EasyAI.exe'
$root = 'E:\Code\EasyVision_Unisen - NoService'
$p = Start-Process $exe -ArgumentList @('--settings-dir', ('"' + $root + '\tmp\EAI-LABEL\smoke-settings"'), '--project', ('"' + $root + '\tmp\EAI-LABEL\smoke-project"'), '--culture', 'vi') -PassThru
function Find-Id($root, $id) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Shot($hwnd, $name) {
  $r = New-Object Pw+RECT; [void][Pw]::GetWindowRect($hwnd, [ref]$r)
  $w = $r.R - $r.L; $h = $r.B - $r.T
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  $ok = [Pw]::PrintWindow($hwnd, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
  $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  "shot $name ${w}x${h} printwindow=$ok"
}
$log = @()
try {
  $main = $null
  for ($i = 0; $i -lt 60 -and -not $main; $i++) {
    Start-Sleep -Milliseconds 700
    $p.Refresh()
    if ($p.MainWindowHandle -ne 0) { $main = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle) }
  }
  if (-not $main) { throw 'main window not found' }
  Start-Sleep -Seconds 3
  $log += Shot $p.MainWindowHandle "$Tag-main"
  $btn = Find-Id $main 'AgentChatButton'
  $log += "AgentChatButton present=$([bool]$btn)"
  if ($btn) {
    [void][Pw]::SetForegroundWindow($p.MainWindowHandle); Start-Sleep -Milliseconds 500
    $log += ('btn enabled=' + $btn.Current.IsEnabled + ' offscreen=' + $btn.Current.IsOffscreen + ' rect=' + $btn.Current.BoundingRectangle)
    $rc = $btn.Current.BoundingRectangle
    [void][Pw]::SetCursorPos([int]($rc.X + $rc.Width/2), [int]($rc.Y + $rc.Height/2)); Start-Sleep -Milliseconds 200
    [Pw]::mouse_event(2,0,0,0,[IntPtr]::Zero); [Pw]::mouse_event(4,0,0,0,[IntPtr]::Zero)
    Start-Sleep -Seconds 2
    Start-Sleep -Seconds 2
    $log += Shot $p.MainWindowHandle "$Tag-main-after-click"
    $chat = Find-Id $main 'AgentChatWindow'
    for ($k = 0; $k -lt 12 -and -not $chat; $k++) {
      Start-Sleep -Milliseconds 700
      $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
      $wins = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $pc)
      foreach ($w in $wins) { if ($w.Current.AutomationId -eq 'AgentChatWindow') { $chat = $w } }
      if ($k -eq 11) { foreach ($w in $wins) { $log += ('win: ' + $w.Current.Name + ' id=' + $w.Current.AutomationId) } }
    }
    $log += "chat window present=$([bool]$chat)"
    if ($chat) {
      $h = [IntPtr]$chat.Current.NativeWindowHandle
      foreach ($id in 'AgentChatComposer','AgentChatPrompt','AgentChatAttach','AgentSuggestPromptButton','PromptModel','AgentChatOptions','AgentChatRun','AgentChatWorkers_1','AgentChatWorkers_2','AgentChatWorkers_3','AgentChatMode_Current') {
        $e = Find-Id $chat $id; $log += "$id present=$([bool]$e)"
      }
      $log += Shot $h "$Tag-chat"
      $opt = Find-Id $chat 'AgentChatOptions'
      if ($opt) { $opt.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 800; $log += Shot $h "$Tag-chat-options" }
    }
  }
} catch { $log += "ERROR $_" }
finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
$log | Set-Content (Join-Path $Out "$Tag-smoke.log")
$log
