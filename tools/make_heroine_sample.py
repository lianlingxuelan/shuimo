# -*- coding: utf-8 -*-
"""水墨风女主验证样品后处理脚本（美术阶段 B）。

流程：抠底透明 -> 裁到内容边界 -> 水印检测与裁除 -> 等比缩放到目标尺寸 -> 输出成品 + 放大预览图。

用法::

    python make_heroine_sample.py

输入：Assets/images/_raw_gen/ 下按修改时间排序的两张原始图（先 idle 后 attack）。
输出：Assets/images/characters/heroine/ 下的两张成品 + 一张 8 倍放大并排预览图。
"""

from __future__ import annotations

import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Optional, Tuple

import numpy as np
from PIL import Image

# --------------------------------------------------------------------------------------
# 常量配置
# --------------------------------------------------------------------------------------

PROJECT_ROOT: Path = Path("F:/AI-project/ancientGame/shuimofeng/shuimofeng")
RAW_DIR: Path = PROJECT_ROOT / "Assets" / "images" / "_raw_gen"
OUT_DIR: Path = PROJECT_ROOT / "Assets" / "images" / "characters" / "heroine"

# 抠底阈值：亮度高于该值且饱和度低于该值的像素才视为“背景候选”。
# 水墨画浅灰极多，阈值取保守值，宁可留浅灰也不抠穿墨色。
BG_BRIGHTNESS_MIN: int = 240
BG_SATURATION_MAX: int = 18

# 边缘羽化：对刚好卡在阈值边界的像素做一次半透明过渡，避免硬锯齿。
FEATHER_BRIGHTNESS_MIN: int = 228

# 边缘主导色采样（解决 AI 出图为浅灰底而非纯白底的情况）：
# 取四边 1.5% 厚度的像素做众数估计，作为“主背景色”参考。
EDGE_SAMPLE_RATIO: float = 0.015
# 与边缘最亮像素的“最大灰度差”容差：用于把灰渐变底（120~200）一并覆盖。
# 80 对水墨人物中等灰度足够安全，对纯黑/朱砂不会误抠。
EDGE_BAND_WIDTH: int = 80

ALPHA_CROP_THRESHOLD: int = 10          # 裁边时判定“有内容”的 alpha 下限
WATERMARK_BAND_RATIO: float = 0.08      # 水印检测区：底部 8%
WATERMARK_MAX_HEIGHT_RATIO: float = 0.10  # 认定为水印的最大高度占比
FALLBACK_BOTTOM_CROP_RATIO: float = 0.05  # 疑似但判定不准时的保守裁切比例

PREVIEW_SCALE: int = 8
PREVIEW_PAD: int = 32
PREVIEW_GAP: int = 32
CHECKER_SIZE: int = 16
CHECKER_LIGHT: Tuple[int, int, int] = (255, 255, 255)
CHECKER_DARK: Tuple[int, int, int] = (222, 222, 222)
BORDER_COLOR: Tuple[int, int, int, int] = (196, 60, 48, 255)  # 朱砂红细边框，标示精灵画布边界

TARGETS: List[Tuple[str, Tuple[int, int], str]] = [
    ("idle", (48, 64), "heroine_idle_sample.png"),
    ("attack", (96, 64), "heroine_attack_sample.png"),
]


@dataclass
class ProcessStat:
    """单张图的处理统计信息。"""

    tag: str = ""
    source: str = ""
    original_size: Tuple[int, int] = (0, 0)
    cropped_size: Tuple[int, int] = (0, 0)
    final_size: Tuple[int, int] = (0, 0)
    watermark_note: str = "未检测到水印"
    transparent_ratio: float = 0.0
    mean_luma: float = 0.0
    output_path: str = ""
    warnings: List[str] = field(default_factory=list)


# --------------------------------------------------------------------------------------
# 抠底
# --------------------------------------------------------------------------------------

def _background_candidate_mask(rgb: np.ndarray, brightness_min: int) -> np.ndarray:
    """返回“背景候选”布尔掩码：足够亮且足够灰（低饱和度）的像素。

    Args:
        rgb: 形状 (H, W, 3) 的 uint8 数组。
        brightness_min: 亮度下限（取 RGB 最小通道值，避免把偏色高亮误判）。

    Returns:
        形状 (H, W) 的布尔数组，True 表示可能是背景。
    """
    arr = rgb.astype(np.int16)
    channel_max = arr.max(axis=2)
    channel_min = arr.min(axis=2)
    saturation = channel_max - channel_min
    return (channel_min >= brightness_min) & (saturation <= BG_SATURATION_MAX)


def _flood_from_borders(mask: np.ndarray) -> np.ndarray:
    """从四条边界向内做连通域扩散（flood fill），只保留与画面外缘连通的背景。

    优先使用 scipy 的 binary_propagation（C 实现，快）；不可用时回退到纯 numpy 的
    逐像素膨胀循环，结果完全等价，只是慢一些。

    Args:
        mask: 背景候选布尔掩码，形状 (H, W)。

    Returns:
        与外缘连通的背景布尔掩码，形状 (H, W)。
    """
    seeds = np.zeros_like(mask, dtype=bool)
    seeds[0, :] = mask[0, :]
    seeds[-1, :] = mask[-1, :]
    seeds[:, 0] = mask[:, 0]
    seeds[:, -1] = mask[:, -1]

    if not seeds.any():
        return np.zeros_like(mask, dtype=bool)

    try:
        from scipy import ndimage  # type: ignore

        structure = np.array([[0, 1, 0], [1, 1, 1], [0, 1, 0]], dtype=bool)
        return ndimage.binary_propagation(seeds, mask=mask, structure=structure)
    except Exception:  # pragma: no cover - 仅在无 scipy 环境下走到
        pass

    filled = seeds.copy()
    height, width = mask.shape
    max_iterations = height + width + 8
    for _ in range(max_iterations):
        grown = filled.copy()
        grown[1:, :] |= filled[:-1, :]
        grown[:-1, :] |= filled[1:, :]
        grown[:, 1:] |= filled[:, :-1]
        grown[:, :-1] |= filled[:, 1:]
        grown &= mask
        if np.array_equal(grown, filled):
            break
        filled = grown
    return filled


def _sample_edge_band_range(rgb: np.ndarray) -> Tuple[int, int]:
    """从图像四边采样，推断背景灰度上下界。

    AI 生成的水墨精灵常以 120~200 的灰渐变作为背景，此时单点主导色 + 欧氏距离
    容差会漏掉大半背景。本函数返回四边采样的 minchan 范围 [low, high]，
    抠底时只要像素的最小通道值落在 [low, high] 内即视作背景候选。

    Args:
        rgb: 形状 (H, W, 3) 的 uint8 数组。

    Returns:
        (low, high) 0~255 的背景灰度上下界，包含边界。
    """
    height, width = rgb.shape[:2]
    band_h = max(int(height * EDGE_SAMPLE_RATIO), 1)
    band_w = max(int(width * EDGE_SAMPLE_RATIO), 1)
    strips: List[np.ndarray] = [
        rgb[:band_h, :, :].reshape(-1, 3),
        rgb[-band_h:, :, :].reshape(-1, 3),
        rgb[:, :band_w, :].reshape(-1, 3),
        rgb[:, -band_w:, :].reshape(-1, 3),
    ]
    edge = np.concatenate(strips).astype(np.int16)
    if edge.size == 0:
        return (240, 255)

    minchan = edge.min(axis=1)
    # 排除极亮极暗两端（极亮可能是白边羽毛，极暗可能是水墨溅点），取中段众数区间。
    p_low = float(np.percentile(minchan, 5))
    p_high = float(np.percentile(minchan, 95))
    if p_high - p_low < 20:
        # 边缘过于均匀（说明是真正的纯白底），用一个安全宽区间。
        p_high = min(float(minchan.max()) + EDGE_BAND_WIDTH, 255)
        p_low = max(p_high - EDGE_BAND_WIDTH * 2, 0)
    return (int(p_low), int(p_high))


def remove_white_background(image: Image.Image) -> Image.Image:
    """两段式抠底：先纯白/高亮灰，再叠加边缘灰度区间。保留人物内部浅灰墨色。

    Args:
        image: 任意模式的 PIL 图像。

    Returns:
        RGBA 模式的 PIL 图像，背景 alpha 为 0，边缘做半透明羽化。
    """
    rgba = image.convert("RGBA")
    arr = np.array(rgba)
    rgb = arr[:, :, :3]

    hard_bg = _flood_from_borders(_background_candidate_mask(rgb, BG_BRIGHTNESS_MIN))
    # 边缘灰度区间：处理 AI 把背景渲染成浅灰渐变而非纯白的情况（idle 那张就是 ~120-200 灰底）。
    low, high = _sample_edge_band_range(rgb)
    if low < high:
        minchan = rgb.min(axis=2)
        # 与外缘连通的、灰度落在边缘区间的像素一并视作背景
        edge_bg = _flood_from_borders((minchan >= low) & (minchan <= high))
        hard_bg |= edge_bg

    # 羽化带：亮度略低于硬阈值、但仍与外缘连通的像素，做部分透明过渡。
    soft_bg = _flood_from_borders(_background_candidate_mask(rgb, FEATHER_BRIGHTNESS_MIN))
    feather = soft_bg & ~hard_bg

    alpha = arr[:, :, 3].astype(np.int16)
    alpha[hard_bg] = 0

    if feather.any():
        luma = rgb.astype(np.float32).min(axis=2)
        span = float(max(BG_BRIGHTNESS_MIN - FEATHER_BRIGHTNESS_MIN, 1))
        ratio = np.clip((BG_BRIGHTNESS_MIN - luma) / span, 0.0, 1.0)
        alpha[feather] = np.minimum(alpha[feather], (ratio[feather] * 255.0).astype(np.int16))

    arr[:, :, 3] = np.clip(alpha, 0, 255).astype(np.uint8)
    return Image.fromarray(arr, mode="RGBA")


# --------------------------------------------------------------------------------------
# 裁边与水印处理
# --------------------------------------------------------------------------------------

def crop_to_content(image: Image.Image) -> Image.Image:
    """按 alpha > 阈值 求 bounding box 裁掉透明边。

    Args:
        image: RGBA 图像。

    Returns:
        裁剪后的 RGBA 图像；若整图透明则原样返回。
    """
    alpha = np.array(image)[:, :, 3]
    ys, xs = np.where(alpha > ALPHA_CROP_THRESHOLD)
    if ys.size == 0 or xs.size == 0:
        return image
    return image.crop((int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1))


def detect_and_strip_watermark(image: Image.Image) -> Tuple[Image.Image, str]:
    """检测并裁掉底部水印条带。

    启发式判定顺序：
      1. 底部若存在与主体被完整透明间隙隔开的独立小块（高度 < 10%），判定为水印，精确裁掉；
      2. 底部条带若呈现“规则纯色块”特征（多数非透明像素颜色高度一致），判定为水印并裁掉；
      3. 疑似但无法确认时，保守裁掉底部 5%；
      4. 明确干净则不裁。

    Args:
        image: 已裁到内容边界的 RGBA 图像。

    Returns:
        (处理后的图像, 处理说明文本)。
    """
    arr = np.array(image)
    height, width = arr.shape[:2]
    if height < 20:
        return image, "图像过矮，跳过水印检测"

    alpha = arr[:, :, 3]
    row_coverage = (alpha > ALPHA_CROP_THRESHOLD).sum(axis=1) / float(width)

    band_start = int(height * (1.0 - WATERMARK_BAND_RATIO))
    max_wm_height = max(int(height * WATERMARK_MAX_HEIGHT_RATIO), 1)

    # 规则 1：从底部往上找“空行间隙”，间隙以下若是独立小块，即为水印。
    search_from = max(int(height * 0.80), 1)
    empty_rows = [r for r in range(search_from, height) if row_coverage[r] <= 0.005]
    if empty_rows:
        gap_end = max(empty_rows)
        blob_height = height - gap_end - 1
        if 0 < blob_height <= max_wm_height and row_coverage[gap_end + 1:].max(initial=0.0) > 0.0:
            gap_start = gap_end
            while gap_start - 1 >= search_from and row_coverage[gap_start - 1] <= 0.005:
                gap_start -= 1
            cropped = image.crop((0, 0, width, gap_start))
            return crop_to_content(cropped), f"检测到底部独立块（高 {blob_height}px，与主体有透明间隙），判定为水印并已裁除"

    # 规则 2：底部条带的“纯色规则块”特征。
    band = arr[band_start:, :, :]
    band_alpha = band[:, :, 3]
    opaque = band_alpha > 128
    opaque_count = int(opaque.sum())
    if opaque_count >= 60:
        colors = band[:, :, :3][opaque].astype(np.float32)
        color_std = float(colors.std(axis=0).mean())
        saturation = float((colors.max(axis=1) - colors.min(axis=1)).mean())
        band_rows = band_alpha.shape[0]
        band_coverage = row_coverage[band_start:]
        # 纯色 + 覆盖率突变 => 疑似 logo / 文字块
        upper_ref = float(row_coverage[max(band_start - band_rows, 0):band_start].mean()) if band_start > 0 else 0.0
        coverage_jump = abs(float(band_coverage.mean()) - upper_ref)
        if color_std < 6.0 and saturation > 40.0 and coverage_jump > 0.12:
            cropped = image.crop((0, 0, width, band_start))
            return crop_to_content(cropped), f"底部条带呈规则纯色块（色彩标准差 {color_std:.1f}，饱和度 {saturation:.1f}），判定为水印并已裁除"
        if color_std < 4.0 and coverage_jump > 0.25:
            cut = height - max(int(height * FALLBACK_BOTTOM_CROP_RATIO), 1)
            cropped = image.crop((0, 0, width, cut))
            return crop_to_content(cropped), "底部条带疑似水印但特征不明确，保守裁掉底部 5%"

    return image, "未检测到水印（底部 8% 区域内容与水墨主体连续，未做额外裁切）"


# --------------------------------------------------------------------------------------
# 缩放与输出
# --------------------------------------------------------------------------------------

def fit_into_canvas(image: Image.Image, target: Tuple[int, int]) -> Image.Image:
    """等比缩放并居中放入指定尺寸的透明画布。

    Args:
        image: RGBA 图像。
        target: (宽, 高) 目标尺寸。

    Returns:
        精确等于 target 尺寸的 RGBA 图像。
    """
    target_w, target_h = target
    src_w, src_h = image.size
    if src_w <= 0 or src_h <= 0:
        return Image.new("RGBA", target, (0, 0, 0, 0))

    scale = min(target_w / float(src_w), target_h / float(src_h))
    new_w = max(int(round(src_w * scale)), 1)
    new_h = max(int(round(src_h * scale)), 1)
    new_w = min(new_w, target_w)
    new_h = min(new_h, target_h)

    resized = image.resize((new_w, new_h), Image.LANCZOS)
    canvas = Image.new("RGBA", target, (0, 0, 0, 0))
    canvas.paste(resized, ((target_w - new_w) // 2, (target_h - new_h) // 2), resized)
    return canvas


def measure(image: Image.Image) -> Tuple[float, float]:
    """统计透明像素占比与非透明区域平均亮度。

    Args:
        image: RGBA 图像。

    Returns:
        (透明像素占比 0~1, 非透明区域平均亮度 0~255)。
    """
    arr = np.array(image).astype(np.float32)
    alpha = arr[:, :, 3]
    total = float(alpha.size)
    transparent_ratio = float((alpha <= ALPHA_CROP_THRESHOLD).sum()) / total if total else 0.0

    visible = alpha > ALPHA_CROP_THRESHOLD
    if not visible.any():
        return transparent_ratio, 0.0
    rgb = arr[:, :, :3]
    luma = 0.299 * rgb[:, :, 0] + 0.587 * rgb[:, :, 1] + 0.114 * rgb[:, :, 2]
    return transparent_ratio, float(luma[visible].mean())


def make_checkerboard(size: Tuple[int, int]) -> Image.Image:
    """生成棋盘格背景，用于在预览图上表示透明区域。

    Args:
        size: (宽, 高)。

    Returns:
        RGBA 棋盘格图像。
    """
    width, height = size
    xs = (np.arange(width) // CHECKER_SIZE)[None, :]
    ys = (np.arange(height) // CHECKER_SIZE)[:, None]
    dark = ((xs + ys) % 2).astype(bool)
    board = np.empty((height, width, 4), dtype=np.uint8)
    board[:, :, :3] = np.where(dark[:, :, None], np.array(CHECKER_DARK, dtype=np.uint8),
                               np.array(CHECKER_LIGHT, dtype=np.uint8))
    board[:, :, 3] = 255
    return Image.fromarray(board, mode="RGBA")


def draw_border(canvas: Image.Image, box: Tuple[int, int, int, int]) -> None:
    """在预览画布上画 1px 边框，标示精灵画布边界。

    Args:
        canvas: 目标 RGBA 画布（原地修改）。
        box: (left, top, right, bottom)，right/bottom 为开区间。
    """
    from PIL import ImageDraw

    draw = ImageDraw.Draw(canvas)
    draw.rectangle([box[0] - 1, box[1] - 1, box[2], box[3]], outline=BORDER_COLOR, width=1)


def build_preview(sprites: List[Image.Image], out_path: Path) -> Tuple[int, int]:
    """把成品按 8 倍最近邻放大后左右并排，输出棋盘格底预览图。

    Args:
        sprites: 成品 RGBA 图像列表（按 idle, attack 顺序）。
        out_path: 输出文件路径。

    Returns:
        预览图尺寸 (宽, 高)。
    """
    scaled = [s.resize((s.width * PREVIEW_SCALE, s.height * PREVIEW_SCALE), Image.NEAREST) for s in sprites]
    total_w = PREVIEW_PAD * 2 + sum(s.width for s in scaled) + PREVIEW_GAP * (len(scaled) - 1)
    total_h = PREVIEW_PAD * 2 + max(s.height for s in scaled)

    canvas = make_checkerboard((total_w, total_h))
    cursor_x = PREVIEW_PAD
    for sprite in scaled:
        top = PREVIEW_PAD + (total_h - PREVIEW_PAD * 2 - sprite.height) // 2
        canvas.alpha_composite(sprite, (cursor_x, top))
        draw_border(canvas, (cursor_x, top, cursor_x + sprite.width, top + sprite.height))
        cursor_x += sprite.width + PREVIEW_GAP

    out_path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(out_path, format="PNG")
    return canvas.size


# --------------------------------------------------------------------------------------
# 主流程
# --------------------------------------------------------------------------------------

def pick_raw_images() -> List[Path]:
    """按修改时间升序取原始图（先生成的是 idle，后生成的是 attack）。

    Returns:
        原始图路径列表。
    """
    files = sorted(
        [p for p in RAW_DIR.glob("*.png") if p.is_file()],
        key=lambda p: p.stat().st_mtime,
    )
    return files


def process_one(src: Path, tag: str, target: Tuple[int, int], out_name: str) -> Tuple[ProcessStat, Image.Image]:
    """完整处理一张原始图并落盘成品。

    Args:
        src: 原始图路径。
        tag: 标签（idle / attack）。
        target: 目标尺寸 (宽, 高)。
        out_name: 输出文件名。

    Returns:
        (统计信息, 成品图像)。
    """
    stat = ProcessStat(tag=tag, source=str(src))
    with Image.open(src) as raw:
        raw.load()
        stat.original_size = raw.size
        cut = remove_white_background(raw)

    cropped = crop_to_content(cut)
    cropped, note = detect_and_strip_watermark(cropped)
    stat.cropped_size = cropped.size
    stat.watermark_note = note

    final = fit_into_canvas(cropped, target)
    stat.final_size = final.size
    stat.transparent_ratio, stat.mean_luma = measure(final)

    out_path = OUT_DIR / out_name
    out_path.parent.mkdir(parents=True, exist_ok=True)
    final.save(out_path, format="PNG")
    stat.output_path = str(out_path)

    if stat.final_size != target:
        stat.warnings.append(f"最终尺寸 {stat.final_size} 与目标 {target} 不符")
    if stat.transparent_ratio <= 0.15:
        stat.warnings.append(f"透明像素占比仅 {stat.transparent_ratio:.1%}，可能抠底未生效")
    return stat, final


def main() -> int:
    """脚本入口。

    Returns:
        进程退出码，0 表示全部检查通过。
    """
    try:
        sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
    except Exception:
        pass

    raws = pick_raw_images()
    if len(raws) < 2:
        print(f"[FATAL] 原始图不足 2 张，实际 {len(raws)} 张，目录：{RAW_DIR}")
        return 2

    selected = raws[:2]
    print("=" * 78)
    print("原始图（按生成时间排序）：")
    for i, p in enumerate(selected):
        print(f"  [{i}] {p.name}")
    print("=" * 78)

    stats: List[ProcessStat] = []
    sprites: List[Image.Image] = []
    for (tag, target, out_name), src in zip(TARGETS, selected):
        stat, sprite = process_one(src, tag, target, out_name)
        stats.append(stat)
        sprites.append(sprite)

    preview_path = OUT_DIR / "_preview_heroine_sample.png"
    preview_size = build_preview(sprites, preview_path)

    print()
    print("处理统计")
    print("-" * 78)
    header = f"{'标签':<8}{'原始尺寸':<14}{'裁后尺寸':<14}{'最终尺寸':<12}{'透明占比':<10}{'平均亮度':<10}"
    print(header)
    for s in stats:
        print(
            f"{s.tag:<8}"
            f"{s.original_size[0]}x{s.original_size[1]:<9}"
            f"{s.cropped_size[0]}x{s.cropped_size[1]:<9}"
            f"{s.final_size[0]}x{s.final_size[1]:<7}"
            f"{s.transparent_ratio * 100:>6.1f}%   "
            f"{s.mean_luma:>7.1f}"
        )
    print("-" * 78)

    for s in stats:
        print(f"[{s.tag}] 水印处理：{s.watermark_note}")
        print(f"[{s.tag}] 成品输出：{s.output_path}")
        for w in s.warnings:
            print(f"[{s.tag}] !! 警告：{w}")

    print(f"[preview] 预览图：{preview_path}  尺寸 {preview_size[0]}x{preview_size[1]}")

    # 一致性自检
    checks: Dict[str, bool] = {}
    checks["原始图 2 张存在"] = len(selected) == 2 and all(p.exists() for p in selected)
    checks["idle 成品 = 48x64"] = stats[0].final_size == (48, 64)
    checks["attack 成品 = 96x64"] = stats[1].final_size == (96, 64)
    checks["成品透明占比 > 15%"] = all(s.transparent_ratio > 0.15 for s in stats)
    checks["预览图可读"] = preview_path.exists() and preview_path.stat().st_size > 0

    print()
    print("自检结果")
    print("-" * 78)
    for name, ok in checks.items():
        print(f"  {'PASS' if ok else 'FAIL'}  {name}")

    all_pass = all(checks.values())
    print()
    print(f"IS_PASS: {'YES' if all_pass else 'NO'}")
    return 0 if all_pass else 1


if __name__ == "__main__":
    raise SystemExit(main())
