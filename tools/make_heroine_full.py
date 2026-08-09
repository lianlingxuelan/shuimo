# -*- coding: utf-8 -*-
"""美术阶段 D：3/4 视角女主全量批量（8 图集 / 39 帧）后处理与客观测量。

背景
----
* 阶段 B2 结论：ImageGen 对「水墨仙侠女修」有极强正面立绘先验，纯俯视压不出来。
* 阶段 C 结论：3/4 视角路线可行——idle / walk_down / attack 三帧可见占比分别
  27.25% / 35.81% / 18.36%，均落 15~45% 窗口，跨帧色板一致。用户拍板转全量。

阶段 D 在阶段 C 基础上做三件事
------------------------------
1. **从 3 帧扩到 39 帧**（8 个 pose），每 pose 独立 `gen_<pose>/f<N>.png` 源目录；
2. **每 pose 统一缩放 + 底边对齐**（关键改进）。阶段 C 的 ``h.fit_into_canvas`` 对每帧
   独立「等比缩放填满画布」，单帧验证没问题，但用在 6 帧行走循环上会导致每帧人物
   大小随内容 bbox 抖动（角色一帧高一帧矮地「脉动」）。本阶段改为：同一 pose 内所有帧
   共用一个缩放系数（由该 pose 各帧内容的最大宽/高共同决定），并统一底边对齐、水平居中，
   于是脚底稳定落在同一条基线上，动画播放时不再抖动；
3. **组内一致性量化**：每帧与该 pose 第 1 帧比对发区/袍区色距、亮度差、色板重合度。

抠底 / 去水印 / 客观测量三段核心算法直接复用阶段 C 已验证实现
（``make_heroine_34`` 的 ``adaptive_remove_background`` / ``measure_sprite`` /
``frame_signature``），保持单一事实源，避免算法漂移。

输入
----
``Assets/images/_raw_gen/phaseD/gen_<pose>/f<N>.png``
（出图后立即重命名为 f1..fN，避免 ImageGen 时间戳文件名带来的匹配歧义）。

输出
----
* 成品 39 张：``Assets/images/characters/heroine/heroine_<pose>_<n>.png``
* 每 pose 预览：``Assets/images/_raw_gen/phaseD/_preview_<pose>.png``（8 倍棋盘格）
* 总览：``Assets/images/_raw_gen/phaseD/_preview_phaseD_all.png``（4 倍棋盘格）
* 数据：``Assets/images/_raw_gen/phaseD/_phaseD_summary.json``、``_sizes.json``

用法::

    python make_heroine_full.py --pose idle        # 单 pose：出成品 + 该 pose 预览 + 合并进 JSON
    python make_heroine_full.py --pose all         # 全量重跑 + 总览预览
    python make_heroine_full.py --pose all --overview-only   # 只重建总览预览
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

# 复用阶段 B / B2 / C 已验证实现（同目录导入；三者均有 __main__ 守卫，导入无副作用）。
_TOOLS_DIR: str = os.path.dirname(os.path.abspath(__file__))
if _TOOLS_DIR not in sys.path:
    sys.path.insert(0, _TOOLS_DIR)

import make_heroine_sample as h          # noqa: E402  抠底/裁边/水印/棋盘格
import make_topdown_variants as tv       # noqa: E402  角标水印移除
import make_heroine_34 as c34            # noqa: E402  阶段 C：自适应抠底 + 测量 + 签名

# --------------------------------------------------------------------------------------
# 路径常量
# --------------------------------------------------------------------------------------

PROJECT_ROOT: Path = Path("F:/AI-project/ancientGame/shuimofeng/shuimofeng")
PHASED_DIR: Path = PROJECT_ROOT / "Assets" / "images" / "_raw_gen" / "phaseD"
OUT_DIR: Path = PROJECT_ROOT / "Assets" / "images" / "characters" / "heroine"

SUMMARY_PATH: Path = PHASED_DIR / "_phaseD_summary.json"
SIZES_PATH: Path = PHASED_DIR / "_sizes.json"
OVERVIEW_PATH: Path = PHASED_DIR / "_preview_phaseD_all.png"

ALPHA_TH: int = h.ALPHA_CROP_THRESHOLD   # 10，与阶段 B/C 保持一致

# --------------------------------------------------------------------------------------
# 验收窗口
# --------------------------------------------------------------------------------------

# 可见像素占画幅比的默认验收窗口（任务书：12%~50%）。
OCCUPANCY_MIN: float = 0.12
OCCUPANCY_MAX: float = 0.50
# death 用 64x64 画布、且末帧化墨消散，占比窗口放宽。
DEATH_OCCUPANCY_MIN: float = 0.10
DEATH_OCCUPANCY_MAX: float = 0.55
# attack 用 96 宽画布，分母大导致像素占比偏低，改用 bbox 占画幅比兜底。
ATTACK_BBOX_FILL_MIN: float = 0.35

# 组内一致性阈值（任务书：发区色距 < 10）。
HAIR_DISTANCE_MAX: float = 10.0
ROBE_DISTANCE_MAX: float = 60.0
LUMA_DELTA_MAX: float = 45.0

# --------------------------------------------------------------------------------------
# 排版常量
# --------------------------------------------------------------------------------------

PREVIEW_SCALE: int = 8          # 单 pose 预览放大倍率
OVERVIEW_SCALE: int = 4         # 总览预览放大倍率（39 帧全铺，8 倍会过大）
PREVIEW_PAD: int = 24
PREVIEW_GAP: int = 24
LABEL_H: int = 18
LABEL_COLOR: Tuple[int, int, int, int] = (40, 40, 40, 255)

# 底边对齐时预留的底部空白（像素，成品画布坐标系）。
BOTTOM_MARGIN: int = 0


# --------------------------------------------------------------------------------------
# Pose 定义
# --------------------------------------------------------------------------------------

@dataclass
class PoseSpec:
    """一个动作图集的规格。

    Attributes:
        key: pose 标识，同时用于文件名与目录名。
        target: 单帧成品画布尺寸 (宽, 高)。
        frames: 帧数。
        occupancy: 可见占比验收窗口 (下限, 上限)。
        ghost_alpha: 残影帧的 alpha 衰减系数，键为帧号（1-based）。
        note: 动作说明。
    """

    key: str = ""
    target: Tuple[int, int] = (48, 64)
    frames: int = 1
    occupancy: Tuple[float, float] = (OCCUPANCY_MIN, OCCUPANCY_MAX)
    ghost_alpha: Dict[int, float] = field(default_factory=dict)
    note: str = ""

    @property
    def gen_dir(self) -> Path:
        """该 pose 的原图目录。"""
        return PHASED_DIR / f"gen_{self.key}"

    @property
    def preview_path(self) -> Path:
        """该 pose 的 8 倍预览图路径。"""
        return PHASED_DIR / f"_preview_{self.key}.png"

    def out_path(self, frame_no: int) -> Path:
        """第 frame_no 帧的成品落盘路径。"""
        return OUT_DIR / f"heroine_{self.key}_{frame_no}.png"


# 尺寸严格依据 docs/art-sprite-plan.md §4①。
# dodge 的「前 2 帧实 + 后 2 帧半透明」残影同样出自 §4①。
POSES: List[PoseSpec] = [
    PoseSpec("idle", (48, 64), 4, note="待机呼吸，衣袂轻摆"),
    PoseSpec("walk_down", (48, 64), 6, note="朝屏幕下方走"),
    PoseSpec("walk_up", (48, 64), 6, note="背面行走"),
    PoseSpec("walk_side", (48, 64), 6, note="侧面行走，默认朝右，左右翻转复用"),
    PoseSpec("attack", (96, 64), 6, note="挥剑，横向留出剑势空间"),
    PoseSpec("hurt", (48, 64), 2, note="受击后仰"),
    PoseSpec("dodge", (48, 64), 4, ghost_alpha={3: 0.62, 4: 0.42},
             note="翻滚，前 2 帧实 + 后 2 帧半透明残影"),
    PoseSpec("death", (64, 64), 5,
             occupancy=(DEATH_OCCUPANCY_MIN, DEATH_OCCUPANCY_MAX),
             note="倒地，末帧化墨消散"),
]

POSE_BY_KEY: Dict[str, PoseSpec] = {p.key: p for p in POSES}

TOTAL_FRAMES: int = sum(p.frames for p in POSES)


# --------------------------------------------------------------------------------------
# 源图读取与内容提取
# --------------------------------------------------------------------------------------

def frame_source(pose: PoseSpec, frame_no: int) -> Optional[Path]:
    """定位某帧的原图。

    优先取规范命名 ``f<N>.png``；找不到时回退到目录内按修改时间排序的第 N 个 PNG，
    以便在忘记重命名的情况下仍能处理。

    Args:
        pose: pose 规格。
        frame_no: 帧号（1-based）。

    Returns:
        原图路径；不存在时返回 None。
    """
    canonical = pose.gen_dir / f"f{frame_no}.png"
    if canonical.is_file():
        return canonical

    if not pose.gen_dir.is_dir():
        return None
    files = sorted(
        (p for p in pose.gen_dir.glob("*.png") if p.is_file() and not p.name.startswith("_")),
        key=lambda p: p.stat().st_mtime,
    )
    if len(files) >= frame_no:
        return files[frame_no - 1]
    return None


def extract_content(src: Path) -> Tuple[Image.Image, Dict[str, object], str]:
    """抠底 -> 去角标水印 -> 裁到内容边界 -> 去底部水印条带。

    Args:
        src: 原图路径。

    Returns:
        (裁到内容边界的 RGBA 图像, 抠底诊断, 水印处理说明)。
    """
    with Image.open(src) as raw:
        raw.load()
        matted, diag = c34.adaptive_remove_background(raw)

    matted = tv.remove_corner_watermark(matted)
    cropped = h.crop_to_content(matted)
    cropped, wm_note = h.detect_and_strip_watermark(cropped)
    # 去掉底部水印条带后可能又空出透明边，再裁一次保证 bbox 紧贴内容。
    cropped = h.crop_to_content(cropped)
    return cropped, diag, wm_note


# --------------------------------------------------------------------------------------
# 统一缩放 + 底边对齐
# --------------------------------------------------------------------------------------

def compute_uniform_scale(contents: Sequence[Image.Image], target: Tuple[int, int]) -> float:
    """计算同一 pose 内所有帧共用的缩放系数。

    取各帧内容宽/高的最大值作为「包络盒」，让包络盒恰好塞进目标画布，
    因此每一帧都用同一系数缩放——角色在整段动画里大小恒定，不会逐帧脉动。

    Args:
        contents: 该 pose 各帧裁到内容边界后的图像。
        target: 目标画布 (宽, 高)。

    Returns:
        缩放系数（>0）。
    """
    target_w, target_h = target
    usable_h = max(target_h - BOTTOM_MARGIN, 1)
    max_w = max((c.width for c in contents if c.width > 0), default=1)
    max_h = max((c.height for c in contents if c.height > 0), default=1)
    scale = min(target_w / float(max_w), usable_h / float(max_h))
    return max(scale, 1e-6)


def place_frame(content: Image.Image, target: Tuple[int, int], scale: float) -> Image.Image:
    """按给定系数缩放并「水平居中 + 底边对齐」放入目标画布。

    底边对齐让脚底稳定落在同一基线上，这是行走/攻击循环不抖动的前提。

    Args:
        content: 裁到内容边界的 RGBA 图像。
        target: 目标画布 (宽, 高)。
        scale: 统一缩放系数。

    Returns:
        精确等于 target 尺寸的 RGBA 图像。
    """
    target_w, target_h = target
    canvas = Image.new("RGBA", (target_w, target_h), (0, 0, 0, 0))
    if content.width <= 0 or content.height <= 0:
        return canvas

    new_w = min(max(int(round(content.width * scale)), 1), target_w)
    new_h = min(max(int(round(content.height * scale)), 1), target_h)
    resized = content.resize((new_w, new_h), Image.LANCZOS)

    left = (target_w - new_w) // 2
    top = max(target_h - BOTTOM_MARGIN - new_h, 0)
    canvas.paste(resized, (left, top), resized)
    return canvas


def apply_ghost(image: Image.Image, factor: float) -> Image.Image:
    """按系数整体衰减 alpha，制作半透明残影帧。

    Args:
        image: RGBA 图像。
        factor: alpha 衰减系数，0~1。

    Returns:
        衰减后的新 RGBA 图像。
    """
    arr = np.array(image)
    alpha = arr[:, :, 3].astype(np.float32) * float(factor)
    arr[:, :, 3] = np.clip(alpha, 0, 255).astype(np.uint8)
    return Image.fromarray(arr, mode="RGBA")


# --------------------------------------------------------------------------------------
# 组内一致性
# --------------------------------------------------------------------------------------

def intra_pose_consistency(frames: List[Dict[str, object]]) -> Dict[str, object]:
    """以第 1 帧为基准，量化同一 pose 内各帧的一致性。

    Args:
        frames: 该 pose 各帧结果（含 measure / signature），按帧号升序。

    Returns:
        含逐帧比对指标与总体判定的字典。
    """
    if len(frames) < 2:
        return {"pairs": [], "consistent": True,
                "note": "帧数不足 2，无需组内比对"}

    base = frames[0]
    base_sig = base["signature"]        # type: ignore[index]
    base_measure = base["measure"]      # type: ignore[index]
    base_palette = {tuple(p["rgb"]) for p in base_sig["palette"]}   # type: ignore[index]

    pairs: List[Dict[str, object]] = []
    for cur in frames[1:]:
        sig = cur["signature"]          # type: ignore[index]
        measure = cur["measure"]        # type: ignore[index]

        hair_d = c34._rgb_distance(base_sig["hair_region_rgb"], sig["hair_region_rgb"])   # type: ignore[index]
        robe_d = c34._rgb_distance(base_sig["robe_region_rgb"], sig["robe_region_rgb"])   # type: ignore[index]
        corr = c34._pearson(base_sig["row_profile"], sig["row_profile"])                  # type: ignore[index]
        luma_d = abs(float(base_measure["mean_luma"]) - float(measure["mean_luma"]))      # type: ignore[index]
        red_d = abs(float(base_measure["red_accent_ratio"])                                # type: ignore[index]
                    - float(measure["red_accent_ratio"]))                                  # type: ignore[index]

        cur_palette = {tuple(p["rgb"]) for p in sig["palette"]}      # type: ignore[index]
        overlap = len(base_palette & cur_palette) / float(max(len(base_palette | cur_palette), 1))

        pairs.append({
            "pair": f"f1 vs f{cur['frame']}",
            "frame": cur["frame"],
            "row_profile_corr": round(corr, 4),
            "hair_rgb_distance": round(hair_d, 2),
            "robe_rgb_distance": round(robe_d, 2),
            "mean_luma_delta": round(luma_d, 2),
            "red_accent_delta": round(red_d, 4),
            "palette_overlap": round(overlap, 4),
            "hair_ok": bool(hair_d < HAIR_DISTANCE_MAX),
        })

    consistent = all(
        float(p["hair_rgb_distance"]) < HAIR_DISTANCE_MAX
        and float(p["robe_rgb_distance"]) < ROBE_DISTANCE_MAX
        and float(p["mean_luma_delta"]) < LUMA_DELTA_MAX
        for p in pairs
    )
    return {"pairs": pairs, "consistent": consistent,
            "thresholds": {
                "hair_rgb_distance_max": HAIR_DISTANCE_MAX,
                "robe_rgb_distance_max": ROBE_DISTANCE_MAX,
                "mean_luma_delta_max": LUMA_DELTA_MAX,
            }}


# --------------------------------------------------------------------------------------
# 预览图
# --------------------------------------------------------------------------------------

def _label(canvas: Image.Image, xy: Tuple[int, int], text: str) -> None:
    """在预览画布上写一行说明文字（使用 PIL 内置位图字体，无外部字体依赖）。

    Args:
        canvas: 目标画布。
        xy: 文本左上角坐标。
        text: ASCII 文本。
    """
    draw = ImageDraw.Draw(canvas)
    draw.text(xy, text, fill=LABEL_COLOR)


def build_pose_preview(pose: PoseSpec, sprites: List[Image.Image],
                       measures: List[Dict[str, object]]) -> Tuple[int, int]:
    """输出该 pose 的 8 倍棋盘格横排预览（每帧下方标注帧号与可见占比）。

    Args:
        pose: pose 规格。
        sprites: 该 pose 成品帧，按帧号升序。
        measures: 与 sprites 对应的测量结果。

    Returns:
        预览图尺寸 (宽, 高)。
    """
    if not sprites:
        raise ValueError(f"{pose.key}: sprites 不能为空")

    scaled = [s.resize((s.width * PREVIEW_SCALE, s.height * PREVIEW_SCALE), Image.NEAREST)
              for s in sprites]
    cell_w = max(s.width for s in scaled)
    cell_h = max(s.height for s in scaled)
    total_w = PREVIEW_PAD * 2 + cell_w * len(scaled) + PREVIEW_GAP * (len(scaled) - 1)
    total_h = PREVIEW_PAD * 2 + LABEL_H + cell_h + LABEL_H

    canvas = h.make_checkerboard((total_w, total_h))
    _label(canvas, (PREVIEW_PAD, 6),
           f"{pose.key}  {pose.target[0]}x{pose.target[1]}  x{len(sprites)} frames"
           f"   (preview {PREVIEW_SCALE}x nearest)")

    cursor_x = PREVIEW_PAD
    top = PREVIEW_PAD + LABEL_H
    for idx, sprite in enumerate(scaled):
        offset_x = cursor_x + (cell_w - sprite.width) // 2
        canvas.alpha_composite(sprite, (offset_x, top))
        h.draw_border(canvas, (offset_x, top, offset_x + sprite.width, top + sprite.height))
        occ = float(measures[idx]["occupancy_ratio"]) * 100.0   # type: ignore[index]
        _label(canvas, (cursor_x, top + cell_h + 4), f"f{idx + 1}  occ {occ:.1f}%")
        cursor_x += cell_w + PREVIEW_GAP

    pose.preview_path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(pose.preview_path, format="PNG")
    return canvas.size


def build_overview(rows: List[Tuple[PoseSpec, List[Image.Image]]]) -> Tuple[int, int]:
    """把全部 pose 拼成一张总览预览（每 pose 一行，4 倍放大）。

    Args:
        rows: (pose, 成品帧列表) 列表，按 POSES 顺序。

    Returns:
        总览图尺寸 (宽, 高)。
    """
    if not rows:
        raise ValueError("rows 不能为空")

    scaled_rows: List[Tuple[PoseSpec, List[Image.Image]]] = [
        (pose, [s.resize((s.width * OVERVIEW_SCALE, s.height * OVERVIEW_SCALE), Image.NEAREST)
                for s in sprites])
        for pose, sprites in rows
    ]

    row_widths: List[int] = []
    row_heights: List[int] = []
    for _, sprites in scaled_rows:
        cell_w = max(s.width for s in sprites)
        row_widths.append(cell_w * len(sprites) + PREVIEW_GAP * (len(sprites) - 1))
        row_heights.append(max(s.height for s in sprites))

    total_w = PREVIEW_PAD * 2 + max(row_widths)
    total_h = PREVIEW_PAD * 2 + sum(rh + LABEL_H + PREVIEW_GAP for rh in row_heights)

    canvas = h.make_checkerboard((total_w, total_h))
    cursor_y = PREVIEW_PAD
    for (pose, sprites), row_h in zip(scaled_rows, row_heights):
        _label(canvas, (PREVIEW_PAD, cursor_y),
               f"{pose.key}  {pose.target[0]}x{pose.target[1]}  x{len(sprites)}")
        cursor_y += LABEL_H
        cell_w = max(s.width for s in sprites)
        cursor_x = PREVIEW_PAD
        for sprite in sprites:
            offset_x = cursor_x + (cell_w - sprite.width) // 2
            offset_y = cursor_y + (row_h - sprite.height)
            canvas.alpha_composite(sprite, (offset_x, offset_y))
            h.draw_border(canvas, (offset_x, offset_y,
                                   offset_x + sprite.width, offset_y + sprite.height))
            cursor_x += cell_w + PREVIEW_GAP
        cursor_y += row_h + PREVIEW_GAP

    OVERVIEW_PATH.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(OVERVIEW_PATH, format="PNG")
    return canvas.size


# --------------------------------------------------------------------------------------
# 单 pose 处理
# --------------------------------------------------------------------------------------

def process_pose(pose: PoseSpec) -> Optional[Dict[str, object]]:
    """处理一个 pose 的全部帧。

    Args:
        pose: pose 规格。

    Returns:
        该 pose 的结果字典；无任何可用原图时返回 None。
    """
    sources: List[Tuple[int, Path]] = []
    for n in range(1, pose.frames + 1):
        src = frame_source(pose, n)
        if src is None:
            print(f"[WARN] {pose.key} f{n}: 未找到原图（{pose.gen_dir / f'f{n}.png'}）")
            continue
        sources.append((n, src))

    if not sources:
        print(f"[FATAL] {pose.key}: 目录内无任何原图，跳过")
        return None

    # 第一遍：抠底 + 裁到内容边界，得到各帧内容。
    contents: List[Tuple[int, Path, Image.Image, Dict[str, object], str]] = []
    for n, src in sources:
        content, diag, wm_note = extract_content(src)
        contents.append((n, src, content, diag, wm_note))

    # 统一缩放系数：全 pose 共用，保证动画中角色大小恒定。
    scale = compute_uniform_scale([c[2] for c in contents], pose.target)

    frames_out: List[Dict[str, object]] = []
    sprites: List[Image.Image] = []
    for n, src, content, diag, wm_note in contents:
        sprite = place_frame(content, pose.target, scale)

        ghost_factor = pose.ghost_alpha.get(n)
        if ghost_factor is not None:
            sprite = apply_ghost(sprite, ghost_factor)

        out_path = pose.out_path(n)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        sprite.save(out_path, format="PNG")

        # 从磁盘重新读取来测量，确保测的是真正落盘的文件而非内存对象。
        with Image.open(out_path) as saved:
            saved.load()
            measure = c34.measure_sprite(saved)
            signature = c34.frame_signature(saved)
            disk_sprite = saved.copy()

        occ_min, occ_max = pose.occupancy
        occ = float(measure["occupancy_ratio"])
        size_ok = list(measure["size"]) == list(pose.target)
        occ_ok = occ_min <= occ <= occ_max
        if pose.key == "attack":
            # 96 宽画布分母大，像素占比天然偏低，用 bbox 占比兜底。
            occ_ok = occ_ok or float(measure["bbox_fill_ratio"]) >= ATTACK_BBOX_FILL_MIN

        frames_out.append({
            "frame": n,
            "source": str(src),
            "output": str(out_path),
            "target_size": list(pose.target),
            "content_size": [content.width, content.height],
            "uniform_scale": round(scale, 5),
            "ghost_alpha": ghost_factor,
            "matte": diag,
            "watermark_note": wm_note,
            "measure": measure,
            "signature": signature,
            "size_ok": size_ok,
            "occupancy_ok": bool(occ_ok),
        })
        sprites.append(disk_sprite)

        print(f"  [{pose.key} f{n}] {measure['size']} {measure['mode']}  "
              f"可见占比={occ * 100:5.2f}%  bbox={measure['bbox']}  "
              f"bbox占比={float(measure['bbox_fill_ratio']) * 100:5.2f}%  "
              f"{'OK' if (size_ok and occ_ok) else 'CHECK'}")

    consistency = intra_pose_consistency(frames_out)
    measures = [f["measure"] for f in frames_out]                    # type: ignore[misc]
    preview_size = build_pose_preview(pose, sprites, measures)       # type: ignore[arg-type]
    print(f"  [preview] {pose.preview_path.name}  {preview_size[0]}x{preview_size[1]}")

    occ_values = [float(f["measure"]["occupancy_ratio"]) for f in frames_out]   # type: ignore[index]
    return {
        "key": pose.key,
        "note": pose.note,
        "target_size": list(pose.target),
        "expected_frames": pose.frames,
        "produced_frames": len(frames_out),
        "uniform_scale": round(scale, 5),
        "occupancy_window": list(pose.occupancy),
        "occupancy_range": [round(min(occ_values), 4), round(max(occ_values), 4)],
        "preview": str(pose.preview_path),
        "preview_size": list(preview_size),
        "frames": frames_out,
        "consistency": consistency,
        "_sprites": sprites,
    }


# --------------------------------------------------------------------------------------
# JSON 合并落盘
# --------------------------------------------------------------------------------------

def _load_json(path: Path) -> Dict[str, object]:
    """读取 JSON，不存在或损坏时返回空字典。"""
    if not path.is_file():
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (ValueError, OSError):
        return {}


def persist(results: Dict[str, Dict[str, object]]) -> None:
    """把本次处理的 pose 结果合并进汇总 JSON（不覆盖其他 pose 的已有数据）。

    Args:
        results: pose key -> 处理结果。
    """
    PHASED_DIR.mkdir(parents=True, exist_ok=True)

    summary = _load_json(SUMMARY_PATH)
    poses_block: Dict[str, object] = dict(summary.get("poses", {}))   # type: ignore[arg-type]
    for key, res in results.items():
        clean = {k: v for k, v in res.items() if not k.startswith("_")}
        poses_block[key] = clean

    produced = 0
    for value in poses_block.values():
        produced += int(value.get("produced_frames", 0))   # type: ignore[union-attr]

    summary.update({
        "phase": "D",
        "route": "3/4 view · img2img anchored to idle master · uniform per-pose scale + bottom align",
        "spec_source": "docs/art-sprite-plan.md §4①",
        "expected_total_frames": TOTAL_FRAMES,
        "produced_total_frames": produced,
        "pose_order": [p.key for p in POSES],
        "poses": poses_block,
    })
    SUMMARY_PATH.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")

    sizes = _load_json(SIZES_PATH)
    for key, res in results.items():
        sizes[key] = {
            "target_size": res["target_size"],
            "expected_frames": res["expected_frames"],
            "produced_frames": res["produced_frames"],
            "frames": {
                f"f{f['frame']}": {                          # type: ignore[index]
                    "output": f["output"],                    # type: ignore[index]
                    "size": f["measure"]["size"],             # type: ignore[index]
                    "mode": f["measure"]["mode"],             # type: ignore[index]
                    "size_ok": f["size_ok"],                  # type: ignore[index]
                    "occupancy_ratio": f["measure"]["occupancy_ratio"],   # type: ignore[index]
                    "bbox_fill_ratio": f["measure"]["bbox_fill_ratio"],   # type: ignore[index]
                    "occupancy_ok": f["occupancy_ok"],        # type: ignore[index]
                }
                for f in res["frames"]                        # type: ignore[union-attr]
            },
        }
    SIZES_PATH.write_text(json.dumps(sizes, ensure_ascii=False, indent=2), encoding="utf-8")


# --------------------------------------------------------------------------------------
# 报表与自检
# --------------------------------------------------------------------------------------

def report(results: Dict[str, Dict[str, object]]) -> bool:
    """打印测量表与自检结论。

    Args:
        results: pose key -> 处理结果。

    Returns:
        是否全部检查通过。
    """
    print()
    print("成品测量")
    print("-" * 96)
    print(f"{'pose':<12}{'帧':<5}{'尺寸':<10}{'模式':<7}{'透明占比':<11}"
          f"{'可见占比':<11}{'bbox占比':<11}{'判定'}")
    for pose in POSES:
        res = results.get(pose.key)
        if res is None:
            continue
        for f in res["frames"]:                                  # type: ignore[union-attr]
            m = f["measure"]                                      # type: ignore[index]
            ok = bool(f["size_ok"]) and bool(f["occupancy_ok"])   # type: ignore[index]
            print(
                f"{pose.key:<12}"
                f"f{f['frame']:<4}"                               # type: ignore[index]
                f"{m['size'][0]}x{m['size'][1]:<6}"               # type: ignore[index]
                f"{str(m['mode']):<7}"                            # type: ignore[index]
                f"{float(m['transparent_ratio']) * 100:>7.2f}%   "        # type: ignore[index]
                f"{float(m['occupancy_ratio']) * 100:>7.2f}%   "          # type: ignore[index]
                f"{float(m['bbox_fill_ratio']) * 100:>7.2f}%   "          # type: ignore[index]
                f"{'OK' if ok else 'CHECK'}"
            )
    print("-" * 96)

    print()
    print("组内一致性（各帧 vs 该 pose 第 1 帧）")
    print("-" * 96)
    for pose in POSES:
        res = results.get(pose.key)
        if res is None:
            continue
        cons = res["consistency"]                                 # type: ignore[index]
        pairs = cons["pairs"]                                     # type: ignore[index]
        if not pairs:
            print(f"  {pose.key:<12} 单帧或双帧，无需比对")
            continue
        hair_max = max(float(p["hair_rgb_distance"]) for p in pairs)
        robe_max = max(float(p["robe_rgb_distance"]) for p in pairs)
        luma_max = max(float(p["mean_luma_delta"]) for p in pairs)
        corr_min = min(float(p["row_profile_corr"]) for p in pairs)
        ovl_min = min(float(p["palette_overlap"]) for p in pairs)
        print(f"  {pose.key:<12} 发区色距max={hair_max:6.2f}  袍区色距max={robe_max:6.2f}  "
              f"亮度差max={luma_max:6.2f}  剖面相关min={corr_min:+.3f}  "
              f"色板重合min={ovl_min:.2f}  "
              f"{'PASS' if bool(cons['consistent']) else 'FAIL'}")   # type: ignore[index]
    print("-" * 96)

    checks: Dict[str, bool] = {}
    for pose in POSES:
        res = results.get(pose.key)
        if res is None:
            continue
        frames = res["frames"]                                    # type: ignore[index]
        checks[f"{pose.key} 帧数齐（{pose.frames}）"] = len(frames) == pose.frames   # type: ignore[arg-type]
        checks[f"{pose.key} 尺寸精确达标"] = all(bool(f["size_ok"]) for f in frames)     # type: ignore[union-attr,index]
        checks[f"{pose.key} 全部 RGBA"] = all(
            bool(f["measure"]["has_alpha"]) for f in frames        # type: ignore[union-attr,index]
        )
        checks[f"{pose.key} 可见占比达标"] = all(
            bool(f["occupancy_ok"]) for f in frames                # type: ignore[union-attr,index]
        )
        checks[f"{pose.key} 组内一致"] = bool(res["consistency"]["consistent"])   # type: ignore[index]
        checks[f"{pose.key} 预览可读"] = (
            pose.preview_path.exists() and pose.preview_path.stat().st_size > 0
        )

    print()
    print("自检")
    print("-" * 96)
    for name, ok in checks.items():
        print(f"  {'PASS' if ok else 'FAIL':<5} {name}")

    all_pass = all(checks.values()) if checks else False
    print()
    print(f"IS_PASS: {'YES' if all_pass else 'NO'}")
    return all_pass


# --------------------------------------------------------------------------------------
# 主流程
# --------------------------------------------------------------------------------------

def run(pose_key: str, overview_only: bool) -> int:
    """执行处理流程。

    Args:
        pose_key: pose 标识或 "all"。
        overview_only: 仅用现有成品重建总览预览。

    Returns:
        进程退出码，0 表示全部检查通过。
    """
    print("=" * 96)
    print(f"美术阶段 D · 3/4 视角女主全量批量（8 图集 / {TOTAL_FRAMES} 帧） · pose={pose_key}")
    print("=" * 96)

    if overview_only:
        rows: List[Tuple[PoseSpec, List[Image.Image]]] = []
        for pose in POSES:
            sprites: List[Image.Image] = []
            for n in range(1, pose.frames + 1):
                path = pose.out_path(n)
                if not path.is_file():
                    continue
                with Image.open(path) as im:
                    im.load()
                    sprites.append(im.convert("RGBA").copy())
            if sprites:
                rows.append((pose, sprites))
        if not rows:
            print("[FATAL] 没有任何成品可用于总览")
            return 2
        size = build_overview(rows)
        total = sum(len(s) for _, s in rows)
        print(f"[overview] {OVERVIEW_PATH}  {size[0]}x{size[1]}  共 {total} 帧")
        return 0

    if pose_key == "all":
        targets = list(POSES)
    else:
        pose = POSE_BY_KEY.get(pose_key)
        if pose is None:
            print(f"[FATAL] 未知 pose: {pose_key}")
            return 2
        targets = [pose]

    results: Dict[str, Dict[str, object]] = {}
    for pose in targets:
        print(f"\n>>> {pose.key}  {pose.target[0]}x{pose.target[1]} x{pose.frames}  ({pose.note})")
        res = process_pose(pose)
        if res is not None:
            results[pose.key] = res

    if not results:
        print("[FATAL] 无任何 pose 被处理")
        return 2

    persist(results)

    if pose_key == "all":
        rows = [(POSE_BY_KEY[k], results[k]["_sprites"]) for k in results]   # type: ignore[misc]
        size = build_overview(rows)                                          # type: ignore[arg-type]
        print(f"\n[overview] {OVERVIEW_PATH}  {size[0]}x{size[1]}")

    all_pass = report(results)
    return 0 if all_pass else 1


def main() -> int:
    """CLI 入口。"""
    try:
        sys.stdout.reconfigure(encoding="utf-8")   # type: ignore[attr-defined]
    except Exception:
        pass

    parser = argparse.ArgumentParser(
        description="美术阶段 D：3/4 视角女主全量批量（8 图集 / 39 帧）后处理"
    )
    parser.add_argument(
        "--pose",
        default="all",
        choices=[p.key for p in POSES] + ["all"],
        help="处理哪个 pose；all 表示全部",
    )
    parser.add_argument(
        "--overview-only",
        action="store_true",
        help="不重新处理，仅用现有成品重建总览预览图",
    )
    args = parser.parse_args()
    return run(args.pose, args.overview_only)


if __name__ == "__main__":
    raise SystemExit(main())
