// -----------------------------------------------------------------------------
// P2_1_BossWiringTests.cs —— P2-1「BOSS 战接线」跨层验收（asmdef: Xianxia.Unity.T2.Tests）
//
// 【本文件与 RunPhaseTests.cs 的分工】
// RunPhaseTests（Xianxia.Combat.Tests）里的 RP15–RP19 只能看到纯逻辑层，
// 它证明"BossPending 这个虚拟计数的语义是对的"。但它看不到 Unity 层，
// 所以证明不了下面这几件真正会出事的事：
//   ① 血条会不会压住既有 HUD（GAP-8，肉眼在静态截图里几乎看不出来）；
//   ② BarFill 的 pivot.x 是不是 0（GAP-8 附带，错了会"血从两头往中间缩"）；
//   ③ 刻度线的 0.65 / 0.30 是不是真的引用了内核常量，而不是又抄了一份；
//   ④ 特效的 1/32 补偿系数是不是真的能把有效半径拉回 200（GAP-7）。
// 这些要么需要 UnityEngine.RectTransform，要么需要同时看到两层的常量，
// 只有在本程序集里测得了。
//
// 【为什么全是 EditMode 纯 [Test]，一个 PlayMode 都没有】
// 本切片的可自动化风险点全都是**不需要场景**就能证伪的：布局是纯数值、
// 阈值同源是纯常量比对、补偿系数是纯算术。能在 EditMode 跑的就别拖进 PlayMode。
// 真正需要场景的那几条（BOSS 出场时序、软锁兜底 12s、特效实例泄漏、重开复位）
// 依赖 1.5s/4s/12s 级别的真实时间推进与完整世界装配，写成自动化用例既慢又脆，
// 已在架构文档 §10 T05 里列为**手测项** AC-T05-5 ~ AC-T05-8，不在本文件覆盖范围内。
// 下面「未覆盖」一节把这条边界写死，免得后来者误以为绿了就等于全测过。
//
// 【怎么跑】
//   Window → General → Test Runner → EditMode → 选 Xianxia.Unity.T2.Tests → Run All
//
// 【验证状态 —— 请如实理解】
// 编写环境**没有 Unity、也没有 dotnet**，本文件**未经编译、未经运行**。
// 它的正确性目前只由"人工比对生产代码签名"保证。
//
// 【红线】只用公开 API（常量、公开属性、公开方法、Transform.Find 路径），
// 不反射任何私有字段。反射会在重构时误报红，也让"公开契约"失去意义。
//
// 【★测试陷阱：includeInactive】
// HudBossBar.Build() 末尾会 _root.SetActive(false)。
// GetComponentInChildren<T>() 默认**跳过未激活对象**，取组件必须写成
//     go.GetComponentInChildren<HudBossBar>(true);   // ← true 不能漏
// Transform.Find 对未激活对象有效，所以查控件一律走路径。
// 这与 P0_5_MenuHudTests.cs:20 记录的 MainMenuHud/PauseMenuHud 是同款坑。
//
// 【覆盖的用例 —— 对应架构文档 §10 验收编号】
//   BW-01  AC-T04-1  BossBar 根：anchor(0.5,1) pivot(0.5,1) pos(0,−72) size 720×64
//   BW-02  AC-T04-1  ★GAP-8：与 HudStatusIcons 目标状态行零重叠，间隙恰好 10px
//   BW-03  AC-T04-2  BarFill.pivot.x == 0（从左往右缩，不是两头缩）
//   BW-04  AC-T04-2  Hp/HpMax = 0.5 时 BarFill.sizeDelta.x 严格 == 360
//   BW-05  AC-T04-3  Build 末尾根节点未激活；includeInactive 能取到组件
//   BW-06  AC-T04-4  Build 幂等：二次调用不产生第二份控件
//   BW-07  AC-T04-9  ★刻度线位置由 CombatConfig 阈值算出，与 BossController 同源
//   BW-08  AC-T04-5  SetPhase 染色；P1 直跳 P3 后颜色为 P3 档
//   BW-09  ——        Show / Hide 语义：Hide 后解绑且不可见，可重复调用
//   BW-10  ——        HpMax == 0 不把 NaN 写进 sizeDelta（NaN 会让整个 Canvas 静默不渲染）
//   BW-11  AC-T04-6  ★GAP-7：1/(ShapePixels*0.5) 补偿后有效半径回到 200 单位
//   BW-12  Q-1       BossFlowConfig 超时序不变量：1.5 < 4 < 12 < 600，且重试排期落在硬超时内
//   BW-13  T01       跨层复核：PendingAwareEnemyCount 的欠债语义在 T2 侧看到的是同一份
//   BW-14  R-2       BossFlowState 枚举取值单向递增（Disabled 0 → Done 4）
//
// 【本文件明确未覆盖（需手测，见架构文档 §10 T05）】
//   AC-T05-5 端到端出场时序   AC-T05-6 重开复位   AC-T05-7 软锁 12s 兜底
//   AC-T05-8 暂停 30s 连续性  AC-T04-7 模板 activeSelf   AC-T04-8 特效实例泄漏
// -----------------------------------------------------------------------------

using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Xianxia.Core;
using Xianxia.Combat;
using Xianxia.Unity.T2;

namespace Xianxia.Unity.T2.Tests
{
    /// <summary>P2-1 BOSS 接线的 EditMode 验收套件。</summary>
    public sealed class P2_1_BossWiringTests
    {
        // 每个用例自己建、自己销。EditMode 下 GameObject 不会自动清理，
        // 残留对象会被后续用例的 FindObjectOfType 命中，必须显式收干净。
        private GameObject _hostGo;
        private RectTransform _canvasRoot;
        private HudBossBar _bar;

        /// <summary>
        /// 搭一个最小可用的 Canvas + HudBossBar。
        ///
        /// 【为什么不直接用 Hud.Build()】那会连带建出十几个控件、订阅一堆东西，
        /// 本套件只关心血条本身。最小夹具跑得快，失败时的信息也更干净。
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            // ★用构造器一次性指定组件类型，而不是 new + AddComponent<RectTransform>()。
            //   RectTransform 无法追加到一个已经有普通 Transform 的 GameObject 上
            //  （Unity 一个对象只能有一个 Transform 类组件），后者会抛异常。
            //   Canvas 虽然会自动带出 RectTransform，但显式写出来意图更清楚。
            _hostGo = new GameObject("BossBarTestHost", typeof(RectTransform), typeof(Canvas));

            Canvas canvas = _hostGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            _canvasRoot = _hostGo.transform as RectTransform;
            Assert.IsNotNull(_canvasRoot, "夹具的 Canvas 根必须是 RectTransform");

            _bar = _hostGo.AddComponent<HudBossBar>();
            _bar.Build(_canvasRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (_hostGo != null)
            {
                // EditMode 里必须用 DestroyImmediate：Object.Destroy 是延迟到帧末的，
                // 而 EditMode 测试之间根本不走帧循环，对象会一直堆着。
                Object.DestroyImmediate(_hostGo);
                _hostGo = null;
            }
            _canvasRoot = null;
            _bar = null;
        }

        // ---------------------------------------------------------------------
        // 助手
        // ---------------------------------------------------------------------

        /// <summary>按路径取血条的子控件。对未激活对象有效（Transform.Find 不看激活状态）。</summary>
        /// <param name="path">相对 Canvas 根的路径，例如 "BossBar/BarFill"。</param>
        /// <returns>找到的 RectTransform；不存在返回 null。</returns>
        private RectTransform FindWidget(string path)
        {
            Transform t = _canvasRoot.Find(path);
            return t != null ? t as RectTransform : null;
        }

        /// <summary>造一只用于血条显示的假 BOSS。</summary>
        /// <param name="hpMax">血量上限。</param>
        /// <param name="hp">当前血量。</param>
        /// <returns>已设好名字与等级的敌方实体。</returns>
        private static Combatant MakeBoss(float hpMax, float hp)
        {
            Combatant boss = Combatant.CreateEnemy(9001, new Vec2(100.0f, 100.0f),
                                                   hpMax, 8.0f, 70.0f);
            boss.IsBoss = true;
            boss.Kind = CombatConfig.KIND_WITCH;
            boss.DisplayName = "竹魈王";
            boss.Level = 5;
            boss.Hp = hp;
            return boss;
        }

        // ---------------------------------------------------------------------
        // BW-01 / BW-02 · 布局（GAP-8）
        // ---------------------------------------------------------------------

        /// <summary>AC-T04-1：根节点的锚点 / 轴心 / 位置 / 尺寸逐项对齐架构文档 §5.3 表格。</summary>
        [Test]
        public void BW01_BossBarRoot_MatchesLayoutContract()
        {
            RectTransform root = FindWidget("BossBar");
            Assert.IsNotNull(root, "BossBar 根节点应当被 Build 出来");

            Assert.AreEqual(0.5f, root.anchorMin.x, 1e-4f, "anchorMin.x 应为 0.5（顶部居中）");
            Assert.AreEqual(1.0f, root.anchorMin.y, 1e-4f, "anchorMin.y 应为 1");
            Assert.AreEqual(0.5f, root.anchorMax.x, 1e-4f, "anchorMax.x 应为 0.5");
            Assert.AreEqual(1.0f, root.anchorMax.y, 1e-4f, "anchorMax.y 应为 1");
            Assert.AreEqual(0.5f, root.pivot.x, 1e-4f, "pivot.x 应为 0.5");
            Assert.AreEqual(1.0f, root.pivot.y, 1e-4f, "pivot.y 应为 1");

            Assert.AreEqual(0.0f, root.anchoredPosition.x, 1e-4f, "根节点应水平居中");
            Assert.AreEqual(-BossFlowConfig.BarTopMargin, root.anchoredPosition.y, 1e-4f,
                            "根节点距顶应为 BarTopMargin（72，不是 PRD 的 40，见 §5.2）");

            Assert.AreEqual(BossFlowConfig.BarWidth, root.sizeDelta.x, 1e-4f, "根宽应为 720");
            Assert.AreEqual(BossFlowConfig.BarRootHeight, root.sizeDelta.y, 1e-4f, "根高应为 64");
        }

        /// <summary>
        /// ★AC-T04-1 GAP-8：血条不许压住 <c>HudStatusIcons</c> 的目标状态行。
        ///
        /// 用两侧的公开常量做数值断言，不靠肉眼看截图 —— 这个重叠在静态图上
        /// 恰恰最不显眼（图标被半透明血槽盖住只是"暗了一点"）。
        /// </summary>
        [Test]
        public void BW02_BossBar_DoesNotOverlapTargetStatusRow()
        {
            // 目标状态行：anchor(0.5,1)、pivot(x,1)、offset(0,−28)、高 IconSize
            //（HudStatusIcons.cs:105-106 + BuildRow 的 sizeDelta）
            const float targetRowTop = 28.0f;
            float targetRowBottom = targetRowTop + HudStatusIcons.IconSize;

            Assert.AreEqual(62.0f, targetRowBottom, 1e-4f,
                            "目标状态行底边应为 62（28 + IconSize 34）。若此断言红了，" +
                            "说明 HudStatusIcons 改了尺寸，BossFlowConfig.BarTopMargin 必须跟着重算");

            float barTop = BossFlowConfig.BarTopMargin;
            float gap = barTop - targetRowBottom;

            Assert.GreaterOrEqual(gap, 10.0f,
                                  "血条顶边与目标状态行底边至少要留 10px 间隙，实测 " + gap);

            // 反向哨兵：PRD 原方案的 40 一定会重叠。把它写成断言，
            // 是为了让"有人把 72 改回 40"这件事当场红，而不是等手测才发现。
            Assert.Less(40.0f, targetRowBottom,
                        "PRD 的『距顶 40』必然与目标状态行重叠，这条断言是防回退哨兵");
        }

        // ---------------------------------------------------------------------
        // BW-03 / BW-04 · 血条缩放方向与比例
        // ---------------------------------------------------------------------

        /// <summary>AC-T04-2：pivot.x 必须为 0，否则宽度会从两头一起缩。</summary>
        [Test]
        public void BW03_BarFill_PivotXIsZero()
        {
            RectTransform fill = FindWidget("BossBar/BarFill");
            Assert.IsNotNull(fill, "BarFill 应当被 Build 出来");

            Assert.AreEqual(0.0f, fill.pivot.x, 1e-4f,
                            "BarFill.pivot.x 必须为 0：非 0 时 sizeDelta.x = 720*ratio " +
                            "会表现为『血从两边往中间消失』，而不是常识中的从右往左掉");
            Assert.AreEqual(1.0f, fill.pivot.y, 1e-4f, "BarFill.pivot.y 应为 1（顶对齐）");
        }

        /// <summary>AC-T04-2：半血时血条宽度严格等于半宽。</summary>
        [Test]
        public void BW04_BarFill_HalfHpGivesHalfWidth()
        {
            Combatant boss = MakeBoss(1000.0f, 500.0f);
            _bar.Show(boss);

            RectTransform fill = FindWidget("BossBar/BarFill");
            Assert.IsNotNull(fill, "BarFill 应当存在");

            Assert.AreEqual(BossFlowConfig.BarWidth * 0.5f, fill.sizeDelta.x, 1e-3f,
                            "半血时血条宽度应为 360");
            Assert.AreEqual(BossFlowConfig.BarHeight, fill.sizeDelta.y, 1e-4f,
                            "刷新宽度时不该顺手改掉高度");
        }

        // ---------------------------------------------------------------------
        // BW-05 / BW-06 · Build 契约
        // ---------------------------------------------------------------------

        /// <summary>AC-T04-3：Build 末尾默认隐藏，且 includeInactive 能取到组件。</summary>
        [Test]
        public void BW05_Build_LeavesRootInactiveButDiscoverable()
        {
            RectTransform root = FindWidget("BossBar");
            Assert.IsNotNull(root, "BossBar 应当存在");
            Assert.IsFalse(root.gameObject.activeSelf,
                           "Build 末尾必须 SetActive(false)：没打 BOSS 时不该有一根空血条挂在屏幕顶上");

            Assert.IsTrue(_bar.IsBuilt, "IsBuilt 应为 true");
            Assert.IsFalse(_bar.IsShown, "刚 Build 完不该是显示状态");

            // ★这就是那个坑：不带 true 会取不到。
            HudBossBar viaInactive = _hostGo.GetComponentInChildren<HudBossBar>(true);
            Assert.IsNotNull(viaInactive, "带 includeInactive:true 必须能取到 HudBossBar");
            Assert.AreSame(_bar, viaInactive, "取到的应当是同一个组件实例");
        }

        /// <summary>AC-T04-4：Build 幂等，二次调用不产生第二份控件。</summary>
        [Test]
        public void BW06_Build_IsIdempotent()
        {
            int before = _canvasRoot.childCount;

            _bar.Build(_canvasRoot);
            _bar.Build(_canvasRoot);

            Assert.AreEqual(before, _canvasRoot.childCount,
                            "重复 Build 不该建出第二份控件（_built 守卫）");

            // 再确认一次没有出现同名兄弟节点：childCount 相等只能说明"总数没变"，
            // 理论上可能是"删一个建一个"。这里直接数同名节点。
            int bossBarCount = 0;
            for (int i = 0; i < _canvasRoot.childCount; i++)
            {
                if (_canvasRoot.GetChild(i).name == "BossBar")
                {
                    bossBarCount++;
                }
            }
            Assert.AreEqual(1, bossBarCount, "名为 BossBar 的子节点应当有且仅有一个");
        }

        // ---------------------------------------------------------------------
        // BW-07 · 刻度线与内核阈值同源
        // ---------------------------------------------------------------------

        /// <summary>
        /// ★AC-T04-9：刻度线的位置必须由 <c>CombatConfig</c> 的阈值算出。
        ///
        /// 这条是防"各写一份"的哨兵：血条里抄一个 0.65、BossController 里用另一个 0.65，
        /// 平衡性调整改了其中一个，玩家看到的变身点就会和刻度线对不上 ——
        /// 而这种偏差不会报任何错，只会让玩家觉得"这游戏的血条不准"。
        /// </summary>
        [Test]
        public void BW07_PhaseTicks_DeriveFromKernelThresholds()
        {
            RectTransform tickP2 = FindWidget("BossBar/TickP2");
            RectTransform tickP3 = FindWidget("BossBar/TickP3");

            Assert.IsNotNull(tickP2, "TickP2 应当被 Build 出来");
            Assert.IsNotNull(tickP3, "TickP3 应当被 Build 出来");

            Assert.AreEqual(BossFlowConfig.BarWidth * CombatConfig.BOSS_PHASE_P2_THRESHOLD,
                            tickP2.anchoredPosition.x, 1e-3f,
                            "P2 刻度线应落在 720 × BOSS_PHASE_P2_THRESHOLD");
            Assert.AreEqual(BossFlowConfig.BarWidth * CombatConfig.BOSS_PHASE_P3_THRESHOLD,
                            tickP3.anchoredPosition.x, 1e-3f,
                            "P3 刻度线应落在 720 × BOSS_PHASE_P3_THRESHOLD");

            // 顺带钉死阈值本身的相对关系与取值，防止有人把两个常量写反。
            Assert.Greater(CombatConfig.BOSS_PHASE_P2_THRESHOLD,
                           CombatConfig.BOSS_PHASE_P3_THRESHOLD,
                           "P2 阈值必须高于 P3 阈值");
            Assert.AreEqual(0.65f, CombatConfig.BOSS_PHASE_P2_THRESHOLD, 1e-6f, "P2 阈值基线 0.65");
            Assert.AreEqual(0.30f, CombatConfig.BOSS_PHASE_P3_THRESHOLD, 1e-6f, "P3 阈值基线 0.30");

            // 刻度线在血槽内、pivot 跨坐在阈值点上。
            Assert.AreEqual(0.5f, tickP2.pivot.x, 1e-4f, "刻度线 pivot.x 应为 0.5（跨坐在阈值上）");
            Assert.AreEqual(BossFlowConfig.TickWidth, tickP2.sizeDelta.x, 1e-4f, "刻度线宽度取 TickWidth");
        }

        // ---------------------------------------------------------------------
        // BW-08 / BW-09 / BW-10 · 显示行为
        // ---------------------------------------------------------------------

        /// <summary>AC-T04-5：阶段染色。P1 直跳 P3（BossController 的连跳只抛一次）后应为 P3 档色。</summary>
        [Test]
        public void BW08_SetPhase_AppliesPhaseColor()
        {
            Combatant boss = MakeBoss(1000.0f, 1000.0f);
            _bar.Show(boss);

            RectTransform fill = FindWidget("BossBar/BarFill");
            Assert.IsNotNull(fill, "BarFill 应当存在");
            Image img = fill.GetComponent<Image>();
            Assert.IsNotNull(img, "BarFill 上应当挂着 Image");

            Assert.AreEqual(BossPhase.P1, _bar.Phase, "Show 之后应重置为 P1");
            AssertColor(HudBossBar.Phase1Color, img.color, "P1 应为正红");

            _bar.SetPhase(BossPhase.P2);
            Assert.AreEqual(BossPhase.P2, _bar.Phase, "阶段应记为 P2");
            AssertColor(HudBossBar.Phase2Color, img.color, "P2 应为橙");

            // 连跳：BossController.CheckPhaseTransition 在 P1 直接跌破 30% 时只抛一次 P3，
            // 所以血条必须能从任意前序阶段一步跳到 P3，不能依赖"必须先经过 P2"。
            _bar.SetPhase(BossPhase.P3);
            Assert.AreEqual(BossPhase.P3, _bar.Phase, "阶段应记为 P3");
            AssertColor(HudBossBar.Phase3Color, img.color, "P3 应为灼白红");

            // 三档颜色必须互不相同，否则"染色"这件事本身就没意义了。
            Assert.AreNotEqual(HudBossBar.Phase1Color.r, HudBossBar.Phase2Color.r,
                               "P1 与 P2 的配色不该相同");
            Assert.AreNotEqual(HudBossBar.Phase2Color.g, HudBossBar.Phase3Color.g,
                               "P2 与 P3 的配色不该相同");
        }

        /// <summary>
        /// 逐分量比对颜色。
        ///
        /// 【为什么不直接 Assert.AreEqual(Color, Color)】uGUI 的 Image.color 在
        /// 赋值链路上可能经过一次 float 往返，直接比对象相等在个别 Unity 版本上会
        /// 因最低位差异误报红。逐分量带容差既严格又不脆。
        /// </summary>
        /// <param name="expected">期望颜色。</param>
        /// <param name="actual">实际颜色。</param>
        /// <param name="message">失败提示。</param>
        private static void AssertColor(Color expected, Color actual, string message)
        {
            Assert.AreEqual(expected.r, actual.r, 1e-3f, message + "（R 分量）");
            Assert.AreEqual(expected.g, actual.g, 1e-3f, message + "（G 分量）");
            Assert.AreEqual(expected.b, actual.b, 1e-3f, message + "（B 分量）");
            Assert.AreEqual(expected.a, actual.a, 1e-3f, message + "（A 分量）");
        }

        /// <summary>Show / Hide 的语义：Hide 后解绑且不可见，且可重复调用不抛。</summary>
        [Test]
        public void BW09_ShowHide_BindsAndUnbinds()
        {
            Combatant boss = MakeBoss(800.0f, 800.0f);

            _bar.Show(boss);
            Assert.IsTrue(_bar.IsShown, "Show 之后应当可见");
            Assert.AreSame(boss, _bar.Boss, "Show 之后应当绑定到该 BOSS");

            _bar.Hide();
            Assert.IsFalse(_bar.IsShown, "Hide 之后应当不可见");
            Assert.IsNull(_bar.Boss, "Hide 之后应当解绑，否则下一局会拿着上一只 BOSS 刷新");

            // 幂等：终局收条与 BOSS 死亡自愈可能在同一帧都调一次。
            Assert.DoesNotThrow(delegate { _bar.Hide(); }, "Hide 必须可重复调用");

            // 空引用防护：Show(null) 是空操作，不该把血条放出来。
            _bar.Show(null);
            Assert.IsFalse(_bar.IsShown, "Show(null) 应当是空操作");
        }

        /// <summary>
        /// HpMax 为 0 时不能把 NaN 写进 sizeDelta。
        ///
        /// 【为什么单独立一条】NaN 进了 RectTransform 会让**整个 Canvas 静默不渲染** ——
        /// 没有异常、没有日志，屏幕上 UI 全消失。这类故障的排查成本极高，
        /// 所以哪怕 HpMax=0 在正常流程里不可能出现，也要把夹死行为钉在测试里。
        /// </summary>
        [Test]
        public void BW10_ZeroHpMax_DoesNotProduceNaN()
        {
            Combatant boss = MakeBoss(0.0f, 0.0f);
            _bar.Show(boss);

            RectTransform fill = FindWidget("BossBar/BarFill");
            Assert.IsNotNull(fill, "BarFill 应当存在");

            Assert.IsFalse(float.IsNaN(fill.sizeDelta.x), "sizeDelta.x 不得为 NaN");
            Assert.IsFalse(float.IsInfinity(fill.sizeDelta.x), "sizeDelta.x 不得为无穷");
            Assert.AreEqual(0.0f, fill.sizeDelta.x, 1e-4f, "HpMax 为 0 时宽度应夹到 0");
        }

        // ---------------------------------------------------------------------
        // BW-11 · 特效缩放补偿（GAP-7）
        // ---------------------------------------------------------------------

        /// <summary>
        /// ★AC-T04-6 GAP-7：1/(ShapePixels*0.5) 的补偿必须把有效半径拉回 200 单位。
        ///
        /// 【链路复盘】
        ///   SpriteFactory.Circle 产 64×64 像素、PPU=1.0 的贴图 → 世界半径 32 单位；
        ///   CombatEventsUnity.SpawnFx 覆盖式写根节点 localScale = radius(200)；
        ///   补偿子节点 localScale = 1/32。
        ///   有效半径 = 32 × 200 × (1/32) = 200 ✓
        /// 没有补偿的话是 32 × 200 = 6400 单位 —— 玩家看到一整屏纯色，以为游戏崩了。
        ///
        /// 这里断言的是**算术恒等式**而不是实例化后的 bounds：后者要激活场景对象、
        /// 等一帧让渲染器算包围盒，属于 PlayMode 的活（AC-T04-6 的 bounds 断言留给手测）。
        /// 但恒等式一旦被破坏（比如有人把 1/32 硬编码成 0.03125 后又改了 ShapePixels），
        /// 这条会立刻红。
        /// </summary>
        [Test]
        public void BW11_ShockwaveScaleCompensation_YieldsRequestedRadius()
        {
            const float requestedRadius = 200.0f;

            // 贴图自带的世界半径：64px / PPU 1.0 → 直径 64 单位 → 半径 32 单位。
            float spriteRadius = SpriteFactory.ShapePixels * 0.5f;
            Assert.AreEqual(32.0f, spriteRadius, 1e-4f,
                            "ShapePixels 基线为 64（半径 32）。若此断言红了，" +
                            "补偿系数写法仍然正确，但请复核所有依赖 32 的注释");

            // 生产代码里的写法：1f / (SpriteFactory.ShapePixels * 0.5f)
            float compensation = 1.0f / (SpriteFactory.ShapePixels * 0.5f);
            Assert.AreEqual(0.03125f, compensation, 1e-6f, "当前 ShapePixels=64 下补偿应为 1/32");

            float effectiveRadius = spriteRadius * requestedRadius * compensation;
            Assert.AreEqual(requestedRadius, effectiveRadius, 1e-3f,
                            "补偿后有效半径必须精确回到请求的 200 单位");

            // 负对照：没有补偿会放大 32 倍，直接把整张地图盖住。
            float uncompensated = spriteRadius * requestedRadius;
            Assert.AreEqual(6400.0f, uncompensated, 1e-3f,
                            "无补偿时是 6400 单位 —— 这就是 GAP-7 的破坏力，留作对照");
        }

        // ---------------------------------------------------------------------
        // BW-12 / BW-13 / BW-14 · 配置与内核契约
        // ---------------------------------------------------------------------

        /// <summary>
        /// Q-1：兜底超时的序关系。这几个数之间的**顺序**比具体取值更要命 ——
        /// 顺序错了会让某一级防线彻底不可达，而不可达的防线不会报错，只会在
        /// 真出事的那天默默缺席。
        /// </summary>
        [Test]
        public void BW12_BossFlowConfig_TimeoutOrderingIsSane()
        {
            Assert.Greater(BossFlowConfig.EntryDelaySeconds, 0.0f,
                           "入场延迟必须为正，否则 BOSS 会和最后一只杂兵同帧出现，没有出场感");

            Assert.Less(BossFlowConfig.EntryDelaySeconds, BossFlowConfig.EntryTimeoutSeconds,
                        "入场延迟必须短于首次重试间隔，否则第一次生成尝试永远等不到");

            Assert.Less(BossFlowConfig.EntryTimeoutSeconds, BossFlowConfig.HardTimeoutSeconds,
                        "重试间隔必须短于硬超时，否则一次重试都排不进去");

            Assert.Less(BossFlowConfig.HardTimeoutSeconds, BossFlowConfig.DiagTimeoutSeconds,
                        "硬超时必须早于诊断日志阈值，否则诊断永远打不出来");

            Assert.GreaterOrEqual(BossFlowConfig.MaxSpawnRetries, 1, "至少要留一次重试");

            // 关键不变量：最后一次重试的排期（EntryTimeout × MaxRetries）必须仍落在硬超时之内，
            // 否则"重试 2 次"这个承诺是空头支票 —— 第二次还没轮到就被硬超时放行了。
            float lastRetryAt = BossFlowConfig.EntryTimeoutSeconds * BossFlowConfig.MaxSpawnRetries;
            Assert.Less(lastRetryAt, BossFlowConfig.HardTimeoutSeconds,
                        "最后一次重试的排期(" + lastRetryAt + "s)必须早于硬超时(" +
                        BossFlowConfig.HardTimeoutSeconds + "s)，否则重试次数是空头承诺");

            // 基线取值（架构文档 §6 Q-1 定稿）。改动需要同步文档，故写成硬断言。
            Assert.AreEqual(1.5f, BossFlowConfig.EntryDelaySeconds, 1e-6f);
            Assert.AreEqual(4.0f, BossFlowConfig.EntryTimeoutSeconds, 1e-6f);
            Assert.AreEqual(12.0f, BossFlowConfig.HardTimeoutSeconds, 1e-6f);
            Assert.AreEqual(2, BossFlowConfig.MaxSpawnRetries);
        }

        /// <summary>
        /// T01 契约的跨层复核：T2 侧看到的 <c>PendingAwareEnemyCount</c> 与内核是同一份。
        ///
        /// RP17 已经在 Combat.Tests 里测过一遍。这里再测一次不是重复劳动 ——
        /// 它证明的是"跨程序集引用没有拿到某个陈旧的副本"，这在改 asmdef 时会出事。
        /// </summary>
        [Test]
        public void BW13_PendingAwareCount_VisibleAcrossAssemblies()
        {
            Encounter enc = new Encounter();
            enc.SetPlayer(Combatant.CreatePlayer(1, 260.0f, Vec2.Zero));
            enc.Add(Combatant.CreateEnemy(200, new Vec2(1000.0f, 0.0f), 30.0f, 4.0f, 70.0f));

            Assert.IsFalse(enc.BossPending, "初始不该欠债");
            Assert.AreEqual(enc.AliveEnemyCount, enc.PendingAwareEnemyCount,
                            "未欠债时两个计数应当相等");

            enc.MarkBossPending();
            Assert.IsTrue(enc.BossPending, "置位后应当欠债");
            Assert.AreEqual(enc.AliveEnemyCount + 1, enc.PendingAwareEnemyCount,
                            "欠债时判定口径应当恰好多 1（不变量 I-1）");

            // 幂等：ArmBossPending 理论上只调一次，但重复调用绝不能把债累加成 2。
            enc.MarkBossPending();
            Assert.AreEqual(enc.AliveEnemyCount + 1, enc.PendingAwareEnemyCount,
                            "MarkBossPending 必须幂等，债只能是 0 或 1");

            bool cleared = enc.ClearBossPending();
            Assert.IsTrue(cleared, "首次销债应当返回 true");
            Assert.IsFalse(enc.BossPending, "销债后不该再欠债");
            Assert.AreEqual(enc.AliveEnemyCount, enc.PendingAwareEnemyCount,
                            "销债后两个计数应当重新相等");

            Assert.IsFalse(enc.ClearBossPending(), "重复销债应当返回 false（没有债可销）");
        }

        /// <summary>R-2：状态机取值单向递增，Disabled 是 0、Done 是终态。</summary>
        [Test]
        public void BW14_BossFlowState_IsMonotonicallyOrdered()
        {
            Assert.AreEqual(0, (int)BossFlowState.Disabled, "Disabled 必须是默认值 0");
            Assert.Less((int)BossFlowState.Pending, (int)BossFlowState.Entering,
                        "Pending 必须排在 Entering 之前");
            Assert.Less((int)BossFlowState.Entering, (int)BossFlowState.Fighting,
                        "Entering 必须排在 Fighting 之前");
            Assert.Less((int)BossFlowState.Fighting, (int)BossFlowState.Done,
                        "Fighting 必须排在 Done 之前");
            Assert.AreEqual(4, (int)BossFlowState.Done, "Done 必须是最大值（终态）");
        }
    }
}
