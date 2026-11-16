"""Frozen train/validation/tuning/final manifests derived from autoloop synthgen.
The original synthgen is not edited. Segmentation masks expose the exact selected
alpha>128 inside compose, rather than filling bounding boxes. Final source IDs,
seeds and hashes are disjoint from every training/tuning split.
"""
from pathlib import Path
import sys, json, hashlib, random, inspect
import numpy as np
import cv2
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/"tools"/"autoloop"))
import synthgen as sg
OUT=Path(r"C:\Demo\LucGiac.segai-tune")
SOURCE=Path(r"C:\Demo\LucGiac\Cam_20261019_160350_387.bmp")
def sha(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def main():
    OUT.mkdir(parents=True,exist_ok=True)
    if (OUT/"dataset"/"manifest.json").exists():
        print("Frozen manifest already exists",flush=True); return
    image=cv2.imread(str(SOURCE)); assert image is not None
    objects=sg.load_polygons(SOURCE.with_suffix(".json"))
    bg=sg.estimate_background(image,objects)
    masks=sg.all_mattes(image,objects,bg)
    bad=sg.touching_ids(objects,masks)
    clean=sorted([o for o in objects if o["id"] not in bad],key=lambda o:o["poly"][:,0].mean())
    assert len(clean)>=6, f"Only {len(clean)} isolated objects; cannot split independently"
    groups={"train":clean[:-3],"val":clean[:-3],"tune":clean[-3:-2],"final":clean[-2:]}
    patches={s:[sg.cut_patch(image,o,bg=bg,mask_full=masks.get(o["id"])) for o in os] for s,os in groups.items()}
    code=inspect.getsource(sg.compose)
    tail='''    semantic = np.zeros((h,w),np.uint8)
    for p in placed:
        ys,xs=p["y0"],p["x0"]
        view=semantic[ys:ys+p["alpha"].shape[0], xs:xs+p["alpha"].shape[1]]
        view[p["alpha"]>128]=1
    return out, objs, info, semantic
'''
    assert code.count("    return out, objs, info")==1
    code=code.replace("    return out, objs, info",tail.rstrip())
    ns=dict(sg.__dict__); exec(compile(code,"synthgen.compose+semantic","exec"),ns)
    compose=ns["compose"]
    data=OUT/"dataset"; records=[]
    counts={"train":120,"val":30,"tune":40,"final":100}
    bases={"train":20261110000,"val":20261120000,"tune":20261130000,"final":20261140000}
    for split,count in counts.items():
        folder=data/split; folder.mkdir(parents=True,exist_ok=True)
        for i in range(count):
            seed=bases[split]+i
            families=sg.TRAIN_BG if split in ("train","val","tune") else sg.TEST_BG
            kind=(sg.UNSEEN_BG[(i//2)%len(sg.UNSEEN_BG)] if split=="final" and i%2==0 else sg.TRAIN_BG[i%len(sg.TRAIN_BG)])
            img,objs,info,mask=compose(patches[split],kind,np.random.default_rng(seed),random.Random(seed))
            ip=folder/f"{i:04d}.png"; mp=folder/f"{i:04d}.mask.png"
            cv2.imwrite(str(ip),img); cv2.imwrite(str(mp),mask)
            record=dict(split=split,index=i,seed=seed,image=str(ip),mask=str(mp),sha256=sha(ip),
                        boxes=[np.asarray(pts).tolist() for label,pts in objs],**info)
            records.append(record)
            with (data/"progress.jsonl").open("a",encoding="utf-8") as f:f.write(json.dumps(record)+"\n")
            print(f"{split} {i+1}/{count} objects={len(objs)} background={kind}",flush=True)
    # The real frame is a diagnostic, not held-out evidence: its crops supply training.
    records.append(dict(split="real",index=0,image=str(SOURCE),sha256=sha(SOURCE),count=len(objects),
                        boxes=[o["poly"].tolist() for o in objects],bg="real",sources=[o["id"] for o in objects]))
    final=records[:-1]
    for a,b in [("train","tune"),("train","final"),("val","tune"),("val","final"),("tune","final")]:
        aa=[r for r in final if r["split"]==a];bb=[r for r in final if r["split"]==b]
        assert not ({x for r in aa for x in r["sources"]}&{x for r in bb for x in r["sources"]})
        assert not ({r["sha256"] for r in aa}&{r["sha256"] for r in bb})
        assert not ({r["seed"] for r in aa}&{r["seed"] for r in bb})
    manifest=dict(source=str(SOURCE),source_sha256=sha(SOURCE),synthgen_sha256=sha(Path(sg.__file__)),
                  generator_sha256=sha(__file__),isolated_ids=[o["id"] for o in clean],dropped_touching=sorted(bad),
                  source_groups={s:[o["id"] for o in os] for s,os in groups.items()},
                  leakage_checks="passed (source identities, image hashes, seeds); train/val share identities intentionally",
                  counts=counts,records=records)
    (data/"manifest.json").write_text(json.dumps(manifest,indent=1),encoding="utf-8")
    report=ROOT/"docs"/"segai-tune"/"REPORT.md"
    with report.open("a",encoding="utf-8") as f:
        f.write("\n### Dataset frozen\n"+json.dumps({k:v for k,v in manifest.items() if k!="records"},indent=1)+"\n")
    print("MANIFEST",data/"manifest.json",flush=True)
if __name__=="__main__":main()
