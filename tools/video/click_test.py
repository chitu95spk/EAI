import sys,time,ctypes
sys.stdout.reconfigure(encoding='utf-8')
sys.path.insert(0,'tools/video')
import uia
from comtypes.gen import UIAutomationClient as U
h=uia.main_window(39056); u=ctypes.windll.user32
def mk(x,y): return (y<<16)|(x&0xFFFF)
def click(x,y):
    u.PostMessageW(h,0x200,0,mk(x,y)); time.sleep(.15)
    u.PostMessageW(h,0x201,1,mk(x,y)); time.sleep(.1)
    u.PostMessageW(h,0x202,0,mk(x,y)); time.sleep(1.5)
click(197,225)
root=uia.ua.ElementFromHandle(h)
t=root.FindFirst(U.TreeScope_Descendants,uia.ua.CreatePropertyCondition(U.UIA_AutomationIdPropertyId,'ToolAiBox'))
print(t.CurrentIsEnabled,t.CurrentHelpText)
