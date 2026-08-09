# -*- coding: utf-8 -*-
"""一次性护栏：对本轮改动的 3 个测试文件做括号配平 + 结构统计。"""
import os
import re
from collections import Counter

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
FILES = [
    "Assets/_Project/Scripts/Runtime/Tests/P0_5_MenuHudTests.cs",
    "Assets/_Project/Scripts/Runtime/Tests/P0_2_P0_4_PlayModeTests.cs",
    "Assets/_Project/Scripts/Runtime/Tests/P1_6_ProgressionIntegrationTests.cs",
]

BS = chr(92)   # backslash
DQ = chr(34)   # double quote
SQ = chr(39)   # single quote


def strip_comments_and_strings(src: str) -> str:
    """去掉注释与字符串字面量，只留下结构性字符。"""
    out = []
    i = 0
    n = len(src)
    while i < n:
        c = src[i]
        if c == "/" and i + 1 < n and src[i + 1] == "/":
            j = src.find("\n", i)
            i = n if j < 0 else j
            continue
        if c == "/" and i + 1 < n and src[i + 1] == "*":
            j = src.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        if c == DQ:
            verbatim = src[i - 1:i] == "@"
            j = i + 1
            while j < n:
                if not verbatim and src[j] == BS:
                    j += 2
                    continue
                if src[j] == DQ:
                    if verbatim and src[j + 1:j + 2] == DQ:
                        j += 2
                        continue
                    break
                j += 1
            i = j + 1
            continue
        if c == SQ:
            j = i + 1
            while j < n:
                if src[j] == BS:
                    j += 2
                    continue
                if src[j] == SQ:
                    break
                j += 1
            i = j + 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def main() -> int:
    failed = 0
    print("=" * 78)
    print("  BugFix 一次性护栏 · 改动文件结构校验")
    print("=" * 78)
    for rel in FILES:
        path = os.path.normpath(os.path.join(ROOT, rel))
        with open(path, encoding="utf-8") as fh:
            src = fh.read()
        body = strip_comments_and_strings(src)

        counts = {"(": 0, "[": 0, "{": 0}
        closers = {")": "(", "]": "[", "}": "{"}
        depth_min = {"(": 0, "[": 0, "{": 0}
        for ch in body:
            if ch in counts:
                counts[ch] += 1
            elif ch in closers:
                k = closers[ch]
                counts[k] -= 1
                depth_min[k] = min(depth_min[k], counts[k])
        bad = [k for k, v in counts.items() if v != 0]
        neg = [k for k, v in depth_min.items() if v < 0]

        attrs = re.findall(
            r"\[(UnityTest|Test|SetUp|TearDown|UnityTearDown|UnitySetUp"
            r"|OneTimeSetUp|OneTimeTearDown|TestFixture)\]", src)
        interp = len(re.findall(re.escape("$") + DQ, src))
        todos = len(re.findall(r"TODO|FIXME|XXX\(", src))

        status = "PASS" if not bad and not neg else "FAIL"
        if status == "FAIL":
            failed += 1
        print()
        print("  [%s] %s" % (status, os.path.basename(rel)))
        print("        括号配平      : %s" % ("全平" if not bad else "不平 %s" % bad))
        print("        提前闭合      : %s" % ("无" if not neg else "有 %s" % neg))
        print("        $-插值字符串  : %d" % interp)
        print("        TODO/FIXME    : %d" % todos)
        print("        NUnit 属性    : %s" % dict(Counter(attrs)))

    print()
    print("=" * 78)
    print("  结果: %s  (%d/%d 文件通过)" % (
        "PASS" if failed == 0 else "FAIL", len(FILES) - failed, len(FILES)))
    print("=" * 78)
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
