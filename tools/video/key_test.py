import sys,time,ctypes
sys.stdout.reconfigure(encoding='utf-8')
sys.path.insert(0,'tools/video')
import uia
from comtypes.gen import UIAutomationClient as U
h=uia.main_window(39056); u=ctypes.windll.user32
root=uia.ua.ElementFromHandle(h)
def byid(a): return root.FindFirst(U.TreeScope_Descendants,uia.ua.CreatePropertyCondition(U.UIA_AutomationIdPropertyId,a))
fg0=u.GetForegroundWindow()
byid('SourceAiModel').SetFocus(); time.sleep(.5)
print('fg changed',u.GetForegroundWindow()!=fg0, 'fg is easyai', u.GetForegroundWindow()==h)
u.PostMessageW(h,0x100,0x20,0x00390001); time.sleep(.1); u.PostMessageW(h,0x101,0x20,0xC0390001); time.sleep(1.5)
t=byid('ToolAiBox'); print(t.CurrentIsEnabled,t.CurrentHelpText)
