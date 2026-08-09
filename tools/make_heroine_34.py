# -*- coding: utf-8 -*-
"""美术阶段 C：3/4 视角女主验证批次（idle / walk_down / attack）后处理与客观测量。

背景
----
阶段 B2 结论：ImageGen 对「水墨仙侠女修」有极强的正面立绘先验，纯俯视提示词压不出来
（V1 可见占比仅 8.43% 不可辨，V2/V3 占比 ~33% 但仍是正面立绘）。阶段 C 转 3/4 视角
路线：角色保留正面朝向、控制画幅内占比、并用 img2img 加跨帧一致性约束。

流程
----
1. 自适应两段式 flood fill 抠底（阈值按每张图的边缘底色实测自适应，覆盖阶段 B2 观测到的
   近白 ~237 / 中灰 ~196 / 深灰 ~104 三档底色）；
2. 角标水印移除 + 底部水印条带裁除；
3. 裁到内容边界，LANCZOS 等比缩放进目标画布（RGBA 保留）；
4. Pillow 客观测量：尺寸 / 模式 / 透明占比 / 可见区 bbox / 可见区占画幅比 / 墨色统计；
5. 跨帧一致性量化：行覆盖剖面相关系数、主色板、朱砂红占比、发区均色；
6. 输出 8 倍棋盘格放大预览图 + `_phaseC_summary.json` + `_sizes.json`。

输入
----
`Assets/images/_raw_gen/phaseC/gen_idle|gen_walk|gen_attack/` 各自目录下最新的 PNG
（每帧独立 output_dir，避免文件名匹配歧义与并发串位）。

输出
----
* 成品：`Assets/images/characters/heroine/heroine_{idle,walk_down,attack}_34.png`
* 预览：`Assets/images/_raw_gen/phaseC/_preview_heroine_34.png`
* 数据：`Assets/images/_raw_gen/phaseC/_phaseC_summary.json`、`_sizes.json`

用法::

    python make_heroine_34.py --stage idle      # 只处理 idle（供后续 img2img 取参考）
    python make_heroine_34.py --stage all       # 三帧全处理 + 预览 + JSON
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Tuple

import numpy as np
from PIL import Image, ImageDraw

# 复用阶段 B / B2 已验证的实现（同目录导入，不会触发它们的 __main__）。
_TOOLS_DIR: str = os.path.dirname(os.path.abspath(__file__))
if _TOOLS_DIR not in sys.path:
    sys.path.insert(0, _TOOLS_DIR)

import make_heroine_sample as h          # noqa: E402  抠底/裁边/水印/缩放/棋盘格
import make_topdown_variants as tv       # noqa: E402  角标水印移除

# --------------------------------------------------------------------------------------
# 路径与帧定义
# --------------------------------------------------------------------------------------

PROJECT_ROOT: Path = Path("F:/AI-project/ancientGame/shuimofeng/shuimofeng")
PHASEC_DIR: Path = PROJECT_ROOT / "Assets" / "images" / "_raw_gen" / "phaseC"
OUT_DIR: Path = PROJECT_ROOT / "Assets" / "images" / "characters" / "heroine"

ALPHA_TH: int = h.ALPHA_CROP_THRESHOLD   # 10，与阶段 B 保持一致

# 可见区占画幅比的验收窗口（任务书：接近 30%，不低于 15%、不高于 45%）。
OCCUPANCY_MIN: float = 0.15
OCCUPANCY_MAX: float = 0.45

# 抠底自适应搜索：容差候选（相对边缘底色灰度的上下浮动）。
TOL_CANDIDATES: Tuple[int, ...] = (16, 22, 28, 34, 42, 50, 60, 72)
# 抠底后整图透明占比的合理区间：低于 MIN 说明没抠干净，高于 MAX 说明把人物也吃掉了。
MATTE_RATIO_MIN: float = 0.60
MATTE_RATIO_MAX: float = 0.985
# 背景候选的饱和度上限（水墨底几乎无彩，朱砂红点缀饱和度远高于此，不会被误抠）。
BG_SAT_MAX: int = 26
# 羽化带相对硬阈值的容差放大倍率。
FEATHER_TOL_SCALE: float = 1.6

# 朱砂红判定：R 明显高于 G/B。
RED_ACCENT_MIN_DELTA: int = 28

# 一致性剖面分箱数
PROFILE_BINS: int = 16
# 主色板量化步长（每通道量化到 32 级）
PALETTE_QUANT: int = 32
PALETTE_TOP_N: int = 5

# 预览图
PREVIEW_SCALE: int = 8
PREVIEW_PAD: int = 32
PREVIEW_GAP: int = 32


@dataclass
class FrameSpec:
    """一帧的生成/输出规格。"""

    key: str = ""
    gen_subdir: str = ""
    out_name: str = ""
    target: Tuple[int, int] = (48, 64)


FRAMES: List[FrameSpec] = [
    FrameSpec("idle", "gen_idle", "heroine_idle_34.png", (48, 64)),
    FrameSpec("walk_down", "gen_walk", "heroine_walk_down_34.png", (48, 64)),
    FrameSpec("attack", "gen_attack", "heroine_attack_34.png", (96, 64)),
]

FRAME_BY_KEY: Dict[str, FrameSpec] = {f.key: f for f in FRAMES}


# --------------------------------------------------------------------------------------
# 背景测量与自适应抠底
# --------------------------------------------------------------------------------------

def measure_background(rgb: np.ndarray) -> Dict[str, float]:
    """测量四边采样带的底色统计，用于自适应抠底阈值。

    Args:
        rgb: 形状 (H, W, 3) 的 uint8/int16 数组。

    Returns:
        含 level（底色灰度中位数）、spread（10~90 分位跨度）、sat（平均饱和度）、
        tier（近白/中灰/深灰分档标签）的字典。
    """
    height, width = rgb.shape[:2]
    band_h = max(int(height * h.EDGE_SAMPLE_RATIO), 1)
    band_w = max(int(width * h.EDGE_SAMPLE_RATIO), 1)
    strips: List[np.ndarray] = [
        rgb[:band_h, :, :].reshape(-1, 3),
        rgb[-band_h:, :, :].reshape(-1, 3),
        rgb[:, :band_w, :].reshape(-1, 3),
        rgb[:, -band_w:, :].reshape(-1, 3),
    ]
    edge = np.concatenate(strips).astype(np.int16)
    if edge.size == 0:
        return {"level": 255.0, "spread": 0.0, "sat": 0.0, "tier": "near_white"}

    minchan = edge.min(axis=1)
    sat = (edge.max(axis=1) - edge.min(axis=1)).astype(np.float32)
    level = float(np.median(minchan))
    spread = float(np.percentile(minchan, 90) - np.percentile(minchan, 10))

    if level >= 225.0:
        tier = "near_white"      # 阶段 B2 实测 ~237
    elif level >= 150.0:
        tier = "mid_gray"        # 阶段 B2 实测 ~196
    else:
        tier = "dark_gray"       # 阶段 B2 实测 ~104

    return {"level": level, "spread": spread, "sat": float(sat.mean()), "tier": tier}


def _flood_band(minchan: np.ndarray, sat: np.ndarray, level: float, tol: float) -> np.ndarray:
    """以底色灰度为中心、tol 为半径构造候选带，并从四边 flood fill。

    Args:
        minchan: 每像素 RGB 最小通道值，形状 (H, W)。
        sat: 每像素饱和度（max-min），形状 (H, W)。
        level: 底色灰度中心。
        tol: 灰度容差半径。

    Returns:
        与画面外缘连通的背景布尔掩码。
    """
    low = max(level - tol, 0.0)
    high = min(level + tol, 255.0)
    candidate = (minchan >= low) & (minchan <= high) & (sat <= BG_SAT_MAX)
    return h._flood_from_borders(candidate)


def _already_transparent(alpha: np.ndarray) -> bool:
    """判断源图是否本身就带有效 alpha 通道（ImageGen 透明背景直出）。

    Args:
        alpha: 形状 (H, W) 的 uint8 alpha 通道。

    Returns:
        True 表示四边基本全透明，无需再抠底。
    """
    border = np.concatenate([
        alpha[0, :], alpha[-1, :], alpha[:, 0], alpha[:, -1],
    ])
    border_clear = float((border <= ALPHA_TH).mean())
    overall_clear = float((alpha <= ALPHA_TH).mean())
    return border_clear >= 0.90 and overall_clear >= 0.20


def adaptive_remove_background(image: Image.Image) -> Tuple[Image.Image, Dict[str, object]]:
    """自适应两段式抠底：底色实测 -> 容差搜索 -> flood fill -> 边缘羽化。

    相比阶段 B 的固定阈值（minchan>=240），本函数以实测底色为中心做对称容差带，
    因此对近白（~237）/ 中灰（~196）/ 深灰（~104）三档底色都能生效；同时保留
    「必须与画面外缘连通」这一硬约束，人物内部的同灰度墨色不会被穿透。

    Args:
        image: 任意模式的 PIL 图像。

    Returns:
        (RGBA 抠底结果, 抠底诊断信息字典)。
    """
    rgba = image.convert("RGBA")
    arr = np.array(rgba)
    rgb = arr[:, :, :3]
    alpha0 = arr[:, :, 3]

    if _already_transparent(alpha0):
        return rgba, {
            "mode": "source_alpha",
            "bg_level": None,
            "tol": None,
            "transparent_ratio": round(float((alpha0 <= ALPHA_TH).mean()), 4),
            "note": "源图自带 alpha 通道（透明背景直出），跳过抠底",
        }

    bg = measure_background(rgb)
    minchan = rgb.min(axis=2).astype(np.int16)
    maxchan = rgb.max(axis=2).astype(np.int16)
    sat = (maxchan - minchan).astype(np.int16)

    base_tol = max(bg["spread"], 12.0)
    trials: List[Tuple[int, float, np.ndarray]] = []
    chosen_tol: Optional[int] = None
    chosen_mask: Optional[np.ndarray] = None

    for tol in TOL_CANDIDATES:
        eff_tol = float(max(tol, base_tol))
        mask = _flood_band(minchan, sat, bg["level"], eff_tol)
        ratio = float(mask.mean())
        trials.append((tol, ratio, mask))
        if MATTE_RATIO_MIN <= ratio <= MATTE_RATIO_MAX:
            chosen_tol, chosen_mask = tol, mask
            break

    if chosen_mask is None:
        # 没有候选落进理想窗口：取「不超过上限」中去背最多的那个；若全部超上限则取最小容差。
        safe = [(t, r, m) for t, r, m in trials if r <= MATTE_RATIO_MAX]
        if safe:
            chosen_tol, _, chosen_mask = max(safe, key=lambda item: item[1])
        else:
            chosen_tol, _, chosen_mask = trials[0]

    hard_bg = chosen_mask
    # 近白底额外并上阶段 B 的原始硬规则，补齐纯白边角。
    if bg["tier"] == "near_white":
        hard_bg = hard_bg | h._flood_from_borders(
            h._background_candidate_mask(rgb, h.BG_BRIGHTNESS_MIN)
        )

    # 羽化：容差放大后新增的外缘连通像素做半透明过渡，消除硬锯齿。
    feather_tol = float(max(chosen_tol, base_tol)) * FEATHER_TOL_SCALE
    soft_bg = _flood_band(minchan, sat, bg["level"], feather_tol)
    feather = soft_bg & ~hard_bg

    alpha = alpha0.astype(np.int16)
    alpha[hard_bg] = 0
    if feather.any():
        # 离底色越远 -> 越不透明。
        dist = np.abs(minchan.astype(np.float32) - float(bg["level"]))
        span = float(max(feather_tol - max(chosen_tol, base_tol), 1.0))
        ratio_map = np.clip((dist - float(max(chosen_tol, base_tol))) / span, 0.0, 1.0)
        alpha[feather] = np.minimum(alpha[feather], (ratio_map[feather] * 255.0).astype(np.int16))

    arr[:, :, 3] = np.clip(alpha, 0, 255).astype(np.uint8)
    out = Image.fromarray(arr, mode="RGBA")

    diag: Dict[str, object] = {
        "mode": "adaptive_flood",
        "bg_level": round(float(bg["level"]), 1),
        "bg_spread": round(float(bg["spread"]), 1),
        "bg_tier": str(bg["tier"]),
        "tol": int(chosen_tol) if chosen_tol is not None else None,
        "transparent_ratio": round(float((arr[:, :, 3] <= ALPHA_TH).mean()), 4),
        "note": f"底色 {bg['level']:.0f}（{bg['tier']}），容差 ±{chosen_tol}",
    }
    return out, diag


# --------------------------------------------------------------------------------------
# 客观测量
# --------------------------------------------------------------------------------------

def measure_sprite(image: Image.Image) -> Dict[str, object]:
    """对成品精灵做完整客观测量。

    Args:
        image: 成品 RGBA 图像。

    Returns:
        含尺寸/模式/透明占比/可见 bbox/可见占比/亮度/朱砂红占比的字典。
    """
    arr = np.array(image)
    height, width = arr.shape[:2]
    alpha = arr[:, :, 3]
    rgb = arr[:, :, :3].astype(np.float32)

    total = float(width * height)
    visible = alpha > ALPHA_TH
    visible_count = int(visible.sum())
    transparent_ratio = float((~visible).sum()) / total if total else 0.0
    occupancy = float(visible_count) / total if total else 0.0

    ys, xs = np.where(visible)
    if xs.size and ys.size:
        x0, y0 = int(xs.min()), int(ys.min())
        x1, y1 = int(xs.max()), int(ys.max())
        bbox = [x0, y0, x1, y1]
        bbox_w = x1 - x0 + 1
        bbox_h = y1 - y0 + 1
    else:
        bbox = [0, 0, 0, 0]
        bbox_w = bbox_h = 0

    bbox_area = float(bbox_w * bbox_h)
    bbox_fill = bbox_area / total if total else 0.0
    ink_density = (float(visible_count) / bbox_area) if bbox_area > 0 else 0.0

    if visible_count > 0:
        luma = 0.299 * rgb[:, :, 0] + 0.587 * rgb[:, :, 1] + 0.114 * rgb[:, :, 2]
        mean_luma = float(luma[visible].mean())
        vis_rgb = rgb[visible]
        mean_sat = float((vis_rgb.max(axis=1) - vis_rgb.min(axis=1)).mean())
        red_accent = float((
            (vis_rgb[:, 0] - vis_rgb[:, 1] > RED_ACCENT_MIN_DELTA)
            & (vis_rgb[:, 0] - vis_rgb[:, 2] > RED_ACCENT_MIN_DELTA)
        ).mean())
    else:
        mean_luma = 0.0
        mean_sat = 0.0
        red_accent = 0.0

    return {
        "size": [width, height],
        "mode": image.mode,
        "has_alpha": image.mode == "RGBA",
        "transparent_ratio": round(transparent_ratio, 4),
        "occupancy_ratio": round(occupancy, 4),
        "visible_pixels": visible_count,
        "bbox": bbox,
        "bbox_size": [bbox_w, bbox_h],
        "bbox_fill_ratio": round(bbox_fill, 4),
        "ink_density_in_bbox": round(ink_density, 4),
        "mean_luma": round(mean_luma, 2),
        "mean_saturation": round(mean_sat, 2),
        "red_accent_ratio": round(red_accent, 4),
    }


def frame_signature(image: Image.Image) -> Dict[str, object]:
    """提取用于跨帧一致性比对的特征签名。

    统一在「可见区 bbox 裁剪 + 归一化分箱」空间上计算，因此 48x64 与 96x64
    两种画布也能直接比对。

    Args:
        image: 成品 RGBA 图像。

    Returns:
        含行覆盖剖面、主色板、发区/袍区均色的字典。
    """
    arr = np.array(image)
    alpha = arr[:, :, 3]
    visible = alpha > ALPHA_TH
    ys, xs = np.where(visible)
    if xs.size == 0 or ys.size == 0:
        return {
            "row_profile": [0.0] * PROFILE_BINS,
            "palette": [],
            "hair_region_rgb": [0, 0, 0],
            "robe_region_rgb": [0, 0, 0],
        }

    x0, x1 = int(xs.min()), int(xs.max()) + 1
    y0, y1 = int(ys.min()), int(ys.max()) + 1
    sub = arr[y0:y1, x0:x1, :]
    sub_vis = sub[:, :, 3] > ALPHA_TH
    sub_h, sub_w = sub_vis.shape

    # 行覆盖剖面：每行可见像素占该行宽度比例，重采样到固定分箱。
    row_cov = sub_vis.sum(axis=1).astype(np.float32) / float(max(sub_w, 1))
    bin_edges = np.linspace(0, sub_h, PROFILE_BINS + 1).astype(int)
    profile: List[float] = []
    for i in range(PROFILE_BINS):
        a, b = bin_edges[i], max(bin_edges[i + 1], bin_edges[i] + 1)
        profile.append(round(float(row_cov[a:b].mean()), 4))

    # 主色板：可见像素量化后取 TOP N。
    vis_rgb = sub[:, :, :3][sub_vis].astype(np.int32)
    if vis_rgb.size:
        quant = (vis_rgb // PALETTE_QUANT) * PALETTE_QUANT + PALETTE_QUANT // 2
        keys = quant[:, 0] * 65536 + quant[:, 1] * 256 + quant[:, 2]
        uniq, counts = np.unique(keys, return_counts=True)
        order = np.argsort(-counts)[:PALETTE_TOP_N]
        palette = [
            {
                "rgb": [int(uniq[i] // 65536), int((uniq[i] // 256) % 256), int(uniq[i] % 256)],
                "share": round(float(counts[i]) / float(len(keys)), 4),
            }
            for i in order
        ]
    else:
        palette = []

    def _region_mean(top_frac: float, bot_frac: float) -> List[int]:
        """取 bbox 纵向区间内可见像素的平均 RGB。"""
        ra, rb = int(sub_h * top_frac), max(int(sub_h * bot_frac), int(sub_h * top_frac) + 1)
        region = sub[ra:rb, :, :]
        rmask = region[:, :, 3] > ALPHA_TH
        if not rmask.any():
            return [0, 0, 0]
        vals = region[:, :, :3][rmask].astype(np.float32).mean(axis=0)
        return [int(round(v)) for v in vals]

    return {
        "row_profile": profile,
        "palette": palette,
        "hair_region_rgb": _region_mean(0.00, 0.25),   # 头/发区
        "robe_region_rgb": _region_mean(0.35, 0.85),   # 服饰主体区
    }


def _pearson(a: Sequence[float], b: Sequence[float]) -> float:
    """计算两个等长序列的 Pearson 相关系数（退化时返回 0）。"""
    va = np.asarray(a, dtype=np.float64)
    vb = np.asarray(b, dtype=np.float64)
    if va.size != vb.size or va.size < 2:
        return 0.0
    va = va - va.mean()
    vb = vb - vb.mean()
    denom = float(np.linalg.norm(va) * np.linalg.norm(vb))
    if denom <= 1e-9:
        return 0.0
    return float(np.dot(va, vb) / denom)


def _rgb_distance(a: Sequence[int], b: Sequence[int]) -> float:
    """两个 RGB 的欧氏距离。"""
    va = np.asarray(a, dtype=np.float64)
    vb = np.asarray(b, dtype=np.float64)
    return float(np.linalg.norm(va - vb))


def compare_frames(results: Dict[str, Dict[str, object]]) -> Dict[str, object]:
    """两两比对帧签名，量化跨帧一致性（img2img 是否生效）。

    Args:
        results: key -> 单帧结果（含 measure / signature）。

    Returns:
        含逐对指标与总体判定的字典。
    """
    keys = [k for k in ("idle", "walk_down", "attack") if k in results]
    pairs: List[Dict[str, object]] = []
    for i in range(len(keys)):
        for j in range(i + 1, len(keys)):
            ka, kb = keys[i], keys[j]
            sa = results[ka]["signature"]      # type: ignore[index]
            sb = results[kb]["signature"]      # type: ignore[index]
            ma = results[ka]["measure"]        # type: ignore[index]
            mb = results[kb]["measure"]        # type: ignore[index]

            corr = _pearson(sa["row_profile"], sb["row_profile"])          # type: ignore[index]
            hair_d = _rgb_distance(sa["hair_region_rgb"], sb["hair_region_rgb"])   # type: ignore[index]
            robe_d = _rgb_distance(sa["robe_region_rgb"], sb["robe_region_rgb"])   # type: ignore[index]
            luma_d = abs(float(ma["mean_luma"]) - float(mb["mean_luma"]))   # type: ignore[index]
            red_d = abs(float(ma["red_accent_ratio"]) - float(mb["red_accent_ratio"]))  # type: ignore[index]

            pa = {tuple(p["rgb"]) for p in sa["palette"]}                   # type: ignore[index]
            pb = {tuple(p["rgb"]) for p in sb["palette"]}                   # type: ignore[index]
            overlap = len(pa & pb) / float(max(len(pa | pb), 1))

            pairs.append({
                "pair": f"{ka} vs {kb}",
                "row_profile_corr": round(corr, 4),
                "hair_rgb_distance": round(hair_d, 2),
                "robe_rgb_distance": round(robe_d, 2),
                "mean_luma_delta": round(luma_d, 2),
                "red_accent_delta": round(red_d, 4),
                "palette_overlap": round(overlap, 4),
            })

    if pairs:
        consistent = all(
            float(p["robe_rgb_distance"]) < 60.0 and float(p["mean_luma_delta"]) < 45.0
            for p in pairs
        )
    else:
        consistent = False

    return {"pairs": pairs, "consistent": consistent}


# --------------------------------------------------------------------------------------
# 单帧处理
# --------------------------------------------------------------------------------------

def newest_png(directory: Path) -> Optional[Path]:
    """取目录下最新修改的 PNG 文件。

    Args:
        directory: 待扫描目录。

    Returns:
        最新 PNG 路径；目录不存在或无 PNG 时返回 None。
    """
    if not directory.is_dir():
        return None
    files = [p for p in directory.glob("*.png") if p.is_file() and not p.name.startswith("_")]
    if not files:
        return None
    return max(files, key=lambda p: p.stat().st_mtime)


def process_frame(spec: FrameSpec) -> Optional[Dict[str, object]]:
    """处理单帧：抠底 -> 去水印 -> 裁边 -> 缩放 -> 落盘 -> 测量。

    Args:
        spec: 帧规格。

    Returns:
        结果字典（含 measure / signature / _sprite）；找不到源图时返回 None。
    """
    src = newest_png(PHASEC_DIR / spec.gen_subdir)
    if src is None:
        print(f"[WARN] {spec.key}: 未找到源图，目录 {PHASEC_DIR / spec.gen_subdir}")
        return None

    with Image.open(src) as raw:
        raw.load()
        original_size = list(raw.size)
        matted, diag = adaptive_remove_background(raw)

    matted = tv.remove_corner_watermark(matted)
    cropped = h.crop_to_content(matted)
    cropped, wm_note = h.detect_and_strip_watermark(cropped)
    cropped_size = list(cropped.size)

    final = h.fit_into_canvas(cropped, spec.target)
    out_path = OUT_DIR / spec.out_name
    out_path.parent.mkdir(parents=True, exist_ok=True)
    final.save(out_path, format="PNG")

    # 从磁盘重新读取来测量，确保测的是真正落盘的文件而不是内存对象。
    with Image.open(out_path) as saved:
        saved.load()
        measure = measure_sprite(saved)
        signature = frame_signature(saved)
        sprite = saved.copy()

    result: Dict[str, object] = {
        "key": spec.key,
        "source": str(src),
        "output": str(out_path),
        "target_size": list(spec.target),
        "original_size": original_size,
        "cropped_size": cropped_size,
        "matte": diag,
        "watermark_note": wm_note,
        "measure": measure,
        "signature": signature,
        "_sprite": sprite,
    }

    m = measure
    print(f"[{spec.key}] src={src.name}")
    print(f"        抠底: {diag['note']}  裁后={cropped_size}")
    print(f"        水印: {wm_note}")
    print(f"        成品: {m['size']} {m['mode']}  透明={float(m['transparent_ratio']):.1%} "
          f"可见占比={float(m['occupancy_ratio']):.1%}  bbox={m['bbox']}")
    return result


# --------------------------------------------------------------------------------------
# 预览图
# --------------------------------------------------------------------------------------

def build_preview(sprites: List[Image.Image], out_path: Path) -> Tuple[int, int]:
    """三帧成品 8 倍最近邻放大、棋盘格底并排预览。

    Args:
        sprites: 成品图像列表（idle / walk_down / attack 顺序）。
        out_path: 输出路径。

    Returns:
        预览图尺寸 (宽, 高)。
    """
    if not sprites:
        raise ValueError("sprites 不能为空")

    scaled = [s.resize((s.width * PREVIEW_SCALE, s.height * PREVIEW_SCALE), Image.NEAREST)
              for s in sprites]
    total_w = PREVIEW_PAD * 2 + sum(s.width for s in scaled) + PREVIEW_GAP * (len(scaled) - 1)
    total_h = PREVIEW_PAD * 2 + max(s.height for s in scaled)

    canvas = h.make_checkerboard((total_w, total_h))
    cursor_x = PREVIEW_PAD
    for sprite in scaled:
        top = PREVIEW_PAD + (total_h - PREVIEW_PAD * 2 - sprite.height) // 2
        canvas.alpha_composite(sprite, (cursor_x, top))
        h.draw_border(canvas, (cursor_x, top, cursor_x + sprite.width, top + sprite.height))
        cursor_x += sprite.width + PREVIEW_GAP

    out_path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(out_path, format="PNG")
    return canvas.size


# --------------------------------------------------------------------------------------
# 主流程
# --------------------------------------------------------------------------------------

def run(stage: str) -> int:
    """执行处理流程。

    Args:
        stage: idle / walk_down / attack / all。

    Returns:
        进程退出码，0 表示全部检查通过。
    """
    if stage == "all":
        specs = list(FRAMES)
    else:
        spec = FRAME_BY_KEY.get(stage)
        if spec is None:
            print(f"[FATAL] 未知 stage: {stage}")
            return 2
        specs = [spec]

    print("=" * 84)
    print(f"美术阶段 C · 3/4 视角女主验证批次 · stage={stage}")
    print("=" * 84)

    results: Dict[str, Dict[str, object]] = {}
    for spec in specs:
        res = process_frame(spec)
        if res is not None:
            results[spec.key] = res

    if not results:
        print("[FATAL] 无任何帧被处理")
        return 2

    # 单帧阶段：不写全局 JSON / 预览，避免覆盖尚未完成的批次数据。
    if stage != "all":
        print(f"\n[stage={stage}] 单帧处理完成，未生成汇总（需 --stage all）")
        return 0

    sprites = [results[f.key]["_sprite"] for f in FRAMES if f.key in results]  # type: ignore[index]
    preview_path = PHASEC_DIR / "_preview_heroine_34.png"
    preview_size = build_preview(sprites, preview_path)  # type: ignore[arg-type]
    print(f"\n[preview] {preview_path}  {preview_size[0]}x{preview_size[1]}")

    consistency = compare_frames(results)

    # ------------------------------ 落盘 JSON ------------------------------
    sizes: Dict[str, object] = {}
    for key, res in results.items():
        m = res["measure"]  # type: ignore[index]
        sizes[key] = {
            "output": res["output"],
            "size": m["size"],                      # type: ignore[index]
            "target_size": res["target_size"],
            "mode": m["mode"],                      # type: ignore[index]
            "size_ok": list(m["size"]) == list(res["target_size"]),  # type: ignore[index]
        }
    (PHASEC_DIR / "_sizes.json").write_text(
        json.dumps(sizes, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    summary: Dict[str, object] = {
        "phase": "C",
        "route": "3/4 view (front-facing, ~30% canvas height, img2img cross-frame lock)",
        "preview": str(preview_path),
        "preview_size": list(preview_size),
        "occupancy_window": [OCCUPANCY_MIN, OCCUPANCY_MAX],
        "frames": [
            {
                "key": key,
                "source": results[key]["source"],
                "output": results[key]["output"],
                "target_size": results[key]["target_size"],
                "original_size": results[key]["original_size"],
                "cropped_size": results[key]["cropped_size"],
                "matte": results[key]["matte"],
                "watermark_note": results[key]["watermark_note"],
                "measure": results[key]["measure"],
                "signature": results[key]["signature"],
            }
            for key in results
        ],
        "cross_frame_consistency": consistency,
    }
    (PHASEC_DIR / "_phaseC_summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    # ------------------------------ 报表 ------------------------------
    print()
    print("成品测量")
    print("-" * 84)
    print(f"{'帧':<12}{'尺寸':<12}{'模式':<8}{'透明占比':<12}{'可见占比':<12}{'bbox':<20}{'墨密度'}")
    for f in FRAMES:
        if f.key not in results:
            continue
        m = results[f.key]["measure"]  # type: ignore[index]
        print(
            f"{f.key:<12}"
            f"{m['size'][0]}x{m['size'][1]:<8}"      # type: ignore[index]
            f"{str(m['mode']):<8}"                    # type: ignore[index]
            f"{float(m['transparent_ratio']) * 100:>7.2f}%    "   # type: ignore[index]
            f"{float(m['occupancy_ratio']) * 100:>7.2f}%    "     # type: ignore[index]
            f"{str(m['bbox']):<20}"                   # type: ignore[index]
            f"{float(m['ink_density_in_bbox']) * 100:>6.1f}%"     # type: ignore[index]
        )
    print("-" * 84)

    print()
    print("跨帧一致性")
    print("-" * 84)
    for p in consistency["pairs"]:  # type: ignore[index]
        print(f"  {p['pair']:<26} 剖面相关={float(p['row_profile_corr']):+.3f}  "
              f"发区色距={float(p['hair_rgb_distance']):6.2f}  "
              f"袍区色距={float(p['robe_rgb_distance']):6.2f}  "
              f"亮度差={float(p['mean_luma_delta']):6.2f}  "
              f"色板重合={float(p['palette_overlap']):.2f}")

    # ------------------------------ 自检 ------------------------------
    checks: Dict[str, bool] = {}
    checks["三帧全部产出"] = len(results) == 3
    checks["尺寸精确达标"] = all(
        list(results[f.key]["measure"]["size"]) == list(f.target)   # type: ignore[index]
        for f in FRAMES if f.key in results
    )
    checks["全部 RGBA"] = all(
        bool(results[f.key]["measure"]["has_alpha"])                 # type: ignore[index]
        for f in FRAMES if f.key in results
    )
    checks[f"可见占比在 {OCCUPANCY_MIN:.0%}~{OCCUPANCY_MAX:.0%}"] = all(
        OCCUPANCY_MIN <= float(results[f.key]["measure"]["occupancy_ratio"]) <= OCCUPANCY_MAX  # type: ignore[index]
        for f in FRAMES if f.key in results
    )
    checks["跨帧配色一致"] = bool(consistency["consistent"])          # type: ignore[index]
    checks["预览图可读"] = preview_path.exists() and preview_path.stat().st_size > 0

    print()
    print("自检")
    print("-" * 84)
    for name, ok in checks.items():
        print(f"  {'PASS' if ok else 'FAIL'}  {name}")

    all_pass = all(checks.values())
    print()
    print(f"IS_PASS: {'YES' if all_pass else 'NO'}")
    return 0 if all_pass else 1


def main() -> int:
    """CLI 入口。"""
    try:
        sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
    except Exception:
        pass

    parser = argparse.ArgumentParser(description="美术阶段 C：3/4 视角女主验证批次后处理")
    parser.add_argument(
        "--stage",
        default="all",
        choices=["idle", "walk_down", "attack", "all"],
        help="处理哪一帧；all 表示三帧全处理并输出汇总",
    )
    args = parser.parse_args()
    return run(args.stage)


if __name__ == "__main__":
    raise SystemExit(main())
