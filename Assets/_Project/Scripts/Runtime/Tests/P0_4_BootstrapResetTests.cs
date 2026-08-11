// -----------------------------------------------------------------------------
// P0_4_BootstrapResetTests.cs —— Bootstrap 静态复位 / 幂等注册 EditMode 验收
//
// 【验证状态 —— 先说清楚，免得被误读为"已通过"】
// 编写环境**没有 Unity、没有 dotnet**，本文件**未经编译、未经运行**。
// 下面所有断言都是静态审查的产物，等待在本地 Unity 里跑一次才算数：
//   Window → General → Test Runner → EditMode → Xianxia.Unity.T2.Tests → Run All
//
// 【它守的是什么 —— 玩家侧症状】
// 死亡复活 / 按 R 重开 / 暂停菜单「重新开始」之后**黑屏**：地图和人物不重建，
// 只剩旧敌人 + 黑背景。根因不止一条，本文件锁死其中两条 static 生命周期缺陷：
//
//   缺陷 1  Bootstrap._firstSceneLoaded 与 _registered 没有复位钩子。
//           关闭 Domain Reload（Enter Play Mode Options）时 static 跨 PlayMode
//           存活 ⇒ 第 2 次进 PlayMode 时 _firstSceneLoaded 仍是 true ⇒
//           EnsureWorldAtRuntime(AfterSceneLoad) 早退、跳过兜底重建；而首场景的
//           sceneLoaded 事件早在订阅之前就发过了，OnSceneLoaded 不会补触发 ⇒
//           世界永远不建 ⇒ 黑屏。**必然复发，不是概率事件。**
//
//   缺陷 2  SceneManager.sceneLoaded 只有 += 没有 -=。同样在关闭 Domain Reload
//           时，第 N 次进 PlayMode 累积 N 个订阅 ⇒ 之后每次重开场景 BuildAll()
//           被调 N 次 ⇒ 世界被完整重建 N 遍，越调越卡且状态可能错乱。
//
// 【为什么本文件叫 P0_4 而不是新起编号】
// 与 PlayMode 侧的 P0_2_P0_4_PlayModeTests 同属「重开 / 终局流程」这条验收线，
// 编号对齐便于将来一起 grep。本文件是它的 EditMode 补集：只测**不需要真实场景**
// 就能证伪的静态契约。
//
// 【EditMode 的三条硬约束（沿用 P1_2_HitFeedbackTests 文件头的结论）】
//   1. Update / LateUpdate 不会被驱动，Time.frameCount 也不推进。
//   2. [RuntimeInitializeOnLoadMethod] **不会**在 EditMode 测试里触发 ——
//      所以本文件对 ResetStatics 一律**显式调用**，并自己负责 SetUp/TearDown 复位。
//   3. SceneManager.LoadScene 在 EditMode 上下文会抛
//      InvalidOperationException（"This can only be used during edit mode,
//      please use EditorSceneManager.OpenScene() instead."）——
//      本工程 PI11 就栽在这。**本文件刻意一个场景都不加载**，因此不需要
//      Application.isPlaying 三分支守卫；若将来有人往本文件加需要场景的用例，
//      请照抄 P1_6_ProgressionIntegrationTests.LoadGameplayScene 的三分支写法。
//   （补记：本文件不碰任何 HUD，所以用不上"GetComponentInChildren 必须带
//     includeInactive:true"那条坑；但它依然成立，别在别处踩。）
//
// 【★ TearDown 的复位责任 —— 本文件自己绝不能变成下一个 MENU09】
// Bootstrap 的两个 static bool 在 EditMode 下没有任何自动复位。本文件的用例会
// 主动把它们改脏（这正是被测对象），若不还原就会污染同一 Editor 会话里后面
// 所有套件。SetUp / TearDown 无条件 ResetStatics()，这是本文件的第一纪律。
//
// 【关于反射的边界 —— 只破例一次，并且说清楚为什么】
// 本工程测试红线是"不反射生产代码的私有字段来造状态"。本文件遵守它，
// 唯一的例外是 BR03：_firstSceneLoaded 的唯一置位者 EnsureWorldAtRuntime 是
// private，且它会**真的去建一个世界**（BuildAll → 烘 Sprite / 造 GameObject），
// 在 EditMode 夹具里调用它代价与副作用都不可接受。为了让"复位确实写了值"
// 这条断言非平凡（而不是对着一个恒 false 的属性自说自话），BR03 用反射把
// 私有字段**置脏**，再通过**公开只读属性** Bootstrap.FirstSceneBootstrapped 观察。
// 观察侧全程走公开 API —— 这也是本次特意新增该只读属性、而不是把字段改
// public 的原因。除 BR03 外，本文件不出现任何反射写私有字段。
//
// 【覆盖的用例】
//   BR01  ResetStatics 存在、public static、且挂在 SubsystemRegistration 这一档
//   BR02  ResetStatics 复位 _registered（全程公开 API，非平凡）
//   BR03  ResetStatics 复位 _firstSceneLoaded（反射置脏 → 公开属性观察）
//   BR04  Register() 幂等：连调 N 次，BuildScene 调用链长度恒为 1
//   BR05  Register() 挂的就是 WorldBuilder.BuildScene 本尊，不是别的目标
//   BR06  ResetStatics 不得拆掉 BuildScene 委托接线（否则编辑器菜单会哑）
//   BR07  复位→重注册 循环（每次进 PlayMode 都会发生一遍）不累积调用链
//   BR08  源码守卫：sceneLoaded 必须"先 -= 后 +="（缺陷 2 的唯一可锁手段）
//   BR09  源码守卫：OnSceneLoaded 必须无条件 BuildAll（保住已有的黑屏修复）
// -----------------------------------------------------------------------------

using System;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Shuimo;

namespace Xianxia.Unity.T2.Tests
{
    /// <summary>Bootstrap 静态复位与幂等注册的 EditMode 验收。</summary>
    [TestFixture]
    public sealed class P0_4_BootstrapResetTests
    {
        // =====================================================================
        // 夹具
        // =====================================================================

        /// <summary>
        /// 进用例前把 Bootstrap 的静态状态摆到出厂值。
        ///
        /// 不能指望 [RuntimeInitializeOnLoadMethod]：它在 EditMode 测试里不触发。
        /// 也不能指望"上一条用例应该已经清干净了"——那种依赖顺序的假设正是
        /// MENU09 假红的成因。每条用例都从确定的初值出发。
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            Bootstrap.ResetStatics();
        }

        /// <summary>
        /// ★★★ 第一纪律：无条件把 static 复位回出厂值。
        ///
        /// 【为什么 TearDown 里额外补一次 Register()】
        /// 本文件不修改 ShuimoSceneBuilder.BuildScene 的接线（ResetStatics 也不碰它），
        /// 所以这一句**不是**用来撤销破坏的，而是一道无条件的安全网：
        /// 本夹具跑完后 _registered 会是 false，若此后有别的代码依赖
        /// "BuildScene 已接好"，而恰好又没人再触发 InitializeOnLoadMethod，
        /// 编辑器菜单「Shuimo/Scene/Clean And Rebuild」就会静默无事发生 ——
        /// 那种故障没有任何报错，且绝对想不到是被一个测试夹具留下的。
        /// Register() 幂等（见 BR04），无条件多调一次零成本。
        ///
        /// 顺序要紧：先 Register()（它会把 _registered 置 true 并确保接线在位），
        /// 再 ResetStatics()（把两个 bool 归零）。反过来会留下 _registered = true，
        /// 那就等于把脏值传给了下一个夹具。
        /// </summary>
        [TearDown]
        public void TearDown()
        {
            Bootstrap.Register();
            Bootstrap.ResetStatics();
        }

        /// <summary>
        /// 取 ShuimoSceneBuilder.BuildScene 当前的调用链长度。null 记 0。
        ///
        /// 这是判定"注册是赋值(=)还是订阅(+=)"的唯一客观指标：
        /// 赋值恒为 1，订阅会随调用次数线性增长。
        /// </summary>
        private static int BuildSceneSubscriberCount()
        {
            Action d = ShuimoSceneBuilder.BuildScene;
            if (d == null)
            {
                return 0;
            }
            return d.GetInvocationList().Length;
        }

        /// <summary>
        /// 定位生产文件 Bootstrap.cs 的绝对路径。找不到返回 null。
        ///
        /// 【为什么先按约定路径直取，再退回递归搜索】
        /// 直取快且明确；递归兜底是为了让本文件在"有人挪了目录"时**不误报红**，
        /// 而是走 Assert.Ignore。源码守卫的价值在于抓住真实回归，
        /// 一条因为路径变了就变红的守卫会很快被人加 [Ignore] 然后彻底失效。
        /// </summary>
        private static string FindBootstrapSourcePath()
        {
            string assets = Application.dataPath;
            string direct = Path.Combine(
                assets, "_Project/Scripts/Runtime/Bootstrap.cs".Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(direct))
            {
                return direct;
            }

            string[] hits = Directory.GetFiles(assets, "Bootstrap.cs", SearchOption.AllDirectories);
            if (hits != null && hits.Length == 1)
            {
                return hits[0];
            }
            return null;
        }

        /// <summary>
        /// 读取 Bootstrap.cs 源码。取不到就 Assert.Ignore 并返回 null。
        /// 调用方必须在拿到 null 时立刻 return（Assert.Ignore 会抛，正常不会走到）。
        /// </summary>
        private static string ReadBootstrapSourceOrIgnore()
        {
            string path = FindBootstrapSourcePath();
            if (string.IsNullOrEmpty(path))
            {
                Assert.Ignore(
                    "未能唯一定位 Assets/_Project/Scripts/Runtime/Bootstrap.cs，"
                    + "跳过源码守卫用例。若文件确实被移动，请更新本 helper 的约定路径。");
                return null;
            }
            return File.ReadAllText(path);
        }

        /// <summary>
        /// 剥掉源码里的行注释（<c>//</c> 与 <c>///</c>），只留可执行文本。
        ///
        /// 【★ 为什么必须先剥注释 —— 本文件差点自己制造两条假绿/假红】
        /// Bootstrap.cs 的注释里**逐字出现**了本节要守的那几个字符串：
        ///   · ResetStatics 的文档注释写着
        ///     「为什么这里不顺手 SceneManager.sceneLoaded -= OnSceneLoaded」；
        ///   · OnSceneLoaded 的注释写着
        ///     「不能再走 BuildWorldIfNeeded() 的 IsWorldLive() 判定」。
        /// 若直接对原文 Contains：
        ///   BR08 会匹配到注释而**恒绿** —— 哪怕真正的 -= 那行代码被删掉，
        ///        守卫照样通过，缺陷 2 会悄无声息地回归；
        ///   BR09 会匹配到注释而**恒红** —— 修复明明还在却报错。
        /// 两种都是错的。剥注释之后，断言的对象才真的是"代码"。
        ///
        /// 【已知局限，写清楚免得被高估】
        /// 只处理行注释，不处理块注释 /* */，也不排除字符串字面量里可能出现的 "//"。
        /// 当前 Bootstrap.cs 没有块注释，被守的两处目标文本也不出现在任何字符串
        /// 字面量里，因此够用。将来 Bootstrap.cs 若引入块注释，这里要一并升级。
        /// </summary>
        private static string StripLineComments(string src)
        {
            string[] lines = src.Split('\n');
            StringBuilder sb = new StringBuilder(src.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int cut = line.IndexOf("//", StringComparison.Ordinal);
                if (cut >= 0)
                {
                    line = line.Substring(0, cut);
                }
                sb.Append(line);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // =====================================================================
        // 一、ResetStatics —— 存在性与档位契约
        // =====================================================================

        /// <summary>
        /// BR01：ResetStatics 必须是 public static，且挂着
        /// [RuntimeInitializeOnLoadMethod(SubsystemRegistration)]。
        ///
        /// 【为什么档位是断言而不是建议】
        /// SubsystemRegistration 是 RuntimeInitializeLoadType 里最早的一档。
        /// Bootstrap 自己的 RegisterBeforeSceneLoad(BeforeSceneLoad) 与
        /// EnsureWorldAtRuntime(AfterSceneLoad) 都会读这两个 static，
        /// 复位挂到它们任意一个的同档或更晚，就等于没挂 —— 脏值照样被读到。
        /// 这条与 FeedbackClock / HitFeedbackConfig 的同名用例保持完全一致的判据。
        /// </summary>
        [Test]
        public void BR01_ResetStatics_IsPublicStatic_AndHookedAtSubsystemRegistration()
        {
            MethodInfo mi = typeof(Bootstrap).GetMethod(
                "ResetStatics", BindingFlags.Public | BindingFlags.Static);

            Assert.IsNotNull(mi,
                "Bootstrap.ResetStatics() 不存在（或不是 public static）。"
                + "没有它，关闭 Domain Reload 时 _firstSceneLoaded 会跨 PlayMode 残留为 true，"
                + "第 2 次进 PlayMode 必然黑屏。");

            RuntimeInitializeOnLoadMethodAttribute attr =
                (RuntimeInitializeOnLoadMethodAttribute)Attribute.GetCustomAttribute(
                    mi, typeof(RuntimeInitializeOnLoadMethodAttribute));

            Assert.IsNotNull(attr,
                "Bootstrap.ResetStatics 缺少 [RuntimeInitializeOnLoadMethod]，"
                + "整条复位保险失效：Unity 永远不会自动调它。");

            Assert.AreEqual(RuntimeInitializeLoadType.SubsystemRegistration, attr.loadType,
                "复位必须挂在 SubsystemRegistration（最早一档）。挂在 BeforeSceneLoad 或"
                + "AfterSceneLoad 会与 Bootstrap 自己的 RegisterBeforeSceneLoad / "
                + "EnsureWorldAtRuntime 同档或更晚，留出'已经有人读到脏值'的窗口。");
        }

        // =====================================================================
        // 二、ResetStatics —— 真的把值写回去了（两条都要非平凡）
        // =====================================================================

        /// <summary>
        /// BR02：ResetStatics 必须复位 _registered。
        ///
        /// 【为什么这条是非平凡的】
        /// _registered 有一个**公开**的置位路径 Bootstrap.Register()，
        /// 所以整条链路（置脏 → 复位 → 观察）全程只用公开 API：
        ///   Register() ⇒ DelegateRegistered == true
        ///   ResetStatics() ⇒ DelegateRegistered == false
        /// 若有人把 ResetStatics 写成空实现，或漏掉 _registered 那一行，这条立刻红。
        ///
        /// 【为什么 _registered 也必须复位，虽然它"只是日志"】
        /// 不复位的话，第 2 次进 PlayMode 时它谎称"已注册过"从而吞掉注册日志。
        /// 排查黑屏时第一眼看的就是有没有这行日志，被降噪吞掉会直接把人带偏。
        /// 副作用只是每局多打一行日志，代价远小于收益。
        /// </summary>
        [Test]
        public void BR02_ResetStatics_ClearsRegisteredFlag()
        {
            Bootstrap.Register();
            Assert.IsTrue(Bootstrap.DelegateRegistered,
                "前置条件不成立：Register() 之后 DelegateRegistered 应为 true。"
                + "若这里就红了，说明 Register() 没有置位 _registered，本用例后半段无意义。");

            Bootstrap.ResetStatics();

            Assert.IsFalse(Bootstrap.DelegateRegistered,
                "ResetStatics() 必须把 _registered 复位为 false。残留为 true 会让"
                + "下一次 PlayMode 的注册日志被静默吞掉，排查黑屏时第一眼就被误导。");
        }

        /// <summary>
        /// BR03：ResetStatics 必须复位 _firstSceneLoaded —— 这是黑屏的直接开关。
        ///
        /// 【★ 本文件唯一一次反射写私有字段，理由见文件头】
        /// _firstSceneLoaded 的唯一置位者 EnsureWorldAtRuntime 是 private，
        /// 且它会真的调 BuildAll() 去建一个世界（造 GameObject、烘 Sprite），
        /// 在 EditMode 夹具里执行代价和副作用都不可接受。为了让本断言非平凡，
        /// 这里用反射**置脏**；观察侧走公开只读属性 FirstSceneBootstrapped，
        /// 不反射读。若将来 Bootstrap 重命名该字段，本用例会 Ignore 而不是误报红。
        /// </summary>
        [Test]
        public void BR03_ResetStatics_ClearsFirstSceneLoadedFlag()
        {
            FieldInfo fi = typeof(Bootstrap).GetField(
                "_firstSceneLoaded", BindingFlags.NonPublic | BindingFlags.Static);

            if (fi == null)
            {
                Assert.Ignore(
                    "Bootstrap._firstSceneLoaded 未找到（可能已重命名/重构）。"
                    + "本用例只能靠反射置脏，故跳过而不是误报红。"
                    + "请人工确认新的首场景标志仍在 ResetStatics 里被复位。");
                return;
            }

            // 置脏：模拟"上一局 PlayMode 留下来的 true"。
            fi.SetValue(null, true);
            Assert.IsTrue(Bootstrap.FirstSceneBootstrapped,
                "前置条件不成立：置脏后公开属性 FirstSceneBootstrapped 应读到 true。"
                + "若这里红了，说明该属性没有真的转发 _firstSceneLoaded。");

            Bootstrap.ResetStatics();

            Assert.IsFalse(Bootstrap.FirstSceneBootstrapped,
                "ResetStatics() 必须把 _firstSceneLoaded 复位为 false。"
                + "残留为 true 时，EnsureWorldAtRuntime(AfterSceneLoad) 会早退并跳过"
                + "兜底重建，而首场景的 sceneLoaded 事件发生在订阅之前不会补触发 —— "
                + "世界永远不建，玩家看到的就是黑屏。");
        }

        // =====================================================================
        // 三、Register() 的幂等性（ResetStatics 敢复位 _registered 的前提）
        // =====================================================================

        /// <summary>
        /// BR04：Register() 幂等 —— 连调多次，BuildScene 调用链长度恒为 1。
        ///
        /// 【它到底在防什么】
        /// 防有人把 `ShuimoSceneBuilder.BuildScene = WorldBuilder.BuildScene;`
        /// 手滑改成 `+=`。改完之后一切看起来都正常（菜单能点、世界能建），
        /// 但每次 BuildAll() 会把整个世界重建 N 遍。这在游戏里的表现只是
        /// "重开怎么越来越卡"，几乎不可能被人工定位。
        ///
        /// 现实中 Register() 每局至少被调 2~3 次（编辑器域重载 / BeforeSceneLoad /
        /// AfterSceneLoad），所以这里也调 5 次，覆盖量足够。
        /// </summary>
        [Test]
        public void BR04_Register_IsIdempotent_SubscriberCountStaysOne()
        {
            for (int i = 0; i < 5; i++)
            {
                Bootstrap.Register();

                Assert.AreEqual(1, BuildSceneSubscriberCount(),
                    "第 " + (i + 1) + " 次 Register() 后 ShuimoSceneBuilder.BuildScene 的"
                    + "调用链长度应恒为 1。不为 1 说明注册用了 += 而不是 =，"
                    + "此后每次 BuildAll() 都会把整个世界重建多遍。");
            }
        }

        /// <summary>
        /// BR05：Register() 挂上去的必须是 WorldBuilder.BuildScene 本尊。
        ///
        /// 【为什么不能只断言"非 null"】
        /// 非 null 只证明"有人挂了东西"，挂错目标（比如挂成一个空 lambda）
        /// 照样非 null，而世界一样建不出来。断言委托的 Method 归属才有意义。
        /// 左右两侧来自不同责任方（Bootstrap 的注册行为 vs WorldBuilder 的方法定义），
        /// 不是同源恒真。
        /// </summary>
        [Test]
        public void BR05_Register_WiresBuildSceneToWorldBuilder()
        {
            Bootstrap.Register();

            Action d = ShuimoSceneBuilder.BuildScene;
            Assert.IsNotNull(d,
                "Register() 之后 ShuimoSceneBuilder.BuildScene 不能为 null，"
                + "否则 BuildAll() 是个空操作，运行时兜底与编辑器菜单都会静默失效。");

            Assert.AreEqual(typeof(WorldBuilder), d.Method.DeclaringType,
                "BuildScene 委托必须指向 WorldBuilder 定义的方法，实际指向 "
                + d.Method.DeclaringType + "。挂错目标时 BuildAll() 依然非 null，"
                + "但世界不会被建出来。");

            Assert.AreEqual("BuildScene", d.Method.Name,
                "BuildScene 委托必须指向 WorldBuilder.BuildScene，实际是 " + d.Method.Name + "。");
        }

        /// <summary>
        /// BR06：ResetStatics 只许动两个记账用的 bool，不许拆掉委托接线。
        ///
        /// 【为什么要专门测这条】
        /// ResetStatics 挂在 SubsystemRegistration，比 BeforeSceneLoad 的
        /// RegisterBeforeSceneLoad 更早。它若顺手把 BuildScene 置 null，
        /// 在**编辑器不进 Play**的场景下（菜单点 Clean And Rebuild）就再也没人
        /// 重新挂回去了 —— 菜单变哑，且没有任何报错。
        /// 复位的语义是"忘掉记过的账"，不是"拆线"，两者必须分清。
        /// </summary>
        [Test]
        public void BR06_ResetStatics_DoesNotUnwireBuildSceneDelegate()
        {
            Bootstrap.Register();
            Assert.AreEqual(1, BuildSceneSubscriberCount(),
                "前置条件不成立：Register() 之后调用链长度应为 1。");

            Bootstrap.ResetStatics();

            Assert.AreEqual(1, BuildSceneSubscriberCount(),
                "ResetStatics() 不得把 ShuimoSceneBuilder.BuildScene 置空或改长度。"
                + "它的语义是'忘掉记过的账'，不是'拆线'。拆了线之后，编辑器不进 Play 时"
                + "菜单 Clean And Rebuild 会静默无事发生，且没有任何报错。");
        }

        /// <summary>
        /// BR07：复位 → 重注册 的完整循环不累积调用链。
        ///
        /// 这正是关闭 Domain Reload 后**每次进 PlayMode 都会发生一遍**的真实序列：
        ///   SubsystemRegistration: ResetStatics()
        ///   BeforeSceneLoad:       Register()
        ///   AfterSceneLoad:        Register()
        /// 跑 3 局，若调用链长到 3 或 6，说明这套复位/重注册组合本身在制造泄漏。
        /// </summary>
        [Test]
        public void BR07_ResetThenRegisterCycle_DoesNotAccumulate()
        {
            for (int playMode = 1; playMode <= 3; playMode++)
            {
                Bootstrap.ResetStatics();   // SubsystemRegistration
                Bootstrap.Register();       // BeforeSceneLoad
                Bootstrap.Register();       // AfterSceneLoad

                Assert.AreEqual(1, BuildSceneSubscriberCount(),
                    "模拟第 " + playMode + " 次进 PlayMode 后，BuildScene 调用链长度应仍为 1。"
                    + "增长说明 ResetStatics + Register 这一组合在跨 PlayMode 累积委托。");

                Assert.IsTrue(Bootstrap.DelegateRegistered,
                    "模拟第 " + playMode + " 次进 PlayMode 后，Register() 应已把 _registered 置回 true。");

                Assert.IsFalse(Bootstrap.FirstSceneBootstrapped,
                    "模拟第 " + playMode + " 次进 PlayMode 后，_firstSceneLoaded 必须是 false —— "
                    + "只有它为 false，EnsureWorldAtRuntime 才会执行首场景的兜底重建。"
                    + "为 true 就是黑屏。");
            }
        }

        // =====================================================================
        // 四、源码守卫
        //
        // 【为什么这两条只能靠读源码，不能靠运行时断言】
        // SceneManager.sceneLoaded 是引擎侧的 static 事件，**没有任何公开 API**
        // 可以枚举或计数它的订阅者（GetInvocationList 只对自己声明的委托字段可用）。
        // 而 EnsureWorldAtRuntime / OnSceneLoaded 都是 private，且一旦调用就会
        // 真的去建世界。于是"订阅是否去重""OnSceneLoaded 是否无条件重建"这两条
        // 在 EditMode 下唯一可锁的手段就是源码守卫 ——
        // 与本仓 t1/t3 那两个 Python 静态护栏是同一套思路：
        // 抓不住运行时，就抓住源码里那条承重的文本不变量。
        //
        // 【它们的已知局限，写在这里免得被高估】
        // 源码守卫只证明"那两行字还在"，不证明"它们在运行时真的生效"。
        // 真正的端到端验证只能靠：关闭 Domain Reload，连续进 3 次 PlayMode，
        // 每次都按 R 重开一次，确认世界只被重建 1 次且不黑屏。
        // 那属于人工/PlayMode 验收，不在本文件范围内。
        // =====================================================================

        /// <summary>
        /// BR08：sceneLoaded 必须"先 -= 后 +="（缺陷 2）。
        ///
        /// 【为什么顺序也要断言】
        /// 只有 "-= 在 += 之前" 才构成去重：反过来写（先加后减）会把刚加的那份
        /// 又减掉，事件彻底不生效，重开将完全没有兜底 —— 那是比重复订阅更糟的退化。
        /// </summary>
        [Test]
        public void BR08_Source_SceneLoadedSubscription_IsIdempotent()
        {
            string raw = ReadBootstrapSourceOrIgnore();
            if (raw == null)
            {
                return;
            }

            // ★ 必须剥注释：ResetStatics 的文档注释里逐字写着
            //   「不顺手 SceneManager.sceneLoaded -= OnSceneLoaded」，
            //   对原文搜索会匹配到它而恒绿。详见 StripLineComments 的注释。
            string src = StripLineComments(raw);

            int unsub = src.IndexOf("sceneLoaded -= OnSceneLoaded", StringComparison.Ordinal);
            int sub = src.IndexOf("sceneLoaded += OnSceneLoaded", StringComparison.Ordinal);

            Assert.Greater(sub, -1,
                "Bootstrap.cs 里找不到 'sceneLoaded += OnSceneLoaded'："
                + "重开场景的兜底重建入口没了，按 R 之后不会有任何东西重建世界。");

            Assert.Greater(unsub, -1,
                "Bootstrap.cs 里找不到 'sceneLoaded -= OnSceneLoaded'（缺陷 2 回归）。"
                + "只有 += 没有 -= 时，关闭 Domain Reload 后第 N 次进 PlayMode 会累积 N 个订阅，"
                + "此后每次重开场景 BuildAll() 被调 N 次，世界被完整重建 N 遍。");

            Assert.Less(unsub, sub,
                "'-=' 必须出现在 '+=' 之前。先加后减会把刚订阅的那份又减掉，"
                + "sceneLoaded 兜底彻底失效 —— 比重复订阅更糟。");
        }

        /// <summary>
        /// BR09：OnSceneLoaded 必须无条件 BuildAll，不得回退到 IsWorldLive() 判定。
        ///
        /// 【守的是工作区已有的那处修复】
        /// 场景整体重载后旧世界根节点必然失效，但在"壳还在、运行时 Sprite/Tile
        /// 已丢失"的边界下 IsWorldLive() 仍可能返回 true，从而跳过 BuildAll()
        /// —— 这就是复活/重开黑屏的另一条成因。BuildScene 首步就是
        /// DestroyGeneratedRoots()，幂等自清理，所以无条件重建绝不会堆叠。
        ///
        /// 【断言方式】只检查 OnSceneLoaded 方法体这一段文本，不做全文件搜索：
        /// BuildWorldIfNeeded 在 EnsureWorldAtRuntime 里仍然是合法且必要的调用，
        /// 全文件搜"有没有 BuildWorldIfNeeded"会误伤。
        /// </summary>
        [Test]
        public void BR09_Source_OnSceneLoaded_RebuildsUnconditionally()
        {
            string raw = ReadBootstrapSourceOrIgnore();
            if (raw == null)
            {
                return;
            }

            // ★ 必须剥注释：OnSceneLoaded 的注释里逐字写着
            //   「不能再走 BuildWorldIfNeeded() 的 IsWorldLive() 判定」，
            //   对原文搜索会匹配到它而恒红（修复还在却报错）。
            string src = StripLineComments(raw);

            const string signature = "private static void OnSceneLoaded(";
            int start = src.IndexOf(signature, StringComparison.Ordinal);

            if (start < 0)
            {
                Assert.Ignore(
                    "未找到 OnSceneLoaded 的方法签名（可能已重构或改了可见性），"
                    + "跳过本源码守卫。请人工确认场景重载后仍会无条件重建世界。");
                return;
            }

            // 截到本方法体结束为止：OnSceneLoaded 是 Bootstrap 的最后一个成员，
            // 取到文件末尾即可；即便将来它后面又加了成员，多截一点也不影响
            // 下面两条断言的判据（BuildAll 必须在、BuildWorldIfNeeded 必须不在）。
            string body = src.Substring(start);

            Assert.IsTrue(body.Contains("ShuimoSceneBuilder.BuildAll()"),
                "OnSceneLoaded 必须调用 ShuimoSceneBuilder.BuildAll()。"
                + "没有它，按 R 重开 / 复活 / 暂停菜单「重新开始」之后不会有任何东西"
                + "重建地图和人物，玩家看到的就是黑屏 + 旧敌人。");

            Assert.IsFalse(body.Contains("BuildWorldIfNeeded()"),
                "OnSceneLoaded 不得回退到 BuildWorldIfNeeded()（黑屏 bug 回归）。"
                + "该方法内部靠 IsWorldLive() 判定，而在'旧世界根节点壳还在、其运行时"
                + "Sprite/Tile/Texture 已丢失'的边界下 IsWorldLive() 会误判为 true 并"
                + "跳过重建。场景整体重载后必须无条件 BuildAll() —— BuildScene 首步"
                + "就是 DestroyGeneratedRoots()，幂等自清理，不会堆叠。");
        }
    }
}
