// -----------------------------------------------------------------------------
// P0_2_P0_4_PlayModeTests.cs —— P0-2 终局暂停弹界面 + P0-4 重开 的 PlayMode 集成验收
//
// 【为什么必须 PlayMode】
// P0-2 / P0-4 的"业务价值"不在 GameOverHud 这一个组件里，而在 CombatBridge 把
// 内核 RunPhase.PhaseChanged 事件接到"暂停战斗 + 弹面板"、以及"按 R 重载场景"这条
// 端到端链路上。这条链路依赖：真实的 MonoBehaviour 生命周期（Start 订阅事件）、
// CombatController.Update 每帧驱动 Scheduler.Tick → Encounter.StepFixed、
// 以及 SceneManager 重载场景。只有进 PlayMode 才具备这些前提。
//
// 【怎么跑】
//   1. GameplaySceneName 已配置为本工程真实的战斗场景 SampleScene，无需再改。
//   2. Unity 编辑器：Window → General → Test Runner → PlayMode
//      → 选 Xianxia.Unity.T2.Tests → Run All。
//   3. 若场景名被清空，相关用例会以 Assert.Ignore 跳过，不会误报红。
//
// 【★ 为什么在 EditMode 标签页下会自动跳过（2026-08 BugFix，寇豆码）】
// 本夹具所在的 Xianxia.Unity.T2.Tests 是 includePlatforms:["Editor"] 的程序集，
// Unity 据此把**整个程序集**归类为 EditMode 测试 —— 于是这 5 条 [UnityTest] 实际上
// 是以「编辑器协程」在 EditMode 标签页里跑的，Application.isPlaying 恒为 false。
// 此时 SceneManager.LoadScene 会直接抛：
//     InvalidOperationException: This can only be used during edit mode,
//     please use EditorSceneManager.OpenScene() instead.
// 每条用例的第一句就是 yield return LoadGameplayScene()，所以 5 条**必然全红**，
// 且和帧时序毫无关系 —— 这正是上一轮"改轮询等待"没能治好它们的原因：
// 执行流根本走不到那些等待 helper。（同一坑已在 P1_6 的 PI-11 上踩过并修过。）
//
// 那为什么不像 PI-11 那样在 EditMode 下用 EditorSceneManager.OpenScene 兜底？
// 因为本套件验的是**帧循环本身**：CombatController.Update → Scheduler.Tick →
// Encounter.StepFixed。编辑器不跑 Update，也不给 AddComponent 的组件调 Awake，
// 于是 EnemySpawner._ctrl 恒为 null，SpawnFirstWave 会打一条 Debug.LogError
// （"找不到已初始化的 CombatController"），而 Unity 测试框架把非预期 LogError
// 判为失败 —— EditMode 下这 5 条**不可能**真实通过。硬凑只会得到假绿。
// 所以这里诚实地 Assert.Ignore：EditMode 跳过，PlayMode 真跑真断言。
// 想拿到真实覆盖，请把本文件迁到一个不限定 includePlatforms 的 PlayMode 测试
// 程序集（见文末回执建议），迁过去后无需改任何一行断言。
//
// 【★ 开局引导面板会冻住调度器（P0-6 回归，同批修复）】
// LoadGameplayScene 置 SkipOnNextLoad=true 之后，CombatBridge.Start 走的是
// 「SetMenuPaused(true) + ControlsGuideHud.ShowPanel()」这一支 —— 注意它**照样暂停**，
// 只是把"解冻"推迟到玩家按任意键关掉引导那一刻（OnPanelClosed → SetMenuPaused(false)）。
// 自动化测试没有输入源，那一键永远不会来，于是 Scheduler.Paused 恒为 true、
// Tick 恒返回 0、一个逻辑步都不跑，判负/判胜/布防全部不会发生。
// 因此每次进场景后都必须调一次 BeginCombat() 代玩家关掉引导，见该方法注释。
//
// 【static 状态卫生】
// LoadGameplayScene() 会把 MainMenuHud.SkipOnNextLoad 置 true 以跳过主菜单。
// 该字段是 static，不随场景销毁，会一路残留到后续任何测试夹具（典型受害者是
// EditMode 的 P0_5_MenuHudTests.MENU09，它断言夹具启动时旗标应为 false）。
// 因此本套件用 [UnityTearDown] 在每个用例结束后无条件复位，杜绝跨夹具污染。
//
// 【验证状态】
// 编写环境无 Unity / dotnet，本文件未经编译运行。请在本地 Unity 编辑器确认无误。
// 红线：本文件只允许用公开 API（bridge.Encounter / bridge.Player /
// GetComponent<CombatController>() / GameOverHud 组件树），不反射私有字段。
// -----------------------------------------------------------------------------

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using Xianxia.Combat;
using Xianxia.Unity.T2;
using Xianxia.Combat.UnityBridge;
using UnityEngine.TestTools;

namespace Xianxia.Unity.T2.Tests
{
    /// <summary>P0-2 终局暂停弹界面 + P0-4 重开的 PlayMode 集成验收。</summary>
    [TestFixture]
    public sealed class P0_2_P0_4_PlayModeTests
    {
        // 本工程真实战斗场景为 SampleScene，已由主理人核实：场景内 GameObject
        // 1602164645 同时挂载 CombatController + CombatBridge，且 playerTransform /
        // enemyPrefab / zoneId / playerHpMax 均已配置齐全；该场景已 enabled 加入
        // ProjectSettings/EditorBuildSettings.asset，PlayMode 下可直接 LoadScene。
        // 留空则相关用例自动 Ignore（供裁剪场景的分支工程使用）。
        private const string GameplaySceneName = "SampleScene";

        /// <summary>
        /// 每个用例结束后把 static 旗标复位，避免污染后续夹具（见文件头「static 状态卫生」）。
        /// 用 [UnityTearDown] 而非 [TearDown]：PlayMode 下前者与 [UnityTest] 的协程
        /// 生命周期同一条时间线，保证在场景卸载之后才执行，语义更确定。
        /// </summary>
        [UnityTearDown]
        public IEnumerator ResetMenuSkipFlagAfterEachTest()
        {
            MainMenuHud.SkipOnNextLoad = false;
            yield return null;
        }

        private IEnumerator LoadGameplayScene()
        {
            if (string.IsNullOrEmpty(GameplaySceneName))
            {
                Assert.Ignore("未配置 GameplaySceneName，跳过需要场景的 P0-2/P0-4 集成用例。");
                yield break;
            }
            // ★ 修复①（根因 A）：EditMode 上下文里 SceneManager.LoadScene 非法，会抛
            //   InvalidOperationException，导致 5 条用例在第一句就全红。
            //   本套件按设计只能在 PlayMode 成立（理由见文件头），EditMode 直接跳过。
            //   守卫必须放在 SkipOnNextLoad 赋值**之前**：Assert.Ignore 是靠抛异常实现的，
            //   放在后面会让 static 旗标带着 true 逃逸出去污染后续夹具
            //   （[UnityTearDown] 虽然会兜底复位，但不该依赖兜底来保证不变量）。
            if (!Application.isPlaying)
            {
                Assert.Ignore(
                    "P0-2/P0-4 端到端用例需要真实帧循环（CombatController.Update → " +
                    "Scheduler.Tick → Encounter.StepFixed），只能在 PlayMode 下成立。" +
                    "当前是 EditMode（Application.isPlaying=false），SceneManager.LoadScene 不可用，故跳过。" +
                    "如需真实覆盖，请把 P0_2_P0_4_PlayModeTests.cs 迁到不限定 includePlatforms 的 " +
                    "PlayMode 测试程序集后在 Test Runner → PlayMode 标签页运行。");
                yield break;
            }

            // P0-5：主菜单开局会把 Scheduler 冻结（_menuPaused）。本套件验的是战斗中的
            // 终局行为，不该被菜单挡住，所以用与"按 R 重开"同一个旗标直接进战斗。
            // ⚠️ 注意：置了它也**不等于**战斗立刻开跑 —— P0-6 引导面板仍会保持冻结，
            //    真正的解冻由 BeginCombat() 代玩家完成。
            MainMenuHud.SkipOnNextLoad = true;
            SceneManager.LoadScene(GameplaySceneName);
            // 等两帧：场景加载 + 所有 Awake/Start（含 CombatBridge 订阅 PhaseChanged）跑完。
            yield return null;
            yield return null;
        }

        /// <summary>
        /// 取场景里的 CombatBridge，并**当场校验装配完整性**。
        ///
        /// 【为什么把判空塞进 helper 而不是散在用例里】
        /// 下面每个用例的第一步都是 bridge.Player / bridge.Encounter。若装配没跑完，
        /// 报错会是一句毫无信息量的 NullReferenceException，堆栈还指向测试文件本身，
        /// 让人误以为是测试写错了。在这里一次性拦下，失败消息直接点名
        /// "哪一环没接上"，把定位成本从"翻生产代码"降到"读一行断言"。
        /// </summary>
        private static CombatBridge FindBridge()
        {
            CombatBridge bridge = Object.FindObjectOfType<CombatBridge>();
            Assert.IsNotNull(bridge,
                "场景中应存在 CombatBridge：确认 " + GameplaySceneName +
                " 已加入 Build Settings 且场景内对象挂了 CombatBridge 组件");

            Assert.IsNotNull(bridge.Player,
                "CombatBridge.Player 为 null：CombatController.BuildEncounter 可能未执行，" +
                "或 Inspector 上 playerTransform 未绑定");
            Assert.IsNotNull(bridge.Encounter,
                "CombatBridge.Encounter 为 null：CombatController.Awake/BuildEncounter 可能未执行，" +
                "或 CombatBridge 与 CombatController 不在同一 GameObject 上");
            Assert.IsNotNull(bridge.Encounter.Bridge,
                "Encounter.Bridge（DifficultyBridge）为 null：难度桥未装配，反调参数无从写入");

            return bridge;
        }

        private static CombatScheduler FindScheduler(CombatBridge bridge)
        {
            CombatController ctrl = bridge.GetComponent<CombatController>();
            Assert.IsNotNull(ctrl, "CombatBridge 应与 CombatController 同体（挂在同一个 GameObject 上）");
            Assert.IsNotNull(ctrl.Scheduler,
                "CombatController 应持有 Scheduler：Awake 可能提前抛异常而中断了初始化");
            return ctrl.Scheduler;
        }

        /// <summary>
        /// 让战斗真正开跑：代玩家关掉 P0-6 开局引导面板，解除菜单类冻结。
        ///
        /// 【为什么非有它不可 —— 根因 B】
        /// SkipOnNextLoad=true 只是跳过**主菜单**，不等于跳过冻结。CombatBridge.Start
        /// 的 skip 分支是：
        ///     SetMenuPaused(true);            // 引导期间保持冻结
        ///     _controlsGuideHud.ShowPanel();  // 弹一屏操作说明
        /// 解冻被推迟到面板关闭那一刻（OnPanelClosed → SetMenuPaused(false)），
        /// 而面板只由 ControlsGuideHud.Update 里的 Input.anyKeyDown 关闭。
        /// 自动化测试没有输入源 ⇒ 面板永不关闭 ⇒ _menuPaused 恒 true
        /// ⇒ Scheduler.Paused 恒 true ⇒ Tick 恒返回 0 ⇒ StepFixed 一次都不跑
        /// ⇒ RunState.Evaluate 从不执行 ⇒ Phase 永远是 Playing。
        /// 于是 WaitForKernelSteps / WaitForRunOver 只会白等满 180 帧再让断言报红 ——
        /// 这就是"锚定 TotalSteps 的轮询修复"在用户机器上依然无效的原因：
        /// 计数器本身就不会动，锚点再准也没用。
        ///
        /// 【为什么走 HidePanel() 而不是直接 bridge.SetMenuPaused(false)】
        /// HidePanel() 正是真人玩家那条路径（按任意键 → HidePanel → OnPanelClosed →
        /// SetMenuPaused(false)），顺带把"引导关闭能否正确解冻"这条接线也一并覆盖了。
        /// 直接写 SetMenuPaused(false) 则会绕过接线，接线断了测试也照样绿 —— 假绿。
        /// 只有在分支工程裁掉了 P0-6（找不到 ControlsGuideHud）时才退化为直接解冻。
        ///
        /// 【幂等】HidePanel() 内部对"已隐藏"直接 return，不会重复触发 OnPanelClosed，
        /// 所以本方法可以安全地被同一条用例调用多次（P4-02 重载后要再调一次）。
        /// </summary>
        private static IEnumerator BeginCombat(CombatBridge bridge)
        {
            ControlsGuideHud guide = Object.FindObjectOfType<ControlsGuideHud>();
            if (guide != null)
            {
                guide.HidePanel();
            }
            else
            {
                // 没有 P0-6 引导面板的分支工程：语义等价地直接解冻。
                bridge.SetMenuPaused(false);
            }

            // 解冻本身是同步生效的（SetMenuPaused → ApplyPauseState → Scheduler.Paused）。
            // 这一帧是留给 CombatController.Update 喂第一次 Tick 用的。
            yield return null;

            Assert.IsFalse(FindScheduler(bridge).Paused,
                "关掉开局引导后战斗仍处于暂停：说明玩法冻结还有第二个来源没解除。" +
                "排查顺序 ①ControlsGuideHud.OnPanelClosed 是否仍绑到 SetMenuPaused(false)；" +
                "②MainMenuHud 是否意外显示着（SkipOnNextLoad 未生效，此时引导面板压根没弹，" +
                "HidePanel 空转）；③CombatBridge.IsGameplayBlocked 是否新增了别的冻结来源。");
        }

        private static GameOverHud FindHud()
        {
            GameOverHud hud = Object.FindObjectOfType<GameOverHud>();
            Assert.IsNotNull(hud,
                "CombatBridge.Start 应动态创建 GameOverHud，但场景里没找到。" +
                "排查方向：CombatBridge.Start 是否完整跑完？请检查 Console 是否有 " +
                "「[T2] CombatBridge 找不到已初始化的 CombatController」或其它初始化期异常——" +
                "Start 中途抛异常会静默跳过 Hud 的创建");
            return hud;
        }

        /// <summary>数一数战场上还剩几只敌人（不改状态，纯观察）。</summary>
        private static int CountEnemies(CombatBridge bridge)
        {
            if (bridge.Encounter == null || bridge.Encounter.Combatants == null) return 0;

            int n = 0;
            foreach (var c in bridge.Encounter.Combatants)
            {
                if (c.IsEnemy) n++;
            }
            return n;
        }

        /// <summary>
        /// 等敌人真正刷出来，最多等 <paramref name="maxFrames"/> 帧。
        ///
        /// 【为什么需要它】原写法进场景后只等 1 帧就遍历 Combatants 清怪。
        /// 但刷怪由 CombatController 在 Update 里按自己的节奏推进，
        /// 并不保证在第 1 帧就完成。若那一帧还是 0 只敌人，"清光敌人"这个动作
        /// 会变成空操作 —— P2-03 就永远等不到 Won，P2-02 也测不出"同归于尽"。
        /// 这是**测试时序假设过强**，不是产品缺陷，所以在测试侧用轮询修正。
        /// 轮询而非固定等 N 帧：刷出来就立刻返回，不平白拖慢用例。
        ///
        /// 【它只保证"怪进了列表"，不保证"裁判见过怪"】
        /// 布防（RunState._armed）发生在 Encounter.StepFixed 里，而逻辑步由定步长
        /// 累加器驱动，与帧不是一回事。所以调用方在清怪之前还必须
        /// <see cref="WaitForKernelSteps"/> 至少一步，否则可能"列表里有怪、
        /// 但一个逻辑步都还没跑过"，清完之后裁判从没见过敌人 ⇒ Won 分支永不打开。
        /// </summary>
        private static IEnumerator WaitForEnemies(CombatBridge bridge, int maxFrames = 180)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                if (CountEnemies(bridge) > 0) yield break;
                yield return null;
            }
        }

        /// <summary>
        /// 等内核真正推进 <paramref name="steps"/> 个固定逻辑步，最多等 <paramref name="maxFrames"/> 帧。
        ///
        /// 【为什么不能用 yield return null 数帧】
        /// CombatScheduler.Tick 是**定步长累加器**：每帧只喂进 Time.deltaTime，
        /// 攒够 1/60 秒才推进一个逻辑步，一帧推进 0~N 步 —— 0 是合法结果。
        /// Test Runner 跑 PlayMode 时 vsync 通常是关的，一帧可能只有 3~5ms，
        /// 「yield return null 两次」累计不到 10ms，**一个逻辑步都不会跑**。
        /// 而判负、判胜、布防、清尸全部只发生在 Encounter.StepFixed 里
        /// （RemoveDead → RunState.Evaluate），逻辑步没跑 = 什么都没发生。
        /// 所以一切时序等待都必须锚定 Scheduler.TotalSteps 这个公开计数器，
        /// 而不是锚定帧数 —— 后者在快机器上会随机漏判，正是典型的时序 flaky。
        /// </summary>
        private static IEnumerator WaitForKernelSteps(CombatScheduler scheduler, int steps = 1, int maxFrames = 180)
        {
            int target = scheduler.TotalSteps + steps;
            for (int f = 0; f < maxFrames && scheduler.TotalSteps < target; f++)
            {
                yield return null;
            }
        }

        /// <summary>
        /// 等这一局真正分出胜负（Phase 脱离 Playing），最多等 <paramref name="maxFrames"/> 帧。
        /// 与 <see cref="WaitForKernelSteps"/> 同理：终局只在逻辑步里结算。
        /// 超时后**不在这里报错**，交给调用方那条带上下文的断言去报红，消息更有信息量。
        /// </summary>
        private static IEnumerator WaitForRunOver(CombatBridge bridge, int maxFrames = 180)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                if (bridge.Encounter != null && bridge.Encounter.Phase != RunPhase.Playing) yield break;
                yield return null;
            }
        }

        /// <summary>把结算面板标题读出来，顺带校验组件树结构，失败时直接指出缺哪一节。</summary>
        private static string TitleTextOf(GameOverHud hud)
        {
            Transform titleTf = hud.transform.Find("GameOverCanvas/GameOverTitle");
            Assert.IsNotNull(titleTf,
                "结算面板应有 GameOverCanvas/GameOverTitle 节点：GameOverHud.Build() 的组件树被改过？");

            UnityEngine.UI.Text title = titleTf.GetComponent<UnityEngine.UI.Text>();
            Assert.IsNotNull(title, "GameOverTitle 应挂 UnityEngine.UI.Text 组件");
            return title.text;
        }

        // ---------------------------------------------------------------------
        // P0-2：玩家死亡 → 内核判负 → 上层暂停并弹"你倒下了"
        // ---------------------------------------------------------------------

        /// <summary>
        /// P2-01 把玩家血量置 0，等一帧让 Scheduler 推进 → RunPhase 判负 →
        /// OnRunPhaseChanged 应：①暂停战斗 ②弹出结算面板且标题为"你 倒 下 了"。
        /// </summary>
        [UnityTest]
        public IEnumerator P2_01_Death_PausesCombatAndShowsDefeatPanel()
        {
            yield return LoadGameplayScene();
            CombatBridge bridge = FindBridge();
            CombatScheduler scheduler = FindScheduler(bridge);

            // 关掉 P0-6 开局引导（等价玩家"按任意键继续"），战斗才真正开跑。
            yield return BeginCombat(bridge);

            Assert.IsFalse(scheduler.Paused,
                "开局引导关闭后战斗应处于进行中（未暂停）。若为 true，检查 SkipOnNextLoad " +
                "是否生效（场景是否仍停在主菜单的 _menuPaused），以及 ControlsGuideHud " +
                "关闭回调是否仍接在 CombatBridge.SetMenuPaused(false) 上");

            // 模拟玩家被打死（不经伤害公式，直接清空血量）。
            bridge.Player.Hp = 0.0f;
            // 等内核真的跑出逻辑步并结算终局。不能按帧数硬等：一帧可能推进 0 步，
            // 那样 Scheduler.Tick → StepFixed → RunState.Evaluate 根本没被调用过。
            yield return WaitForRunOver(bridge);

            Assert.AreEqual(RunPhase.Lost, bridge.Encounter.Phase,
                $"玩家血空后应判负，实际 Phase={bridge.Encounter.Phase}（玩家 Hp={bridge.Player.Hp}）");
            Assert.IsTrue(bridge.Encounter.IsRunOver, "IsRunOver 应同步为 true");
            Assert.IsTrue(bridge.IsRunOver, "CombatBridge.IsRunOver 应转发为 true");
            Assert.IsTrue(scheduler.Paused, "终局回调应暂停战斗调度器（表现层暂停，内核只观察）");

            GameOverHud hud = FindHud();
            Assert.IsTrue(hud.gameObject.activeSelf, "死亡后应弹出结算面板");
            // 标题文本挂在 GameOverCanvas/GameOverTitle 下（与 EditMode 套件同款结构）。
            Assert.AreEqual("你 倒 下 了", TitleTextOf(hud), "死亡面板标题应为「你 倒 下 了」");
        }

        /// <summary>
        /// P2-02 同一步同归于尽 → 仍判负（RunPhase 的"Lost 优先于 Won"语义在端到端链路成立）。
        /// 这里清掉所有敌人同时把玩家打死，验证面板仍按失败呈现。
        /// </summary>
        [UnityTest]
        public IEnumerator P2_02_MutualDeath_ShowsDefeatNotVictory()
        {
            yield return LoadGameplayScene();
            CombatBridge bridge = FindBridge();
            CombatScheduler scheduler = FindScheduler(bridge);

            // ⓪ 先关掉开局引导解冻调度器 —— 不解冻的话下面 ② 的 WaitForKernelSteps
            //    会白等 180 帧（Tick 恒返回 0，TotalSteps 纹丝不动）。
            yield return BeginCombat(bridge);

            // ① 等敌人进入 Combatants。
            yield return WaitForEnemies(bridge);
            // ② 再等内核至少跑完一个逻辑步，让裁判"布防"（见过活敌人）。
            //    本条判负其实不依赖布防，但与 P2-03 共用同一套时序前提，
            //    两条用例的"战场已就绪"含义保持一致，排查时不必分别推演。
            yield return WaitForKernelSteps(scheduler);

            Assert.IsNotNull(bridge.Encounter.Combatants, "Encounter.Combatants 不应为 null");
            int enemyCount = 0;
            foreach (var c in bridge.Encounter.Combatants)
            {
                if (c.IsEnemy)
                {
                    c.Hp = 0.0f;
                    enemyCount++;
                }
            }
            Assert.Greater(enemyCount, 0,
                "前置：等满 180 帧仍未刷出任何敌人。检查 CombatController 的 enemyPrefab / zoneId " +
                "配置是否为空，以及 Scheduler 是否真的在跑（Paused 应为 false）");

            bridge.Player.Hp = 0.0f;
            yield return WaitForRunOver(bridge);

            Assert.AreEqual(RunPhase.Lost, bridge.Encounter.Phase,
                $"同归于尽必须判负，不可误判胜利。实际 Phase={bridge.Encounter.Phase}，" +
                $"清空了 {enemyCount} 只敌人，玩家 Hp={bridge.Player.Hp}");
            Assert.AreEqual("你 倒 下 了", TitleTextOf(FindHud()), "同归于尽时面板仍应显示失败");
        }

        /// <summary>
        /// P2-03 清光所有敌人 → 内核判胜 → 面板标题应为"胜 利"。
        /// </summary>
        [UnityTest]
        public IEnumerator P2_03_Victory_ShowsVictoryPanel()
        {
            yield return LoadGameplayScene();
            CombatBridge bridge = FindBridge();
            CombatScheduler scheduler = FindScheduler(bridge);

            // ⓪ ★ 先关掉开局引导解冻调度器。这一步是 ② 能成立的前提：
            //    调度器暂停时 TotalSteps 永不增长，"布防"也就永远不会发生。
            yield return BeginCombat(bridge);

            // ① 等敌人进入 Combatants。
            yield return WaitForEnemies(bridge);
            // ② ★ 关键：再等内核跑完至少一个逻辑步。
            //    RunState 只在 StepFixed 里看见"活敌人数 > 0"才会布防（_armed），
            //    而布防是 Won 分支的**唯一开关**。只等怪进列表是不够的：
            //    若此刻一个逻辑步都没跑过就把怪清光，裁判从未见过敌人，
            //    aliveEnemyCount 直接从 0 走到 0 ⇒ 永远判不出 Won，本条必红。
            yield return WaitForKernelSteps(scheduler);

            Assert.IsNotNull(bridge.Encounter.Combatants, "Encounter.Combatants 不应为 null");
            int enemyCount = 0;
            foreach (var c in bridge.Encounter.Combatants)
            {
                if (c.IsEnemy)
                {
                    c.Hp = 0.0f;
                    enemyCount++;
                }
            }
            Assert.Greater(enemyCount, 0,
                "前置：判胜要求裁判先'见过活敌人'。等满 180 帧仍是 0 只，说明刷怪没跑起来，" +
                "此时 Won 分支根本不会开 —— 这条红灯指向刷怪配置，而非终局逻辑");

            yield return WaitForRunOver(bridge);

            Assert.AreEqual(RunPhase.Won, bridge.Encounter.Phase,
                $"敌人全清且玩家存活应判胜。实际 Phase={bridge.Encounter.Phase}，" +
                $"清空了 {enemyCount} 只敌人，玩家 Hp={bridge.Player.Hp}/{bridge.Player.HpMax}");
            GameOverHud hud = FindHud();
            Assert.IsTrue(hud.gameObject.activeSelf, "胜利后应弹出结算面板");
            Assert.AreEqual("胜 利", TitleTextOf(hud), "胜利面板标题应为「胜 利」");
        }

        // ---------------------------------------------------------------------
        // P0-4：对局结束按 R 重载场景（重开一局）
        // ---------------------------------------------------------------------

        /// <summary>
        /// P4-01 终局之后，CombatBridge.Update 的"IsRunOver && 按 R"分支应可达
        /// （即重开门控成立）。此处直接断言 IsRunOver 已在死亡后置 true。
        /// 真正的"按 R 键"由用户在本机 PlayMode 手动验证（Input 注入不在测试框架内）。
        /// </summary>
        [UnityTest]
        public IEnumerator P4_01_AfterDeath_RestartGateIsOpen()
        {
            yield return LoadGameplayScene();
            CombatBridge bridge = FindBridge();

            // 关掉开局引导：调度器还暂停着的话，判负永远不会结算。
            yield return BeginCombat(bridge);

            bridge.Player.Hp = 0.0f;
            yield return WaitForRunOver(bridge);

            Assert.IsTrue(bridge.IsRunOver,
                $"死亡后 IsRunOver 应为 true，P0-4『按 R 重开』分支才可达。" +
                $"实际 IsRunOver={bridge.IsRunOver}，Encounter.Phase={bridge.Encounter.Phase}，" +
                $"玩家 Hp={bridge.Player.Hp}");
        }

        /// <summary>
        /// P4-02 重载场景后，新一局应回到 Playing 且裁判已复位
        /// （等价于"按 R 触发 SceneManager.LoadScene"的结果验证）。
        /// </summary>
        [UnityTest]
        public IEnumerator P4_02_ReloadScene_ResetsToFreshRun()
        {
            yield return LoadGameplayScene();
            CombatBridge bridge = FindBridge();

            // 关掉开局引导：调度器还暂停着的话，判负永远不会结算。
            yield return BeginCombat(bridge);

            bridge.Player.Hp = 0.0f;
            yield return WaitForRunOver(bridge);
            Assert.IsTrue(bridge.IsRunOver,
                $"前置：应先进入终局。实际 Encounter.Phase={bridge.Encounter.Phase}");

            // 等价 P0-4 的"按 R"动作：重载当前场景。
            // P0-5 后按 R 会先置 SkipOnNextLoad（重开不再回主菜单），这里同步照做，
            // 否则新一局停在主菜单上，下面"重载后 Paused 应为 false"必然失败。
            // 该旗标由 [UnityTearDown] 统一复位，不会漏给后续夹具。
            // 这里不再重复 Application.isPlaying 守卫：本行只有在 LoadGameplayScene
            // 已经放行（即确已处于 PlayMode）时才可达，EditMode 下用例早在第一句就 Ignore 了。
            MainMenuHud.SkipOnNextLoad = true;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
            yield return null;
            yield return null;
            yield return null;

            // FindBridge 内部已校验 fresh / Player / Encounter / Encounter.Bridge 均非空，
            // 重载后装配不全会在这里就报出具体环节，而不是在下面几行抛 NullReference。
            CombatBridge fresh = FindBridge();
            Assert.AreNotSame(bridge, fresh,
                "重载后应拿到**新的** CombatBridge 实例；拿到旧实例说明场景其实没重载成功");
            Assert.IsFalse(fresh.IsRunOver,
                $"重载后新一局应未分胜负（回到 Playing），实际 Phase={fresh.Encounter.Phase}");
            Assert.AreEqual(RunPhase.Playing, fresh.Encounter.Phase,
                $"重载后 RunPhase 应复位为 Playing，实际={fresh.Encounter.Phase}");

            // 新一局同样会弹一次开局引导（CombatBridge.Start 的 skip 分支对"重开"也照弹），
            // 所以这里必须再代玩家关一次，否则下面这条"未暂停"断言必然失败。
            yield return BeginCombat(fresh);

            Assert.IsFalse(FindScheduler(fresh).Paused,
                "重载后关掉引导，战斗调度器应恢复运行。若仍为 true，检查 SkipOnNextLoad 是否被 " +
                "CombatBridge『读完即复位』的一次性语义提前消费掉（导致新一局停在主菜单、" +
                "引导面板压根没弹、HidePanel 空转）");
        }
    }
}
