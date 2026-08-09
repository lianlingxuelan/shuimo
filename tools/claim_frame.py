# -*- coding: utf-8 -*-
"""美术阶段 D 辅助：把 ImageGen 刚落盘的原图重命名为规范帧名 ``f<N>.png``。

ImageGen 以「提示词片段 + 时间戳」命名输出文件，直接交给后处理脚本会有匹配歧义
（同目录多帧时无法确定谁是第几帧）。本脚本在每次出图后立即把「目录内最新的、
尚未规范命名的 PNG」认领为指定帧号，使 ``make_heroine_full.py`` 能稳定按
``f1..fN`` 读取。

用法::

    python claim_frame.py idle 2          # 认领为 gen_idle/f2.png
    python claim_frame.py attack 5        # 认领为 gen_attack/f5.png
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path
from typing import List, Optional

from PIL import Image

PHASED_DIR: Path = Path(
    "F:/AI-project/ancientGame/shuimofeng/shuimofeng/Assets/images/_raw_gen/phaseD"
)

# 已规范命名的帧文件，认领时需要排除，避免把上一帧当成新图。
CANONICAL_RE: re.Pattern[str] = re.compile(r"^f\d+\.png$")

# ImageGen 返回的原图最小可接受边长（低于此值说明出图异常）。
MIN_SIDE: int = 512


def newest_unclaimed(gen_dir: Path) -> Optional[Path]:
    """取目录内最新的、尚未规范命名的 PNG。

    Args:
        gen_dir: pose 的原图目录。

    Returns:
        待认领的 PNG 路径；没有则返回 None。
    """
    if not gen_dir.is_dir():
        return None
    cands: List[Path] = [
        p for p in gen_dir.glob("*.png")
        if p.is_file() and not CANONICAL_RE.match(p.name) and not p.name.startswith("_")
    ]
    if not cands:
        return None
    return max(cands, key=lambda p: p.stat().st_mtime)


def claim(pose: str, frame_no: int) -> int:
    """把最新原图认领为 ``gen_<pose>/f<frame_no>.png``。

    Args:
        pose: pose 标识。
        frame_no: 帧号（1-based）。

    Returns:
        退出码，0 表示成功。
    """
    gen_dir = PHASED_DIR / f"gen_{pose}"
    src = newest_unclaimed(gen_dir)
    if src is None:
        print(f"[FATAL] {gen_dir} 内没有待认领的新 PNG")
        return 2

    dst = gen_dir / f"f{frame_no}.png"
    if dst.exists():
        dst.unlink()
    src.rename(dst)

    with Image.open(dst) as im:
        im.load()
        width, height = im.size
        mode = im.mode

    ok = min(width, height) >= MIN_SIDE
    print(f"[claim] {pose} f{frame_no}  <- {src.name}")
    print(f"        {dst}  {width}x{height} {mode}  "
          f"{'OK' if ok else f'WARN 分辨率过小(<{MIN_SIDE})，img2img 参考可能被拒'}")
    return 0 if ok else 1


def main() -> int:
    """CLI 入口。"""
    try:
        sys.stdout.reconfigure(encoding="utf-8")   # type: ignore[attr-defined]
    except Exception:
        pass

    parser = argparse.ArgumentParser(description="认领 ImageGen 输出为规范帧名")
    parser.add_argument("pose", help="pose 标识，如 idle / walk_down / attack")
    parser.add_argument("frame", type=int, help="帧号（1-based）")
    args = parser.parse_args()
    return claim(args.pose, args.frame)


if __name__ == "__main__":
    raise SystemExit(main())
