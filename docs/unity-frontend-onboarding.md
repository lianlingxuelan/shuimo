# 给前端同学的 Unity 上手导览（Shuimo 项目版）

> 适用对象：有 7 年前端（React / CSS）经验、正在入门 Unity 的同学。
> 目标：用你已经懂的前端概念做锚点，快速建立 Unity 心智模型，并能对着 Shuimo 工程读真实代码。
> 本文档不依赖编译，写完即可看；文中所有路径都是本工程真实存在的文件，可直接在 Project 窗口搜索。

---

## 0. 你已有的优势（先安心）

你不需要从零学「编程」，只需要学「另一个运行时 + 一套编辑器」：

- 你懂**组件化**（React 组件 = 一段可复用能力）→ Unity 的 Prefab / MonoBehaviour 就是组件化。
- 你懂**声明式布局**（CSS Grid/Flex）→ UGUI 的 RectTransform + LayoutGroup 是同一思想。
- 你懂**事件驱动**（addEventListener）→ Unity 的 UnityEvent / 接口回调是同一思想。
- 你懂**状态**（state / 配置）→ Unity 的字段 / ScriptableObject 是同一思想。

差异只在「壳」：React 是函数返回 DOM 树、由框架调度；Unity 是引擎每帧调用挂在特定对象上的 C# 方法、由编辑器组织对象树。

---

## 1. 心智模型对照表（最重要的页，建议打印）

| 前端概念 | Unity 概念 | 关键差异 / 备注 |
|---|---|---|
| React 组件类 / Vue 组件 | `MonoBehaviour`（挂在物体上的脚本） | 你没有 `return` 出 UI，而是引擎每帧回调你的 `Update()` |
| JSX 树 / DOM 树 | **Hierarchy**（场景里的物体树） | 在编辑器里手拖，或用代码 `Instantiate` 动态生成 |
| `<div>` 空节点 | **GameObject**（空游戏对象） | 本身啥都不做，靠「挂组件」获得能力 |
| 给 div 加 class / 指令 | 给 GameObject 加 **Component** | `AddComponent<T>()` ≈ 给元素挂能力 |
| CSS 盒模型 / `position` | **Transform** / **RectTransform** | RectTransform 多了一个「锚点 anchor + 轴心 pivot」，≈ 把 position + transform-origin 合在一起 |
| `display:grid` / `flex` | **GridLayoutGroup** / **HorizontalLayoutGroup** | 挂在父物体上，子物体自动排；不写 CSS，改 Inspector 数值 |
| `background-image` / `<img>` | **Image** 组件（UGUI） | `Image Type = Sliced` ≈ `border-image`（九宫格，拉伸不变形） |
| `<button>` + `onClick` | **Button** 组件 + **UnityEvent** `onClick` | 在 Inspector 里拖方法进去，≈ 可视化绑事件 |
| props / 配置 JSON | **[SerializeField]** 字段 + **Inspector** 面板 | 你在编辑器里填的值，运行时就是字段初值（≈ 组件默认 props） |
| `state`（会变的） | 普通 C# 字段 / 属性 | 没有「不可变 state」的概念，字段随便改 |
| 全局配置 / 单例数据 | **ScriptableObject** | 一份存在工程里的「数据资产」，不挂在场景；≈ TS 类型 + 一份 JSON 实例，但可视化可编辑 |
| `import` 模块路径 | `using` 命名空间 + **asmdef 编译单元** | 见 §5，引用靠 asmdef 声明依赖，不靠路径字符串 |
| `addEventListener('click')` | 实现 **`IPointerClickHandler`** 等接口 | 不是全局监听，是「这个物体实现某接口就能收事件」 |
| `useEffect` / 生命周期 | `Awake` / `Start` / `Update` / `OnDestroy` | 引擎在固定时机回调，≈ 组件 mounted/updated/unmounted |
| `requestAnimationFrame` 每帧 | **`Update()`** 每帧 | 别在 `Update` 里 `new` 对象（≈ 别在 rAF 里频繁分配，会 GC 卡顿） |
| `async/await` + `setTimeout` | **协程 `IEnumerator`** + `yield return new WaitForSeconds` | Unity 没有内建 async 驱动游戏循环，协程是主流做法 |
| 浏览器事件冒泡 | **EventSystem + 物理射线** | 不是 DOM 树冒泡，是靠射线打到哪个物体；理解这点能少踩很多坑 |
| 路由 / 多页面 | **Scene（场景）** | 一个 .unity 文件 = 一屏/一关；`SceneManager.LoadScene` ≈ 路由跳转 |
| `npm install` / 包 | **Package Manager** / `.unitypackage` | 编辑器内管理依赖 |
| `npm run build` | **Build Settings**（出包） | 真出包才需要，平时靠编辑器 Play |
| 本地 dev server + HMR | **Play 模式**（编辑器内运行） | 改 C# 要等编译（没有 HMR）；改 Inspector 数值即时生效 |
| `console.log` | **`Debug.Log`** | 输出到编辑器 Console 窗口 |

---

## 2. 你每天会碰的 6 个核心对象

1. **GameObject**：空节点。≈ `<div>`。一切物体的容器。
2. **Component**：挂在 GO 上的能力模块。≈ 给 div 加的 class / 指令 / hook。
3. **Transform / RectTransform**：位置、旋转、缩放。RectTransform（UI 专用）多 anchor/pivot。
4. **MonoBehaviour**：你能写脚本的基类。生命周期回调由引擎调用。
5. **Prefab**：「模板物体」，存成资产后可 `Instantiate` 出 N 个副本。≈ 你定义的 `<Slot/>` 组件，被 `map` 渲染多次。
6. **ScriptableObject**：不挂在场景里的数据资产。≈ 全局配置对象 / TS 类型 + 实例数据，但可在编辑器里可视化编辑。

> 一句话：**GameObject 是 div，Component 是挂在它身上的能力，Prefab 是可复用的组件模板，MonoBehaviour 是你写的那个「组件逻辑」类。**

---

## 3. UGUI（做界面）—— 你最熟的领域

做背包 / 菜单 / HUD 全靠 UGUI，思路和 CSS 布局一一对应：

- **Canvas**：一张全屏画布，渲染在最上层。≈ `position:fixed; inset:0; z-index:9999` 的全屏层。
- **Image**：≈ `<img>` / `background`。`Type=Sliced` ≈ `border-image`（九宫格，边框角不变形，做国风格子边框就靠它）。
- **Text（Legacy）/ TextMeshPro**：≈ `<span>`。TMP 是专业文字方案，.


- **Button**：≈ `<button>`，自带 `onClick` 这个 UnityEvent，可在 Inspector 拖方法。
- **GridLayoutGroup**：挂在父物体上，设「几列 / 格子多大 / 间距」，子物体自动排成网格。≈ `display:grid; grid-template-columns:repeat(6, 1fr); gap:8px`。
- **ScrollRect**：包一层，内容超出可滚动。≈ `overflow:auto` 容器。
- **EventSystem**：事件分发中枢。注意它靠**物理射线**打到物体，不是 DOM 冒泡。

> 本工程里已有背包 UI 雏形：`Assets/_Project/Scripts/Runtime/InventoryHud.cs` 和 `PlayerInventory.cs`。它们是未来背包系统的落点，但**物品字段/内容尚未定义**，先读、别急着深改（见 §8）。

---

## 4. C# 代码层对照

```csharp
// ≈ 一个 React 组件类
public class PlayerController : MonoBehaviour
{
    [SerializeField] private float moveSpeed = 200f;   // ≈ 组件的可配置 prop（Inspector 里填默认值）
    public int Hp { get; set; }                         // ≈ state

    void Awake() { /* ≈ componentDidMount / 初始化 */ }
    void Start() { /* 第一帧前调用 */ }
    void Update()                                       // ≈ requestAnimationFrame 每帧
    {
        // 移动逻辑……别在这里 new 大对象
    }

    // 协程 ≈ async/await + setTimeout
    IEnumerator HitFlash()
    {
        yield return new WaitForSeconds(0.2f);
        // 0.2 秒后做的事
    }

    // 事件 ≈ addEventListener('click')
    public void OnPointerClick(PointerEventData eventData) { /* ... */ }
}
```

- `[SerializeField]` ≈ 把内部字段暴露成「可在编辑器填的 prop」。
- `public` 字段也会自动出现在 Inspector；`private` 不会，加 `[SerializeField]` 强制显示。
- 命名空间 + **asmdef** ≈ 模块系统 / 包划分（见 §5）。

---

## 5. Shuimo 工程结构对照（真实路径，可直接搜）

本工程刻意做了「纯逻辑内核」与「Unity 表现层」分层，和前端「纯函数模块 / 组件层」划分思路一致：

| 目录 / 文件 | 角色 | 前端类比 |
|---|---|---|
| `Assets/Scripts/Core`、`Assets/Scripts/Systems` | 纯游戏逻辑（不引用 UnityEngine） | 纯函数 / store / 不依赖 DOM 的模块 |
| `Assets/_Project/Scripts/Runtime/` | Unity 表现层（桥接内核与引擎） | React 容器组件 / 表现层 |
| `Assets/_Project/Scripts/Runtime/Xianxia.Unity.T2.asmdef` | 表现层编译单元（允许引用 UnityEngine） | 一个「允许依赖浏览器 API」的包 |
| `Assets/_Project/Prefabs/HeroineBone.prefab` | 女主骨骼预制体 | 组件模板 |
| `Assets/_Project/Resources/HeroineBone.prefab` | 同上（Resources 内，运行时按名加载） | — |
| `Assets/_Project/Materials/MAT_InkGroundRich.mat` | 重墨地面材质 | ≈ 主题变量 / 复杂样式 |
| `Assets/_Project/Shaders/Ink/InkGroundRich.shader` | 水墨地表着色器 | ≈ CSS 里最复杂的样式逻辑 |
| `Assets/_Project/Scripts/Runtime/CombatBridge.cs` | 内核↔Unity 的桥接中枢 | 状态管理 / 连接器 |
| `Assets/_Project/Scripts/Runtime/PlayerController.cs` | 玩家移动控制 | 交互组件 |
| `Assets/_Project/Scripts/Runtime/InventoryHud.cs` / `PlayerInventory.cs` | 背包 UI 雏形 | 待填充的列表组件 |
| `docs/` | 设计 / 规划档案 | 项目文档 |

> **asmdef（程序集定义）要点**：纯逻辑文件的 asmdef 不引用 UnityEngine（≈ 纯模块）；表现层文件的 asmdef 才允许 `using UnityEngine`。混用会破坏分层、拖慢编译。改代码前先看清文件属于哪个 asmdef。

---

## 6. 调试心智（和前端不同之处）

- 没有「看 DOM 树猜状态」；用 **Debug.Log**（≈ console.log）打到 Console 窗口。
- **Play 模式** = 编辑器内运行游戏（≈ 本地 dev server）。进 Play 后改 Inspector 数值即时生效（≈ 运行时改 state，方便但危险，退出 Play 会还原）。
- **没有 HMR**：改 C# 要等引擎重编译。本工程有个 `Shuimo/Scene/Force Recompile` 菜单可强制重编——你每次拉到我的改动后点它即可。
- 资产引用靠**隐藏的 GUID**（在每个 `.meta` 文件里），不是文件名。改名文件不破坏引用，但删 `.meta` 或用 GUID 当文件名搜会踩坑（搜资源要用 GUID 全工程搜，别当文件名搜）。

---

## 7. 前三周学习路径（每天 30–60 分钟）

1. **第 1–2 天｜认编辑器**：打开工程，熟悉五个窗口——Hierarchy（物体树）/ Scene（3D 视图）/ Game（运行画面）/ Inspector（选中物体属性）/ Project（资产库）。对应前端：Hierarchy≈DOM 树，Inspector≈元素审查面板，Project≈文件树。
2. **第 3–4 天｜写一个组件**：新建 Cube，挂一个 `MonoBehaviour`，`Update` 里 `Debug.Log("hello")` 并让它转起来。≈ 写一个会动的组件。
3. **第 5–6 天｜做一个按钮**：放 Canvas → Button + Text，`onClick` 里打印一句话。≈ 做一个带点击事件的组件。
4. **第 7–9 天｜做九宫格**：用 GridLayoutGroup 排 9 个 Image，体会「设列数/间距、子物体自动排」。≈ 写一个 grid 布局。
5. **第 10–14 天｜读真实代码**：打开 `CombatBridge.cs`（桥接中枢）、`PlayerController.cs`（移动）、`InventoryHud.cs`（背包雏形），对照本文 §1 的对照表逐行理解。这是从「会 Unity」到「懂本项目」的关键一步。
6. **第 15–21 天｜改一个数值看效果**：比如把 `PlayerController` 里的移速调大，Force Recompile → Play，看女主跑更快。≈ 改一个 prop 看页面变化。

> 学完前三周，你就能和我「对等协作」改东西了——那时再定物品/技能/数值、做完整背包系统，会顺畅很多。

---

## 8. 红线 / 约定（避免你踩坑，来自项目长期记忆）

- **角色资产绝不删、绝不覆盖**：女主骨骼 prefab（`Assets/_Project/Prefabs/HeroineBone.prefab` 与 `Resources/` 下两份）、其原图、以及已有小怪占位，都是既成的。新资源一律**新增并列**（新文件/新 prefab/新目录），不要替换或删除既有角色。
- **背包等内容系统先别深改**：`InventoryHud.cs` / `PlayerInventory.cs` 已有雏形，但物品字段（类型/品质/堆叠/是否可装备）尚未由你定。在内容定义前，只可读性学习，别重构字段。
- **改完本地验收**：我落地的代码最终靠你本地 `Shuimo/Scene/Force Recompile` + PlayMode 验收（本环境无 Unity，我无法替你编译运行）。
- **asmdef 分层别混**：纯逻辑 vs 表现层（Bridge）不要互相越界引用。
- **动资源前 triple-check**：引用资源只要 GUID 对得上、`.meta` 在盘，资源就在，别凭单次空搜索下「缺失」结论。

---

## 9. 一句话总结

> Unity 不是另一种语言，是「另一个运行时 + 一套可视化编辑器」。你已有的组件化 / 布局 / 事件 / 状态直觉 **完全复用**，只是壳换了名字。先照 §7 走完前三周，边读 `CombatBridge.cs` 这类真实代码边对照 §1 的表，一个月后你就能自己改东西了。

（本环境无 Unity，本文档为纯文字导览，不依赖编译，随时可看。）
