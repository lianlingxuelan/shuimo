# -*- coding: utf-8 -*-
"""括号配平检查：t3 的 B 检查只覆盖 33 个清单文件，本批次 T2 新文件不在其中。只读。"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "Assets/_Project/Scripts/Runtime"

FILES = [
    "DamagePopupLayer.cs",
    "HitFeedbackDirector.cs",
    "HitFeedbackConfig.cs",
    "PlayerHitFlash.cs",
    "CombatBridge.cs",
    "Tests/P1_2_HitFeedbackTests.cs",
    "Tests/P1_6_ProgressionIntegrationTests.cs",
]


def strip(s):
    s = re.sub(r'/\*.*?\*/', ' ', s, flags=re.S)
    s = re.sub(r'//[^\n]*', ' ', s)
    s = re.sub(r'"(?:\\.|[^"\\])*"', '""', s)
    s = re.sub(r"'(?:\\.|[^'\\])*'", "''", s)
    return s


bad = 0
for rel in FILES:
    p = ROOT / rel
    s = strip(p.read_text(encoding="utf-8", errors="replace"))
    res = {
        "{}": s.count("{") - s.count("}"),
        "()": s.count("(") - s.count(")"),
        "[]": s.count("[") - s.count("]"),
    }
    ok = all(v == 0 for v in res.values())
    if not ok:
        bad += 1
    print("%-46s %s %s" % (rel, "PASS" if ok else "FAIL", "" if ok else res))

print("=" * 60)
print("配平结果: %d/%d PASS" % (len(FILES) - bad, len(FILES)))
