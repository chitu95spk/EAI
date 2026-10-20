import time,ctypes,sys,json
ctypes.windll.kernel32.SetProcessAffinityMask(ctypes.windll.kernel32.GetCurrentProcess(),0x5)
dur=float(sys.argv[1]);t0=time.time();cyc=[];work=0
while time.time()-t0<dur:
    s=time.perf_counter();c=time.process_time()
    while time.process_time()-c<0.030: work+=1
    time.sleep(0.070)
    cyc.append(time.perf_counter()-s)
print(json.dumps(dict(cycles=len(cyc),mean_cycle_ms=round(1000*sum(cyc)/len(cyc),1),work=work)))
