import glob, json, math, sys
FACTOR = 4.0
apply = len(sys.argv) > 1 and sys.argv[1] == "apply"
root = "E:/Training/TrainLOGONEW/images/"
changed = roundtrip_bad = boxes = 0
for f in sorted(glob.glob(root + "*.json")):
    raw = open(f, "rb").read()
    text = raw.decode("utf-8")
    d = json.loads(text)
    same = json.dumps(d, indent=2, ensure_ascii=False).replace("\n", "\r\n") == text
    if not same:
        roundtrip_bad += 1
    n = 0
    for s in d["shapes"]:
        if s["label"] != "LECH_BOT" or len(s["points"]) != 4:
            continue
        p = s["points"]
        e1 = (p[1][0] - p[0][0], p[1][1] - p[0][1])
        e2 = (p[2][0] - p[1][0], p[2][1] - p[1][1])
        short = e1 if math.hypot(*e1) < math.hypot(*e2) else e2
        L = math.hypot(*short)
        u = (short[0] / L, short[1] / L)
        cx = sum(q[0] for q in p) / 4
        cy = sum(q[1] for q in p) / 4
        for q in p:
            t = (q[0] - cx) * u[0] + (q[1] - cy) * u[1]
            q[0] += t * (FACTOR - 1) * u[0]
            q[1] += t * (FACTOR - 1) * u[1]
        n += 1
    if n:
        boxes += n
        changed += 1
        if apply:
            out = json.dumps(d, indent=2, ensure_ascii=False).replace("\n", "\r\n")
            open(f, "wb").write(out.encode("utf-8"))
print("files", changed, "boxes", boxes, "roundtrip-not-identical", roundtrip_bad, "applied", apply)
