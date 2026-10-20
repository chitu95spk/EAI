$run = "E:\Code\EasyVision_Unisen - NoService\artifacts\EAI\EAI-CLS\p1\s2c\run"
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class P {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool SetDllDirectory(string p);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr LoadLibrary(string p);
  [DllImport("kernel32.dll")] public static extern IntPtr GetProcAddress(IntPtr h, string n);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int F();
}
"@
[P]::SetDllDirectory($run) | Out-Null
$h = [P]::LoadLibrary("$run\BeeNativeSegAI.dll")
"handle=$h"
foreach ($n in "SEGAI_GetVersion","SEGAI_GetClassApiVersion","SEGAI_RunFeatureSelfTest") {
  $p = [P]::GetProcAddress($h,$n)
  $f = [Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($p,[P+F])
  "$n = " + $f.Invoke()
}
Get-Process -Id $PID | % { $_.Modules | ? { $_.ModuleName -eq "BeeNativeSegAI.dll" } | % { "loaded=" + $_.FileName } }
