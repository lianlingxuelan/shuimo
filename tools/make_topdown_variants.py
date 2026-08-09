# -*- coding: utf-8 -*-
"""美术阶段 B2：俯视角提示词三变体横向试错 —— 后处理与三联对比。

严格复用上一轮 tools/make_heroine_sample.py 的两段式 flood fill 抠底 + 裁边
+ LANCZOS 等比缩放逻辑，新增：
  1) 角标水印移除（AI 出图常在右下角带「AI生成」类角标，原底部条带检测抓不到）；
  2) 三联对比图：每变体一列，上=原图缩略(256宽)，下=48x64 成品的 8 倍最近邻放大
     (384x512)，列间 24px，棋盘格底，列顶 12px 色条区分（朱砂红/墨黑/灰），无文字。

输入：Assets/images/_raw_gen/phaseB2/ 下 3 张原始图（按生成时间 V1<V2<V3）。
输出（同目录）：
  v1_topdown_48x64.png
  v2_angle45_48x64.png
  v3_silhouette_48x64.png
  _compare_variants.png
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Dict, List, Optional, Tuple

import numpy as np
from PIL import Image

# 复用上一轮脚本的实现（同目录，导入不会触发其 __main__）。
_TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
if _TOOLS_DIR not in sys.path:
    sys.path.insert(0, _TOOLS_DIR)
import make_heroine_sample as h  # noqa: E402

# --------------------------------------------------------------------------------------
# 路径与变体定义
# --------------------------------------------------------------------------------------

PROJECT_ROOT: Path = Path("F:/AI-project/ancientGame/shuimofeng/shuimofeng")
RAW_DIR: Path = PROJECT_ROOT / "Assets" / "images" / "_raw_gen" / "phaseB2"

TARGET: Tuple[int, int] = (48, 64)

# (文件名关键字, 变体标签, 成品名, 列顶色条 RGBA)
# 顺序即最终三联对比图的列顺序：V1 朱砂红 / V2 墨黑 / V3 灰。
VARIANTS: List[Tuple[str, str, str, Tuple[int, int, int, int]]] = [
    ("top_down_RPG_game_sprite", "V1", "v1_topdown_48x64.png", (196, 60, 48, 255)),
    ("45_degree_high_angle_overhead", "V2", "v2_angle45_48x64.png", (20, 20, 20, 255)),
    ("minimal_ink_silhouette", "V3", "v3_silhouette_48x64.png", (128, 128, 128, 255)),
]

# 角标水印：相对整图面积占比低于该值、且锚定在四角的连通块判定为水印移除。
CORNER_WM_MAX_AREA_RATIO: float = 0.02

# 对比图布局
COL_W: int = TARGET[0] * 8          # 384（成品 8 倍放大宽度）
PROD_W: int = TARGET[0] * 8         # 384
PROD_H: int = TARGET[1] * 8         # 512
BAR_H: int = 12                     # 列顶色条高
THUMB_W: int = 256                  # 原图缩略宽
GAP_X: int = 24                     # 列间距
GAP_ROW: int = 16                   # 缩略与成品行间距
GAP_BAR: int = 8                    # 色条与缩略间距
PAD: int = 24                       # 画布外边距


# --------------------------------------------------------------------------------------
# 角标水印移除（原底部条带检测的补强）
# --------------------------------------------------------------------------------------

def _flood4_from_corner(mask: np.ndarray, cy: int, cx: int) -> np.ndarray:
    """对布尔掩码做 4 邻接 flood fill，从角点 (cy, cx) 出发，返回连通块掩码。"""
    h, w = mask.shape
    comp = np.zeros_like(mask, dtype=bool)
    if not mask[cy, cx]:
        return comp
    stack = [(cy, cx)]
    comp[cy, cx] = True
    while stack:
        y, x = stack.pop()
        for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
            ny, nx = y + dy, x + dx
            if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not comp[ny, nx]:
                comp[ny, nx] = True
                stack.append((ny, nx))
    return comp


# 与 make_heroine_sample.ALPHA_CROP_THRESHOLD 保持一致，避免误删边缘半透明羽化。
ALPHA_TH: int = 10


def remove_corner_watermark(image: Image.Image, max_area_ratio: float = CORNER_WM_MAX_AREA_RATIO) -> Image.Image:
    """移除锚定在四角的独立小连通块（典型为「AI生成」类角标水印）。

    中心人物与角标在 alpha 上互不连通，故从四角 flood 得到的连通块若是小面积，
    必为角标/水印，直接置透明；不会误伤中心人物，也不会误伤 V3 的朱砂红点缀
    （点缀通常贴近人物、不在四角外缘带内）。

    Args:
        image: RGBA 图像（已抠底，水印为不透明像素）。
        max_area_ratio: 角块面积占比上限，超过则保留（避免误删大块内容）。

    Returns:
        处理后的 RGBA 图像。
    """
    arr = np.array(image).copy()
    alpha = arr[:, :, 3]
    ih, iw = alpha.shape
    total = float(ih * iw)
    corners = [(0, 0), (0, iw - 1), (ih - 1, 0), (ih - 1, iw - 1)]

    try:
        from scipy import ndimage  # type: ignore

        labels, _ = ndimage.label(alpha > ALPHA_TH)
        for cy, cx in corners:
            if alpha[cy, cx] <= ALPHA_TH:
                continue
            lab = int(labels[cy, cx])
            if lab == 0:
                continue
            area = int((labels == lab).sum())
            if area < max_area_ratio * total:
                arr[labels == lab, 3] = 0
    except Exception:
        mask = alpha > ALPHA_TH
        for cy, cx in corners:
            if not mask[cy, cx]:
                continue
            comp = _flood4_from_corner(mask, cy, cx)
            if int(comp.sum()) < max_area_ratio * total:
                arr[comp, 3] = 0

    return Image.fromarray(arr, mode="RGBA")


# --------------------------------------------------------------------------------------
# 单变体处理
# --------------------------------------------------------------------------------------

def process_variant(src: Path, out_name: str) -> Dict:
    """完整处理一个变体：抠底 -> 角标水印 -> 裁边 -> 底部水印 -> 缩放到 48x64。

    Args:
        src: 原始图路径。
        out_name: 成品文件名。

    Returns:
        含尺寸/统计/中间图像的字典。
    """
    with Image.open(src) as raw:
        raw.load()
        original_size = raw.size
        matted = h.remove_white_background(raw)

    matted = remove_corner_watermark(matted)
    cropped = h.crop_to_content(matted)
    cropped, wm_note = h.detect_and_strip_watermark(cropped)
    cropped_size = cropped.size

    # 视角代理指标：内容占整图面积比、裁剪框宽高比、质心偏移。
    arr = np.array(cropped)
    alpha = arr[:, :, 3]
    ys, xs = np.where(alpha > ALPHA_TH)
    if xs.size and ys.size:
        bbox_w = int(xs.max() - xs.min() + 1)
        bbox_h = int(ys.max() - ys.min() + 1)
        cx = float(xs.mean()) / cropped_size[0]
        cy = float(ys.mean()) / cropped_size[1]
    else:
        bbox_w = bbox_h = 1
        cx = cy = 0.5
    occupancy = float((alpha > ALPHA_TH).sum()) / float(alpha.size)
    aspect = bbox_w / float(bbox_h)

    final = h.fit_into_canvas(cropped, TARGET)
    final_size = final.size
    transparent_ratio, mean_luma = h.measure(final)

    out_path = RAW_DIR / out_name
    final.save(out_path, format="PNG")

    # 缩略图（用于对比图上行）：抠底+去水印后的内容，透明居中，LANCZOS 缩到 256 宽。
    thumb = cropped.resize((THUMB_W, max(int(round(THUMB_W * cropped_size[1] / cropped_size[0])), 1)),
                           Image.LANCZOS)

    return {
        "src": str(src),
        "out": str(out_path),
        "original_size": original_size,
        "cropped_size": cropped_size,
        "final_size": final_size,
        "watermark_note": wm_note,
        "transparent_ratio": transparent_ratio,
        "mean_luma": mean_luma,
        "occupancy": occupancy,        # 内容占整图比例（俯视小精灵应偏低）
        "aspect_w_over_h": aspect,     # 裁剪框宽高比（俯视≈1，立绘<1）
        "centroid": (round(cx, 3), round(cy, 3)),
        "_thumb": thumb,
        "_final": final,
    }


# --------------------------------------------------------------------------------------
# 三联对比图
# --------------------------------------------------------------------------------------

def build_compare(results: List[Dict], bar_colors: List[Tuple[int, int, int, int]], out_path: Path) -> Tuple[int, int]:
    """生成三列对比图：列顶色条 / 上行原图缩略(256宽) / 下行成品 8 倍最近邻放大。

    Args:
        results: 三个变体的处理结果（含 _thumb / _final）。
        bar_colors: 三列色条 RGBA。
        out_path: 输出路径。

    Returns:
        画布尺寸 (宽, 高)。
    """
    thumbs = [r["_thumb"] for r in results]
    finals = [r["_final"] for r in results]
    thumb_h_list = [t.height for t in thumbs]
    max_thumb_h = max(thumb_h_list) if thumb_h_list else THUMB_W

    col_content_h = BAR_H + GAP_BAR + max_thumb_h + GAP_ROW + PROD_H
    canvas_w = PAD * 2 + COL_W * len(results) + GAP_X * (len(results) - 1)
    canvas_h = PAD * 2 + col_content_h

    canvas = h.make_checkerboard((canvas_w, canvas_h))

    for i, (thumb, final, bar) in enumerate(zip(thumbs, finals, bar_colors)):
        x0 = PAD + i * (COL_W + GAP_X)
        # 列顶色条
        from PIL import ImageDraw
        ImageDraw.Draw(canvas).rectangle(
            [x0, PAD, x0 + COL_W, PAD + BAR_H], fill=bar
        )
        # 上行：原图缩略（透明居中于列宽）
        tx = x0 + (COL_W - thumb.width) // 2
        ty = PAD + BAR_H + GAP_BAR
        canvas.alpha_composite(thumb, (tx, ty))
        # 下行：成品 8 倍最近邻放大（384x512）
        px = x0 + (COL_W - PROD_W) // 2
        py = PAD + BAR_H + GAP_BAR + max_thumb_h + GAP_ROW
        canvas.alpha_composite(final, (px, py))

    out_path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(out_path, format="PNG")
    return canvas.size


# --------------------------------------------------------------------------------------
# 主流程
# --------------------------------------------------------------------------------------

def pick_variant_sources() -> List[Tuple[Path, str, str, Tuple[int, int, int, int]]]:
    """按关键字把原始图映射到变体（按文件名关键字匹配，稳定可靠）。"""
    files = sorted([p for p in RAW_DIR.glob("*.png") if p.is_file()],
                   key=lambda p: p.stat().st_mtime)
    matched: List[Tuple[Path, str, str, Tuple[int, int, int, int]]] = []
    for stem, tag, out_name, color in VARIANTS:
        hit = next((p for p in files if stem in p.name), None)
        if hit is None:
            print(f"[WARN] 未找到匹配 '{stem}' 的原始图")
            continue
        matched.append((hit, tag, out_name, color))
    return matched


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
    except Exception:
        pass

    matched = pick_variant_sources()
    if len(matched) < 3:
        print(f"[FATAL] 仅匹配到 {len(matched)} 个变体，需 3 个")
        return 2

    results: List[Dict] = []
    bar_colors: List[Tuple[int, int, int, int]] = []
    for src, tag, out_name, color in matched:
        print(f"[proc] {tag} <- {src.name}")
        res = process_variant(src, out_name)
        res["tag"] = tag
        results.append(res)
        bar_colors.append(color)
        print(f"       cropped={res['cropped_size']} final={res['final_size']} "
              f"wm='{res['watermark_note']}' occ={res['occupancy']:.3f} "
              f"aspect={res['aspect_w_over_h']:.3f} cen={res['centroid']}")

    compare_path = RAW_DIR / "_compare_variants.png"
    cw, ch = build_compare(results, bar_colors, compare_path)
    print(f"[compare] {compare_path.name}  {cw}x{ch}")

    # 结构化摘要（供校验）
    summary = {
        "compare": str(compare_path),
        "compare_size": [cw, ch],
        "variants": [
            {
                "tag": r["tag"],
                "out": r["out"],
                "final_size": list(r["final_size"]),
                "transparent_ratio": round(r["transparent_ratio"], 4),
                "mean_luma": round(r["mean_luma"], 2),
                "occupancy": round(r["occupancy"], 4),
                "aspect_w_over_h": round(r["aspect_w_over_h"], 4),
                "centroid": r["centroid"],
                "watermark_note": r["watermark_note"],
            }
            for r in results
        ],
    }
    (RAW_DIR / "_phaseB2_summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    # 一致性自检
    checks: Dict[str, bool] = {}
    checks["3 个变体均处理"] = len(results) == 3
    checks["3 张成品 = 48x64 且有真实透明"] = all(
        r["final_size"] == TARGET and r["transparent_ratio"] > 0.10 for r in results
    )
    checks["对比图可读"] = compare_path.exists() and compare_path.stat().st_size > 0
    print()
    print("自检")
    print("-" * 60)
    for name, ok in checks.items():
        print(f"  {'PASS' if ok else 'FAIL'}  {name}")
    all_pass = all(checks.values())
    print(f"IS_PASS: {'YES' if all_pass else 'NO'}")
    return 0 if all_pass else 1


if __name__ == "__main__":
    raise SystemExit(main())
