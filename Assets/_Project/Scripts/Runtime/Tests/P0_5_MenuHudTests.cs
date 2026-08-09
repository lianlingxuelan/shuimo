// -----------------------------------------------------------------------------
// P0_5_MenuHudTests.cs —— P0-5（主菜单 / ESC 暂停）+ P0-6（操作引导）EditMode 验收套件
//
// 【测试范围】
// 只测三个"表现层组件"本身：MainMenuHud / PauseMenuHud / ControlsGuideHud。
// 断言限定在 Build 出来的 UI 结构、显隐状态切换、按钮数量与文案、Action 回调接线、
// 以及 Canvas 层级契约。这些都不依赖场景、不依赖内核事件，纯 [Test] 即可在
// EditMode 下运行（Build 只是创建 Canvas/Text/Image 并改属性，不需要渲染管线）。
//
// 【不在本文件测什么，以及为什么】
// "ESC 切暂停""终局优先""SkipOnNextLoad 跨场景存活"这三条属于 CombatBridge 的
// 编排逻辑，需要真场景 + Input + LoadScene，只能进 PlayMode。它们已在静态复核中
// 逐行确认（CombatBridge.cs L994-L1013 / L135-L137 / L332-L343），此处不重复覆盖，
// 避免在 EditMode 里造一个半吊子的假场景反而掩盖真问题。
//
// 【EditMode 的两条硬约束（写用例前务必记住）】
//   1. Awake/Start 在 AddComponent 时会立即执行，但 Update **不会**被驱动。
//      因此 MainMenuHud.Update 的 Enter/Space 兜底、ControlsGuideHud.Update 的
//      "任意键关闭"都无法在此断言——不要写依赖 Update 的用例。
//   2. MainMenuHud/PauseMenuHud 的 Build() 末尾会 gameObject.SetActive(false)。
//      对**非激活**对象必须用 GetComponentsInChildren<T>(true) 显式带上
//      includeInactive，否则一律返回空 —— 这是本文件最容易踩的坑。
//      （transform.Find 不受激活状态影响，可以放心用。）
//
// 【验证状态】
// 编写环境无 Unity / dotnet，本文件未经编译运行。请在本地 Unity 编辑器确认：
//   Window → General → Test Runner → EditMode → 选 Xianxia.Unity.T2.Tests → Run All
//
// 【禁止事项】不得引用 Unity 表现层之外的生产逻辑；只通过公开 API
// （Build/Show/Hide/ShowPanel/HidePanel）与组件树读取断言所需状态，不反射私有字段。
// -----------------------------------------------------------------------------

using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Xianxia.Unity.T2;

namespace Xianxia.Unity.T2.Tests
{
    /// <summary>P0-5 主菜单 / 暂停菜单 与 P0-6 操作引导 组件验收。</summary>
    [TestFixture]
    public sealed class P0_5_MenuHudTests
    {
        // --- 组件树路径常量（与三个 Build() 里的 GameObject 名称一一对应）-------
        private const string MainCanvas = "MainMenuCanvas";
        private const string PauseCanvas = "PauseMenuCanvas";
        private const string GuideCanvas = "ControlsGuideCanvas";

        // --- 层级契约（三个文件头注释里明定的 sortingOrder）---------------------
        // Hud 是 100，但把 Hud 也 Build 出来代价过大（依赖较多），这里以常量参与比较。
        private const int HudOrder = 100;
        private const int GameOverOrder = 200;
        private const int GuideOrder = 220;
        private const int PauseOrder = 250;
        private const int MainMenuOrder = 300;

        private GameObject _mainHost;
        private MainMenuHud _main;

        private GameObject _pauseHost;
        private PauseMenuHud _pause;

        private GameObject _guideHost;
        private ControlsGuideHud _guide;

        /// <summary>
        /// 进夹具前抓一次 static 旗标的初值。
        ///
        /// 【为什么要抓、而不是在用例里直接读】static 字段不随场景销毁（这正是
        /// SkipOnNextLoad 存在的意义），因此它天然是跨用例的共享可变状态。
        /// 先存一份初值供 MENU-09 断言，再在 TearDown 里还原，
        /// 保证本夹具既能验证初值、又不会把脏值漏给后续用例。
        /// </summary>
        private static bool _skipFlagAtFixtureStart;

        /// <summary>
        /// 【探针语义，不要改成"先归零再取样"】直接取样本夹具启动时的真实值。
        ///
        /// 【为什么不能在取样前归零】那样会让 MENU-09 退化成恒真假绿——
        /// 正是 P1_6 PI06 注释亲自批判的"假绿"反模式：两边传同一个值，断言永远真，
        /// 即使生产代码把 SkipOnNextLoad 的复位逻辑删了也不会红。主理人不能亲手制造它。
        ///
        /// 【真隔离在哪】由上游 PlayMode 套件的 [Unity/]TearDown 复位保证。
        /// 那条 TearDown 在，污染就不会发生，OneTimeSetUp 取样到 false 就是**真绿**。
        ///
        /// 【若仍读到 true 怎么办】说明上一轮 PlayMode 把它置 true 后没干净复位
        /// （例如 PlayMode 套件中途崩溃没走到 TearDown）。此时我们**本夹具内顺手清掉**
        /// 以免拖累后续用例，但 _skipFlagAtFixtureStart 仍记录真值 true，
        /// 让 MENU-09 如实报红、暴露"跨夹具污染"这种真问题，而不是静默抹平。
        /// </summary>
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // 探针：取样真实初值。
            _skipFlagAtFixtureStart = MainMenuHud.SkipOnNextLoad;
            if (MainMenuHud.SkipOnNextLoad)
            {
                // 检测到上一轮 PlayMode 残留污染：本夹具内先清掉环境，
                // 但上面的样本已记下 true，MENU-09 仍会如实报红。
                MainMenuHud.SkipOnNextLoad = false;
            }
        }

        [SetUp]
        public void SetUp()
        {
            _mainHost = new GameObject("MainMenuHudTestHost");
            _main = _mainHost.AddComponent<MainMenuHud>();
            _main.Build();

            _pauseHost = new GameObject("PauseMenuHudTestHost");
            _pause = _pauseHost.AddComponent<PauseMenuHud>();
            _pause.Build();

            _guideHost = new GameObject("ControlsGuideHudTestHost");
            _guide = _guideHost.AddComponent<ControlsGuideHud>();
            _guide.Build();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_mainHost);
            Object.DestroyImmediate(_pauseHost);
            Object.DestroyImmediate(_guideHost);

            // MainMenuHud/PauseMenuHud 的 Build 会调 EnsureEventSystem()，在测试场景里
            // 凭空造一个 "EventSystem" 游离对象。它不是任何 Host 的子节点，
            // 不清掉就会一直累积并被下一个用例的 FindObjectOfType 命中，
            // 属于典型的用例间污染。
            EventSystem es = Object.FindObjectOfType<EventSystem>();
            while (es != null)
            {
                Object.DestroyImmediate(es.gameObject);
                es = Object.FindObjectOfType<EventSystem>();
            }

            MainMenuHud.SkipOnNextLoad = _skipFlagAtFixtureStart;
        }

        // --- 组件树辅助 ---------------------------------------------------------
        // 一律走 transform.Find（不受激活状态影响）+ includeInactive 的 GetComponents，
        // 原因见文件头"EditMode 硬约束 2"。

        private static Transform Child(Component root, string path)
        {
            Transform t = root.transform.Find(path);
            Assert.IsNotNull(t, "Build() 应创建节点 " + path);
            return t;
        }

        private static Button FindButton(Component root, string path)
        {
            Button b = Child(root, path).GetComponent<Button>();
            Assert.IsNotNull(b, path + " 应挂 Button 组件");
            return b;
        }

        private static string LabelOf(Button btn)
        {
            Text t = btn.GetComponentInChildren<Text>(true);
            Assert.IsNotNull(t, btn.name + " 应有子级 Text 作为文案");
            return t.text;
        }

        private static Canvas CanvasOf(Component root)
        {
            Canvas[] all = root.GetComponentsInChildren<Canvas>(true);
            Assert.AreEqual(1, all.Length, root.name + " 应恰好创建一个 Canvas");
            return all[0];
        }

        // =====================================================================
        // MainMenuHud
        // =====================================================================

        /// <summary>MENU-01 主菜单 Build 不抛异常，且默认隐藏（由 CombatBridge 决定要不要显示）。</summary>
        [Test]
        public void MENU01_MainMenu_Build_DefaultsHidden()
        {
            Assert.IsFalse(_mainHost.activeSelf, "Build 后主菜单应默认隐藏");
            Assert.IsNotNull(CanvasOf(_main), "Build 应创建 Canvas");
        }

        /// <summary>MENU-02 主菜单 Show/Hide 正确切换 activeSelf。</summary>
        [Test]
        public void MENU02_MainMenu_ShowHide_TogglesActiveSelf()
        {
            _main.Show();
            Assert.IsTrue(_mainHost.activeSelf, "Show 后主菜单应可见");

            _main.Hide();
            Assert.IsFalse(_mainHost.activeSelf, "Hide 后主菜单应隐藏");
        }

        /// <summary>MENU-03 主菜单应恰好 2 个按钮（开始 / 退出）。</summary>
        [Test]
        public void MENU03_MainMenu_HasExactlyTwoButtons()
        {
            Button[] buttons = _main.GetComponentsInChildren<Button>(true);
            Assert.AreEqual(2, buttons.Length, "主菜单应恰好 2 个按钮：开始游戏 / 退出游戏");
        }

        /// <summary>MENU-04 主菜单按钮文案正确。</summary>
        [Test]
        public void MENU04_MainMenu_ButtonLabelsAreCorrect()
        {
            Assert.AreEqual("开 始 游 戏", LabelOf(FindButton(_main, MainCanvas + "/StartButton")));
            Assert.AreEqual("退 出 游 戏", LabelOf(FindButton(_main, MainCanvas + "/QuitButton")));
        }

        /// <summary>MENU-05 主菜单主标题文案正确。</summary>
        [Test]
        public void MENU05_MainMenu_TitleTextIsCorrect()
        {
            Text title = Child(_main, MainCanvas + "/MainMenuTitle").GetComponent<Text>();
            Assert.IsNotNull(title, "主标题应为 Text 组件");
            Assert.AreEqual("幽 篁 竹 海", title.text, "主菜单标题文案不符");
        }

        /// <summary>
        /// MENU-06 点"开始游戏"应触发 OnStartClicked。
        ///
        /// 【为什么可以直接 Invoke】onClick 是 UnityEvent，Invoke() 直接派发已注册的
        /// 运行时监听器，不走 interactable / activeInHierarchy 那套点击链路，
        /// 因此对 Build 之后处于隐藏态的按钮同样有效。
        /// 【为什么这条值得测】Build 里的监听器读的是**字段**而非捕获的实参
        /// （CombatBridge 是 Build 之后才绑回调的）。若哪天被改成捕获实参，
        /// 回调会永远是 null，点开始游戏毫无反应——本用例正是那道防线。
        /// </summary>
        [Test]
        public void MENU06_MainMenu_StartButton_InvokesCallback()
        {
            bool fired = false;
            _main.OnStartClicked = () => { fired = true; };

            FindButton(_main, MainCanvas + "/StartButton").onClick.Invoke();

            Assert.IsTrue(fired, "点击开始游戏应触发 OnStartClicked");
        }

        /// <summary>MENU-07 点"退出游戏"应触发 OnQuitClicked，且不误触发开始。</summary>
        [Test]
        public void MENU07_MainMenu_QuitButton_InvokesCallback()
        {
            bool quitFired = false;
            bool startFired = false;
            _main.OnQuitClicked = () => { quitFired = true; };
            _main.OnStartClicked = () => { startFired = true; };

            FindButton(_main, MainCanvas + "/QuitButton").onClick.Invoke();

            Assert.IsTrue(quitFired, "点击退出游戏应触发 OnQuitClicked");
            Assert.IsFalse(startFired, "退出按钮不应串到开始回调");
        }

        /// <summary>
        /// MENU-08 回调未绑定时点击应静默返回而非空引用崩溃。
        /// 对应 Build 里每个监听器的 `if (xxx != null)` 空值守卫。
        /// </summary>
        [Test]
        public void MENU08_MainMenu_ClickWithoutCallback_DoesNotThrow()
        {
            Button start = FindButton(_main, MainCanvas + "/StartButton");
            Button quit = FindButton(_main, MainCanvas + "/QuitButton");

            Assert.DoesNotThrow(() => start.onClick.Invoke(), "未绑回调时点开始不应抛异常");
            Assert.DoesNotThrow(() => quit.onClick.Invoke(), "未绑回调时点退出不应抛异常");
        }

        /// <summary>
        /// MENU-09 SkipOnNextLoad 的 static 初值应为 false —— 即"冷启动先看主菜单"。
        /// 若初值为 true，玩家第一次进游戏会直接跳过主菜单，P0-5 等于没做。
        /// </summary>
        [Test]
        public void MENU09_SkipOnNextLoad_DefaultsToFalse()
        {
            Assert.IsFalse(_skipFlagAtFixtureStart,
                "SkipOnNextLoad 初值应为 false（冷启动先看主菜单）。" +
                "若此处为 true：多半是上一轮 PlayMode 套件把它置 true 后没干净复位" +
                "——请重跑前先跑一次 PlayMode 套件（其 [Unity]TearDown 会复位），或重启 Unity 清空 static。");
        }

        /// <summary>
        /// MENU-10 旗标可读写，供 CombatBridge 的"读完即复位"一次性语义使用。
        /// 这里只验证它是一个真正可写的 static（TearDown 会还原）。
        /// </summary>
        [Test]
        public void MENU10_SkipOnNextLoad_IsWritable()
        {
            MainMenuHud.SkipOnNextLoad = true;
            Assert.IsTrue(MainMenuHud.SkipOnNextLoad, "旗标应可置 true（重开路径要用）");

            MainMenuHud.SkipOnNextLoad = false;
            Assert.IsFalse(MainMenuHud.SkipOnNextLoad, "旗标应可复位（返回主菜单路径要用）");
        }

        // =====================================================================
        // PauseMenuHud
        // =====================================================================

        /// <summary>MENU-11 暂停面板 Build 不抛异常、默认隐藏，且 IsShown 同步为 false。</summary>
        [Test]
        public void MENU11_PauseMenu_Build_DefaultsHidden()
        {
            Assert.IsFalse(_pauseHost.activeSelf, "Build 后暂停面板应默认隐藏");
            Assert.IsFalse(_pause.IsShown, "IsShown 应与 activeSelf 同步为 false");
            Assert.IsNotNull(CanvasOf(_pause), "Build 应创建 Canvas");
        }

        /// <summary>
        /// MENU-12 Show/Hide 切换 activeSelf，且 IsShown 始终与之一致。
        /// IsShown 是 CombatBridge.TogglePauseMenu 判断"该开还是该关"的唯一依据
        /// （CombatBridge.cs L1048），一旦它和真实显隐脱钩，ESC 就会开关颠倒。
        /// </summary>
        [Test]
        public void MENU12_PauseMenu_ShowHide_KeepsIsShownInSync()
        {
            _pause.Show();
            Assert.IsTrue(_pauseHost.activeSelf, "Show 后暂停面板应可见");
            Assert.IsTrue(_pause.IsShown, "Show 后 IsShown 应为 true");

            _pause.Hide();
            Assert.IsFalse(_pauseHost.activeSelf, "Hide 后暂停面板应隐藏");
            Assert.IsFalse(_pause.IsShown, "Hide 后 IsShown 应为 false");
        }

        /// <summary>MENU-13 暂停面板应恰好 4 个按钮。</summary>
        [Test]
        public void MENU13_PauseMenu_HasExactlyFourButtons()
        {
            Button[] buttons = _pause.GetComponentsInChildren<Button>(true);
            Assert.AreEqual(4, buttons.Length,
                "暂停面板应恰好 4 个按钮：继续 / 重开 / 返回主菜单 / 退出");
        }

        /// <summary>MENU-14 暂停面板按钮文案正确。</summary>
        [Test]
        public void MENU14_PauseMenu_ButtonLabelsAreCorrect()
        {
            Assert.AreEqual("继 续 游 戏", LabelOf(FindButton(_pause, PauseCanvas + "/ResumeButton")));
            Assert.AreEqual("重 新 开 始", LabelOf(FindButton(_pause, PauseCanvas + "/RestartButton")));
            Assert.AreEqual("返 回 主 菜 单", LabelOf(FindButton(_pause, PauseCanvas + "/MainMenuButton")));
            Assert.AreEqual("退 出 游 戏", LabelOf(FindButton(_pause, PauseCanvas + "/QuitButton")));
        }

        /// <summary>MENU-15 暂停面板标题文案正确。</summary>
        [Test]
        public void MENU15_PauseMenu_TitleTextIsCorrect()
        {
            Text title = Child(_pause, PauseCanvas + "/PauseTitle").GetComponent<Text>();
            Assert.IsNotNull(title, "暂停标题应为 Text 组件");
            Assert.AreEqual("已 暂 停", title.text, "暂停面板标题文案不符");
        }

        /// <summary>
        /// MENU-16 四个按钮各自触发各自的回调，互不串味。
        /// 四路一起测是刻意的：这四个 Action 字段类型完全相同，
        /// 复制粘贴写错绑定（比如"返回主菜单"接到 OnRestartClicked）编译期毫无提示，
        /// 只有逐一 Invoke 才能抓出来。
        /// </summary>
        [Test]
        public void MENU16_PauseMenu_EachButton_InvokesItsOwnCallback()
        {
            int resume = 0;
            int restart = 0;
            int mainMenu = 0;
            int quit = 0;

            _pause.OnResumeClicked = () => { resume++; };
            _pause.OnRestartClicked = () => { restart++; };
            _pause.OnMainMenuClicked = () => { mainMenu++; };
            _pause.OnQuitClicked = () => { quit++; };

            FindButton(_pause, PauseCanvas + "/ResumeButton").onClick.Invoke();
            Assert.AreEqual(1, resume, "继续游戏应只触发 OnResumeClicked");
            Assert.AreEqual(0, restart + mainMenu + quit, "继续游戏不应串到其它回调");

            FindButton(_pause, PauseCanvas + "/RestartButton").onClick.Invoke();
            Assert.AreEqual(1, restart, "重新开始应只触发 OnRestartClicked");

            FindButton(_pause, PauseCanvas + "/MainMenuButton").onClick.Invoke();
            Assert.AreEqual(1, mainMenu, "返回主菜单应只触发 OnMainMenuClicked");

            FindButton(_pause, PauseCanvas + "/QuitButton").onClick.Invoke();
            Assert.AreEqual(1, quit, "退出游戏应只触发 OnQuitClicked");

            Assert.AreEqual(1, resume, "后续点击不应重复触发继续回调");
        }

        /// <summary>MENU-17 暂停面板回调未绑定时点击应静默返回。</summary>
        [Test]
        public void MENU17_PauseMenu_ClickWithoutCallback_DoesNotThrow()
        {
            Button[] buttons = _pause.GetComponentsInChildren<Button>(true);
            foreach (Button b in buttons)
            {
                Button captured = b;
                Assert.DoesNotThrow(() => captured.onClick.Invoke(),
                    "未绑回调时点击 " + captured.name + " 不应抛异常");
            }
        }

        // =====================================================================
        // ControlsGuideHud
        // =====================================================================

        /// <summary>
        /// MENU-18 引导 Build 后：说明面板隐藏、底部常驻小抄存在且可见、
        /// 组件自身的 gameObject 保持激活。
        ///
        /// 【这条是 P0-6 的结构底线】常驻小抄必须挂在 GuidePanel **之外**，
        /// 否则关掉说明面板会把小抄一起带走，"忘了技能键"的问题立刻复发。
        /// </summary>
        [Test]
        public void MENU18_Guide_Build_PanelHiddenButStripAlive()
        {
            Assert.IsTrue(_guideHost.activeSelf,
                "引导组件自身必须保持激活，否则常驻小抄也会被一起隐藏");
            Assert.IsFalse(_guide.PanelShown, "Build 后说明面板应默认隐藏");

            Transform strip = Child(_guide, GuideCanvas + "/ControlsStrip");
            Assert.IsTrue(strip.gameObject.activeSelf, "底部常驻小抄应始终可见");

            Transform panel = Child(_guide, GuideCanvas + "/GuidePanel");
            Assert.IsFalse(panel.gameObject.activeSelf, "说明面板节点应处于隐藏态");
            Assert.AreNotSame(panel, strip.parent,
                "常驻小抄不能是说明面板的子节点，否则关面板会把它一起带走");
        }

        /// <summary>MENU-19 ShowPanel/HidePanel 切换说明面板，且不影响常驻小抄。</summary>
        [Test]
        public void MENU19_Guide_ShowHidePanel_DoesNotAffectStrip()
        {
            Transform strip = Child(_guide, GuideCanvas + "/ControlsStrip");

            _guide.ShowPanel();
            Assert.IsTrue(_guide.PanelShown, "ShowPanel 后说明面板应显示");
            Assert.IsTrue(strip.gameObject.activeSelf, "开面板不应影响常驻小抄");

            _guide.HidePanel();
            Assert.IsFalse(_guide.PanelShown, "HidePanel 后说明面板应收起");
            Assert.IsTrue(strip.gameObject.activeSelf, "关面板同样不应带走常驻小抄");
        }

        /// <summary>
        /// MENU-20 未调用 Build 时 ShowPanel/HidePanel 应静默返回（_panelRoot 空值守卫），
        /// 且 PanelShown 为 false 而不是抛空引用。
        /// </summary>
        [Test]
        public void MENU20_Guide_WithoutBuild_DoesNotThrow()
        {
            ControlsGuideHud raw = new GameObject("RawGuide").AddComponent<ControlsGuideHud>();

            Assert.DoesNotThrow(() => raw.ShowPanel(), "未 Build 时 ShowPanel 不应抛异常");
            Assert.DoesNotThrow(() => raw.HidePanel(), "未 Build 时 HidePanel 不应抛异常");
            Assert.IsFalse(raw.PanelShown, "未 Build 时 PanelShown 应为 false");

            Object.DestroyImmediate(raw.gameObject);
        }

        /// <summary>
        /// MENU-21 引导文案必须覆盖玩家实际用得到的键位。
        /// 这些是"进去了不知道能按什么"那条实测反馈的直接兜底，缺一条就等于没兜住。
        /// </summary>
        [Test]
        public void MENU21_Guide_BodyCoversEssentialKeys()
        {
            Text body = Child(_guide, GuideCanvas + "/GuidePanel/GuideBody").GetComponent<Text>();
            Assert.IsNotNull(body, "说明正文应为 Text 组件");

            StringAssert.Contains("W A S D", body.text, "说明应包含移动键");
            StringAssert.Contains("闪避", body.text, "说明应包含闪避");
            StringAssert.Contains("ESC", body.text, "说明应包含 ESC 菜单键");
            StringAssert.Contains("R", body.text, "说明应包含 R 重开键");
        }

        /// <summary>
        /// MENU-22 【预期红灯 · 对应 BUG-1】引导文案不得宣传不存在的技能槽。
        ///
        /// GameAction 枚举只有 Attack / Skill1 / Skill2 / Dodge / LockOn / ToggleDebug
        /// （Input/GameAction.cs L33-L38），根本没有"技能三"；而右键 Mouse1 在
        /// InputBindingProfile.cs L53 是绑给 **Skill1** 的，与 K 键同槽。
        /// 因此说明里的"鼠标右键 → 技能三"是一条会误导玩家的假键位。
        /// 本用例在 ControlsGuideHud.cs L53 修正前会失败，这是刻意保留的红灯。
        /// </summary>
        [Test]
        public void MENU22_Guide_DoesNotAdvertiseNonexistentSkillSlot()
        {
            Text body = Child(_guide, GuideCanvas + "/GuidePanel/GuideBody").GetComponent<Text>();

            StringAssert.DoesNotContain("技能三", body.text,
                "工程内不存在技能三；右键实际绑定的是技能一（Skill1），文案会误导玩家");
        }

        // =====================================================================
        // 层级契约（三份文件头注释共同约定的叠放次序）
        // =====================================================================

        /// <summary>
        /// MENU-23 Canvas sortingOrder 分层：主菜单 300 > 暂停 250 > 引导 220
        /// > 终局 200 > Hud 100。
        ///
        /// 【为什么要把整条链一次断言完】单独看每个数字都"没错"，出事的永远是相对关系：
        /// 比如把暂停调到 190，单测它等于 190 会过，但实际表现是暂停面板被死亡面板盖住、
        /// 玩家点不到"返回主菜单"。所以这里既钉死绝对值，也钉死大小关系。
        /// </summary>
        [Test]
        public void MENU23_CanvasSortingOrder_LayeringIsCorrect()
        {
            GameOverHud over = new GameObject("GameOverHudForOrder").AddComponent<GameOverHud>();
            over.Build();

            try
            {
                int main = CanvasOf(_main).sortingOrder;
                int pause = CanvasOf(_pause).sortingOrder;
                int guide = CanvasOf(_guide).sortingOrder;
                int gameOver = CanvasOf(over).sortingOrder;

                Assert.AreEqual(MainMenuOrder, main, "主菜单 sortingOrder 应为 300");
                Assert.AreEqual(PauseOrder, pause, "暂停面板 sortingOrder 应为 250");
                Assert.AreEqual(GuideOrder, guide, "操作引导 sortingOrder 应为 220");
                Assert.AreEqual(GameOverOrder, gameOver, "终局面板 sortingOrder 应为 200");

                Assert.Greater(main, pause, "主菜单必须压住暂停面板");
                Assert.Greater(pause, guide, "暂停面板必须压住操作引导");
                Assert.Greater(guide, gameOver, "操作引导必须压住终局面板");
                Assert.Greater(gameOver, HudOrder, "终局面板必须压住 Hud(100)");
            }
            finally
            {
                Object.DestroyImmediate(over.gameObject);
            }
        }

        /// <summary>MENU-24 三个面板都应是 ScreenSpaceOverlay 全屏覆盖模式。</summary>
        [Test]
        public void MENU24_AllPanels_RenderModeIsScreenSpaceOverlay()
        {
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, CanvasOf(_main).renderMode,
                "主菜单应为全屏覆盖模式");
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, CanvasOf(_pause).renderMode,
                "暂停面板应为全屏覆盖模式");
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, CanvasOf(_guide).renderMode,
                "操作引导应为全屏覆盖模式");
        }

        /// <summary>
        /// MENU-25 主菜单/暂停面板的遮罩必须拦射线（raycastTarget = true），
        /// 否则玩家隔着菜单能点到下层世界，属于穿透 bug。
        /// </summary>
        [Test]
        public void MENU25_Overlay_BlocksRaycast()
        {
            Image mainDim = Child(_main, MainCanvas + "/Dim").GetComponent<Image>();
            Image pauseDim = Child(_pause, PauseCanvas + "/Dim").GetComponent<Image>();

            Assert.IsNotNull(mainDim, "主菜单应有 Dim 遮罩");
            Assert.IsNotNull(pauseDim, "暂停面板应有 Dim 遮罩");
            Assert.IsTrue(mainDim.raycastTarget, "主菜单遮罩必须吞掉点击，避免穿透到下层");
            Assert.IsTrue(pauseDim.raycastTarget, "暂停遮罩必须吞掉点击，避免穿透到下层");
        }

        /// <summary>
        /// MENU-26 Build 应保证场景里有 EventSystem，否则按钮"看得见、点不动"
        /// 且控制台一声不吭（静默失效）。
        /// </summary>
        [Test]
        public void MENU26_Build_EnsuresEventSystemExists()
        {
            Assert.IsNotNull(Object.FindObjectOfType<EventSystem>(),
                "Build 应补齐 EventSystem，否则 uGUI 按钮收不到点击");
        }
    }
}
