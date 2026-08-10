# -*- coding: utf-8 -*-
"""变异测试：验证 audio_syntax_check 的 key 对齐检查真的会失败。

一个从不报错的护栏等于没有护栏。本脚本注入 4 种已知会导致线上事故的
变异，逐一确认护栏能捕获；全部捕获才退出 0。

用法（改动过 audio_syntax_check.py 之后务必复跑）：
    python audio_guard_mutation_test.py

注意：monkeypatch 的是内存中的文件内容，**不写盘**，AudioConfig.cs 全程只读。
每个变异前有 assert 前置自检，防止目标 key 改名后 replace 退化为空操作
造成假阴性（首版就踩过这个坑）。"""
import sys, io, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

import audio_syntax_check as G

orig_read = G._read
real_cfg = orig_read(G.AUDIO_CONFIG)


def run(label, mutate):
    def fake(path):
        if path == G.AUDIO_CONFIG:
            return mutate(real_cfg)
        return orig_read(path)
    G._read = fake
    try:
        errs, warns, _ = G.check_key_alignment()
    finally:
        G._read = orig_read
    ok = len(errs) > 0 or len(warns) > 0
    print("[%s] %s" % ("捕获" if ok else "漏检!!", label))
    for e in errs:
        print("     ERROR: %s" % e)
    for w in warns:
        print("     WARN : %s" % w)
    return ok


results = []

# 前置自检：确保变异目标真实存在，否则 replace 变空操作 → 假阴性
for probe_key in ('"sfx_enemy_hit"', '"sfx_poise_break"'):
    assert probe_key in real_cfg, "探针失效：AudioConfig 中找不到 %s" % probe_key

# 变异 1：把内核 key 拼错一个字母 —— 运行时该音效会静默哑掉
results.append(run(
    "变异1 表中 sfx_enemy_hit 拼错成 sfx_enemy_hti",
    lambda s: s.replace('"sfx_enemy_hit"', '"sfx_enemy_hti"', 1)))

# 变异 2：技能 key 从常量引用退化回历史上写错过的短名字面量
results.append(run(
    "变异2 技能 key 退回历史错误短名 \"circle_burst\"",
    lambda s: s.replace('SkillConfig.SKILL_CIRCLE_BURST', '"circle_burst"', 1)))

# 变异 3：整行删除一个已登记 key —— 表缺项
results.append(run(
    "变异3 删除 sfx_poise_break 整行登记",
    lambda s: "\n".join(l for l in s.split("\n")
                        if '"sfx_poise_break"' not in l)))

# 变异 4：表里塞一个谁都不会发出的 key —— 死 key，应报 WARN
results.append(run(
    "变异4 表中插入无来源的死 key sfx_ghost",
    lambda s: s.replace('new SfxSpec("sfx_enemy_hit"',
                        'new SfxSpec("sfx_ghost", RecipeKind.HitLight, 22050,'
                        ' 0.2f, 1f, 0f, false, 0.2f, 1f, 1f),\n            '
                        'new SfxSpec("sfx_enemy_hit"', 1)))

print("-" * 60)
print("变异测试：%d/%d 被护栏捕获" % (sum(results), len(results)))
sys.exit(0 if all(results) else 1)
