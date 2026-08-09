#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
t3_selfcheck.py —— T3-T05 静态护栏（无编译器环境下的 C# 表面自检）

【它为什么存在】
本地环境没有 Unity、没有 dotnet，T3「战斗系统深化」的 31 个 .cs 文件写完之后
**一行都没被编译器看过**。而这三类错误，恰恰是不编译就发现不了、一编译又
必然炸的那种：

  ① 文件被截断 / 两个文件被误合并  → 括号不配平，Unity 打开时整个程序集编译失败，
     报错还指向莫名其妙的行号，排查成本极高。
  ② 纯逻辑层混进了 UnityEngine     → Xianxia.Combat.asmdef 写的是
     noEngineReferences=true，这是「内核可 headless 编译 / 可被 Python 对拍」
     的地基。一旦破防，t1_selfcheck.py 那套对拍就失去意义，是**最硬的红线**。
  ③ 引用了一个根本不存在的类型     → 手抖拼错类名、或某个文件压根忘了创建。
     人眼 review 极难发现，因为看起来完全合理。

于是把「编译器最先做的那几件事」用纯文本解析抢先做一遍。它不是编译器，
不做类型推导、不解析重载；它只保证：**文件是完整的、红线是干净的、
引用的每个类型都真的存在于工程里**。这三条过了，剩下的错误交给 Unity 才划算。

【它检查什么】
  T3-T05-A  存在性与非空 —— 清单里的 31 个 .cs 都在、且不是空壳
  T3-T05-B  括号配平    —— () [] {} 逐字符配对；<> 用「泛型形态」识别，
                            不把 a < b 这种比较运算当成括号
  T3-T05-C  红线合规    —— 13 个纯逻辑文件里不得出现 UnityEngine（注释除外）
  T3-T05-D  跨文件类型解析 —— 扫全 Assets 树建「已知类型宇宙」，
                            T3 文件里引用到的类型必须能在宇宙或白名单里找到

【为什么红线检查必须跑在「去注释」之后】
纯逻辑层每个文件的文件头都写着一行 `// 【禁止事项】不得引用 UnityEngine。`
—— 这是给人看的规约。如果直接 grep UnityEngine，13 个文件会 100% 全红，
护栏就变成了狼来了，很快会被人加 `# noqa` 绕过去。所以先把注释和字符串
剥成空格（保留换行以便报准行号），再在剩下的**真代码**里找 UnityEngine。

【它刻意不做什么】
不做语义分析。「未解析类型引用」用的是启发式，宁可白名单写长一点，
也不接受「为了让脚本变绿而放宽红线」。所有豁免项都在 IGNORE 里逐条写了理由。

用法:
    python t3_selfcheck.py
退出码 0 = 全过，1 = 有 FAIL。
"""

import re
import sys
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")


# =============================================================================
# 路径定位：Tests → Combat → Systems → Scripts → Assets
# =============================================================================

_HERE = Path(__file__).resolve().parent
_ASSETS = _HERE.parents[3]              # .../Assets
_ROOT = _ASSETS.parent                  # 工程根

if _ASSETS.name != "Assets" or not _ASSETS.is_dir():
    print("[FATAL] 未能定位 Assets 目录，实际推导为: %s" % _ASSETS)
    sys.exit(1)


# =============================================================================
# T3 表面清单
#
# 为什么写死清单而不是纯 glob：glob 会把 T0/T1/T2 的老文件一起卷进来，
# 那样「T3 护栏」就变成了「全工程护栏」，一旦老文件有历史包袱，T3 就永远绿不了，
# 责任边界也说不清。清单负责划定责任范围，glob 只做兜底定位（见 resolve_one）。
# =============================================================================

# 纯逻辑层：Xianxia.Combat，asmdef noEngineReferences=true —— UnityEngine 红线区
T3_PURE = [
    "Scripts/Systems/Combat/ICombatEventsT3.cs",
    "Scripts/Systems/Combat/Progression.cs",
    "Scripts/Systems/Combat/ResourcePool.cs",
    "Scripts/Systems/Combat/RunPhase.cs",
    "Scripts/Systems/Combat/Skills/ActionState.cs",
    "Scripts/Systems/Combat/Skills/DodgeAction.cs",
    "Scripts/Systems/Combat/Skills/PlayerIntent.cs",
    "Scripts/Systems/Combat/Skills/SkillConfig.cs",
    "Scripts/Systems/Combat/Skills/SkillDef.cs",
    "Scripts/Systems/Combat/Skills/SkillResolver.cs",
    "Scripts/Systems/Combat/Skills/SkillRuntime.cs",
    "Scripts/Systems/Combat/Status/ActiveStatus.cs",
    "Scripts/Systems/Combat/Status/StatusComponent.cs",
    "Scripts/Systems/Combat/Status/StatusConfig.cs",
    "Scripts/Systems/Combat/Status/StatusEffectDef.cs",
]

# Unity 表现层：Xianxia.Unity.T2 —— 允许并且必然引用 UnityEngine
T3_UNITY = [
    "_Project/Scripts/Runtime/CombatBridge.cs",
    "_Project/Scripts/Runtime/CombatEventsT3Unity.cs",
    "_Project/Scripts/Runtime/DeterminismDump.cs",
    "_Project/Scripts/Runtime/DodgeController.cs",
    "_Project/Scripts/Runtime/Hud.cs",
    "_Project/Scripts/Runtime/HudSkillBar.cs",
    "_Project/Scripts/Runtime/HudStatusIcons.cs",
    "_Project/Scripts/Runtime/SkillController.cs",
    "_Project/Scripts/Runtime/VfxSkill.cs",
    "_Project/Scripts/Runtime/VfxStatus.cs",
    "_Project/Scripts/Runtime/AttackController.cs",
    "_Project/Scripts/Runtime/Input/GameAction.cs",
    "_Project/Scripts/Runtime/Input/GamepadInputSource.cs",
    "_Project/Scripts/Runtime/Input/IInputSource.cs",
    "_Project/Scripts/Runtime/Input/InputBinder.cs",
    "_Project/Scripts/Runtime/Input/InputBindingProfile.cs",
    "_Project/Scripts/Runtime/Input/KeyboardMouseInputSource.cs",
]

# T2 接线层：Xianxia.Combat.Unity —— 内核与引擎之间的唯一缝合点
T3_BRIDGE = [
    "Scripts/Systems/Combat/Unity/CombatController.cs",
]

T3_ALL = T3_PURE + T3_UNITY + T3_BRIDGE
EXPECTED_COUNT = 33


# =============================================================================
# 白名单：Unity / System / .NET 内置类型 + C# 关键字
#
# 这些类型不在 Assets 树里，永远解析不到，但它们的存在是编译器保证的。
# 把它们列出来，是为了让「未解析类型引用」这条断言保持高信噪比 ——
# 白名单越准，剩下的报告就越接近「真的写错了」。
# =============================================================================

CSHARP_KEYWORDS = set("""
if for while void int float double bool string var return new using public private
protected internal static readonly sealed abstract virtual override partial enum
struct class interface namespace this base null true false typeof as is in out ref
get set event lock switch case default break continue try catch finally throw params
async await ushort uint ulong short long byte char decimal sbyte object dynamic const
unsafe fixed sizeof checked unchecked operator explicit implicit where yield do foreach
else goto nameof when global add remove value stackalloc delegate record init required
""".split())

BUILTIN_TYPES = set("""
MonoBehaviour GameObject Transform RectTransform Vector2 Vector3 Vector4 Vector2Int
Vector3Int Quaternion Matrix4x4 Color Color32 Gradient Sprite SpriteRenderer Texture
Texture2D Material Shader Mesh MeshRenderer MeshFilter ScriptableObject Component
Behaviour GameObjectUtility Time Debug Mathf Random Input KeyCode Camera Screen
Application Physics Physics2D Collider Collider2D BoxCollider2D CircleCollider2D
Rigidbody Rigidbody2D Collision Collision2D RaycastHit RaycastHit2D LayerMask
Coroutine WaitForSeconds WaitForEndOfFrame WaitForFixedUpdate YieldInstruction
Canvas CanvasGroup CanvasRenderer CanvasScaler GraphicRaycaster EventSystem
RenderMode HorizontalWrapMode VerticalWrapMode ScaleMode BlendMode
Text Image RawImage Button Slider Toggle Graphic Font TextAnchor FontStyle
HorizontalLayoutGroup VerticalLayoutGroup GridLayoutGroup LayoutElement
ContentSizeFitter AspectRatioFitter Rect RectOffset Bounds Resources
Gamepad Keyboard Mouse Joystick GUILayout GUI GUIStyle GUIContent
AnimationCurve Keyframe ParticleSystem AudioSource AudioClip LineRenderer
TrailRenderer Light SortingLayer Space RuntimeInitializeLoadType
SceneManager Scene PlayerPrefs JsonUtility Gizmos Handles

Object Type Attribute Action Func Predicate Comparison EventHandler Nullable
IEnumerable IEnumerator IEnumerable1 IList ICollection IReadOnlyList IComparable
IEquatable IDisposable IFormattable ICloneable IComparer IEqualityComparer
List Dictionary HashSet Queue Stack LinkedList SortedList SortedDictionary
KeyValuePair Array ArraySegment Tuple ValueTuple Span ReadOnlySpan
String StringBuilder StringComparison StringSplitOptions Char Boolean Byte SByte
Int16 Int32 Int64 UInt16 UInt32 UInt64 Single Double Decimal Math MathF
Convert Enum Guid DateTime TimeSpan Exception ArgumentException
ArgumentNullException ArgumentOutOfRangeException InvalidOperationException
NotSupportedException NotImplementedException IndexOutOfRangeException
NullReferenceException OverflowException FormatException
Console Environment File Directory Path Stream StreamWriter StreamReader
TextWriter TextReader Encoding UTF8Encoding ASCIIEncoding UnicodeEncoding
CultureInfo NumberFormatInfo IFormatProvider
Regex Match Group Capture Interlocked Monitor Thread Task CancellationToken
GCHandle GCHandleType GC IntPtr UIntPtr Marshal StructLayout LayoutKind
BitConverter BindingFlags MethodInfo FieldInfo PropertyInfo Assembly

SerializeField SerializeReference NonSerialized Serializable Obsolete Flags
Range RangeAttribute Header HeaderAttribute Tooltip TooltipAttribute
Space SpaceAttribute TextArea Multiline HideInInspector
DefaultExecutionOrder DisallowMultipleComponent RequireComponent ExecuteAlways
ExecuteInEditMode AddComponentMenu CreateAssetMenu ContextMenu Min
RuntimeInitializeOnLoadMethod Conditional DebuggerStepThrough MethodImpl
MethodImplOptions CompilerGenerated CallerMemberName Pure
""".split())

# 外部根命名空间。`using System.Collections.Generic;` / `UnityEngine.UI.Image`
# 会让 `System` / `UnityEngine` 落进「X. 成员访问」这条抽取规则里。它们是
# 命名空间不是类型，工程里也永远不会声明，所以在这里认掉。
#
# 注意：把 UnityEngine 放进白名单**不会**削弱红线 —— 检查 C 是完全独立的一遍
# 扫描，不查询这个集合。这里只影响「类型能否解析」，两条检查互不干涉。
EXTERNAL_NAMESPACES = set("""
System UnityEngine UnityEditor Unity Microsoft TMPro
""".split())

# ---------------------------------------------------------------------------
# IGNORE：启发式已知误报的豁免集合
#
# 纪律：每一项都必须写清「为什么它不是真问题」。这里只放**解析器能力不足**
# 导致的误报，绝不放「代码确实有问题但先放过」。集合保持最小，宁缺毋滥。
# ---------------------------------------------------------------------------

IGNORE = {
    # 单/双字母泛型形参（T、TKey、TValue…）。声明处 class Foo<T> / Method<T>()
    # 已经被 collect_generic_params 收走了，这里兜底的是「只在方法签名里出现、
    # 声明与使用跨行」的场景。它们是类型形参，不是具体类型，本就不该存在于宇宙。
    "T", "TKey", "TValue", "TResult", "TSource", "TItem", "TComponent",
}


# =============================================================================
# 词法：把注释与字符串剥成空格
#
# 为什么保留长度和换行：报错要能报准行号。整段替换成空格而不是删掉，
# 偏移量与原文逐字符对齐，行号 = 前缀里 '\n' 的个数 + 1，不用另做映射。
# =============================================================================

def strip_comments_and_strings(text):
    """返回与 text 等长的字符串：注释/字面量/预处理指令变空格，换行原样保留。"""
    out = []
    i, n = 0, len(text)
    line_start = True       # 当前位置之前是否只有空白（用于识别预处理指令行）

    def blank(seg):
        return "".join(ch if ch == "\n" else " " for ch in seg)

    while i < n:
        c = text[i]

        # 预处理指令行 #if / #endif / #region / #pragma
        # 为什么整行剥掉：#if UNITY_2023_1_OR_NEWER 里的 UNITY_2023_1_OR_NEWER
        # 是编译符号，不是类型。不剥的话它会被当成「未解析类型」误报，
        # 而给它开白名单等于把整类预处理符号都放进来 —— 从词法上认掉更干净。
        # 只剥指令行本身，被守卫的代码照常参与配平与类型检查。
        if c == "#" and line_start:
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
            continue

        # 行注释 //
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
            continue

        # 块注释 /* */
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append(blank(text[i:j]))
            i = j
            line_start = False
            continue

        # 逐字字符串 @"..."（内部 "" 表示一个引号），$@" / @$" 前缀同样落到这里
        if c == "@" and i + 1 < n and text[i + 1] == '"':
            j = i + 2
            while j < n:
                if text[j] == '"':
                    if j + 1 < n and text[j + 1] == '"':
                        j += 2
                        continue
                    j += 1
                    break
                j += 1
            out.append(blank(text[i:j]))
            i = j
            line_start = False
            continue

        # 普通/内插字符串 "..."（内插洞里的表达式一并当字符串处理：
        # 内核里 $"{a}" 只用于日志，洞里不会出现影响配平的括号结构）
        if c == '"':
            j = i + 1
            while j < n:
                if text[j] == "\\":
                    j += 2
                    continue
                if text[j] == '"':
                    j += 1
                    break
                if text[j] == "\n":      # 未闭合，防御性止损
                    break
                j += 1
            out.append(blank(text[i:j]))
            i = j
            line_start = False
            continue

        # 字符字面量 'x' / '\n' / '\u0041'
        if c == "'":
            j = i + 1
            while j < n:
                if text[j] == "\\":
                    j += 2
                    continue
                if text[j] == "'":
                    j += 1
                    break
                if text[j] == "\n":
                    break
                j += 1
            out.append(blank(text[i:j]))
            i = j
            line_start = False
            continue

        out.append(c)
        line_start = (c == "\n") or (line_start and c in " \t\r")
        i += 1

    return "".join(out)


def line_of(code, idx):
    return code.count("\n", 0, idx) + 1


# =============================================================================
# 检查 B：括号配平
# =============================================================================

_CLOSE_OF = {"(": ")", "[": "]", "{": "}"}
_OPEN_OF = {")": "(", "]": "[", "}": "{"}


def check_brackets(code):
    """() [] {} 逐字符配对。返回错误描述列表，空列表 = 配平。"""
    errors = []
    stack = []
    for idx, ch in enumerate(code):
        if ch in _CLOSE_OF:
            stack.append((ch, idx))
        elif ch in _OPEN_OF:
            if not stack:
                errors.append("第 %d 行 多余的 '%s'" % (line_of(code, idx), ch))
                continue
            op, oidx = stack.pop()
            if _CLOSE_OF[op] != ch:
                errors.append(
                    "第 %d 行 '%s' 与第 %d 行 '%s' 不匹配"
                    % (line_of(code, idx), ch, line_of(code, oidx), op)
                )
    for op, oidx in stack:
        errors.append("第 %d 行 '%s' 未闭合" % (line_of(code, oidx), op))
    return errors


# <> 允许出现在泛型实参里的字符：标识符、逗号、点（命名空间限定）、
# 数组、可空、空白、嵌套尖括号。碰到这个集合之外的字符就说明它是比较运算符。
_GENERIC_INNER = set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
                     "_,.?[] \t\r\n<>")


def check_angles(code):
    """
    泛型形态的 <> 配平。

    为什么不能像 () 那样硬数：C# 里 '<' 绝大多数时候是小于号（i < n、a <= b、
    x << 2）。硬数的结果是几乎每个文件都「不配平」，护栏立刻失效。
    所以只认「标识符紧跟 <」且内部字符都属于泛型合法集合的片段；
    一旦扫到非法字符就判定它是比较运算，直接跳过不计。

    真正报错的只有一种情况：一个泛型候选一路扫到文件结尾都没闭合 ——
    那正是文件被截断的特征信号，也正是这条检查想抓的东西。
    """
    errors = []
    pairs = 0
    i, n = 0, len(code)
    while i < n:
        if code[i] != "<":
            i += 1
            continue
        prev = code[i - 1] if i > 0 else ""
        nxt = code[i + 1] if i + 1 < n else ""
        # 必须紧跟标识符（List<、Foo<）；排除 <=、<<
        if not (prev.isalnum() or prev == "_") or nxt in ("=", "<"):
            i += 1
            continue

        depth = 1
        j = i + 1
        matched = -1
        while j < n:
            ch = code[j]
            if ch == "<":
                depth += 1
            elif ch == ">":
                depth -= 1
                if depth == 0:
                    matched = j
                    break
            elif ch not in _GENERIC_INNER:
                break
            j += 1

        if matched >= 0:
            pairs += 1
            i = matched + 1
        elif j >= n and depth > 0:
            errors.append("第 %d 行 泛型 '<' 直到文件结尾仍未闭合（疑似截断）"
                          % line_of(code, i))
            i += 1
        else:
            i += 1          # 是比较运算符，不计入
    return errors, pairs


# =============================================================================
# 检查 C：UnityEngine 红线
# =============================================================================

_UNITY_RE = re.compile(r"\bUnityEngine\b")


def scan_unity_refs(code):
    """在**去注释后**的代码里找 UnityEngine，返回 [(行号, 行内容), ...]。"""
    hits = []
    for m in _UNITY_RE.finditer(code):
        ln = line_of(code, m.start())
        hits.append((ln, code.split("\n")[ln - 1].strip()))
    return hits


# =============================================================================
# 检查 D：跨文件类型解析
# =============================================================================

_RE_TYPE_DECL = re.compile(
    r"\b(?:class|struct|interface|enum|record)\s+([A-Za-z_]\w*)")
_RE_DELEGATE = re.compile(
    r"\bdelegate\s+[\w<>\[\],.?]+\s+([A-Za-z_]\w*)\s*[<(]")
_RE_NAMESPACE = re.compile(r"\bnamespace\s+([A-Za-z_][\w.]*)")
_RE_USING_ALIAS = re.compile(r"\busing\s+([A-Za-z_]\w*)\s*=")
_RE_GENERIC_DECL = re.compile(
    r"\b(?:class|struct|interface|record)\s+[A-Za-z_]\w*\s*<([^>]*)>")
_RE_GENERIC_METHOD = re.compile(r"\b[A-Za-z_]\w*\s*<([A-Z]\w*)>\s*\(")


# --- 成员宇宙 --------------------------------------------------------------
# 为什么需要它：抽取规则里的「X. 成员访问」这一条无法区分
#     StatusEffectDef.Foo   （类型 . 静态成员）
#     Frames.IframeLen      （本类字段 . 它的字段）
# 两者词法完全一样。工程里大量使用 `public const int SlotCount`、
# `public static readonly Vector2 HpBarSize`、`public FrameData Frames` 这类
# PascalCase 成员，不认掉它们就会产生十几条稳定误报。
#
# 与「往 IGNORE 里塞十几个名字」相比，收集成员声明是可自我维护的：
# 成员改名了它自动跟着走，而 IGNORE 会悄悄过期变成一个洞。
#
# 代价要说清楚：这确实放宽了检查 —— 一个拼错的类型名如果恰好与工程里
# 某个成员同名，就会被放过。但「拼错且恰好撞上某个已声明成员」是小概率，
# 而「漏建文件 / 类名整个不存在」这类真问题依然会被抓住，收益远大于代价。
_RE_MEMBER_DECL = re.compile(
    r"\b(?:public|private|protected|internal)\s+"
    r"(?:(?:static|readonly|const|virtual|override|abstract|sealed|event|"
    r"extern|unsafe|new|partial|async)\s+)*"
    r"[\w<>\[\],.?]+\s+([A-Z]\w*)\s*[{=;(]")
_RE_ENUM_BODY = re.compile(r"\benum\s+[A-Za-z_]\w*[^{]*\{([^}]*)\}", re.S)
_RE_ENUM_MEMBER = re.compile(r"(?<![\w.])([A-Z]\w*)")


def collect_members(files):
    """收集全工程声明过的 PascalCase 成员名（字段/属性/方法/枚举项）。"""
    members = set()
    for _path, code in files:
        for m in _RE_MEMBER_DECL.finditer(code):
            members.add(m.group(1))
        for m in _RE_ENUM_BODY.finditer(code):
            for em in _RE_ENUM_MEMBER.finditer(m.group(1)):
                members.add(em.group(1))
    return members


def collect_universe(files):
    """扫全 Assets 树，收集「已知类型宇宙」。"""
    types, namespaces = set(), set()
    for path, code in files:
        for m in _RE_TYPE_DECL.finditer(code):
            types.add(m.group(1))
        for m in _RE_DELEGATE.finditer(code):
            types.add(m.group(1))
        for m in _RE_USING_ALIAS.finditer(code):
            types.add(m.group(1))
        for m in _RE_GENERIC_DECL.finditer(code):
            for part in m.group(1).split(","):
                part = part.strip()
                if part:
                    types.add(part.split()[-1])
        for m in _RE_NAMESPACE.finditer(code):
            full = m.group(1)
            namespaces.add(full)
            for seg in full.split("."):
                namespaces.add(seg)
    return types, namespaces


def collect_generic_params(code):
    """本文件内声明的泛型形参（class Foo<T> / Method<T>(...)），不算未解析。"""
    out = set()
    for m in _RE_GENERIC_DECL.finditer(code):
        for part in m.group(1).split(","):
            part = part.strip()
            if part:
                out.add(part.split()[-1])
    for m in _RE_GENERIC_METHOD.finditer(code):
        out.add(m.group(1))
    return out


# --- 类型引用位置的抽取规则 ---------------------------------------------------
# 每条规则都对应 C# 里一个「这个位置只能是类型」的语法坑位。
_REF_PATTERNS = [
    re.compile(r"\bnew\s+([A-Z]\w+)"),                        # new X(...)
    re.compile(r"\btypeof\s*\(\s*([A-Z]\w+)"),                # typeof(X)
    re.compile(r"\b(?:is|as)\s+([A-Z]\w+)"),                  # x is X / x as X
    re.compile(r"(?<![\w.])([A-Z]\w+)\s*\."),                 # X.Member
    re.compile(r"[(,]\s*(?:ref|out|in|params)?\s*([A-Z]\w+)(?:<[^<>]*>)?"
               r"(?:\[\])?\??\s+[A-Za-z_]\w*\s*[,)=]"),       # 形参 (X y, X z)
    re.compile(r"(?<![\w.])([A-Z]\w+)(?:<[^<>]*>)?(?:\[\])?\??\s+"
               r"[A-Za-z_]\w*\s*[=;]"),                       # 声明 X y = / X y;
    re.compile(r"\)\s*(?:\(\s*)?\(([A-Z]\w+)\)"),             # 强制转型 (X)v
    re.compile(r"=\s*\(([A-Z]\w+)\)\s*[A-Za-z_(]"),           # v = (X)expr
    re.compile(r"\[\s*([A-Z]\w+)(?:\s*\(|\s*\])"),            # 特性 [X] / [X(..)]
]

# 基类/接口列表：class Foo : A, B<C>
_RE_BASE_LIST = re.compile(
    r"\b(?:class|struct|interface|record)\s+[A-Za-z_]\w*(?:<[^>]*>)?\s*:\s*([^{\r\n]+)")
# 泛型实参：<A, B.C>
_RE_GENERIC_USE = re.compile(r"<([A-Za-z_][\w,.\s\[\]?]*)>")

_RE_IDENT_HEAD = re.compile(r"^([A-Za-z_]\w*)")


def extract_type_refs(code):
    """从一个 T3 文件里抽出「疑似类型引用」标识符集合。"""
    refs = set()

    for pat in _REF_PATTERNS:
        for m in pat.finditer(code):
            refs.add(m.group(1))

    for m in _RE_BASE_LIST.finditer(code):
        for part in m.group(1).split(","):
            part = part.strip()
            # 只取首段：Xianxia.Combat.IFoo → Xianxia（命名空间在宇宙里）
            head = _RE_IDENT_HEAD.match(part)
            if head:
                refs.add(head.group(1))

    for m in _RE_GENERIC_USE.finditer(code):
        for part in m.group(1).split(","):
            part = part.strip().rstrip("?").replace("[]", "").strip()
            head = _RE_IDENT_HEAD.match(part)
            if head:
                refs.add(head.group(1))

    # 只保留 PascalCase 且长度 >= 2 的标识符
    return {r for r in refs if len(r) >= 2 and r[0].isupper()}


# =============================================================================
# 汇总框架（风格对齐 t1_selfcheck.py）
# =============================================================================

_results = []


def check(name, ok, detail=""):
    _results.append((name, bool(ok), detail))
    print("  [%s] %-52s %s" % ("PASS" if ok else "FAIL", name, detail))
    return ok


def section(title):
    print("")
    print("-" * 78)
    print("  " + title)
    print("-" * 78)


# =============================================================================
# 文件定位：清单优先，glob 兜底
# =============================================================================

def resolve_one(rel):
    """清单路径 → 实际文件。找不到就在 Assets 树里按文件名兜底一次。"""
    p = _ASSETS / rel
    if p.is_file():
        return p, ""
    hits = [q for q in _ASSETS.rglob(Path(rel).name) if q.is_file()]
    if len(hits) == 1:
        return hits[0], "（glob 兜底: %s）" % hits[0].relative_to(_ASSETS).as_posix()
    return None, ""


def main():
    print("=" * 78)
    print("  T3-T05 静态护栏 —— 无编译器环境下的 C# 表面自检")
    print("  工程根: %s" % _ROOT)
    print("=" * 78)

    # -------------------------------------------------------------------------
    section("A. 存在性与非空（T3 表面 %d 个 .cs）" % EXPECTED_COUNT)
    # -------------------------------------------------------------------------

    resolved = {}
    missing, empty = [], []
    for rel in T3_ALL:
        path, note = resolve_one(rel)
        if path is None:
            missing.append(rel)
            continue
        text = path.read_text(encoding="utf-8-sig", errors="replace")
        if not text.strip():
            empty.append(rel)
            continue
        resolved[rel] = (path, text, note)

    check("T3-T05-A1 清单条目数 = %d" % EXPECTED_COUNT,
          len(T3_ALL) == EXPECTED_COUNT,
          "实际 %d（纯逻辑 %d / Unity %d / 接线 %d）"
          % (len(T3_ALL), len(T3_PURE), len(T3_UNITY), len(T3_BRIDGE)))
    check("T3-T05-A2 全部文件存在", not missing,
          "缺失: %s" % ", ".join(missing) if missing else "%d/%d 命中"
          % (len(resolved) + len(empty), len(T3_ALL)))
    check("T3-T05-A3 全部文件非空", not empty,
          "空文件: %s" % ", ".join(empty) if empty else "最小 %d 字节"
          % min((len(t) for _, t, _ in resolved.values()), default=0))

    if missing or empty:
        print("")
        print("  [ABORT] 文件不完整，后续检查无意义。")
        return finish()

    # 去注释后的代码，后面所有检查都基于它
    stripped = {rel: strip_comments_and_strings(text)
                for rel, (_, text, _) in resolved.items()}

    # -------------------------------------------------------------------------
    section("B. 括号配平（() [] {} 逐字符 · <> 泛型形态）")
    # -------------------------------------------------------------------------

    bad_pairs, bad_angles, total_angle_pairs = [], [], 0
    for rel in T3_ALL:
        code = stripped[rel]
        errs = check_brackets(code)
        if errs:
            bad_pairs.append((rel, errs))
        aerrs, npair = check_angles(code)
        total_angle_pairs += npair
        if aerrs:
            bad_angles.append((rel, aerrs))

    check("T3-T05-B1 () [] {} 配平", not bad_pairs,
          "%d 个文件全平" % len(T3_ALL) if not bad_pairs
          else "%d 个文件失衡" % len(bad_pairs))
    for rel, errs in bad_pairs:
        for e in errs[:5]:
            print("        - %s: %s" % (rel, e))

    check("T3-T05-B2 <> 泛型配平", not bad_angles,
          "识别泛型 %d 处，无未闭合" % total_angle_pairs if not bad_angles
          else "%d 个文件有未闭合泛型" % len(bad_angles))
    for rel, errs in bad_angles:
        for e in errs[:5]:
            print("        - %s: %s" % (rel, e))

    # -------------------------------------------------------------------------
    section("C. 红线：纯逻辑层零 UnityEngine（Xianxia.Combat 程序集）")
    # -------------------------------------------------------------------------

    violations = []
    for rel in T3_PURE:
        for ln, src in scan_unity_refs(stripped[rel]):
            violations.append((rel, ln, src))

    check("T3-T05-C1 %d 个纯逻辑文件无 UnityEngine" % len(T3_PURE),
          not violations,
          "红线干净" if not violations else "%d 处违规" % len(violations))
    for rel, ln, src in violations:
        print("        - %s:%d  %s" % (rel, ln, src))

    # 反向锚点：Unity 层如果一个 UnityEngine 都没有，说明清单或解析器出了问题
    unity_hit = sum(1 for rel in T3_UNITY if scan_unity_refs(stripped[rel]))
    check("T3-T05-C2 Unity 层确实引用 UnityEngine（解析器反向自检）",
          unity_hit >= len(T3_UNITY) - 1,
          "%d/%d 个文件含 UnityEngine" % (unity_hit, len(T3_UNITY)))

    # -------------------------------------------------------------------------
    section("D. 跨文件类型解析（宇宙 = 全 Assets 树）")
    # -------------------------------------------------------------------------

    all_cs = sorted(_ASSETS.rglob("*.cs"))
    universe_files = []
    for p in all_cs:
        try:
            t = p.read_text(encoding="utf-8-sig", errors="replace")
        except OSError:
            continue
        universe_files.append((p, strip_comments_and_strings(t)))

    types, namespaces = collect_universe(universe_files)
    members = collect_members(universe_files)
    known = (types | namespaces | EXTERNAL_NAMESPACES
             | BUILTIN_TYPES | CSHARP_KEYWORDS | IGNORE)

    check("T3-T05-D1 已知类型宇宙已建立", len(types) >= 30,
          "%d 个 .cs → 类型 %d / 命名空间 %d / 内置 %d / 成员 %d"
          % (len(universe_files), len(types), len(namespaces),
             len(BUILTIN_TYPES), len(members)))

    unresolved = {}
    via_member = set()          # 靠成员宇宙认掉的，报告里公示，避免变成暗箱
    for rel in T3_ALL:
        code = stripped[rel]
        local = collect_generic_params(code)
        for r in sorted(extract_type_refs(code)):
            if r in known or r in local:
                continue
            if r in members:
                via_member.add(r)
                continue
            unresolved.setdefault(r, []).append(rel)

    check("T3-T05-D2 无未解析类型引用", not unresolved,
          "全部可解析" if not unresolved else "%d 个未解析标识符" % len(unresolved))
    if via_member:
        print("        （其中 %d 个经成员宇宙判定为字段/属性/枚举项而非类型: %s）"
              % (len(via_member), ", ".join(sorted(via_member))))
    for name in sorted(unresolved):
        where = unresolved[name]
        print("        - %-28s 出现于 %d 个文件: %s"
              % (name, len(where), ", ".join(Path(w).name for w in where[:3])))

    return finish()


def finish():
    passed = sum(1 for _, ok, _ in _results if ok)
    failed = len(_results) - passed

    print("")
    print("=" * 78)
    print("  T3-T05 静态护栏: %d passed / %d failed" % (passed, failed))
    print("=" * 78)

    if failed:
        print("  结果: FAIL")
        for name, ok, detail in _results:
            if not ok:
                print("    - %s  %s" % (name, detail))
        return 1
    print("  结果: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
