"""Apply opaque redactions from an external JSON spec; source paths are never saved."""
from pathlib import Path
import argparse,hashlib,json
from PIL import Image,ImageDraw,ImageFont,ImageChops

parser=argparse.ArgumentParser();parser.add_argument('spec',type=Path);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
args.output.mkdir(parents=True,exist_ok=True)
font=ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',14)
records=[]
for item in json.loads(args.spec.read_text(encoding='utf-8-sig')):
    source=Image.open(item['source']).convert('RGB');image=source.copy();draw=ImageDraw.Draw(image)
    for rectangle in item.get('rectangles',[]):
        x0,y0,x1,y1=rectangle
        if not(0<=x0<x1<=image.width and 0<=y0<y1<=image.height):raise ValueError('Invalid mask rectangle')
        draw.rectangle((x0,y0,x1-1,y1-1),fill='#DDE4EA')
        if x1-x0>150 and y1-y0>23:draw.text((x0+8,y0+5),'个人信息已遮挡',font=font,fill='#46576A')
    for replacement in item.get('replacements',[]):
        x0,y0,x1,y1=replacement['rect']
        sample=replacement.get('sample',[min(image.width-1,x1+1),max(0,y0-2)])
        fill=source.getpixel(tuple(sample))
        draw.rectangle((x0,y0,x1-1,y1-1),fill=fill)
        if replacement.get('icon'):
            draw.rounded_rectangle((x0+2,y0+2,x1-3,y1-3),radius=3,fill='#88A7D3')
        elif replacement.get('text'):
            label_font=ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',replacement.get('fontSize',12))
            draw.text((x0+1,y0+2),replacement['text'],font=label_font,fill='#174568',anchor='lt')
    output=args.output/(item['name']+'.png');image.save(output,optimize=True)
    # Reopen and verify masks; replacement pixels depend only on the opaque
    # fill/label, never on their original source contents. No metadata is copied.
    saved=Image.open(output)
    if saved.size!=source.size or saved.info:raise RuntimeError('Unexpected image metadata or geometry')
    allowed=Image.new('L',source.size,0);regions=ImageDraw.Draw(allowed)
    for rect in item.get('rectangles',[])+[p['rect'] for p in item.get('replacements',[])]:regions.rectangle((rect[0],rect[1],rect[2]-1,rect[3]-1),fill=255)
    difference=ImageChops.difference(source,saved).convert('L')
    if ImageChops.multiply(difference,ImageChops.invert(allowed)).getbbox():raise RuntimeError('Image changed outside approved label regions')
    records.append({'file':output.name,'width':image.width,'height':image.height,'rectangles':item.get('rectangles',[]),'replacements':[{'rect':p['rect'],'text':p.get('text'),'icon':p.get('icon',False)} for p in item.get('replacements',[])],'sha256':hashlib.sha256(output.read_bytes()).hexdigest()})
(args.output/'redactions.json').write_text(json.dumps(records,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
print(f'Redacted {len(records)} unique screenshots; source images unchanged.')
