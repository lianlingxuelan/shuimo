// -----------------------------------------------------------------------------
// P0_2_GameOverHudTests.cs —— P0-2 胜负结算面板（GameOverHud）EditMode 验收套件
//
// 【测试范围】
// 本文件只测 GameOverHud 这一个"表现层组件"本身：Build 出来的 UI 结构对不对、
// Show/Won/Lost 标题与显隐状态对不对、Hide 是否收起、Build 未调用时 Show 是否安全。
// 这些断言**不依赖任何场景、不依赖 RunPhase 事件**，因此用 NUnit 的纯 [Test]
// 在 EditMode 下就能跑（GameOverHud.Build 只是创建 Canvas/Text 组件并改属性，
// 不需要渲染管线）。
//
// 【为什么单独一个文件】
// CombatBridge 把"订阅 RunPhase.PhaseChanged → 暂停 + 弹面板"的逻辑放在了
// P0_2_P0_4_PlayModeTests.cs（需要真场景的 PlayMode 集成测试）。
// GameOverHud 这一层是纯 UI，离内核最远、最稳，单独抽出来后 EditMode 即可全绿，
// 不必每次都进 PlayMode。两层各管各的，diff 也干净。
//
// 【验证状态】
// 编写环境无 Unity / dotnet，本文件未经编译运行。请在本地 Unity 编辑器确认：
//   Window → General → Test Runner → EditMode → 选 Xianxia.Unity.T2.Tests → Run All
//
// 【禁止事项】不得引用 Unity 表现层之外的生产逻辑；只通过公开 API（Build/Show/Hide）
// 与组件树（transform.Find）读取断言所需状态，不反射私有字段。
// -----------------------------------------------------------------------------

using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Xianxia.Combat;
using Xianxia.Unity.T2;

namespace Xianxia.Unity.T2.Tests
{
    /// <summary>P0-2 结算面板组件验收。</summary>
    [TestFixture]
    public sealed class P0_2_GameOverHudTests
    {
        private const string CanvasName = "GameOverCanvas";
        private const string TitleName = "GameOverTitle";
        private const string HintName = "GameOverHint";

        private GameObject _host;
        private GameOverHud _hud;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("GameOverHudTestHost");
            _hud = _host.AddComponent<GameOverHud>();
            _hud.Build();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_host);
        }

        // --- 组件树辅助：只走公开 transform 结构，不碰私有字段 -----------------

        private Text FindText(string childName)
        {
            Transform t = _hud.transform.Find(CanvasName + "/" + childName);
            Assert.IsNotNull(t, "Build() 应创建 " + CanvasName + "/" + childName);
            Text txt = t.GetComponent<Text>();
            Assert.IsNotNull(txt, childName + " 应为 Text 组件");
            return txt;
        }

        // --- 用例 ---------------------------------------------------------------

        /// <summary>GOH-01 Build 之后面板应默认隐藏（胜负发生才显示）。</summary>
        [Test]
        public void GOH01_Build_DefaultsHidden()
        {
            Assert.IsFalse(_host.activeSelf, "Build 后面板应默认隐藏");
        }

        /// <summary>GOH-02 Lost → 标题"你 倒 下 了"且面板激活。</summary>
        [Test]
        public void GOH02_Show_Lost_SetsDefeatTitleAndActivates()
        {
            _hud.Show(RunPhase.Lost);

            Assert.IsTrue(_host.activeSelf, "Lost 后面板应显示");
            Assert.AreEqual("你 倒 下 了", FindText(TitleName).text,
                "Lost 标题应为「你 倒 下 了」");
        }

        /// <summary>GOH-03 Won → 标题"胜 利"且面板激活（与 Lost 分支互不串味）。</summary>
        [Test]
        public void GOH03_Show_Won_SetsVictoryTitleAndActivates()
        {
            _hud.Show(RunPhase.Won);

            Assert.IsTrue(_host.activeSelf, "Won 后面板应显示");
            Assert.AreEqual("胜 利", FindText(TitleName).text, "Won 标题应为「胜 利」");
        }

        /// <summary>GOH-04 副标题应为"按 R 重新开始"，提示重开键位。</summary>
        [Test]
        public void GOH04_Show_HintTextIsRestartPrompt()
        {
            _hud.Show(RunPhase.Lost);
            Assert.AreEqual("按 R 重新开始", FindText(HintName).text,
                "副标题应提示按 R 重开");
        }

        /// <summary>GOH-05 Hide 之后面板应回到隐藏态（供重开后复位）。</summary>
        [Test]
        public void GOH05_Hide_DeactivatesPanel()
        {
            _hud.Show(RunPhase.Lost);
            Assert.IsTrue(_host.activeSelf, "Show 后应可见");

            _hud.Hide();
            Assert.IsFalse(_host.activeSelf, "Hide 后应为隐藏");
        }

        /// <summary>
        /// GOH-06 Show 的 _titleText 空值守卫：若 Build 未调用（_titleText 为 null），
        /// Show 应静默返回而非抛空引用。对应 CombatBridge.Start 注释里"不调 Build 则静默失效"。
        /// </summary>
        [Test]
        public void GOH06_Show_WithoutBuild_DoesNotThrow()
        {
            GameOverHud raw = new GameObject("RawHud").AddComponent<GameOverHud>();
            Assert.DoesNotThrow(() => raw.Show(RunPhase.Lost),
                "Build 未调用时 Show 不应抛异常");
            Object.DestroyImmediate(raw.gameObject);
        }

        /// <summary>
        /// GOH-07 面板 Canvas 的 sortingOrder 应为 200，高于 Hud 的 100，
        /// 否则"你倒下了"会被血条遮挡（GameOverHud.cs 头注释明定的层级契约）。
        /// </summary>
        [Test]
        public void GOH07_Build_CanvasSortingOrderAboveHud()
        {
            Canvas canvas = _hud.GetComponentInChildren<Canvas>(true);
            Assert.IsNotNull(canvas, "Build 应创建 Canvas");
            Assert.AreEqual(200, canvas.sortingOrder,
                "死亡面板 sortingOrder 必须高于 Hud(100)，否则被遮挡");
        }

        /// <summary>
        /// GOH-08 渲染模式应为 ScreenSpaceOverlay（全屏覆盖，结算面板语义）。
        /// </summary>
        [Test]
        public void GOH08_Build_RenderModeIsScreenSpaceOverlay()
        {
            Canvas canvas = _hud.GetComponentInChildren<Canvas>(true);
            Assert.IsNotNull(canvas);
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode,
                "结算面板应为全屏覆盖模式");
        }
    }
}
