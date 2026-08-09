// -----------------------------------------------------------------------------
// GameOverHud.cs —— 胜负结算面板（P0-2，asmdef: Xianxia.Unity.T2）
//
// 【这个面板是干嘛的】
// 玩家血量归零（或者打完所有敌人）时，内核的 RunPhaseTracker 会广播一个
// "RunPhase.Lost / Won" 事件。CombatBridge 订阅了这个事件，在回调里做两件事：
//   1. 把战斗调度器（CombatScheduler）的 Paused 置 true，让怪和伤害都停下；
//   2. 调用本脚本的 Show(phase)，把"你 倒 下 了 / 胜 利"弹出来。
// 本脚本只负责"把字画出来"，不碰任何战斗逻辑——这符合本工程一贯的分层：
// 内核只给事实，表现层决定怎么呈现。
//
// 【为什么用 Legacy Text 而不是 TextMeshPro】
// 和 Hud.cs 同款理由：TMP 首次使用要导入一堆二进制 Essentials 资产，
// 与"零美术资源"目标冲突。Legacy Text 用 Unity 内置字体，工程一个文件都不落。
// 死亡面板只是几个大字提示，字形质量完全够用。
//
// 【为什么 sortingOrder = 200，比 Hud 的 100 还高】
// ScreenSpaceOverlay 模式下，多个 Canvas 叠在一起时，sortingOrder 大的盖在小的上面。
// Hud（血条/敌数）用的是 100；死亡面板必须压在血条之上，否则"你倒下了"会被
// 血条挡掉一角、甚至看不见。所以这里取 200，明确高于 Hud。
//
// 【为什么不分一个独立 asmdef】
// 本脚本属于既有的 Xianxia.Unity.T2 装配集，与 CombatBridge / Hud 同文同宗，
// 直接用现成的引用关系即可（T2 已经引用了 UnityEngine.UI）。新建 asmdef 只会
// 多一道引用配置、还可能把循环依赖引进来，完全没有好处。
//
// 【RunPhase 从哪来】
// Show 的参数类型是 Xianxia.Combat.RunPhase。本文件加了 using Xianxia.Combat;
// （与 CombatBridge 一致），这样 RunPhase 才能被识别——它是内核定义的枚举，
// 表现层只读、不重定义。
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.UI;
using Xianxia.Combat;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 胜负结算面板。挂在由 CombatBridge 动态创建的空物体上，
    /// 默认隐藏，胜负事件触发后由 CombatBridge 调 Show 显示。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameOverHud : MonoBehaviour
    {
        /// <summary>主标题（"你 倒 下 了" / "胜 利"）。</summary>
        private Text _titleText;

        /// <summary>副标题提示（"按 R 重新开始"）。</summary>
        private Text _hintText;

        /// <summary>
        /// 构建面板。在 AddComponent 之后由 CombatBridge 调一次即可。
        /// 套路与 Hud.Build() 完全一致：new Canvas + CanvasScaler + GraphicRaycaster +
        /// 几个 Legacy Text，只是 sortingOrder 用 200 压在血条之上。
        /// </summary>
        public void Build()
        {
            GameObject canvasGo = new GameObject("GameOverCanvas");
            canvasGo.transform.SetParent(transform, false);
            canvasGo.layer = LayerMask.NameToLayer("UI");

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;   // 高于 Hud 的 100，确保死亡面板压在血条之上

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920.0f, 1080.0f);
            // 0.5 = 宽高各占一半权重，与 Hud 同款，保证三档分辨率版式一致
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            // --- 主标题：屏幕正中，大字 ---
            _titleText = NewText("GameOverTitle", canvasGo.transform, 72,
                                 TextAnchor.MiddleCenter, Color.white);
            RectTransform titleRt = _titleText.rectTransform;
            Anchor(titleRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            titleRt.anchoredPosition = new Vector2(0.0f, 40.0f);
            titleRt.sizeDelta = new Vector2(1200.0f, 120.0f);

            // --- 副标题：主标题下方，小字 ---
            _hintText = NewText("GameOverHint", canvasGo.transform, 28,
                                TextAnchor.MiddleCenter, new Color(0.85f, 0.85f, 0.85f, 0.95f));
            RectTransform hintRt = _hintText.rectTransform;
            Anchor(hintRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            hintRt.anchoredPosition = new Vector2(0.0f, -40.0f);
            hintRt.sizeDelta = new Vector2(1200.0f, 60.0f);
            _hintText.text = "按 R 重新开始";

            // 默认藏起来，胜负发生才显示。
            gameObject.SetActive(false);
        }

        /// <summary>
        /// 显示结算面板。
        /// </summary>
        /// <param name="phase">终局阶段。Lost → 你倒下了；Won → 胜利。</param>
        public void Show(RunPhase phase)
        {
            if (_titleText == null)
            {
                return;
            }
            if (phase == RunPhase.Lost)
            {
                _titleText.text = "你 倒 下 了";
            }
            else
            {
                _titleText.text = "胜 利";
            }
            gameObject.SetActive(true);
        }

        /// <summary>隐藏结算面板（重开一局后由 CombatBridge 调用）。</summary>
        public void Hide()
        {
            gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------------
        // uGUI 小工具（与 Hud.cs 同款写法，避免两套约定）
        // ---------------------------------------------------------------------

        private static Text NewText(string name, Transform parent, int size, TextAnchor align, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            Text t = go.AddComponent<Text>();
            t.font = BuiltinFont();
            t.fontSize = size;
            t.alignment = align;
            t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            t.supportRichText = false;
            return t;
        }

        private static void Anchor(RectTransform rt, Vector2 min, Vector2 max, Vector2 pivot)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.pivot = pivot;
        }

        private static Font BuiltinFont()
        {
            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null)
            {
                f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            return f;
        }
    }
}
