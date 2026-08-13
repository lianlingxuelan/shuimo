from PIL import Image, ImageFilter
import numpy as np
import os
from collections import deque


def remove_black_bg(data, threshold=20):
    """把与四角连通的纯黑背景变透明，保留被亮色轮廓线隔开的黑色衣服。"""
    h, w = data.shape[:2]
    # 背景候选：很暗的像素
    bg = (
        (data[:, :, 0] < threshold) &
        (data[:, :, 1] < threshold) &
        (data[:, :, 2] < threshold)
    )
    visited = np.zeros((h, w), dtype=bool)
    q = deque()

    # 从四条边的所有暗像素开始蔓延
    edges = []
    edges += [(0, x) for x in range(w)]
    edges += [(h - 1, x) for x in range(w)]
    edges += [(y, 0) for y in range(1, h - 1)]
    edges += [(y, w - 1) for y in range(1, h - 1)]

    for y, x in edges:
        if bg[y, x] and not visited[y, x]:
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


def process_jpg(input_path, output_path,
                edge_shrink=1,
                pad=12,
                target_h=1024):
    img = Image.open(input_path).convert('RGBA')
    data = np.array(img)

    # 1. 去黑底
    data = remove_black_bg(data, threshold=20)

    out = Image.fromarray(data)

    # 2. 轻微收缩 alpha 边缘去杂边
    if edge_shrink > 0:
        alpha = out.split()[-1]
        for _ in range(edge_shrink):
            alpha = alpha.filter(ImageFilter.MinFilter(3))
        out.putalpha(alpha)

    # 3. 裁切到内容
    bbox = out.getbbox()
    if bbox:
        out = out.crop(bbox)

    # 4. 加透明 padding
    padded = Image.new('RGBA', (out.width + pad * 2, out.height + pad * 2), (0, 0, 0, 0))
    padded.paste(out, (pad, pad))

    # 5. 统一高度
    ratio = target_h / padded.height
    final = padded.resize((int(padded.width * ratio), target_h), Image.LANCZOS)

    final.save(output_path)
    print(f"saved {output_path}: {final.size}")


if __name__ == '__main__':
    src_dir = 'F:/AI-assets/lora训练/shuimo'
    dst_dir = 'F:/AI-project/ancientGame/shuimofeng/shuimofeng/Assets/_Project/Art/Characters/Heroine2D'
    os.makedirs(dst_dir, exist_ok=True)

    jobs = [
        ('四肢张开去背景.jpg', 'heroine_base_open.png'),
        ('正常正视图去背景.jpg', 'heroine_base_front.png'),
        ('正常侧视图去背景.jpg', 'heroine_base_side.png'),
        ('入仙去背景.jpg', 'heroine_xian.png'),
        ('入魔去背景.jpg', 'heroine_mo.png'),
        ('三视图正面去背景.jpg', 'heroine_tri_front.png'),
        ('三视图侧面去背景.jpg', 'heroine_tri_side.png'),
        ('三视图背面去背景.jpg', 'heroine_tri_back.png'),
    ]

    for src, dst in jobs:
        process_jpg(
            os.path.join(src_dir, src),
            os.path.join(dst_dir, dst),
        )
