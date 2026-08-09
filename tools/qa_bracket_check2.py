# -*- coding: utf-8 -*-
"""Round2 严格括号配平检查：注释 / 普通串 / 逐字串(@"") / 插值串 / 字符字面量 感知。

用法: python qa_bracket_check2.py <file.cs> [file2.cs ...]
输出: 每文件 {} () [] 计数、最终深度 final_depth、首个负深度位置、以及 depth 变化轨迹尾部。
"""
import sys

BS = chr(92)   # \
DQ = chr(34)   # "
SQ = chr(39)   # '
AT = chr(64)   # @


def strip_code(src):
    """返回 (清洗后字符, 每个保留字符对应的原始行号) 两个并行列表。"""
    out = []
    lines = []
    i, n = 0, len(src)
    line = 1
    while i < n:
        c = src[i]
        nxt = src[i + 1] if i + 1 < n else ''

        # 行注释
        if c == '/' and nxt == '/':
            while i < n and src[i] != '\n':
                i += 1
            continue
        # 块注释
        if c == '/' and nxt == '*':
            i += 2
            while i + 1 < n and not (src[i] == '*' and src[i + 1] == '/'):
                if src[i] == '\n':
                    line += 1
                i += 1
            i += 2
            continue
        # 逐字字符串 @"..."  ("" 为转义, 反斜杠不转义, 可跨行)
        if c == AT and nxt == DQ:
            i += 2
            while i < n:
                if src[i] == DQ:
                    if i + 1 < n and src[i + 1] == DQ:
                        i += 2
                        continue
                    i += 1
                    break
                if src[i] == '\n':
                    line += 1
                i += 1
            continue
        # 插值逐字串 $@"..." / @$"..."
        if c == '$' and nxt == AT and i + 2 < n and src[i + 2] == DQ:
            i += 1
            continue
        # 普通字符串 (含 $"..." 前缀; 插值内 {} 会被计入, 故下方特殊处理)
        if c == DQ:
            i += 1
            while i < n:
                if src[i] == BS:
                    i += 2
                    continue
                if src[i] == DQ:
                    i += 1
                    break
                if src[i] == '\n':
                    line += 1
                    break
                i += 1
            continue
        # 字符字面量
        if c == SQ:
            i += 1
            while i < n:
                if src[i] == BS:
                    i += 2
                    continue
                if src[i] == SQ:
                    i += 1
                    break
                i += 1
            continue

        if c == '\n':
            line += 1
        out.append(c)
        lines.append(line)
        i += 1
    return out, lines


def check(path):
    with open(path, encoding='utf-8-sig') as f:
        src = f.read()
    chars, lines = strip_code(src)

    print('  ' + path)
    ok = True
    for a, b in [('{', '}'), ('(', ')'), ('[', ']')]:
        ca = chars.count(a)
        cb = chars.count(b)
        good = (ca == cb)
        ok = ok and good
        print('     %s%s   %4d / %4d   %s' % (a, b, ca, cb,
                                              'BALANCED' if good else 'MISMATCH (diff %+d)' % (ca - cb)))

    # 大括号深度轨迹
    depth = 0
    first_neg = None
    stack = []
    for idx, ch in enumerate(chars):
        if ch == '{':
            depth += 1
            stack.append(lines[idx])
        elif ch == '}':
            depth -= 1
            if stack:
                stack.pop()
            if depth < 0 and first_neg is None:
                first_neg = lines[idx]
    print('     final_depth = %d  %s' % (depth, 'OK' if depth == 0 else '!! 未闭合' if depth > 0 else '!! 过度闭合'))
    if first_neg is not None:
        print('     !! 首次负深度出现在原始行 %d' % first_neg)
        ok = False
    if depth != 0:
        ok = False
    if stack:
        print('     !! 未闭合 { 的起始行: %s' % (', '.join(str(x) for x in stack)))
    return ok


if __name__ == '__main__':
    targets = sys.argv[1:]
    print('=' * 72)
    print('  Round2 严格括号配平检查')
    print('=' * 72)
    res = [check(t) for t in targets]
    print('=' * 72)
    print('  结果: %s (%d/%d)' % ('PASS' if all(res) else 'FAIL',
                                  sum(1 for r in res if r), len(res)))
    print('=' * 72)
    sys.exit(0 if all(res) else 1)
