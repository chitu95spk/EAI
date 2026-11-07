import ctypes, sys, time
from ctypes import wintypes
import comtypes.client as cc
cc.GetModule('UIAutomationCore.dll')
from comtypes.gen import UIAutomationClient as U
ua=cc.CreateObject('{ff48dba4-60ef-4201-aa87-54103eef594e}',interface=U.IUIAutomation)

def main_window(pid):
    EP=ctypes.WINFUNCTYPE(wintypes.BOOL,wintypes.HWND,wintypes.LPARAM)
    p=wintypes.DWORD(); out=[]
    def cb(h,l):
        ctypes.windll.user32.GetWindowThreadProcessId(h,ctypes.byref(p))
        if p.value==pid and ctypes.windll.user32.IsWindowVisible(h):
            b=ctypes.create_unicode_buffer(100); ctypes.windll.user32.GetWindowTextW(h,b,100)
            if b.value.startswith('EasyAI'): out.append(h)
        return True
    ctypes.windll.user32.EnumWindows(EP(cb),0)
    return out[0]

def find(root,name):
    return root.FindFirst(U.TreeScope_Descendants,ua.CreatePropertyCondition(U.UIA_NamePropertyId,name))

def invoke(el):
    w=ua.ControlViewWalker
    while el is not None:
        pat=el.GetCurrentPattern(U.UIA_InvokePatternId)
        if pat:
            pat.QueryInterface(U.IUIAutomationInvokePattern).Invoke(); return True
        pat=el.GetCurrentPattern(U.UIA_SelectionItemPatternId)
        if pat:
            pat.QueryInterface(U.IUIAutomationSelectionItemPattern).Select(); return True
        el=w.GetParentElement(el)
    return False

if __name__=='__main__':
    root=ua.ElementFromHandle(main_window(int(sys.argv[1])))
    e=find(root,sys.argv[2])
    print('found',bool(e)); print('invoked',invoke(e) if e else None)

