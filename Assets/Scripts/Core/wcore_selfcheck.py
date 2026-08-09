#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
wcore_selfcheck.py —— T0 核心层算法对拍脚本（Godot 基线）

【它为什么存在】
本地环境没有 dotnet，Core/*.cs 无法编译验证。但「W-CORE 承伤频率」是整个手感
的地基，不能等到 Unity 侧才发现算错。于是把 C# 里的算法用 Python 一比一重写
一遍，直接对拍 Godot 的 r2_t01 实测日志。两套独立实现得出同一组数字，才说明
移植的是「算法」而不是「一堆看起来很像的代码」。

【对拍基线】.qa_logs/qa_t01_foundation.log
    1 源: 1.700 次/s (34 次/20s)      2 源: 3.350 次/s (67 次/20s)
    3 源: 4.300 次/s (86 次/20s)      4 源: 4.300 次/s
    8 源: 4.300 次/s                  围攻/单挑倍率 = 2.53x
    C8 软上限: 强灌 200 来源不 tick → 裁剪后 32 条

用法:
    python wcore_selfcheck.py
退出码 0 = 全过，1 = 有 FAIL。
"""

import json
import math
import os
import struct
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

# =============================================================================
# 与 C# 常量逐值对齐（改这里 = 改手感，必须同步改 WCore.cs / Difficulty.cs）
# =============================================================================

PER_SOURCE_HIT_CD = 0.6
GLOBAL_HIT_GAP = 0.2167
HIT_CD_MAP_SOFT_CAP = 32
DODGE_IFRAME = 0.25
GATE_EPSILON = 1e-6

# ★ 固定逻辑步长必须写成 1/60，绝不能写字面量 0.01667。
#   13 × 0.01667 = 0.21671 > 0.2167 → 全局闸门第 13 步就放行 → 围攻 4.65 次/s
#   13 × (1/60)  = 0.216667 < 0.2167 → 必须等到第 14 步 → 围攻 4.30 次/s（基线）
FIXED_STEP = 1.0 / 60.0
MAX_STEPS_PER_ADVANCE = 8

# Godot r2_t01 实测基线
BASELINE_FREQ = {1: 1.700, 2: 3.350, 3: 4.300, 4: 4.300, 8: 4.300}
BASELINE_RATIO = 2.53
FREQ_TOL = 0.06
RATIO_TOL = 0.05


# =============================================================================
# WCoreState 的 Python 等价实现（对应 Core/WCore.cs）
# =============================================================================

class WCoreState:
    """两层闸门的承伤状态机。逻辑与 C# 版逐行对应。"""

    def __init__(self):
        self.iframe = 0.0
        self.hp = 0.0
        self.hp_max = 1.0
        self.dodge_enabled = False       # 对拍必须关闭，否则污染纯承伤频率
        self.dodge_roll = None
        self.damage_filter = None
        self._source_cd = {}
        self._global_gap = 0.0

    @property
    def source_cd_count(self):
        return len(self._source_cd)

    @property
    def global_gap_remaining(self):
        return self._global_gap

    def tick(self, dt):
        """推进两层闸门。归零即删，防止冷却表随本局见过的敌人总数单调增长。"""
        if dt <= 0.0:
            return
        if self.iframe > 0.0:
            self.iframe -= dt
            if self.iframe <= GATE_EPSILON:
                self.iframe = 0.0
        if self._global_gap > 0.0:
            self._global_gap -= dt
            if self._global_gap <= GATE_EPSILON:
                self._global_gap = 0.0
        if not self._source_cd:
            return
        for sid in list(self._source_cd.keys()):
            left = self._source_cd[sid] - dt
            if left <= GATE_EPSILON:
                del self._source_cd[sid]
            else:
                self._source_cd[sid] = left

    def take_damage_from(self, source_id, dmg):
        """判定顺序：闪避无敌 → 全局最小间隔 → 该来源冷却 → 落闸 → 闪避骰 → 扣血。"""
        if self.iframe > 0.0:
            return False
        if self._global_gap > 0.0:
            return False
        if self._source_cd.get(source_id, 0.0) > 0.0:
            return False

        self._arm_hit_gates(source_id)

        if self.dodge_enabled and self.dodge_roll is not None and self.dodge_roll():
            self.iframe = DODGE_IFRAME
            return True

        applied = self.damage_filter(dmg) if self.damage_filter else dmg
        self.hp -= applied
        if self.hp <= 0.0:
            self.hp = 0.0
        return True

    def _arm_hit_gates(self, source_id):
        self._global_gap = GLOBAL_HIT_GAP
        self._source_cd[source_id] = PER_SOURCE_HIT_CD
        if len(self._source_cd) > HIT_CD_MAP_SOFT_CAP:
            self._trim_hit_cds()

    def _trim_hit_cds(self):
        """保留剩余冷却最长的 32 条。次级键用 source_id 升序，保证结果完全确定。"""
        items = sorted(self._source_cd.items(), key=lambda kv: (-kv[1], kv[0]))
        for sid, _ in items[HIT_CD_MAP_SOFT_CAP:]:
            del self._source_cd[sid]

    def reset(self):
        self._source_cd.clear()
        self._global_gap = 0.0
        self.iframe = 0.0

    def qa_force_arm_gates(self, source_id):
        """【仅供压测】绕过两层闸门直接落闸，用于触达裁剪分支。对应 C# QaForceArmGates。"""
        self._arm_hit_gates(source_id)


class FixedStepAccumulator:
    """把任意帧率的 deltaTime 切成整数个 1/60 秒逻辑步（对应 Core/WCore.cs）。"""

    def __init__(self):
        self.acc = 0.0

    def advance(self, delta_time, on_step):
        if delta_time > 0.0:
            self.acc += delta_time
        steps = 0
        while self.acc >= FIXED_STEP:
            self.acc -= FIXED_STEP
            steps += 1
            on_step(FIXED_STEP)
            if steps >= MAX_STEPS_PER_ADVANCE:
                self.acc = 0.0
                break
        return steps


# =============================================================================
# PCG32（对应 Core/PCG32.cs）
# =============================================================================

PCG_MULT = 6364136223846793005
PCG_DEFAULT_INC = 1442695040888963407
PCG_DEFAULT_SEED = 20260730
U64 = (1 << 64) - 1
U32 = 0xFFFFFFFF


class PCG32:
    def __init__(self, seed=PCG_DEFAULT_SEED, inc=PCG_DEFAULT_INC):
        self.inc = inc | 1
        self.state = seed & U64
        self.next_uint()   # 与 Godot RandomPCG::seed() 一致：播种后强制推进一次

    def next_uint(self):
        old = self.state
        self.state = (old * PCG_MULT + self.inc) & U64
        xorshifted = (((old >> 18) ^ old) >> 27) & U32
        rot = old >> 59
        return ((xorshifted >> rot) | (xorshifted << ((-rot) & 31))) & U32

    def next_float(self):
        return (self.next_uint() >> 8) * (1.0 / 16777216.0)

    def next_range(self, lo, hi):
        return lo if hi <= lo else lo + (hi - lo) * self.next_float()

    def next_range_int(self, lo, hi):
        return lo if hi <= lo else lo + self.next_uint() % (hi - lo + 1)


def djb2(s):
    """Godot String::hash()。注意是 djb2 不是 FNV-1a，遍历单位是 Unicode 码点。"""
    h = 5381
    for ch in s:
        h = ((h << 5) + h + ord(ch)) & U32
    return h


def fnv1a32(s):
    h = 2166136261
    for b in s.encode("utf-8"):
        h ^= b
        h = (h * 16777619) & U32
    return h


ZONE_SEED_MIX = 2654435761


def zone_seed(zone_id, is_safe, visits):
    h = djb2(zone_id)
    return h if is_safe else h ^ (visits * ZONE_SEED_MIX)


# =============================================================================
# Difficulty（对应 Core/Difficulty.cs）
# =============================================================================

DEFF_DIVISOR = 65.0
DEFF_DIVISOR_MAX = 73.8
DEFF_DIVISOR_MIN = 58.3
DEF_SOFTCAP_K = 100.0


def compute_deff(hp_max):
    return hp_max / DEFF_DIVISOR


def mitigation_of(defense, k=DEF_SOFTCAP_K):
    d = max(0.0, defense)
    return d / (d + max(1.0, k))


def damage_taken(raw, defense, k=DEF_SOFTCAP_K):
    return max(1.0, raw * (1.0 - mitigation_of(defense, k)))


def estimate_ttk(hp_max, deff, freq):
    return float("inf") if deff <= 0 or freq <= 0 else hp_max / (deff * freq)


# =============================================================================
# 模拟器
# =============================================================================

def simulate_lockstep(n_sources, duration=20.0, step=FIXED_STEP):
    """
    锁步模拟：直接按固定步长推进（等价于 Godot 60Hz headless 测试台）。
    每一步先 tick 闸门，再让 n 个来源按固定顺序各尝试攻击一次。
    """
    w = WCoreState()
    w.hp_max = 1e9
    w.hp = 1e9
    hits = 0
    total_steps = int(round(duration / FIXED_STEP))
    for _ in range(total_steps):
        w.tick(step)
        for sid in range(n_sources):
            if w.take_damage_from(sid, 1.0):
                hits += 1
    return hits, hits / duration


def simulate_at_fps(n_sources, fps, duration=20.0):
    """
    真实帧率驱动：用 deltaTime = 1/fps 喂累加器，逻辑仍落在 1/60 网格上。
    这是 Unity 侧的实际接法，用来证明手感与帧率解耦。
    """
    w = WCoreState()
    w.hp_max = 1e9
    w.hp = 1e9
    acc = FixedStepAccumulator()
    hits = [0]
    steps = [0]

    def on_step(dt):
        steps[0] += 1
        w.tick(dt)
        for sid in range(n_sources):
            if w.take_damage_from(sid, 1.0):
                hits[0] += 1

    frames = int(round(duration * fps))
    dt = 1.0 / fps
    for _ in range(frames):
        acc.advance(dt, on_step)
    return hits[0], steps[0], hits[0] / duration


# =============================================================================
# 断言框架
# =============================================================================

_results = []


def check(name, ok, detail=""):
    _results.append((name, bool(ok), detail))
    print("  [%s] %-46s %s" % ("PASS" if ok else "FAIL", name, detail))
    return ok


def approx(a, b, tol):
    return abs(a - b) <= tol


def section(title):
    print("")
    print("-" * 78)
    print(title)
    print("-" * 78)


# =============================================================================
# 用例
# =============================================================================

def test_frequency_table():
    section("T1 · W-CORE 承伤频率对拍（20s 窗口，dodge 关闭，固定步长 1/60）")
    print("  %-8s %-10s %-12s %-12s %-10s" % ("来源数", "命中次数", "实测 次/s", "Godot 基线", "偏差"))
    freqs = {}
    for n in (1, 2, 3, 4, 8):
        hits, freq = simulate_lockstep(n)
        freqs[n] = freq
        base = BASELINE_FREQ[n]
        print("  %-8d %-10d %-12.3f %-12.3f %+.4f" % (n, hits, freq, base, freq - base))
    print("")
    for n in (1, 2, 3, 4, 8):
        check("%d 源频率 ≈ %.3f 次/s" % (n, BASELINE_FREQ[n]),
              approx(freqs[n], BASELINE_FREQ[n], FREQ_TOL),
              "实测 %.4f（容差 ±%.2f）" % (freqs[n], FREQ_TOL))
    return freqs


def test_ratio(freqs):
    section("T2 · 围攻 / 单挑 承伤倍率")
    ratio = freqs[3] / freqs[1]
    check("倍率 ≈ %.2fx" % BASELINE_RATIO, approx(ratio, BASELINE_RATIO, RATIO_TOL),
          "实测 %.4fx（容差 ±%.2f）" % (ratio, RATIO_TOL))
    # 旧模型（命中即全局 iframe）恒为 1.00x —— 这正是「围攻无威胁」的根因
    check("倍率显著大于旧模型 1.00x", ratio > 2.0, "实测 %.4fx" % ratio)
    return ratio


def test_frame_quantization():
    section("T3 · 帧量化：证明固定步长常量必须写 1/60 而不是 0.01667")
    _, f_correct = simulate_lockstep(3, step=FIXED_STEP)
    _, f_wrong = simulate_lockstep(3, step=0.01667)
    print("  step = 1/60     → 围攻 %.3f 次/s  （14 步量化，= Godot 基线）" % f_correct)
    print("  step = 0.01667  → 围攻 %.3f 次/s  （13 步量化，倍率会飙到 %.2fx）"
          % (f_wrong, f_wrong / 1.7))
    check("1/60 步长复现基线 4.300", approx(f_correct, 4.300, FREQ_TOL), "%.4f" % f_correct)
    check("0.01667 字面量确实会跑偏（反例成立）", not approx(f_wrong, 4.300, FREQ_TOL),
          "%.4f ≠ 4.300" % f_wrong)


def test_frame_rate_independence():
    section("T4 · 帧率无关性（累加器驱动，攻击尝试锁在 1/60 网格）")
    print("  %-8s %-10s %-10s %-12s" % ("帧率", "逻辑步数", "命中次数", "频率 次/s"))
    base = None
    ok_all = True
    for fps in (30, 60, 90, 144, 240):
        hits, steps, freq = simulate_at_fps(3, fps)
        print("  %-8d %-10d %-10d %-12.3f" % (fps, steps, hits, freq))
        if base is None:
            base = freq
        if not approx(freq, BASELINE_FREQ[3], FREQ_TOL):
            ok_all = False
    check("30/60/90/144/240 fps 下围攻频率均 ≈ 4.300", ok_all,
          "全部落在 4.300 ± %.2f" % FREQ_TOL)


def test_soft_cap():
    section("T5 · C8 软上限压测（强灌 200 来源不 tick）")
    w = WCoreState()
    w.hp_max = 1e9
    w.hp = 1e9
    # 不 tick 时全局闸门一直亮着，正常路径只有第一次能过，冷却表永远涨不到 32 条
    # 以上、裁剪分支测不到。走 QA 后门直接落闸——这正是 Godot QA 脚本的做法。
    for sid in range(200):
        w.qa_force_arm_gates(sid)
    check("200 来源灌入后冷却表被裁到 32 条",
          w.source_cd_count == HIT_CD_MAP_SOFT_CAP,
          "实际 %d 条（上限 %d）" % (w.source_cd_count, HIT_CD_MAP_SOFT_CAP))
    # 裁剪保留「剩余冷却最长」的一批；此处全部相等，故按 source_id 升序保留前 32
    kept = sorted(w._source_cd.keys())
    check("裁剪结果确定（并列时按 source_id 升序保留）",
          kept == list(range(32)), "保留 %s..%s" % (kept[0], kept[-1]))


def test_gates():
    section("T6 · 闸门语义细节")
    w = WCoreState()
    w.hp_max = 1000.0
    w.hp = 1000.0

    # 同一帧内第二个来源必被全局闸门拦下
    a = w.take_damage_from(1, 10.0)
    b = w.take_damage_from(2, 10.0)
    check("同一逻辑步内第 2 个来源被全局闸门拦下", a and not b, "src1=%s src2=%s" % (a, b))

    # 全局闸门放行后，同一来源仍被自己的 0.6s 冷却挡住
    for _ in range(14):
        w.tick(FIXED_STEP)
    check("14 步后全局闸门开启", w.global_gap_remaining == 0.0,
          "剩余 %.6f" % w.global_gap_remaining)
    check("但来源 1 仍在自身 0.6s 冷却中", not w.take_damage_from(1, 10.0),
          "剩余 %.4f s" % w._source_cd.get(1, 0.0))
    check("来源 2 可以命中（每来源冷却互相独立）", w.take_damage_from(2, 10.0))

    # iframe 一票否决
    w.reset()
    w.iframe = DODGE_IFRAME
    check("闪避无敌期间任何来源都打不动", not w.take_damage_from(3, 10.0))

    # reset 清空
    w.reset()
    check("reset 后闸门与冷却表全清",
          w.source_cd_count == 0 and w.global_gap_remaining == 0.0 and w.iframe == 0.0)

    # 冷却归零即删（避免冷却表随本局敌人总数单调增长）
    w2 = WCoreState()
    w2.hp_max = 1e9
    w2.hp = 1e9
    w2.take_damage_from(7, 1.0)
    check("命中后冷却表 1 条", w2.source_cd_count == 1)
    for _ in range(36):
        w2.tick(FIXED_STEP)
    check("36 步（0.6s）后条目被移除而非留 0", w2.source_cd_count == 0)


def test_dodge_switch():
    section("T7 · 闪避开关（默认关，供对拍）")
    w = WCoreState()
    w.hp_max = 1000.0
    w.hp = 1000.0
    check("默认 dodge_enabled = False", w.dodge_enabled is False)
    w.dodge_enabled = True
    w.dodge_roll = lambda: True
    hit = w.take_damage_from(1, 999.0)
    check("闪避成功仍返回 True 且不扣血", hit and w.hp == 1000.0, "hp=%.1f" % w.hp)
    check("闪避成功后进入 %.2fs 短无敌" % DODGE_IFRAME, approx(w.iframe, DODGE_IFRAME, 1e-9),
          "iframe=%.4f" % w.iframe)


def test_difficulty():
    section("T8 · 模型 B 难度靶子（d_eff = hp_max / 65）")
    h = 300.0
    deff = compute_deff(h)
    a1 = estimate_ttk(h, deff, BASELINE_FREQ[3])   # 围攻 4.300
    a2 = estimate_ttk(h, deff, BASELINE_FREQ[1])   # 单挑 1.700
    print("  hp_max=300 → d_eff=%.4f   A1(围攻)=%.2fs   A2(单挑)=%.2fs" % (deff, a1, a2))
    print("  有效区间: %.4f ≤ d_eff ≤ %.4f" % (h / DEFF_DIVISOR_MAX, h / DEFF_DIVISOR_MIN))
    check("d_eff(300) ≈ 4.62", approx(deff, 4.62, 0.01), "%.4f" % deff)
    check("A1 落在 [10,16]s（文档校验值 15.1）", 10.0 <= a1 <= 16.0 and approx(a1, 15.1, 0.1),
          "%.2fs" % a1)
    check("A2 落在 [35,60]s（文档校验值 38.2）", 35.0 <= a2 <= 60.0 and approx(a2, 38.2, 0.1),
          "%.2fs" % a2)
    check("靶子落在有效区间内", h / DEFF_DIVISOR_MAX <= deff <= h / DEFF_DIVISOR_MIN)

    # A2 软上限减伤
    check("mitigation(0)=0", approx(mitigation_of(0.0), 0.0, 1e-9))
    check("mitigation(100)=0.5（def=K 时正好减半）", approx(mitigation_of(100.0), 0.5, 1e-9))
    check("减伤恒 < 1（无免疫悬崖）", mitigation_of(1e9) < 1.0, "%.9f" % mitigation_of(1e9))
    check("承伤下限 1.0 生效", approx(damage_taken(1.0, 1e6), 1.0, 1e-9))


def test_pcg32():
    section("T9 · PCG32 确定性")
    r = PCG32(PCG_DEFAULT_SEED)
    seq = [r.next_uint() for _ in range(8)]
    print("  seed=20260730 前 8 个 uint32:")
    print("    %s" % ", ".join(str(v) for v in seq))

    expected = [2674124880, 642922184, 2695042333, 1167291617,
                2978475972, 3047763484, 2354280111, 902273261]
    check("参考向量匹配（C# WCoreTests 内嵌同一组）", seq == expected)

    r2 = PCG32(PCG_DEFAULT_SEED)
    check("同种子重放序列完全一致", [r2.next_uint() for _ in range(8)] == seq)

    r3 = PCG32(PCG_DEFAULT_SEED + 1)
    check("不同种子序列不同", [r3.next_uint() for _ in range(8)] != seq)

    r4 = PCG32(PCG_DEFAULT_SEED)
    floats = [r4.next_float() for _ in range(2000)]
    check("next_float 全部落在 [0,1)", all(0.0 <= f < 1.0 for f in floats),
          "min=%.6f max=%.6f" % (min(floats), max(floats)))
    mean = sum(floats) / len(floats)
    check("next_float 均值 ≈ 0.5（±0.03）", approx(mean, 0.5, 0.03), "%.5f" % mean)

    r5 = PCG32(PCG_DEFAULT_SEED)
    ints = [r5.next_range_int(1, 6) for _ in range(6000)]
    check("next_range_int(1,6) 闭区间无越界", min(ints) == 1 and max(ints) == 6,
          "min=%d max=%d" % (min(ints), max(ints)))

    r6 = PCG32(PCG_DEFAULT_SEED)
    rng_vals = [r6.next_range(2.0, 5.0) for _ in range(1000)]
    check("next_range(2,5) 落在区间内", all(2.0 <= v < 5.0 for v in rng_vals))


def test_zone_seed():
    section("T10 · 区域种子派生")
    print("  %-18s %-12s %-14s %-14s" % ("zone_id", "djb2", "visits=0", "visits=3"))
    for zid in ("zone_fangshi", "zone_youhuang", "zone_chiyan"):
        h = djb2(zid)
        print("  %-18s %-12d %-14d %-14d" % (zid, h, zone_seed(zid, False, 0), zone_seed(zid, False, 3)))

    check("djb2('zone_fangshi') = 1943691360", djb2("zone_fangshi") == 1943691360)
    check("djb2('zone_youhuang') = 3189750928", djb2("zone_youhuang") == 3189750928)
    check("安全区种子与 visits 无关",
          zone_seed("zone_fangshi", True, 0) == zone_seed("zone_fangshi", True, 9)
          == djb2("zone_fangshi"))
    check("战斗区首次进入(visits=0) 种子 = hash",
          zone_seed("zone_youhuang", False, 0) == djb2("zone_youhuang"))
    check("战斗区 visits=1 与 visits=0 种子不同",
          zone_seed("zone_youhuang", False, 1) != zone_seed("zone_youhuang", False, 0))
    check("visits=3 种子超出 uint32（必须用 64 位存）",
          zone_seed("zone_youhuang", False, 3) > U32,
          "%d" % zone_seed("zone_youhuang", False, 3))
    # 连续 visits 的种子应彼此差异巨大（黄金比乘子的作用）
    seeds = [zone_seed("zone_chiyan", False, v) for v in range(8)]
    check("连续 8 次访问种子互不相同", len(set(seeds)) == 8)
    check("djb2 ≠ FNV-1a（订正规格：Godot 用 djb2）",
          djb2("zone_youhuang") != fnv1a32("zone_youhuang"),
          "djb2=%d fnv1a=%d" % (djb2("zone_youhuang"), fnv1a32("zone_youhuang")))


def test_zones_json():
    section("T11 · zones.json 数据契约")
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.normpath(os.path.join(here, "..", "..", "Data", "zones.json"))
    if not os.path.exists(path):
        check("找到 Assets/Data/zones.json", False, path)
        return
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)
    zones = doc.get("zones", [])
    check("找到 Assets/Data/zones.json", True, os.path.relpath(path, here))
    check("区域数量 = 6", len(zones) == 6, "实际 %d" % len(zones))
    ids = [z["zone_id"] for z in zones]
    check("zone_id 唯一", len(set(ids)) == len(ids))
    check("有且仅有 1 个 is_hub", sum(1 for z in zones if z.get("is_hub")) == 1)
    check("战斗区全部配了 boss",
          all(z.get("boss") for z in zones if not z.get("is_safe")))
    check("theme.size 全部合法",
          all(len(z["theme"]["size"]) == 2 and min(z["theme"]["size"]) > 0 for z in zones))
    check("_version 字段存在", "_version" in doc, "v%s" % doc.get("_version"))
    print("  区域: %s" % ", ".join(ids))
    print("  atk_mult 种子值（★ 非最终生效值，Unity 侧需按 d_eff 反调）: %s"
          % [z["enemies"]["atk_mult"] for z in zones])


def test_float32_safety():
    section("T12 · float32 精度安全（C# 用 float，必须确认不会翻车）")

    def f32(x):
        return struct.unpack("f", struct.pack("f", x))[0]

    step = f32(1.0 / 60.0)
    gap = f32(GLOBAL_HIT_GAP)
    v = gap
    for _ in range(13):
        v = f32(v - step)
    check("float32 下 13 步后全局闸门仍 > 0（不会误开）", v > GATE_EPSILON, "剩余 %.3e" % v)
    v = f32(v - step)
    check("float32 下第 14 步闸门归零", v <= GATE_EPSILON, "剩余 %.3e" % v)

    cd = f32(PER_SOURCE_HIT_CD)
    for _ in range(35):
        cd = f32(cd - step)
    check("float32 下 35 步后来源冷却仍 > 0", cd > GATE_EPSILON, "剩余 %.3e" % cd)
    cd = f32(cd - step)
    check("float32 下第 36 步来源冷却归零", cd <= GATE_EPSILON, "剩余 %.3e" % cd)
    print("  ⇒ 1e-6 容差远大于累积误差（~1e-7）、远小于最近真实边界（3.33e-5），安全。")


# =============================================================================
# main
# =============================================================================

def main():
    print("=" * 78)
    print("  W-CORE 核心层对拍自检 —— 对拍 Godot r2_t01 基线")
    print("  PER_SOURCE_HIT_CD=%.4f  GLOBAL_HIT_GAP=%.4f  FIXED_STEP=1/60=%.8f"
          % (PER_SOURCE_HIT_CD, GLOBAL_HIT_GAP, FIXED_STEP))
    print("=" * 78)

    freqs = test_frequency_table()
    ratio = test_ratio(freqs)
    test_frame_quantization()
    test_frame_rate_independence()
    test_soft_cap()
    test_gates()
    test_dodge_switch()
    test_difficulty()
    test_pcg32()
    test_zone_seed()
    test_zones_json()
    test_float32_safety()

    passed = sum(1 for _, ok, _ in _results if ok)
    failed = len(_results) - passed

    print("")
    print("=" * 78)
    print("  汇总: %d / %d 通过" % (passed, len(_results)))
    print("  频率表: " + "  ".join("%d源=%.3f" % (n, freqs[n]) for n in (1, 2, 3, 4, 8)))
    print("  围攻/单挑倍率: %.4fx（基线 %.2fx）" % (ratio, BASELINE_RATIO))
    print("=" * 78)
    if failed:
        print("  结果: FAIL（%d 项未通过）" % failed)
        for name, ok, detail in _results:
            if not ok:
                print("    - %s  %s" % (name, detail))
        return 1
    print("  结果: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
