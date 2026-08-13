from PIL import Image, ImageFilter
import numpy as np
import os


def process_image(input_path, output_path,
                  white_threshold=235,
                  gray_low=50, gray_high=210,
                  watermark_crop_bottom=55,
                  edge_shrink=1,
                  pad=12):
    """
    去白底 + 去右下角灰水印 + 裁切 + padding。
    """
    img = Image.open(input_path).convert('RGBA')
    w, h = img.size

    # 1. 保守 crop 底部水印条
    if watermark_crop_bottom > 0 and h > watermark_crop_bottom + 100:
        img = img.crop((0, 0, w, h - watermark_crop_bottom))
        w, h = img.size

    data = np.array(img)
    r, g, b, a = data.T

    # 2. 纯白/近白背景 -> 透明
    white_mask = (r > white_threshold) & (g > white_threshold) & (b > white_threshold)
    data[..., 3][white_mask.T] = 0

    # 3. 右下角残留灰水印 -> 透明（只在右下 1/3 区域，避免误伤阴影）
    gray_mask = (
        (np.abs(r.astype(np.int16) - g.astype(np.int16)) < 35) &
        (np.abs(g.astype(np.int16) - b.astype(np.int16)) < 35) &
        (np.abs(r.astype(np.int16) - b.astype(np.int16)) < 35) &
        (r > gray_low) & (r < gray_high) &
        (g > gray_low) & (g < gray_high) &
        (b > gray_low) & (b < gray_high)
    )
    region_h = h // 3
    region_w = w // 3
    y_start = max(0, h - region_h)
    x_start = max(0, w - region_w)
    gray_region = np.zeros_like(gray_mask)
    gray_region[y_start:, x_start:] = gray_mask[y_start:, x_start:]
    data[..., 3][gray_region.T] = 0

    out = Image.fromarray(data)

    # 4. 轻微收缩 alpha 边缘，去掉残留白边
    if edge_shrink > 0:
        alpha = out.split()[-1]
        for _ in range(edge_shrink):
            alpha = alpha.filter(ImageFilter.MinFilter(3))
        out.putalpha(alpha)

    # 5. 裁切到内容 bbox
    bbox = out.getbbox()
    if bbox:
        out = out.crop(bbox)

    # 6. 加透明 padding，方便 Unity 里定位
    padded = Image.new('RGBA', (out.width + pad * 2, out.height + pad * 2), (0, 0, 0, 0))
    padded.paste(out, (pad, pad))

    # 7. 缩放至 2D 骨骼常用尺寸（高度约 1024，宽度自适应）
    target_h = 1024
    ratio = target_h / padded.height
    new_size = (int(padded.width * ratio), target_h)
    final = padded.resize(new_size, Image.LANCZOS)

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
            watermark_crop_bottom=55,
        )
