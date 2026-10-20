import sys,glob,os
from PIL import Image, ImageDraw
pat,out,n,off=sys.argv[1],sys.argv[2],int(sys.argv[3]),int(sys.argv[4])
fs=sorted(glob.glob(pat))[off:off+n]
W=480;H=320;cols=4;rows=(len(fs)+cols-1)//cols
S=Image.new('RGB',(W*cols,H*rows))
for i,f in enumerate(fs):
    im=Image.open(f).convert('RGB');im.thumbnail((W,H-14));S.paste(im,((i%cols)*W,(i//cols)*H+14))
    ImageDraw.Draw(S).text(((i%cols)*W+2,(i//cols)*H),os.path.basename(f),fill=(255,255,0))
S.save(out,quality=80)
