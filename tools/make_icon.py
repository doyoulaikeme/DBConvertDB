"""生成 DBConvertDB 的应用程序图标（多尺寸 .ico）。

用法：python tools/make_icon.py
输出：assets/app.ico、assets/app-256.png
"""
import os
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "assets")
os.makedirs(ASSETS, exist_ok=True)

SIZE = 1024
MARGIN = 56
RADIUS = 210

canvas = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))

# 圆角矩形 + 对角渐变（与界面主色 #2563EB 同族）
grad = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
gd = ImageDraw.Draw(grad)
top = (86, 163, 255)
bottom = (24, 74, 214)
for y in range(SIZE):
    t = y / (SIZE - 1)
    # 轻微的斜向过渡：左上加一点亮度
    r = int(top[0] * (1 - t) + bottom[0] * t)
    g = int(top[1] * (1 - t) + bottom[1] * t)
    b = int(top[2] * (1 - t) + bottom[2] * t)
    gd.line([(0, y), (SIZE, y)], fill=(r, g, b, 255))

mask = Image.new("L", (SIZE, SIZE), 0)
ImageDraw.Draw(mask).rounded_rectangle(
    [MARGIN, MARGIN, SIZE - MARGIN, SIZE - MARGIN], radius=RADIUS, fill=255
)
canvas.paste(grad, (0, 0), mask)

d = ImageDraw.Draw(canvas)

# 左上高光，让纯色块不至于太平
glow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
gdr = ImageDraw.Draw(glow)
gdr.ellipse([MARGIN - 260, MARGIN - 300, SIZE * 0.72, SIZE * 0.55], fill=(255, 255, 255, 46))
glow = glow.filter(__import__("PIL.ImageFilter", fromlist=["ImageFilter"]).GaussianBlur(160))
canvas.alpha_composite(Image.composite(glow, Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0)), mask))

# 文字 DB（居中的唯一主体，保证 16px 下仍可辨识）
font_path = r"C:\Windows\Fonts\arialbd.ttf"
font = ImageFont.truetype(font_path, 470)
text = "DB"
box = d.textbbox((0, 0), text, font=font)
tw, th = box[2] - box[0], box[3] - box[1]
tx = (SIZE - tw) / 2 - box[0]
ty = (SIZE - th) / 2 - box[1] - 6

shadow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
sd = ImageDraw.Draw(shadow)
sd.text((tx, ty + 16), text, font=font, fill=(10, 30, 80, 100))
from PIL import ImageFilter
shadow = shadow.filter(ImageFilter.GaussianBlur(20))
canvas.alpha_composite(shadow)
d.text((tx, ty), text, font=font, fill=(255, 255, 255, 255))

canvas.save(os.path.join(ASSETS, "app-256.png"), sizes=[(256, 256)])

sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (40, 40), (32, 32), (24, 24), (20, 20), (16, 16)]
canvas.save(os.path.join(ASSETS, "app.ico"), format="ICO", sizes=sizes)
print("ok:", os.path.join(ASSETS, "app.ico"), os.path.getsize(os.path.join(ASSETS, "app.ico")))