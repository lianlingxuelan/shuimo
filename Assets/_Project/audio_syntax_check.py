# -*- coding: utf-8 -*-
"""
P1-3 音频层静态语法护栏（主理人质量闸门）

本环境没有 Unity / dotnet，无法编译。这个脚本做的是"编译器能抓到的一小部分"：
括号/引号配对、大括号平衡、C# 常见低级错误的形态学检查。

它**不能**替代编译。它只能保证「不会因为少一个 } 而整个文件报废」——
本工程历史上真出过 CS1513 事故，所以这一关值得单独设一道。

用法： python audio_syntax_check.py
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
TARGETS = [
    "Scripts/Runtime/AudioConfig.cs",
    "Scripts/Runtime/SfxSynth.cs",
    "Scripts/Runtime/SfxRecipes.cs",
    "Scripts/Runtime/AudioClipFactory.cs",
    "Scripts/Runtime/AudioDirector.cs",
    "Scripts/Runtime/AmbienceLayer.cs",
]

failures = []
checked = []


def strip_code(src):
    """
    去掉字符串字面量、字符字面量与注释，只留下真正的代码骨架。
    括号配对必须在这个骨架上算，否则注释里的一个中文「｝」或字符串里的 "{"
    都会造成误报。
    """
    out = []
    i = 0
    n = len(src)
    while i < n:
        c = src[i]
        # 逐行注释
        if c == '/' and i + 1 < n and src[i + 1] == '/':
            while i < n and src[i] != '\n':
                i += 1
            continue
        # 块注释
        if c == '/' and i + 1 < n and src[i + 1] == '*':
            i += 2
            while i + 1 < n and not (src[i] == '*' and src[i + 1] == '/'):
                i += 1
            i += 2
            continue
        # 逐字字符串 @"..."
        if c == '@' and i + 1 < n and src[i + 1] == '"':
            i += 2
            while i < n:
                if src[i] == '"':
                    if i + 1 < n and src[i + 1] == '"':
                        i += 2
                        continue
                    i += 1
                    break
                i += 1
            out.append(' ')
            continue
        # 普通字符串
        if c == '"':
            i += 1
            while i < n:
                if src[i] == '\\':
                    i += 2
                    continue
                if src[i] == '"':
                    i += 1
                    break
                i += 1
            out.append(' ')
            continue
        # 字符字面量
        if c == "'":
            i += 1
            while i < n:
                if src[i] == '\\':
                    i += 2
                    continue
                if src[i] == "'":
                    i += 1
                    break
                i += 1
            out.append(' ')
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def check_balance(path, code):
    """大括号 / 圆括号 / 方括号配对，报出第一个失衡位置的行号。"""
    pairs = {'}': '{', ')': '(', ']': '['}
    opens = {'{': '}', '(': ')', '[': ']'}
    stack = []
    line = 1
    for ch in code:
        if ch == '\n':
            line += 1
        elif ch in opens:
            stack.append((ch, line))
        elif ch in pairs:
            if not stack:
                return "第 %d 行出现多余的 '%s'（没有对应的开括号）" % (line, ch)
            top, tl = stack.pop()
            if top != pairs[ch]:
                return "第 %d 行 '%s' 与第 %d 行的 '%s' 不匹配" % (line, ch, tl, top)
    if stack:
        top, tl = stack[-1]
        return "文件结束时仍有未闭合的 '%s'（开于第 %d 行）—— 典型 CS1513" % (top, tl)
    return None


def check_namespace(path, code):
    if 'namespace Xianxia.Unity.T2' not in code:
        return "缺少 namespace Xianxia.Unity.T2"
    return None


def check_unity_free(path, src):
    """SfxSynth 必须零 UnityEngine 依赖（架构 §4.1 硬约束）。"""
    if not path.endswith('SfxSynth.cs'):
        return None
    for m in re.finditer(r'^\s*using\s+UnityEngine', src, re.M):
        ln = src[:m.start()].count('\n') + 1
        return "第 %d 行出现 using UnityEngine —— 违反「SfxSynth 零 Unity 依赖」硬约束" % ln
    return None


def check_forbidden_rng(path, code):
    """严禁 UnityEngine.Random / 内核 PCG32 / SkillRng（会让 2.5294x 指纹漂移）。"""
    bad = []
    for pat, why in [
        (r'UnityEngine\.Random', 'UnityEngine.Random（进程级共享状态）'),
        (r'\bPCG32\b', '内核 PCG32 随机流'),
        (r'\bSkillRng\b', '内核 SkillRng 随机流'),
    ]:
        for m in re.finditer(pat, code):
            ln = code[:m.start()].count('\n') + 1
            bad.append("第 %d 行使用了 %s" % (ln, why))
    return '；'.join(bad) if bad else None


def check_timescale(path, code):
    """禁用 Time.timeScale（最高裁定 A-1）。"""
    for m in re.finditer(r'Time\.timeScale', code):
        ln = code[:m.start()].count('\n') + 1
        return "第 %d 行使用了 Time.timeScale —— 违反最高裁定 A-1（会冻住确定性内核）" % ln
    return None


def check_feedbackclock(path, code):
    """音频层不得依赖 FeedbackClock（架构 §2.5：会导致连击静音）。"""
    for m in re.finditer(r'FeedbackClock', code):
        ln = code[:m.start()].count('\n') + 1
        return "第 %d 行引用了 FeedbackClock —— 违反架构 §2.5（顿帧期间节流窗口不推进→连击静音）" % ln
    return None


def strip_comments(src):
    """
    只去注释，**保留字符串字面量**。
    key 提取必须用它：既不能被注释里的示例代码骗到，又要能看见真正的 "sfx_xxx"。
    """
    out = []
    i = 0
    n = len(src)
    while i < n:
        c = src[i]
        if c == '/' and i + 1 < n and src[i + 1] == '/':
            while i < n and src[i] != '\n':
                i += 1
            continue
        if c == '/' and i + 1 < n and src[i + 1] == '*':
            i += 2
            while i + 1 < n and not (src[i] == '*' and src[i + 1] == '/'):
                i += 1
            i += 2
            continue
        # 字符串原样保留（但要正确跳过，避免里面的 // 被当注释）
        if c == '"':
            out.append(c)
            i += 1
            while i < n:
                if src[i] == '\\':
                    out.append(src[i:i + 2])
                    i += 2
                    continue
                out.append(src[i])
                if src[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def _read(path):
    if not os.path.exists(path):
        return None
    with open(path, 'r', encoding='utf-8') as f:
        return strip_comments(f.read())


# ---------------------------------------------------------------------------
# 跨文件 key 对齐（QA 报告 P1-1 建议 A）
#
# 为什么需要它：出口① 的 8 个 key 在内核里是**裸字面量**，C# 侧无常量可引用，
# NUnit 测试只能复制一份副本去比对。于是形成三方副本（内核 / AudioConfig / 测试），
# 测试只校验后两者。若有人把 CombatEventsUnity.cs 里的 "sfx_boss_phase" 改成
# "sfx_bossphase"，53 个测试全绿，但运行时该音效静默哑掉、且不报任何错。
#
# 本检查直接从**源文件**两侧提取，做双向差集，不依赖任何副本。
# ---------------------------------------------------------------------------

KERNEL_EVENTS = os.path.normpath(os.path.join(
    ROOT, "..", "Scripts", "Systems", "Combat", "Unity", "CombatEventsUnity.cs"))
SKILL_CONFIG = os.path.normpath(os.path.join(
    ROOT, "..", "Scripts", "Systems", "Combat", "Skills", "SkillConfig.cs"))
T3_EVENTS = os.path.join(ROOT, "Scripts", "Runtime", "CombatEventsT3Unity.cs")
AUDIO_CONFIG = os.path.join(ROOT, "Scripts", "Runtime", "AudioConfig.cs")
AMBIENCE = os.path.join(ROOT, "Scripts", "Runtime", "AmbienceLayer.cs")


def collect_emitted_keys():
    """
    收集**运行时真正会被 PlaySfx 发出**的全部 key。
    返回 (keys:set, notes:list, err:str|None)
    """
    notes = []
    emitted = set()

    # --- 出口①：内核 CombatEventsUnity，裸字面量，含三元表达式 ---
    src = _read(KERNEL_EVENTS)
    if src is None:
        return None, notes, "找不到内核事件源 %s" % KERNEL_EVENTS
    calls = re.findall(r'PlaySfx\s*\(([^;]*?)\)\s*;', src, re.S)
    if not calls:
        return None, notes, "在 CombatEventsUnity.cs 中没抓到任何 PlaySfx 调用（正则失效？）"
    for arg in calls:
        lits = re.findall(r'"([^"]*)"', arg)
        if not lits:
            notes.append("出口①有一处非字面量实参，无法静态解析：%s" % arg.strip()[:60])
        emitted.update(lits)
    notes.append("出口① CombatEventsUnity：%d 处调用 → %d 个 key" % (len(calls), len(emitted)))

    # --- 技能 id 常量表 ---
    sk = _read(SKILL_CONFIG)
    if sk is None:
        return None, notes, "找不到 %s" % SKILL_CONFIG
    skill_map = dict(re.findall(
        r'const\s+string\s+(SKILL_\w+)\s*=\s*"([^"]+)"', sk))
    if not skill_map:
        return None, notes, "在 SkillConfig.cs 中没抓到 SKILL_* 常量（正则失效？）"

    # --- 出口②：T3 层，传技能 id ---
    t3 = _read(T3_EVENTS)
    if t3 is None:
        return None, notes, "找不到 %s" % T3_EVENTS
    t3_calls = re.findall(r'PlaySfx\s*\(([^;]*?)\)\s*;', t3, re.S)
    dynamic = False
    for arg in t3_calls:
        a = arg.strip()
        m = re.search(r'SkillConfig\.(SKILL_\w+)', a)
        if m:
            name = m.group(1)
            if name not in skill_map:
                return None, notes, "出口②引用了未定义的常量 SkillConfig.%s" % name
            emitted.add(skill_map[name])
        elif re.search(r'\bdef\s*\.\s*Id\b', a):
            # 动态：任何已注册技能都可能从这里发出。保守取全集。
            dynamic = True
        else:
            lits = re.findall(r'"([^"]*)"', a)
            if lits:
                emitted.update(lits)
            else:
                notes.append("出口②有一处无法静态解析的实参：%s" % a[:60])
    if dynamic:
        emitted.update(skill_map.values())
        notes.append("出口② CombatEventsT3Unity：传 def.Id（动态）→ 保守取 SkillConfig 全部 %d 个技能 id"
                     % len(skill_map))
    else:
        notes.append("出口② CombatEventsT3Unity：%d 处调用" % len(t3_calls))

    return emitted, notes, None


def collect_table_keys():
    """
    从 AudioConfig 的 SfxSpec 表提取已登记 key。
    key 实参有三种写法，都要能解析：字面量 / SkillConfig.SKILL_* / AudioConfig 本地 const。
    返回 (keys:set, local_consts:dict, err)
    """
    src = _read(AUDIO_CONFIG)
    if src is None:
        return None, None, "找不到 %s" % AUDIO_CONFIG
    sk = _read(SKILL_CONFIG)
    skill_map = dict(re.findall(
        r'const\s+string\s+(SKILL_\w+)\s*=\s*"([^"]+)"', sk or ''))
    # AudioConfig 自身的 const string（如 AmbienceKey = "amb_wind_loop"）
    local = dict(re.findall(
        r'const\s+string\s+(\w+)\s*=\s*"([^"]+)"', src))

    keys = set()
    for arg in re.findall(r'new\s+SfxSpec\s*\(\s*([^,]+?)\s*,', src):
        a = arg.strip()
        m = re.match(r'"([^"]*)"$', a)
        if m:
            keys.add(m.group(1))
            continue
        m = re.search(r'SkillConfig\.(SKILL_\w+)', a)
        if m:
            name = m.group(1)
            if name not in skill_map:
                return None, None, "表中引用了未定义的常量 SkillConfig.%s" % name
            keys.add(skill_map[name])
            continue
        if a in local:
            keys.add(local[a])
            continue
        return None, None, "表中有无法静态解析的 key 实参：%s" % a[:60]
    if not keys:
        return None, None, "在 AudioConfig.cs 中没抓到任何 SfxSpec 行（正则失效？）"
    return keys, local, None


def check_key_alignment():
    """双向差集。返回 (errors:list, warns:list, notes:list)"""
    errors, warns = [], []
    emitted, notes, err = collect_emitted_keys()
    if err:
        return ["key 对齐检查无法执行：%s" % err], [], notes
    table, local_consts, err = collect_table_keys()
    if err:
        return ["key 对齐检查无法执行：%s" % err], [], notes
    notes.append("AudioConfig 表：%d 个 key" % len(table))
    # key → 常量名 反查，供自驱动判定用（AmbienceLayer 引的是 AudioConfig.AmbienceKey）
    by_value = {v: n for n, v in (local_consts or {}).items()}

    # 方向 A：发出了但表里没有 —— 运行时静默哑掉，且不报错。致命。
    missing = sorted(emitted - table)
    for k in missing:
        errors.append('运行时会发出 "%s"，但 AudioConfig 表中查不到 —— '
                      '该音效将静默哑掉且不抛异常' % k)

    # 方向 B：表里有但没人发出 —— 死 key。自驱动的除外。
    orphan = sorted(table - emitted)
    amb = _read(AMBIENCE) or ''
    for k in orphan:
        # 自驱动播放的 key 不走 PlaySfx 事件，需在 AmbienceLayer 中找到引用才算合法。
        # 引用形式有两种：直接字面量，或经 AudioConfig 常量（如 AudioConfig.AmbienceKey）。
        const_name = by_value.get(k)
        cited = ('"%s"' % k) in amb
        if not cited and const_name:
            cited = re.search(r'AudioConfig\s*\.\s*%s\b' % re.escape(const_name),
                              amb) is not None
        if cited:
            via = ('字面量' if ('"%s"' % k) in amb
                   else 'AudioConfig.%s' % const_name)
            notes.append('"%s" 由 AmbienceLayer 自驱动播放（引用形式：%s），'
                         '非事件触发，合法' % (k, via))
        else:
            warns.append('表中登记了 "%s"，但没有任何调用点会发出它，'
                         'AmbienceLayer 中也查无引用 —— 疑似死 key' % k)

    if not missing and not orphan:
        notes.append("双向差集为空：发出集 ≡ 登记集（%d 个）" % len(table))
    return errors, warns, notes


def main():
    for rel in TARGETS:
        path = os.path.join(ROOT, rel.replace('/', os.sep))
        if not os.path.exists(path):
            print("  [跳过] %s（尚未落盘）" % rel)
            continue
        with open(path, 'r', encoding='utf-8') as f:
            src = f.read()
        code = strip_code(src)
        checked.append(rel)
        for fn in (check_balance, check_namespace, check_unity_free,
                   check_forbidden_rng, check_timescale, check_feedbackclock):
            # check_unity_free 需要原文（看 using 行），其余用去注释后的骨架
            arg = src if fn is check_unity_free else code
            err = fn(path, arg)
            if err:
                failures.append("%s :: %s" % (rel, err))

    key_errors, key_warns, key_notes = check_key_alignment()
    failures.extend("key 对齐 :: " + e for e in key_errors)

    print("=" * 68)
    print("P1-3 音频层静态护栏")
    print("=" * 68)
    print("已检查 %d 个文件：" % len(checked))
    for c in checked:
        print("  - %s" % c)
    print("-" * 68)
    print("跨文件 key 对齐（源文件双向差集，不依赖任何副本）：")
    for n in key_notes:
        print("  · %s" % n)
    for w in key_warns:
        print("  ! WARN %s" % w)
    print("-" * 68)
    if failures:
        print("FAIL —— 共 %d 项：" % len(failures))
        for f in failures:
            print("  x %s" % f)
        return 1
    print("PASS —— 括号配对 / 命名空间 / 零 Unity 依赖 / 随机源 / timeScale /")
    print("        FeedbackClock / 跨文件 key 双向对齐 全部通过")
    print()
    print("⚠️ 本脚本不是编译器。它只保证不会因低级语法错误整体报废，")
    print("   真正的编译与运行时行为仍须用户在本地 Unity 验证。")
    return 0


if __name__ == '__main__':
    sys.exit(main())
