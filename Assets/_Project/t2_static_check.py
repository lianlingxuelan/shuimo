# -*- coding: utf-8 -*-
"""
T2 静态一致性检查（无 Unity / dotnet 环境下的替代品）。

它不是编译器，只做两件编译器一定会抓、而人眼常常放过的事：
  1. 括号配平——漏一个 } 的报错位置常常离真凶几十行
  2. 跨类型符号解析——A.cs 调用 B.Foo() 而 B 根本没有 Foo，
     这类错误在多文件同时落地时最容易出现

用法：python t2_static_check.py
"""
import io
import os
import re
import glob
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.abspath(os.path.join(ROOT, ".."))

RT = os.path.join(PROJ, "_Project", "Scripts", "Runtime")
ED = os.path.join(PROJ, "_Project", "Scripts", "Editor")
COMBAT = os.path.join(PROJ, "Scripts", "Systems", "Combat")
CORE = os.path.join(PROJ, "Scripts", "Core")

T2_FILES = sorted(glob.glob(os.path.join(RT, "*.cs"))) + sorted(glob.glob(os.path.join(ED, "*.cs")))

DEP_FILES = [
    os.path.join(COMBAT, "Unity", "CombatController.cs"),
    os.path.join(COMBAT, "Unity", "CombatView.cs"),
    os.path.join(COMBAT, "Unity", "CombatEventsUnity.cs"),
    os.path.join(COMBAT, "DifficultyBridge.cs"),
    os.path.join(COMBAT, "Encounter.cs"),
    os.path.join(COMBAT, "CombatScheduler.cs"),
    os.path.join(COMBAT, "Combatant.cs"),
    os.path.join(COMBAT, "DamageResolver.cs"),
    os.path.join(COMBAT, "CombatConfig.cs"),
    os.path.join(COMBAT, "EnemyAI.cs"),
    os.path.join(COMBAT, "Vec2.cs"),
    os.path.join(CORE, "Difficulty.cs"),
    os.path.join(CORE, "ZoneSeed.cs"),
    os.path.join(CORE, "ZoneLoader.cs"),
    os.path.join(CORE, "ZoneData.cs"),
    os.path.join(CORE, "PCG32.cs"),
    os.path.join(CORE, "WCore.cs"),
]

STR_RE = re.compile(r'"(?:\\.|[^"\\])*"')
CHR_RE = re.compile(r"'(?:\\.|[^'\\])*'")
BLOCK_RE = re.compile(r"/\*.*?\*/", re.S)
LINE_RE = re.compile(r"//[^\n]*")


def strip_code(s):
    """去掉字符串、字符、注释，只留下结构性代码。"""
    s = BLOCK_RE.sub("", s)
    s = STR_RE.sub('""', s)
    s = CHR_RE.sub("''", s)
    s = LINE_RE.sub("", s)
    return s


TYPE_RE = re.compile(
    r"\b(?:public|internal)\s+"
    r"(?:static\s+|sealed\s+|abstract\s+|partial\s+)*"
    r"(?:class|struct|enum|interface)\s+(\w+)")

MEMBER_RE = re.compile(
    r"\bpublic\s+"
    r"(?:static\s+|readonly\s+|const\s+|sealed\s+|override\s+|virtual\s+|new\s+|event\s+|abstract\s+)*"
    r"[\w\.\<\>\[\]\?,]+(?:\s*\[\s*\])?\s+"
    r"(\w+)\s*(?:\(|\{|=|;|=>|$)")

ENUM_MEMBER_RE = re.compile(r"^\s*(\w+)\s*(?:=\s*[\w\.\-]+\s*)?,?\s*$")


def collect_members(paths):
    """类型名 -> 公开成员名集合。粗粒度但足够抓出拼写/签名漂移。"""
    table = {}
    enums = set()
    for path in paths:
        if not os.path.exists(path):
            continue
        code = strip_code(io.open(path, encoding="utf-8").read())
        current = None
        is_enum = False
        for line in code.split("\n"):
            m = TYPE_RE.search(line)
            if m:
                current = m.group(1)
                is_enum = " enum " in line
                table.setdefault(current, set())
                if is_enum:
                    enums.add(current)
                continue
            if current is None:
                continue
            for mm in MEMBER_RE.finditer(line):
                table[current].add(mm.group(1))
            if is_enum:
                em = ENUM_MEMBER_RE.match(line)
                if em and em.group(1) not in ("public", "private"):
                    table[current].add(em.group(1))
    return table, enums


def main():
    problems = []
    all_files = T2_FILES + DEP_FILES

    missing = [p for p in all_files if not os.path.exists(p)]
    for p in missing:
        problems.append("[缺失] 找不到文件 %s" % p)
    all_files = [p for p in all_files if os.path.exists(p)]

    # --- 1. 括号配平 ---
    for path in all_files:
        code = strip_code(io.open(path, encoding="utf-8").read())
        for op, cl, name in (("{", "}", "花括号"), ("(", ")", "圆括号"), ("[", "]", "方括号")):
            a, b = code.count(op), code.count(cl)
            if a != b:
                problems.append("[括号] %s %s 不配平：%d 开 / %d 闭"
                                % (os.path.basename(path), name, a, b))

    # --- 2. 跨类型符号解析 ---
    table, _ = collect_members(all_files)

    watched = [
        "SpriteFactory", "WorldBuilder", "Bootstrap", "CombatBridge", "AttackController",
        "VfxSlash", "PlayerController", "CameraFollow", "EnemySpawner", "DeterminismDump",
        "CombatController", "CombatView", "DifficultyBridge", "CombatScheduler",
        "ZoneSeed", "ZoneLoader", "Difficulty", "DamageResolver", "CombatConfig",
        "Combatant", "Encounter", "ShuimoGenerated", "ShuimoSceneBuilder",
    ]
    watched = [w for w in watched if w in table]
    use_re = re.compile(r"\b(" + "|".join(watched) + r")\.(\w+)")

    for path in T2_FILES:
        code = strip_code(io.open(path, encoding="utf-8").read())
        for m in use_re.finditer(code):
            owner, member = m.group(1), m.group(2)
            if member in table.get(owner, set()):
                continue
            problems.append("[成员] %s 调用 %s.%s，但 %s 未声明该公开成员"
                            % (os.path.basename(path), owner, member, owner))

    print("检查文件：%d 个（T2 %d + 依赖 %d）" % (len(all_files), len(T2_FILES), len(DEP_FILES)))
    print("收集类型：%d 个" % len(table))
    uniq = sorted(set(problems))
    if uniq:
        print("\n发现 %d 处疑点：" % len(uniq))
        for p in uniq:
            print("  " + p)
        return 1
    print("\n括号配平 + 跨类型符号解析：全部通过。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
