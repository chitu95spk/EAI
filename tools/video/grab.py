import ctypes, sys
from ctypes import wintypes
from PIL import Image
sys.path.insert(0,'tools/video')
import uia
hwnd=uia.main_window(int(sys.argv[1]))
u,g=ctypes.windll.user32,ctypes.windll.gdi32
r=wintypes.RECT(); u.GetWindowRect(hwnd,ctypes.byref(r)); w,h=r.right-r.left,r.bottom-r.top
dc=u.GetWindowDC(hwnd); m=g.CreateCompatibleDC(dc); b=g.CreateCompatibleBitmap(dc,w,h); g.SelectObject(m,b)
u.PrintWindow(hwnd,m,2)
class BI(ctypes.Structure): _fields_=[('s',ctypes.c_uint32),('w',ctypes.c_int32),('h',ctypes.c_int32),('p',ctypes.c_uint16),('bc',ctypes.c_uint16),('c',ctypes.c_uint32),('si',ctypes.c_uint32),('x',ctypes.c_int32),('y',ctypes.c_int32),('u',ctypes.c_uint32),('i',ctypes.c_uint32)]
bi=BI(ctypes.sizeof(BI),w,-h,1,32,0,0,0,0,0,0)
buf=ctypes.create_string_buffer(w*h*4); g.GetDIBits(m,b,0,h,buf,ctypes.byref(bi),0)
Image.frombuffer('RGBA',(w,h),buf,'raw','BGRA',0,1).convert('RGB').save(sys.argv[2])
g.DeleteObject(b); g.DeleteDC(m); u.ReleaseDC(hwnd,dc)
