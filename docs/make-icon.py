from PIL import Image, ImageDraw
import sys
S=1024
def lerp(a,b,t): return tuple(int(a[i]+(b[i]-a[i])*t) for i in range(3))
img=Image.new("RGBA",(S,S),(0,0,0,0))
# gradient square
grad=Image.new("RGBA",(S,S))
gd=ImageDraw.Draw(grad)
c1=(124,92,255); c2=(0,200,255)
for y in range(S):
    for_x=None
for i in range(S*2):
    t=i/(S*2-1)
    gd.line([(i,0),(0,i)],fill=lerp(c1,c2,t)+(255,),width=2)
mask=Image.new("L",(S,S),0)
ImageDraw.Draw(mask).rounded_rectangle([0,0,S-1,S-1],radius=int(S*0.23),fill=255)
img.paste(grad,(0,0),mask)
d=ImageDraw.Draw(img)
W=(255,255,255,255)
# trim corner brackets
m=int(S*0.2); L=int(S*0.17); T=int(S*0.065)
x0,y0,x1,y1=m,m,S-m,S-m
for (cx,cy,sx,sy) in [(x0,y0,1,1),(x1,y0,-1,1),(x0,y1,1,-1),(x1,y1,-1,-1)]:
    d.rounded_rectangle(sorted([cx,cx+sx*L])[0:1]+[min(cy,cy+sy*T)]+[max(cx,cx+sx*L)]+[max(cy,cy+sy*T)],radius=T//2,fill=W)
    d.rounded_rectangle([min(cx,cx+sx*T),min(cy,cy+sy*L),max(cx,cx+sx*T),max(cy,cy+sy*L)],radius=T//2,fill=W)
# play triangle
cx,cy=S*0.52,S*0.5; r=S*0.155
pts=[(cx-r*0.75,cy-r),(cx-r*0.75,cy+r),(cx+r*0.95,cy)]
d.polygon(pts,fill=W)
img.save(sys.argv[1]+".png")
sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)]
img.save(sys.argv[1]+".ico",sizes=sizes)
