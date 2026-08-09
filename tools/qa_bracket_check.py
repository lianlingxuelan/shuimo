# -*- coding: utf-8 -*-
"""注释/字符串感知的括号配平检查（本环境无编译器时的语法面护栏）。"""
import os

BS = chr(92)      # backslash
DQ = chr(34)      # "
SQ = chr(39)      # '


def strip_comments_and_strings(src):
    out = []
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        if c == '/' and i + 1 < n and src[i + 1] == '/':
            while i < n and src[i] != '\n':
                i += 1
        elif c == '/' and i + 1 < n and src[i + 1] == '*':
            i += 2
            while i + 1 < n and not (src[i] == '*' and src[i + 1] == '/'):
                i += 1
            i += 2
        elif c == DQ:
            i += 1
            while i < n:
                if src[i] == BS:
                    i += 2
                    continue
                if src[i] == DQ:
                    i += 1
                    break
                i += 1
        elif c == SQ:
            i += 1
            while i < n:
                if src[i] == BS:
                    i += 2
                    continue
                if src[i] == SQ:
                    i += 1
                    break
                i += 1
        else:
            out.append(c)
            i += 1
    return ''.join(out)


def check(path):
    src = open(path, encoding='utf-8-sig').read()
    s = strip_comments_and_strings(src)
    print('  ' + path)
    all_ok = True
    for a, b in [('(', ')'), ('[', ']'), ('{', '}')]:
        ca, cb = s.count(a), s.count(b)
        ok = ca == cb
        all_ok = all_ok and ok
        print('     %s%s  %4d / %4d  %s' % (a, b, ca, cb, 'BALANCED' if ok else 'MISMATCH'))
    depth = {'(': 0, '[': 0, '{': 0}
    pair = {')': '(', ']': '[', '}': '{'}
    neg = False
    for ch in s:
        if ch in depth:
            depth[ch] += 1
        elif ch in pair:
            depth[pair[ch]] -= 1
            if depth[pair[ch]] < 0:
                neg = True
    print('     深度遍历: %s' % ('OK（从未出现负深度）' if not neg else '!! 出现负深度（提前闭合）'))
    return all_ok and not neg


if __name__ == '__main__':
    base = os.path.join('Assets', '_Project', 'Scripts', 'Runtime')
    files = [
        os.path.join(base, 'DamagePopupLayer.cs'),
        os.path.join(base, 'HitFeedbackDirector.cs'),
        os.path.join(base, 'HitFeedbackConfig.cs'),
        os.path.join(base, 'Tests', 'P1_2_HitFeedbackTests.cs'),
    ]
    print('=' * 70)
    print('  注释/字符串感知括号配平检查')
    print('=' * 70)
    results = [check(f) for f in files]
    print('=' * 70)
    print('  结果: %s (%d/%d)' % ('PASS' if all(results) else 'FAIL',
                                  sum(1 for r in results if r), len(results)))
    print('=' * 70)
