from PIL import Image, ImageFilter
import numpy as np
import os
from collections import deque


def remove_connected_background(data, threshold=248):
    """只去除与图像边缘连通的纯白背景，保留角色内部白色高光。"""
    h, w = data.shape[:2]
    bg = (
        (data[:, :, 0] > threshold) &
        (data[:, :, 1] > threshold) &
        (data[:, :, 2] > threshold)
    )
    visited = np.zeros((h, w), dtype=bool)
    q = deque()

    # 从四个角开始蔓延
    seeds = [(0, 0), (0, w - 1), (h - 1, 0), (h - 1, w - 1)]
    for y, x in seeds:
        if 0 <= y < h and 0 <= x < w and bg[y, x] and not visited[y, x]:
            visited[y, x] = True
            q.append((y, x))

    while q:
        y, x = q.popleft()
        for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
            ny, nx = y + dy, x + dx
            if 0 <= ny < h and 0 <= nx < w and not visited[ny, nx] and bg[ny, nx]:
                visited[ny, nx] = True
                q.append((ny, nx))

    data[:, :, 3][visited] = 0
    return data


def remove_gray_watermark(data):
    """右下角豆包水印 -> 直接裁掉该区域 alpha。损失一点裙摆边角，但彻底干净。"""
    h, w = data.shape[:2]
    y_start = max(0, h - 90)
    x_start = max(0, w - 500)
    data[y_start:, x_start:, 3] = 0
    return data


def process_image(input_path, output_path,
                  bg_threshold=248,
                  watermark_crop_bottom=55,
                  edge_shrink=1,
                  pad=12,
                  target_h=1024):
    img = Image.open(input_path).convert('RGBA')
    w, h = img.size

    # 保守 crop 底部水印条
    if watermark_crop_bottom > 0 and h > watermark_crop_bottom + 100:
        img = img.crop((0, 0, w, h - watermark_crop_bottom))
        w, h = img.size

    data = np.array(img)

    # 1. 去连通白背景（保留人物内部高光）
    data = remove_connected_background(data, bg_threshold)

    # 2. 去右下角灰水印
    data = remove_gray_watermark(data)

    out = Image.fromarray(data)

    # 3. 轻微收缩 alpha 边缘，去掉残留白边
    if edge_shrink > 0:
        alpha = out.split()[-1]
        for _ in range(edge_shrink):
            alpha = alpha.filter(ImageFilter.MinFilter(3))
        out.putalpha(alpha)

    # 4. 裁切到内容
    bbox = out.getbbox()
    if bbox:
        out = out.crop(bbox)

    # 5. 加透明 padding
    padded = Image.new('RGBA', (out.width + pad * 2, out.height + pad * 2), (0, 0, 0, 0))
    padded.paste(out, (pad, pad))

    # 6. 统一高度
    ratio = target_h / padded.height
    final = padded.resize((int(padded.width * ratio), target_h), Image.LANCZOS)

    final.save(output_path)
    print(f"saved {output_path}: {final.size}")


if __name__ == '__main__':
    src_dir = 'F:/AI-assets/lora训练/shuimo'
    dst_dir = 'F:/AI-project/ancientGame/shuimofeng/shuimofeng/Assets/_Project/Art/Characters/Heroine2D'
    os.makedirs(dst_dir, exist_ok=True)

    jobs = [
        ('四肢张开.png', 'heroine_base_open.png'),
        ('正常正视图.png', 'heroine_base_front.png'),
        ('入仙.png', 'heroine_xian.png'),
        ('入魔.png', 'heroine_mo.png'),
        ('正常侧视图.png', 'heroine_base_side.png'),
        ('三视图.png', 'heroine_tri.png'),
    ]

    for src, dst in jobs:
        process_image(
            os.path.join(src_dir, src),
            os.path.join(dst_dir, dst),
            watermark_crop_bottom=70,
        )
