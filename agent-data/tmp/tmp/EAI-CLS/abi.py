import ctypes,sys,os
for p in sys.argv[1:]:
    d=os.path.dirname(os.path.abspath(p)); os.add_dll_directory(d)
    try:
        os.environ['PATH']=d+os.pathsep+os.environ['PATH']
        m=ctypes.CDLL(os.path.abspath(p))
        v=m.SEGAI_GetVersion(); m.SEGAI_GetBuildInfo.restype=ctypes.c_char_p
        print(p,'ABI',v,(m.SEGAI_GetBuildInfo() or b'')[:120])
    except Exception as e: print(p,'ERR',e)
