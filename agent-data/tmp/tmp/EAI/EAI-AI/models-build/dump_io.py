import sys, onnxruntime as ort
for f in sys.argv[1:]:
    s = ort.InferenceSession(f, providers=["CPUExecutionProvider"])
    print("==", f)
    for i in s.get_inputs(): print(" in ", i.name, i.shape, i.type)
    for o in s.get_outputs(): print(" out", o.name, o.shape, o.type)
    md = s.get_modelmeta().custom_metadata_map
    print(" meta keys:", list(md.keys())[:6])
