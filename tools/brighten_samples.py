# -*- coding: utf-8 -*-
"""brighten_samples.py -- 水墨参考样品调亮工具（纯 Pillow，不调用任何 AI 生成接口）。

用途
----
把 ``Assets/images/samples/`` 下的水墨参考图提亮，让墨色晕染的层次更清晰可读，
同时保住宣纸的留白与浓墨的黑，作为后续批量出图的风格基准图。

实测前提（重要，决定了本工具的曲线设计）
--------------------------------------
先跑 ``--analyze`` 量过三张原图，结论与"图偏暗"的直觉相反：

===========  ======  ========  =====  =====  ==========
图           mean    median    p05    p95    暗部<64
===========  ======  ========  =====  =====  ==========
女侠立绘     171.1   204       39     237    11.9%
妖魔小怪     189.5   231        7     245    15.6%
山水场景     197.3   240       23     248     9.2%
===========  ======  ========  =====  =====  ==========

三张都是**亮宣纸底**，不是暗底。因此"整体 gamma 提亮"是错的处方：

* 纸白已经在 235~250，再抬只会把留白推成死白（过曝），拿不到任何层次收益；
* 真正丢信息的地方在暗部——妖魔那张 ``p05=7``、15.6% 的像素挤在 64 以下，
  墨团内部的浓淡变化被压扁了；
* 实测过一版"全局 gamma1.35 + 高支点对比"，结果 ``p05`` 被压到 **0**
  （黑更死）、``p95`` 反而从 245 掉到 244（白更平），两头都变差。

所以这里默认走 ``shadow`` 模式：**提亮只作用在暗部，纸白几乎不动**。

默认参数 ``gamma=1.30 / contrast=1.16`` 是跑参数扫描选出来的：在
"墨区加权斜率三张图全 >=1.0"（层次不被压扁）的约束下，取提亮幅度最大的一组。
再往上加 gamma，女侠那张的斜率就会跌破 1，墨色开始发灰。

曲线设计
--------
1. **查找表（LUT）**：256 项一次算好，``Image.point`` 直接套，比逐像素循环快
   两个数量级，且完全可复现。
2. **暗部加权 gamma**：先算全局提亮值 ``lifted = v ** (1/gamma)``，
   再用权重 ``w = (1 - v) ** shadow_focus`` 把它按亮度混回去::

       out = v + (lifted - v) * w

   ``w`` 在 v=0 处为 1、在 v=1 处为 0，于是暗部拿到全部提亮量、
   纸白拿到 0。实测 v=0.94（纸白 240）时 w≈0.004，等于原样不动。
   这正是它优于全局 gamma 的地方：**收益全给墨，风险不给纸**。
3. **温和对比**：以 ``pivot`` 为轴做小幅线性拉伸找回反差。pivot 固定取中灰
   0.5 而非中位亮度——中位在 204~240 之间，拿它当支点会把整个暗部往下拽，
   这就是上一版把 ``p05`` 压到 0 的元凶。
4. **高光软限幅**：knee 以上用渐近压缩代替硬 clamp，数学上永远到不了 1.0，
   从根上杜绝大片纸白被削平成一块死白。

另提供 ``--mode global`` 保留"全局 gamma"的字面实现，便于对照，但不推荐。

不做的事
--------
* 不裁剪、不缩放、不改长宽比。
* 不覆盖、不删除原图；只往 ``samples_brightened/`` 写新文件。
* 不碰 ``.meta``（Unity 导入记录，动了会让工程认不出资源）。

用法
----
    python brighten_samples.py --analyze              # 只看统计，不写文件
    python brighten_samples.py                        # 默认 shadow 模式处理
    python brighten_samples.py --mode global          # 字面版全局 gamma（对照）
    python brighten_samples.py --gamma 1.4 --contrast 1.10
"""

from __future__ import annotations

import argparse
import math
import os
import sys
from typing import Dict, List, Sequence, Tuple

try:
    from PIL import Image
except ImportError:  # pragma: no cover - 环境缺库时给出明确指引
    sys.stderr.write(
        "[FATAL] 未找到 Pillow。请先安装：\n"
        "  python -m pip install Pillow\n"
    )
    raise SystemExit(2)


# --- 路径常量（锁死绝对路径，避免 cwd 漂移到别的工程）-----------------------
PROJECT_ROOT: str = "F:/AI-project/ancientGame/shuimofeng/shuimofeng"
SRC_DIR: str = os.path.join(PROJECT_ROOT, "Assets", "images", "samples")
DST_DIR: str = os.path.join(PROJECT_ROOT, "Assets", "images", "samples_brightened")

# --- 默认调亮参数 -----------------------------------------------------------
DEFAULT_MODE: str = "shadow"
DEFAULT_GAMMA: float = 1.30          # 暗部提亮强度
DEFAULT_SHADOW_FOCUS: float = 2.0    # 提亮量随亮度衰减的指数，越大越只管暗部
DEFAULT_CONTRAST: float = 1.16       # 对比度系数
DEFAULT_PIVOT: float = 0.50          # 对比支点，固定中灰
DEFAULT_EDGE_TAPER: float = 1.0      # 对比在两端的收敛指数，保端点不被钳位
DEFAULT_KNEE: float = 0.95           # 高光软限幅拐点
SUFFIX: str = "_brightened"

# 墨区阈值：灰阶低于此值的像素视为"墨"，用于统计晕染层次是否被拉开。
INK_THRESHOLD: int = 200


def build_lut(
    mode: str,
    gamma: float,
    shadow_focus: float,
    contrast: float,
    pivot: float,
    edge_taper: float,
    knee: float,
) -> List[int]:
    """构造 256 项亮度映射表。

    Args:
        mode: ``"shadow"`` 暗部加权提亮（推荐）；``"global"`` 全局 gamma（对照）。
        gamma: 提亮强度，>1 抬暗部。
        shadow_focus: 暗部权重指数，仅 ``shadow`` 模式生效。越大则提亮越集中在暗部。
        contrast: 对比度系数，以 ``pivot`` 为轴线性拉伸。
        pivot: 对比支点（0~1）。
        edge_taper: 对比强度在 0/1 两端的收敛指数。
        knee: 高光软限幅拐点（0~1）。

    Returns:
        长度 256 的整数列表，范围 0~255，可直接喂给 ``Image.point``。
    """
    inv_gamma: float = 1.0 / max(gamma, 1e-6)
    lut: List[int] = [0] * 256

    for i in range(256):
        v: float = i / 255.0

        # 1) 提亮。
        lifted: float = v ** inv_gamma
        if mode == "shadow":
            # 权重在暗部为 1、亮部趋 0，于是纸白不被抬。
            w: float = (1.0 - v) ** max(shadow_focus, 0.0)
            v = v + (lifted - v) * w
        else:
            v = lifted

        # 2) 以固定中灰为轴补回对比度。
        #
        # 关键：线性拉伸 (v-pivot)*c+pivot 在 v 接近 0 时会算出负数，
        # 钳位后一整段暗部被拍平成纯黑——实测这会让最暗的 2% 墨色糊成一团。
        # 所以给对比量乘一个两端归零的权重 4v(1-v)，让 0 和 1 成为不动点：
        # 中间调拿满对比，端点原样保留，钳位再也不会触发。
        contrasted: float = (v - pivot) * contrast + pivot
        edge_w: float = 4.0 * v * (1.0 - v)
        if edge_w < 0.0:
            edge_w = 0.0
        elif edge_w > 1.0:
            edge_w = 1.0
        if edge_taper != 1.0:
            edge_w = edge_w ** max(edge_taper, 0.0)
        v = v + (contrasted - v) * edge_w

        # 3) 高光软限幅：渐近压缩，永远到不了 1.0。
        if v > knee:
            over: float = v - knee
            headroom: float = max(1.0 - knee, 1e-6)
            v = knee + headroom * (over / (over + headroom))

        # 4) 落地钳位。
        if v < 0.0:
            v = 0.0
        elif v > 1.0:
            v = 1.0

        lut[i] = int(round(v * 255.0))

    return lut


def luma_histogram(img: Image.Image) -> List[int]:
    """返回图像灰度直方图（256 项）。仅统计 RGB，忽略 alpha。"""
    gray: Image.Image = img.convert("L")
    return list(gray.histogram())


def hist_stats(hist: Sequence[int]) -> Dict[str, float]:
    """从直方图算出亮度统计量。

    Args:
        hist: 长度 256 的直方图。

    Returns:
        统计字典。``ink_std`` 为墨区（灰阶 < :data:`INK_THRESHOLD`）的标准差，
        它是"晕染层次是否被拉开"的核心指标——越大说明墨的浓淡分得越开。
    """
    total: int = sum(hist)
    if total <= 0:
        return {
            "mean": 0.0, "median": 0.0, "p05": 0.0, "p95": 0.0,
            "shadow_ratio": 0.0, "clipped_ratio": 0.0,
            "black_ratio": 0.0, "ink_std": 0.0,
        }

    mean: float = sum(i * n for i, n in enumerate(hist)) / total

    def percentile(frac: float) -> float:
        """返回累计占比首次达到 frac 时的灰阶值。"""
        target: float = total * frac
        acc: int = 0
        for i, n in enumerate(hist):
            acc += n
            if acc >= target:
                return float(i)
        return 255.0

    # 墨区标准差：只看灰阶 < INK_THRESHOLD 的像素。
    ink_n: int = sum(hist[:INK_THRESHOLD])
    ink_std: float = 0.0
    if ink_n > 0:
        ink_mean: float = sum(
            i * hist[i] for i in range(INK_THRESHOLD)) / ink_n
        ink_var: float = sum(
            hist[i] * (i - ink_mean) ** 2 for i in range(INK_THRESHOLD)) / ink_n
        ink_std = math.sqrt(max(ink_var, 0.0))

    return {
        "mean": mean,
        "median": percentile(0.50),
        "p05": percentile(0.05),
        "p95": percentile(0.95),
        "shadow_ratio": sum(hist[:64]) / total,
        "clipped_ratio": hist[255] / total,
        "black_ratio": hist[0] / total,
        "ink_std": ink_std,
    }


def ink_weighted_slope(hist: Sequence[int], lut: Sequence[int]) -> float:
    """墨区的直方图加权曲线斜率——判断晕染层次是被拉开还是被压扁的核心指标。

    为什么不用"墨区标准差"：提亮会把一部分暗像素推过
    :data:`INK_THRESHOLD` 而离开统计区间，样本population 本身变了，
    标准差的升降因此无法归因，是个被污染的指标。

    曲线斜率没有这个问题。``lut[i+1] - lut[i]`` 就是输入相差 1 级时输出相差
    多少级：等于 1 表示原样，大于 1 表示这一段的浓淡差异被放大（层次更清楚），
    小于 1 表示被压扁。按墨区像素数加权平均，得到的就是
    "墨色层次平均被放大了多少倍"。

    Args:
        hist: 长度 256 的原图直方图。
        lut: 长度 256 的映射表。

    Returns:
        加权平均斜率。>=1.0 表示墨区层次整体没有损失。
    """
    total: int = 0
    acc: float = 0.0
    for i in range(min(INK_THRESHOLD, 255)):
        n: int = hist[i]
        if n <= 0:
            continue
        acc += n * float(lut[i + 1] - lut[i])
        total += n
    if total <= 0:
        return 1.0
    return acc / total


def format_stats(tag: str, st: Dict[str, float]) -> str:
    """把统计字典排成一行可读文本。"""
    return (
        "    {tag:<7} mean={mean:6.2f}  median={median:5.1f}  "
        "p05={p05:5.1f}  p95={p95:5.1f}  "
        "暗部<64={sr:5.1%}  纯黑0={br:5.2%}  纯白255={cr:5.2%}  "
        "墨区std={ink:5.2f}"
    ).format(
        tag=tag,
        mean=st["mean"], median=st["median"],
        p05=st["p05"], p95=st["p95"],
        sr=st["shadow_ratio"], br=st["black_ratio"],
        cr=st["clipped_ratio"], ink=st["ink_std"],
    )


def verdict(
    before: Dict[str, float],
    after: Dict[str, float],
    slope: float,
) -> List[str]:
    """对比处理前后，给出质量判定。

    判据（任一不满足即视为该项失败）：

    * 不过曝：纯白 255 占比增量 < 0.5 个百分点；
    * 不压黑：纯黑 0 占比增量 < 0.5 个百分点，且 ``p05`` 不下降；
    * 有提亮：整体 ``mean`` 上升；
    * 有层次：墨区加权斜率 >= 0.98（容许 2% 的数值误差）。

    Args:
        before: 处理前统计。
        after: 处理后统计。
        slope: 墨区直方图加权曲线斜率，见 :func:`ink_weighted_slope`。

    Returns:
        判定文本列表，每项以 OK / WARN 开头。
    """
    lines: List[str] = []

    d_clip: float = after["clipped_ratio"] - before["clipped_ratio"]
    lines.append(
        ("    OK   不过曝：纯白占比 {a:+.3%}（阈值 <0.5%）"
         if d_clip < 0.005 else
         "    WARN 过曝：纯白占比 {a:+.3%} 超阈值 0.5%").format(a=d_clip))

    d_black: float = after["black_ratio"] - before["black_ratio"]
    d_p05: float = after["p05"] - before["p05"]
    if d_black < 0.005 and d_p05 >= 0.0:
        lines.append(
            "    OK   不压黑：纯黑占比 {a:+.3%}，p05 {b:+.1f}".format(
                a=d_black, b=d_p05))
    else:
        lines.append(
            "    WARN 压黑：纯黑占比 {a:+.3%}，p05 {b:+.1f}".format(
                a=d_black, b=d_p05))

    d_mean: float = after["mean"] - before["mean"]
    lines.append(
        ("    OK   有提亮：mean {a:+.2f}" if d_mean > 0.0 else
         "    WARN 未提亮：mean {a:+.2f}").format(a=d_mean))

    d_ink: float = after["ink_std"] - before["ink_std"]
    lines.append(
        ("    OK   层次保持：墨区加权斜率 {s:.3f}（>=0.98），"
         "墨区 std {a:+.2f}（参考值）"
         if slope >= 0.98 else
         "    WARN 层次压扁：墨区加权斜率 {s:.3f} < 0.98，"
         "墨区 std {a:+.2f}（参考值）").format(s=slope, a=d_ink))

    return lines


def collect_sources(src_dir: str) -> List[str]:
    """列出源目录下所有 PNG（排除 .meta），按文件名排序。"""
    if not os.path.isdir(src_dir):
        raise FileNotFoundError("源目录不存在: " + src_dir)

    names: List[str] = [
        n for n in os.listdir(src_dir)
        if n.lower().endswith(".png") and not n.lower().endswith(".meta")
    ]
    names.sort()
    return names


def apply_lut(img: Image.Image, lut: Sequence[int]) -> Image.Image:
    """对 RGB 三通道套用同一条 LUT，alpha 原样保留。

    三通道共用一条曲线是刻意的：分通道各自映射会改变色相，
    水墨图那点朱砂红会被拧成橙或品红。

    Args:
        img: 源图像，任意模式。
        lut: 256 项映射表。

    Returns:
        处理后的新图像（RGBA 或 RGB，与输入是否含 alpha 一致）。
    """
    has_alpha: bool = img.mode in ("RGBA", "LA") or (
        img.mode == "P" and "transparency" in img.info
    )
    table: List[int] = list(lut)

    if has_alpha:
        rgba: Image.Image = img.convert("RGBA")
        r, g, b, a = rgba.split()
        return Image.merge(
            "RGBA", (r.point(table), g.point(table), b.point(table), a))

    rgb: Image.Image = img.convert("RGB")
    return rgb.point(table * 3)


def process_one(
    name: str,
    src_dir: str,
    dst_dir: str,
    lut: Sequence[int],
    analyze_only: bool,
) -> Tuple[str, bool, bool]:
    """处理单张图。

    Args:
        name: 文件名（不含目录）。
        src_dir: 源目录绝对路径。
        dst_dir: 输出目录绝对路径。
        lut: 已构造好的 256 项映射表。
        analyze_only: 为 True 时只打印统计，不写文件。

    Returns:
        ``(输出文件绝对路径, 是否写了文件, 是否全部判定通过)``。
    """
    src_path: str = os.path.join(src_dir, name)
    stem, ext = os.path.splitext(name)
    out_name: str = stem + SUFFIX + ext
    dst_path: str = os.path.join(dst_dir, out_name)

    with Image.open(src_path) as im:
        im.load()
        src_hist: List[int] = luma_histogram(im)
        before: Dict[str, float] = hist_stats(src_hist)
        out_img: Image.Image = apply_lut(im, lut)
        after: Dict[str, float] = hist_stats(luma_histogram(out_img))
        slope: float = ink_weighted_slope(src_hist, lut)

        print("  " + name)
        print("    尺寸={w}x{h}  模式={m}".format(
            w=im.width, h=im.height, m=im.mode))
        print(format_stats("处理前", before))
        print(format_stats("处理后", after))

        checks: List[str] = verdict(before, after, slope)
        for line in checks:
            print(line)
        all_ok: bool = all(c.strip().startswith("OK") for c in checks)

        if analyze_only:
            out_img.close()
            return (dst_path, False, all_ok)

        os.makedirs(dst_dir, exist_ok=True)
        out_img.save(dst_path, format="PNG", optimize=True)
        out_img.close()

    print("    -> 已写出 {n}  ({s:.0f} KB)".format(
        n=out_name, s=os.path.getsize(dst_path) / 1024.0))
    return (dst_path, True, all_ok)


def main(argv: Sequence[str]) -> int:
    """命令行入口。返回进程退出码，0 表示全部通过。"""
    parser = argparse.ArgumentParser(
        description="水墨参考样品调亮（Pillow，纯本地处理）")
    parser.add_argument("--mode", choices=("shadow", "global"),
                        default=DEFAULT_MODE,
                        help="shadow=暗部加权提亮（推荐）；global=全局 gamma（对照）")
    parser.add_argument("--gamma", type=float, default=DEFAULT_GAMMA,
                        help="提亮强度，>1 抬暗部（默认 %(default)s）")
    parser.add_argument("--shadow-focus", type=float,
                        default=DEFAULT_SHADOW_FOCUS,
                        help="暗部权重指数，越大越只管暗部（默认 %(default)s）")
    parser.add_argument("--contrast", type=float, default=DEFAULT_CONTRAST,
                        help="对比度系数，>1 增强反差（默认 %(default)s）")
    parser.add_argument("--pivot", type=float, default=DEFAULT_PIVOT,
                        help="对比支点 0~1（默认 %(default)s）")
    parser.add_argument("--edge-taper", type=float, default=DEFAULT_EDGE_TAPER,
                        help="对比在两端的收敛指数（默认 %(default)s）")
    parser.add_argument("--knee", type=float, default=DEFAULT_KNEE,
                        help="高光软限幅拐点（默认 %(default)s）")
    parser.add_argument("--analyze", action="store_true",
                        help="只输出统计，不写任何文件")
    parser.add_argument("--src", type=str, default=SRC_DIR, help="源目录")
    parser.add_argument("--dst", type=str, default=DST_DIR, help="输出目录")
    args = parser.parse_args(list(argv))

    print("源目录: " + args.src)
    print("输出目录: " + args.dst)
    print(("参数: mode={m}  gamma={g}  shadow_focus={sf}  contrast={c}  "
           "pivot={p}  edge_taper={e}  knee={k}  analyze_only={a}").format(
        m=args.mode, g=args.gamma, sf=args.shadow_focus, c=args.contrast,
        p=args.pivot, e=args.edge_taper, k=args.knee, a=args.analyze))
    print("-" * 96)

    try:
        names: List[str] = collect_sources(args.src)
    except FileNotFoundError as exc:
        sys.stderr.write("[FATAL] " + str(exc) + "\n")
        return 1

    if not names:
        sys.stderr.write("[FATAL] 源目录下没有 PNG: " + args.src + "\n")
        return 1

    lut: List[int] = build_lut(
        args.mode, args.gamma, args.shadow_focus,
        args.contrast, args.pivot, args.edge_taper, args.knee)

    # 打印曲线采样点，便于复核"纸白不动、暗部抬起"。
    probes: List[int] = [0, 16, 32, 64, 96, 128, 160, 200, 230, 240, 250, 255]
    print("曲线采样  " + "  ".join(
        "{i}->{o}".format(i=p, o=lut[p]) for p in probes))
    print("-" * 96)

    written: List[str] = []
    all_pass: bool = True
    for name in names:
        path, ok, passed = process_one(
            name, args.src, args.dst, lut, args.analyze)
        if ok:
            written.append(path)
        all_pass = all_pass and passed
        print("")

    print("-" * 96)
    print("共扫描 {n} 张，写出 {w} 张。判定: {v}".format(
        n=len(names), w=len(written),
        v="全部通过" if all_pass else "存在 WARN，请复核参数"))
    for p in written:
        print("  " + p)
    return 0 if all_pass else 3


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
