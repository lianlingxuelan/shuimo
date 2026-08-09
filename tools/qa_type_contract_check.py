# -*- coding: utf-8 -*-
"""int/float 契约 + 跨文件符号存在性校验（无编译器环境下的替代护栏）。

做三件事：
 1. 解析 HitFeedbackConfig 的全部 const / static 成员及其声明类型。
 2. 扫描所有消费方对 HitFeedbackConfig.X 的引用，确认 X 存在（抓拼写错 / 已删除成员）。
 3. 对"窄化赋值"做专项检查：
      int  <name> = HitFeedbackConfig.<float 成员>        -> CS0266
      int  <name> = Mathf.Clamp(...含 float 实参...)      -> CS0266
    以及 Mathf.Clamp 的三实参类型是否同为 int（决定走 int 还是 float 重载）。
"""
import os
import re
import sys

RUNTIME = os.path.join('Assets', '_Project', 'Scripts', 'Runtime')
CONFIG = os.path.join(RUNTIME, 'HitFeedbackConfig.cs')

MEMBER_RE = re.compile(
    r'public\s+(?:(const)|(?:static\s+(?:readonly\s+)?))\s*([\w<>\.\[\]]+)\s+(\w+)\s*(?:=|\()')


def parse_config():
    """返回 {成员名: 类型字符串}。"""
    table = {}
    with open(CONFIG, encoding='utf-8-sig') as f:
        for line in f:
            m = MEMBER_RE.search(line)
            if m:
                table[m.group(3)] = m.group(2)
    return table


def collect_consumers():
    files = []
    for root, _dirs, names in os.walk('Assets'):
        for n in names:
            if n.endswith('.cs'):
                files.append(os.path.join(root, n))
    return files


REF_RE = re.compile(r'HitFeedbackConfig\.(\w+)')
# int x = HitFeedbackConfig.Y;
INT_ASSIGN_RE = re.compile(r'\bint\s+(\w+)\s*=\s*HitFeedbackConfig\.(\w+)\s*;')
# [SerializeField] private int x = HitFeedbackConfig.Y;
INT_FIELD_RE = re.compile(r'\bint\s+(\w+)\s*=\s*HitFeedbackConfig\.(\w+)')
CLAMP_ASSIGN_RE = re.compile(r'\bint\s+(\w+)\s*=\s*Mathf\.Clamp\s*\(', re.S)

NUMERIC = {'int', 'float', 'double', 'long', 'short', 'byte'}


def widen_ok(src_type, dst_type):
    """C# 隐式数值转换：int->float OK，float->int 不 OK。"""
    if src_type == dst_type:
        return True
    order = {'byte': 0, 'short': 1, 'int': 2, 'long': 3, 'float': 4, 'double': 5}
    if src_type in order and dst_type in order:
        return order[src_type] <= order[dst_type]
    return False


def main():
    table = parse_config()
    print('=' * 72)
    print('  int/float 契约 + 符号存在性校验')
    print('=' * 72)
    print('  HitFeedbackConfig 解析到 %d 个公开成员' % len(table))

    focus = ['HitFxCapacity', 'HitFxCapacityMin', 'HitFxCapacityMax',
             'HitFxLifetime', 'KillFxLifetime', 'PopupCapacity',
             'PopupColorOf', 'CritColor', 'PopupHeavyScaleOvershoot']
    print('\n  --- QA 点名复核的符号 ---')
    focus_ok = True
    for name in focus:
        if name in table:
            print('     OK  %-28s : %s' % (name, table[name]))
        else:
            print('     !!  %-28s : 缺失' % name)
            focus_ok = False

    problems = []
    missing = []
    print('\n  --- 引用点扫描 ---')
    for path in collect_consumers():
        with open(path, encoding='utf-8-sig') as f:
            text = f.read()
        if 'HitFeedbackConfig.' not in text:
            continue
        lines = text.split('\n')
        refs = 0
        for idx, line in enumerate(lines, start=1):
            stripped = line.strip()
            if stripped.startswith('//') or stripped.startswith('///'):
                continue
            for m in REF_RE.finditer(line):
                refs += 1
                if m.group(1) not in table:
                    missing.append((path, idx, m.group(1)))
            # 窄化赋值：int x = HitFeedbackConfig.Y
            for m in INT_FIELD_RE.finditer(line):
                var, member = m.group(1), m.group(2)
                t = table.get(member)
                if t and t in NUMERIC and not widen_ok(t, 'int'):
                    problems.append((path, idx, 'int %s = HitFeedbackConfig.%s (%s)'
                                     % (var, member, t), 'CS0266 %s->int' % t))
        # Mathf.Clamp 赋给 int 的多行形态
        for m in CLAMP_ASSIGN_RE.finditer(text):
            start = m.end()
            depth = 1
            j = start
            while j < len(text) and depth > 0:
                if text[j] == '(':
                    depth += 1
                elif text[j] == ')':
                    depth -= 1
                j += 1
            args_src = text[start:j - 1]
            line_no = text[:m.start()].count('\n') + 1
            arg_members = REF_RE.findall(args_src)
            types = [table.get(a, '?') for a in arg_members]
            floaty = [a for a, t in zip(arg_members, types) if t == 'float']
            tag = 'OK ' if not floaty else '!! '
            print('     %sL%-5d %s  Mathf.Clamp -> int, 配置实参: %s'
                  % (tag, line_no, os.path.basename(path),
                     ', '.join('%s:%s' % (a, t) for a, t in zip(arg_members, types)) or '(无)'))
            if floaty:
                problems.append((path, line_no,
                                 'int = Mathf.Clamp(... %s ...)' % ', '.join(floaty),
                                 'CS0266 float 重载->int'))
        print('     -- %s : %d 处引用' % (os.path.basename(path), refs))

    print('\n  --- 结论 ---')
    if missing:
        for p, l, n in missing:
            print('     !! 未解析符号 %s:%d  HitFeedbackConfig.%s' % (p, l, n))
    else:
        print('     OK  所有 HitFeedbackConfig.X 引用均能解析到已声明成员')

    if problems:
        for p, l, expr, why in problems:
            print('     !! %s:%d  %s  => %s' % (p, l, expr, why))
    else:
        print('     OK  未发现 float -> int 窄化赋值')

    ok = focus_ok and not missing and not problems
    print('\n  结果: %s' % ('PASS' if ok else 'FAIL'))
    print('=' * 72)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
