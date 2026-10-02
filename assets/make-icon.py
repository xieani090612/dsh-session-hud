"""
把 assets/icon-source.webp 转成应用图标。

要点：
  * 源图是带 alpha 的 RGBA，四周有大量全透明留白。直接缩小的话人物会只占中间一小块，
    任务栏 16~32px 下基本看不清 —— 所以先按 alpha 的 bbox 裁掉透明边，再补成正方形。
  * ICO 里塞多个尺寸（16 起），Windows 会按场景挑：任务栏用 24/32，Alt+Tab 用 32/48，
    资源管理器大图标用 256。
  * 小尺寸用 LANCZOS 重采样，避免锯齿。

输出分两处，是有意的：
  * app/app.ico        —— 窗口真正使用的图标（exe 内嵌 + AppWindow.SetIcon）；
  * docs/icon-*.png    —— 只给 README 看的预览图。放 docs/ 而不是 assets/，
    是因为 docs/ 随 npm 包发布而 assets/ 不发布：README 里引用的图必须能取到，
    否则在 npm 上看 README 就是一堆裂图（test/package.test.mjs 会盯着这一点）。

用法：
    python assets/make-icon.py
"""

import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SRC = os.path.join(HERE, "icon-source.webp")
OUT_ICO = os.path.join(ROOT, "app", "app.ico")
OUT_PNG = os.path.join(ROOT, "docs", "icon-256.png")
OUT_PREVIEW = os.path.join(ROOT, "docs", "icon-preview.png")

# Windows 会在不同场景挑不同尺寸，都给上。
ICO_SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
# 内容周围留一点边距，免得人物贴边（任务栏里贴边会显得很挤）。
PADDING_RATIO = 0.03


def build_master():
    image = Image.open(SRC).convert("RGBA")
    print(f"source: {image.size} {image.mode}")

    # 关键一步：裁掉全透明留白，让人物尽可能占满画面。
    bbox = image.getchannel("A").getbbox()
    if bbox:
        image = image.crop(bbox)
        print(f"trimmed transparent margin -> {image.size}")

    # 补成正方形，人物居中。
    side = max(image.size)
    pad = int(round(side * PADDING_RATIO))
    canvas = Image.new("RGBA", (side + pad * 2, side + pad * 2), (0, 0, 0, 0))
    canvas.paste(image, ((canvas.width - image.width) // 2, (canvas.height - image.height) // 2), image)

    master = canvas.resize((256, 256), Image.LANCZOS)
    return master


def build_preview(master):
    """把各档小尺寸并排画出来，方便肉眼确认小图标下还认得出。"""
    scales = [16, 24, 32, 48, 64, 128]
    gap = 16
    tiles = []
    for s in scales:
        tiles.append(master.resize((s, s), Image.LANCZOS))

    width = sum(t.width for t in tiles) + gap * (len(tiles) + 1)
    height = max(t.height for t in tiles) + gap * 2
    preview = Image.new("RGBA", (width, height), (255, 255, 255, 255))
    x = gap
    for tile in tiles:
        y = (height - tile.height) // 2
        preview.alpha_composite(tile, (x, y))
        x += tile.width + gap

    # 再来一条深色底，确认透明背景下在深色任务栏上的观感。
    dark = Image.new("RGBA", (width, height), (32, 32, 32, 255))
    x = gap
    for tile in tiles:
        y = (height - tile.height) // 2
        dark.alpha_composite(tile, (x, y))
        x += tile.width + gap

    combo = Image.new("RGBA", (width, height * 2 + 8), (128, 128, 128, 255))
    combo.alpha_composite(preview, (0, 0))
    combo.alpha_composite(dark, (0, height + 8))
    combo.save(OUT_PREVIEW)
    print(f"preview -> {OUT_PREVIEW}  (sizes {scales}, 上=浅色底 下=深色底)")


def main():
    master = build_master()
    master.save(OUT_PNG)
    print(f"png -> {OUT_PNG}")

    # Pillow 会按 sizes 逐档重采样并写进同一个 ICO。
    master.save(OUT_ICO, format="ICO", sizes=ICO_SIZES)
    print(f"ico -> {OUT_ICO}  ({os.path.getsize(OUT_ICO)} bytes, sizes {[s[0] for s in ICO_SIZES]})")

    build_preview(master)


if __name__ == "__main__":
    main()
