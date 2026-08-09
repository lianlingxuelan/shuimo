# -*- coding: utf-8 -*-
"""结构面校验：确认 namespace/class/方法 的嵌套深度符合预期，且方法数量无吞并。

括号配平只能证明"数量相等"，证明不了"闭合在正确的位置"。
本脚本重建大括号嵌套树，输出每个 namespace / class / 成员声明所在的深度，
用来抓"某个方法的 } 漏了导致后续方法被吞进它体内"这类结构错误。
"""
import re
import sys

BS = chr(92)
DQ = chr(34)
SQ = chr(39)
AT = chr(64)


def depth_per_line(src):
    """返回 {行号: 该行起始处的大括号深度}。"""
    i, n = 0, len(src)
    line = 1
    depth = 0
    start_depth = {1: 0}
    while i < n:
        c = src[i]
        nxt = src[i + 1] if i + 1 < n else ''
        if c == '/' and nxt == '/':
            while i < n and src[i] != '\n':
                i += 1
            continue
        if c == '/' and nxt == '*':
            i += 2
            while i + 1 < n and not (src[i] == '*' and src[i + 1] == '/'):
                if src[i] == '\n':
                    line += 1
                    start_depth[line] = depth
                i += 1
            i += 2
            continue
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
                    start_depth[line] = depth
                i += 1
            continue
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
                    break
                i += 1
            continue
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
        if c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
        elif c == '\n':
            line += 1
            start_depth[line] = depth
        i += 1
    return start_depth, depth


DECL = re.compile(
    r'^\s*(?:\[[^\]]*\]\s*)*'
    r'((?:public|private|protected|internal|static|sealed|readonly|const|override|virtual|partial|async|new)\s+)*'
    r'(namespace|class|struct|enum|interface)\s+(\w+)')

MEMBER = re.compile(
    r'^\s*(?:public|private|protected|internal)\s+'
    r'(?:static\s+|override\s+|virtual\s+|sealed\s+|async\s+|readonly\s+|const\s+)*'
    r'[\w<>\[\],\.\?]+\s+(\w+)\s*\(')


def run(path, expect_ns_depth=0, expect_type_depth=1, expect_member_depth=2):
    with open(path, encoding='utf-8-sig') as f:
        src = f.read()
    sd, final = depth_per_line(src)
    lines = src.split('\n')

    print('  ' + path)
    print('     final_depth = %d' % final)
    bad = []
    members = 0
    for idx, text in enumerate(lines, start=1):
        d = sd.get(idx, None)
        if d is None:
            continue
        m = DECL.match(text)
        if m:
            kind, name = m.group(2), m.group(3)
            exp = expect_ns_depth if kind == 'namespace' else expect_type_depth
            flag = 'OK ' if d == exp else '!! '
            if d != exp:
                bad.append((idx, kind, name, d, exp))
            print('     %sL%-5d depth=%d  %s %s' % (flag, idx, d, kind, name))
            continue
        mm = MEMBER.match(text)
        if mm and d == expect_member_depth:
            members += 1
        elif mm and d != expect_member_depth:
            bad.append((idx, 'member', mm.group(1), d, expect_member_depth))
            print('     !! L%-5d depth=%d (期望 %d)  member %s'
                  % (idx, d, expect_member_depth, mm.group(1)))
    print('     正常深度的成员方法/属性数: %d' % members)
    ok = (final == 0 and not bad)
    print('     => %s' % ('STRUCTURE OK' if ok else 'STRUCTURE PROBLEM (%d)' % len(bad)))
    return ok


if __name__ == '__main__':
    print('=' * 72)
    print('  结构嵌套深度校验')
    print('=' * 72)
    res = [run(p) for p in sys.argv[1:]]
    print('=' * 72)
    print('  结果: %s (%d/%d)' % ('PASS' if all(res) else 'FAIL',
                                  sum(1 for r in res if r), len(res)))
    print('=' * 72)
    sys.exit(0 if all(res) else 1)
