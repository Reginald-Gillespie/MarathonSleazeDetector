"""Contact sheet of crops, enlarged: sheet.py out.png scale file1.png [file2.png ...]"""
import sys
from PIL import Image, ImageDraw

out, scale, files = sys.argv[1], int(sys.argv[2]), sys.argv[3:]
ims = [Image.open(f).convert('RGB') for f in files]
W = max(i.width for i in ims) * scale
H = sum(i.height * scale + 4 for i in ims)
o = Image.new('RGB', (W, H), (0, 0, 90))
y = 0
for i in ims:
    o.paste(i.resize((i.width * scale, i.height * scale), Image.NEAREST), (0, y))
    y += i.height * scale + 4
o.save(out)
