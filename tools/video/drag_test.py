import sys, time, ctypes
sys.path.insert(0,'tools/video')
import uia
pid=39056
h=uia.main_window(pid)
root=uia.ua.ElementFromHandle(h)
time.sleep(1)
u=ctypes.windll.user32
from ctypes import wintypes
pt=wintypes.POINT(0,0); u.ClientToScreen(h,ctypes.byref(pt))
r=wintypes.RECT(); u.GetWindowRect(h,ctypes.byref(r))
print('client origin',pt.x,pt.y,'win',r.left,r.top,flush=True)
dx,dy=pt.x-r.left,pt.y-r.top   # capture coords -> client coords: subtract (dx,dy)
def mk(x,y): return ((y-dy)<<16)|((x-dx)&0xFFFF)
def click(x,y):
    u.PostMessageW(h,0x200,0,mk(x,y)); time.sleep(.1); u.PostMessageW(h,WM_DN,1,mk(x,y)); time.sleep(.1); u.PostMessageW(h,WM_UP,0,mk(x,y)); time.sleep(.5)
WM_DN,WM_UP=0x201,0x202
pass
# screw (vertical, below the NG one) capture coords box
x1,y1,x2,y2=636,612,674,692
WM_MOVE,WM_DN,WM_UP=0x200,0x201,0x202
u.PostMessageW(h,WM_MOVE,0,mk(x1,y1)); time.sleep(.2)
u.PostMessageW(h,WM_DN,1,mk(x1,y1)); time.sleep(.2)
for i in range(1,11):
    u.PostMessageW(h,WM_MOVE,1,mk(x1+(x2-x1)*i//10,y1+(y2-y1)*i//10)); time.sleep(.05)
u.PostMessageW(h,WM_UP,0,mk(x2,y2))
print('dragged',flush=True)


