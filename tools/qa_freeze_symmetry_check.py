# -*- coding: utf-8 -*-
"""冻结/解冻对称性校验（BUG-4 回归护栏）。

三态收敛约定：任何一路反馈通道只要出现 <target>.FreezeAll(true)，
就必须在同一文件里存在配对的 <target>.FreezeAll(false)，
否则"暂停一次后永久定格"。本脚本按调用目标（_popup / _hitFx / ...）分组核对。
"""
import os
import re
import sys

CALL_RE = re.compile(r'(\w+)\s*\??\.\s*FreezeAll\s*\(\s*(true|false)\s*\)')


def audit(path):
    with open(path, encoding='utf-8-sig') as f:
        lines = f.read().split('\n')

    groups = {}
    for idx, line in enumerate(lines, start=1):
        stripped = line.strip()
        # 跳过注释行，避免把说明文字里的示例当成真实调用
        if stripped.startswith('//') or stripped.startswith('///') or stripped.startswith('*'):
            continue
        for m in CALL_RE.finditer(line):
            target, arg = m.group(1), m.group(2)
            groups.setdefault(target, {'true': [], 'false': []})[arg].append(idx)

    print('  ' + path)
    if not groups:
        print('     (无 FreezeAll 调用)')
        return True

    ok = True
    for target in sorted(groups):
        t = groups[target]['true']
        f_ = groups[target]['false']
        good = (len(t) > 0) == (len(f_) > 0)
        if not good:
            ok = False
        print('     %s %-10s FreezeAll(true) @ %-12s FreezeAll(false) @ %-12s %s'
              % ('OK ' if good else '!! ',
                 target,
                 ','.join(str(x) for x in t) or '-',
                 ','.join(str(x) for x in f_) or '-',
                 '对称' if good else '不对称 —— 会永久定格'))
    return ok


if __name__ == '__main__':
    base = os.path.join('Assets', '_Project', 'Scripts', 'Runtime')
    targets = sys.argv[1:] or [os.path.join(base, 'HitFeedbackDirector.cs')]
    print('=' * 72)
    print('  冻结 / 解冻 对称性校验')
    print('=' * 72)
    res = [audit(t) for t in targets]
    print('=' * 72)
    print('  结果: %s' % ('PASS' if all(res) else 'FAIL'))
    print('=' * 72)
    sys.exit(0 if all(res) else 1)
