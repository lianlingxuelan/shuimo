# -*- coding: utf-8 -*-
"""
QA 独立断链检查（P1-2 受击反馈层）—— 无 dotnet 环境下的编译期防线。

t3 的 D2 只检查 T3 清单里的 33 个文件，本批次新增/改动的 5 个 T2 文件
不在其覆盖范围内。本脚本补上这块：对新文件做「静态成员访问」解析，
凡是 `已知类型.成员` 形式，都要在该类型的定义里找到同名成员。

只读，不改任何工程文件。
"""
import re
import sys
from pathlib import Path

ASSETS = Path(__file__).resolve().parents[1] / "Assets"

TARGETS = [
    "_Project/Scripts/Runtime/DamagePopupLayer.cs",
    "_Project/Scripts/Runtime/HitFeedbackDirector.cs",
    "_Project/Scripts/Runtime/HitFeedbackConfig.cs",
    "_Project/Scripts/Runtime/PlayerHitFlash.cs",
    "_Project/Scripts/Runtime/CombatBridge.cs",
]

# Unity / BCL 内置类型：不在 Assets 树里，解析不到属正常
BUILTIN = set("""
Mathf Vector2 Vector3 Vector4 Color Color32 Quaternion Time Debug Object GameObject
Transform RectTransform Camera Screen Application Random Input Physics Physics2D
Canvas CanvasScaler Text Image Outline Font Resources Sprite Texture2D Rect
String Math Convert Array List Dictionary Enum Single Int32 Boolean Char Double
TextAnchor FontStyle RenderMode ScaleMode SceneManager EditorSceneManager
RuntimeInitializeLoadType HideFlags Space LayerMask Gizmos Graphics Shader Material
StringComparison CultureInfo Path File Directory Encoding Guid DateTime TimeSpan
Assert Is Has Does Throws Assume LogAssert NUnit Selection EditorApplication
KeyCode Cursor QualitySettings Screen Vector2Int Vector3Int Bounds Matrix4x4
""".split())


def strip_comments_strings(src):
    src = re.sub(r'/\*.*?\*/', ' ', src, flags=re.S)
    src = re.sub(r'//[^\n]*', ' ', src)
    src = re.sub(r'"(?:\\.|[^"\\])*"', '""', src)
    src = re.sub(r"'(?:\\.|[^'\\])*'", "''", src)
    return src


def build_universe():
    """类型名 -> 该类型体内出现的所有成员名集合"""
    members = {}
    kinds = {}
    for f in sorted(ASSETS.rglob("*.cs")):
        src = strip_comments_strings(f.read_text(encoding="utf-8", errors="replace"))
        for m in re.finditer(
            r'\b(class|struct|enum|interface)\s+(\w+)', src
        ):
            kind, name = m.group(1), m.group(2)
            # 取类型体（从声明后第一个 { 起做括号配平）
            start = src.find("{", m.end())
            if start < 0:
                continue
            depth, i = 0, start
            while i < len(src):
                if src[i] == "{":
                    depth += 1
                elif src[i] == "}":
                    depth -= 1
                    if depth == 0:
                        break
                i += 1
            body = src[start:i]
            got = set(re.findall(r'\b(\w+)\s*(?:=|;|\(|,|\}|:)', body))
            got |= set(re.findall(r'\b(\w+)\s*\{', body))  # 属性
            members.setdefault(name, set()).update(got)
            kinds[name] = kind
    return members, kinds


def main():
    members, kinds = build_universe()
    print("类型宇宙: %d 个类型（来自 %d 个 .cs）"
          % (len(members), len(list(ASSETS.rglob("*.cs")))))

    problems = []
    checked = 0
    for rel in TARGETS:
        p = ASSETS / rel
        if not p.is_file():
            problems.append(("FILE-MISSING", rel, "", ""))
            continue
        src = strip_comments_strings(p.read_text(encoding="utf-8", errors="replace"))
        # 静态访问：大写开头的标识符 . 成员
        for m in re.finditer(r'\b([A-Z]\w*)\.(\w+)', src):
            tname, mem = m.group(1), m.group(2)
            if tname in BUILTIN or tname not in members:
                continue
            checked += 1
            if mem not in members[tname]:
                line = src[:m.start()].count("\n") + 1
                problems.append(("MEMBER-MISSING", rel, "%s.%s" % (tname, mem),
                                 "L%d" % line))

    print("已解析静态成员访问: %d 处" % checked)
    print("=" * 62)
    if not problems:
        print("结论: 无断链 —— 全部静态成员访问均命中定义")
        return 0
    seen = set()
    for kind, rel, sym, loc in problems:
        key = (rel, sym)
        if key in seen:
            continue
        seen.add(key)
        print("  [%s] %s  %s  %s" % (kind, rel.split("/")[-1], sym, loc))
    print("=" * 62)
    print("疑似断链: %d 处（需人工复核）" % len(seen))
    return 1


if __name__ == "__main__":
    sys.exit(main())
