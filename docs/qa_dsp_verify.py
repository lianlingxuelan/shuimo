# -*- coding: utf-8 -*-
"""
QA 复刻验算：把 SfxSynth / SfxRecipes 的关键算法用 Python 重写一遍，
独立验证 amb_wind_loop 的削波 / NaN / 循环接缝 / 样本数。
纯 stdlib，不依赖 numpy。
"""
import math, random, struct

def f32(x):
    """模拟 C# float 单精度截断。"""
    return struct.unpack('f', struct.pack('f', x))[0]

PI = math.pi
TWO_PI = 2.0 * math.pi

# ---- SfxSynth 原语复刻 ----
def svf_coeff(fc, fs):
    if fs <= 0: return 0.0
    nyq = fs * 0.5
    f = max(1.0, min(fc, nyq * 0.98))
    return f32(2.0 * math.sin(PI * f / fs))

def svf_clamp(f, q):
    if q < 0.02: q = 0.02
    fmax = f32(-q + math.sqrt(q * q + 3.6))
    if fmax < 0.001: fmax = 0.001
    if f > fmax: f = fmax
    if f < 0.0: f = 0.0
    return f, q

class Svf:
    __slots__ = ('low', 'band', 'high')
    def __init__(self): self.low = 0.0; self.band = 0.0; self.high = 0.0

def svf_step(s, x, f, q):
    ff, qq = svf_clamp(f, q)
    s.low = f32(s.low + ff * s.band)
    s.high = f32(x - s.low - qq * s.band)
    s.band = f32(s.band + ff * s.high)
    return s.band

def white_noise(rng):
    return f32(2.0 * rng.random() - 1.0)

def rms(buf):
    return math.sqrt(sum(v * v for v in buf) / len(buf))

def peak(buf):
    return max(abs(v) for v in buf)

def sanitize(buf):
    n = 0
    for i, v in enumerate(buf):
        if math.isnan(v) or math.isinf(v):
            buf[i] = 0.0; n += 1
    return n

def soft_clip(x):
    th = 0.70
    a = -x if x < 0 else x
    if a <= th: return x
    sign = -1.0 if x < 0 else 1.0
    over = (a - th) / (1.0 - th)
    return f32(sign * (th + (1.0 - th) * math.tanh(over)))

def normalize(buf, mode, target):
    """mode: 'Peak' | 'Rms'"""
    if not buf or target <= 0: return 0
    bad = sanitize(buf)
    measured = rms(buf) if mode == 'Rms' else peak(buf)
    if measured < 1e-9: return bad
    scale = target / measured
    for i in range(len(buf)):
        buf[i] = f32(buf[i] * scale)
    if mode == 'Rms':
        for i in range(len(buf)):
            buf[i] = soft_clip(buf[i])
    return bad

def crossfade_loop(src, loop_samples, xf_samples):
    if src is None or loop_samples <= 0:
        return [0.0] * max(loop_samples, 0)
    l = min(loop_samples, len(src))
    dst = [0.0] * l
    xf = max(0, xf_samples)
    if xf > l: xf = l
    if l + xf > len(src):
        xf = max(0, len(src) - l)
    for i in range(xf):
        w = i / float(xf)
        fade_in = math.sin(PI * w * 0.5)
        fade_out = math.cos(PI * w * 0.5)
        dst[i] = f32(src[i] * fade_in + src[l + i] * fade_out)
    for i in range(xf, l):
        dst[i] = src[i]
    return dst

# ---- WindLoop 配方复刻（SfxRecipes.cs:1369-1413）----
WL = dict(MainFcBase=500.0, MainFcDepth=200.0, MainAmpBase=0.775, MainAmpDepth=0.225,
          MainQ=0.9, SubCenter=1131.0, SubQ=1.2, SubGain=0.18,
          SubAmpBase=0.5, SubAmpDepth=0.5, Lfo1=1.0, Lfo2=2.0, Lfo3=3.0)

def lfo(freq_hz, t):
    return f32(math.sin(TWO_PI * freq_hz * t))

def wind_loop(fs=22050, dur=12.0, xf_sec=0.20, norm_target=0.12, seed=12345):
    loop_len = int(dur * fs)
    xf = int(xf_sec * fs)
    total = loop_len + xf
    rng = random.Random(seed)
    l1 = WL['Lfo1'] / dur; l2 = WL['Lfo2'] / dur; l3 = WL['Lfo3'] / dur
    sm, ss = Svf(), Svf()
    qm = 1.0 / WL['MainQ']; qs = 1.0 / WL['SubQ']
    f_sub = svf_coeff(WL['SubCenter'], fs)
    src = [0.0] * total
    for i in range(total):
        t = f32(i / float(fs))
        fc = f32(WL['MainFcBase'] + WL['MainFcDepth'] * lfo(l1, t))
        fm = svf_coeff(fc, fs)
        svf_step(sm, white_noise(rng), fm, qm)
        main = f32(sm.low * (WL['MainAmpBase'] + WL['MainAmpDepth'] * lfo(l2, t)))
        band = svf_step(ss, white_noise(rng), f_sub, qs)
        sub = f32(band * WL['SubGain'] * (WL['SubAmpBase'] + WL['SubAmpDepth'] * lfo(l3, t)))
        src[i] = f32(main + sub)
    raw_peak = peak(src)
    buf = crossfade_loop(src, loop_len, xf)
    bad = normalize(buf, 'Rms', norm_target)
    return buf, loop_len, xf, raw_peak, bad

def report():
    print("=" * 68)
    print("amb_wind_loop  DSP 复刻验算 (fs=22050, dur=12.0s, Rms target=0.12)")
    print("=" * 68)
    buf, loop_len, xf, raw_peak, bad = wind_loop()

    # ① 样本数
    expect = int(12.0 * 22050)
    print(f"[1] 样本数    : {len(buf)}  期望 {expect}  -> {'PASS' if len(buf)==expect else 'FAIL'}")
    print(f"    交叉淡化长度 xf = {xf} 样本 ({xf/22050.0:.3f}s)")

    # ② NaN / Inf
    nan_n = sum(1 for v in buf if math.isnan(v))
    inf_n = sum(1 for v in buf if math.isinf(v))
    print(f"[2] NaN={nan_n}  Inf={inf_n}  归一化前被 Sanitize 清除={bad}  -> "
          f"{'PASS' if nan_n==0 and inf_n==0 and bad==0 else 'FAIL'}")

    # ③ 削波
    pk = peak(buf); r = rms(buf)
    over = sum(1 for v in buf if abs(v) > 1.0)
    at_sc = sum(1 for v in buf if abs(v) > 0.70)
    print(f"[3] 峰值      : {pk:.6f}   RMS = {r:.6f} (目标 0.12)")
    print(f"    |x|>1.0 的样本数 = {over}  -> {'PASS (无削波)' if over==0 else 'FAIL'}")
    print(f"    进入软削波区(|x|>0.70)的样本数 = {at_sc} ({at_sc/len(buf)*100:.4f}%)")
    print(f"    归一化前原始峰值 = {raw_peak:.6f}  波峰因数 crest = {pk/r:.2f}")

    # ④ 循环接缝（核心：R-10 一票否决项）
    seam = abs(buf[0] - buf[-1])
    deltas = [abs(buf[i] - buf[i - 1]) for i in range(1, len(buf))]
    mean_d = sum(deltas) / len(deltas)
    max_d = max(deltas)
    srt = sorted(deltas)
    p999 = srt[int(len(srt) * 0.999)]
    print(f"[4] 接缝跳变  : |buf[0]-buf[L-1]| = {seam:.6f}")
    print(f"    相邻样本差: 均值={mean_d:.6f}  99.9分位={p999:.6f}  最大={max_d:.6f}")
    verdict = 'PASS (接缝落在正常相邻差分布内，无阶跃)' if seam <= p999 else 'WARN'
    print(f"    -> {verdict}")

    # ⑤ 首尾能量连续性（循环时不应有音量凹陷）：比较接缝两侧各 0.1s 的 RMS
    w = int(0.1 * 22050)
    head = rms(buf[:w]); tail = rms(buf[-w:]); mid = rms(buf[len(buf)//2 - w//2: len(buf)//2 + w//2])
    print(f"[5] 接缝两侧能量: 头0.1s RMS={head:.5f}  尾0.1s RMS={tail:.5f}  中段={mid:.5f}")
    ratio = head / tail if tail > 0 else 0
    db = 20 * math.log10(ratio) if ratio > 0 else -99
    print(f"    头/尾 = {ratio:.4f} ({db:+.2f} dB)  -> "
          f"{'PASS (无 3dB 凹陷)' if abs(db) < 1.5 else 'WARN 可能有音量凹陷'}")

    # ⑥ 等功率 vs 线性 对照（验证工程师选型的必要性）
    print("[6] 等功率交叉淡化选型验证：")
    for w_ in (0.5,):
        eq = math.sin(PI*w_*0.5)**2 + math.cos(PI*w_*0.5)**2
        lin = w_**2 + (1-w_)**2
        print(f"    中点(w=0.5) 不相关噪声功率:  等功率={eq:.4f} (0.00 dB)   "
              f"线性={lin:.4f} ({10*math.log10(lin):+.2f} dB)")
    print("    -> 实现采用 sin/cos 等功率，规避了线性淡化的 -3dB 周期性凹陷")

    # ⑦ Peak 模式配方的解析证明
    print()
    print("=" * 68)
    print("其余 12 张 Peak 模式配方 —— 解析证明（无需逐张复刻）")
    print("=" * 68)
    targets = {'sfx_enemy_hit':0.89,'sfx_player_hurt':0.89,'sfx_enemy_death':0.85,
               'sfx_boss_death':0.95,'sfx_boss_phase':0.89,'sfx_boss_summon':0.89,
               'sfx_boss_shockwave':0.95,'sfx_poise_break':0.90,'skill_basic_slash':0.72,
               'skill_circle_burst':0.86,'skill_blood_lotus':0.88,'dodge_roll':0.75}
    print("Normalize(buf, Peak, target) 令 max|x| 恒等于 target（SfxSynth.cs:657-667）：")
    worst = max(targets.values())
    for k, v in targets.items():
        print(f"    {k:<20} peak == {v:.2f}  {'OK' if v < 1.0 else 'CLIP'}")
    print(f"  最大目标 = {worst} < 1.0  -> 12 张 Peak 配方**构造上不可能削波**")
    print("  Sanitize() 在测量前清除 NaN/Inf（SfxSynth.cs:654），故 NaN 不会传播为全 NaN 缓冲")
    print("  measured < 1e-9 时提前返回（:657-661），规避除零 -> 无 Inf")

if __name__ == '__main__':
    report()
