// -----------------------------------------------------------------------------
// GameManager.cs —— 全局状态机（asmdef: Xianxia.Unity.T2）
//
// 【它是什么】
// 蓝图 0.2 要求的 "GameManager"。一个跨场景持久的单例，持有游戏宏观相位
// （启动 / 主菜单 / 游玩 / 暂停 / 结算），并作为其他核心管理器的"总开关"。
//
// 【为什么是懒加载单例 + DontDestroyOnLoad，而不是塞进场景文件】
// 本工程一贯"零场景资产"：Hud / GameOverHud / MainMenuHud 全是运行时
// new GameObject().AddComponent 出来的，场景 .unity 文件里不落任何脚本实例。
// GameManager 沿用同款套路——Ensure() 在第一次被需要时自己造一个持久物体，
// 既不必动你本地的场景文件（红线：不碰在途改动），又能在 LoadScene 后存活。
//
// 【为什么不写 ResetStatics】
// 本类的"状态"是 Instance（一个 UnityEngine.Object 引用）和 Phase（枚举）。
// 关 Domain Reload 时，挂了 DontDestroyOnLoad 的物体会跨 PlayMode 活着，
// Instance 自然保持有效；开 Domain Reload 时物体被销毁，Unity 的 == 重载
// 会让 Instance 表现成 null，Ensure() 的 `if (Instance == null)` 立刻重建。
// 两种情况下都不需要手动把 Instance 置空来"复位"——置空反而在 Domain
// Reload 关闭时制造一个多余的新物体（旧的 DontDestroyOnLoad 残骸还在）。
// 所以这里刻意不写 ResetStatics，与 Bootstrap 的纪律不冲突，只是适用条件不同。
//
// 【相位怎么被驱动】
// CombatBridge 在关键节点调 Notify*：Start 完 → NotifyRunStarted（Playing）；
// 终局 → NotifyRunEnded（GameOver）；SetMenuPaused → Paused/Playing；
// 退出 → NotifyReturnedToMenu。GameManager 自己不读输入、不碰战斗逻辑，
// 只镜像 CombatBridge 已经算好的状态，保持"内核给事实、表现层决定呈现"的分层。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2.Core
{
    /// <summary>游戏宏观相位。GameManager 持有，任意系统可订阅 GamePhaseChangedEvent 感知。</summary>
    public enum GamePhase
    {
        /// <summary>刚启动 / 尚未进入任何可玩状态。</summary>
        Boot,
        /// <summary>主菜单（本周冷启动默认不进，预留给将来开始菜单）。</summary>
        MainMenu,
        /// <summary>游玩中（世界生成完、角色可动）。</summary>
        Playing,
        /// <summary>被菜单/引导冻结（经 CombatScheduler.Paused，不用 Time.timeScale）。</summary>
        Paused,
        /// <summary>胜负已分（结算面板在）。</summary>
        GameOver,
    }

    /// <summary>
    /// 全局游戏总管。单例、跨场景持久。通过 <see cref="Ensure"/> 懒加载创建。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameManager : MonoBehaviour
    {
        /// <summary>全局唯一实例。Ensure() 保证非 null（首次访问时自造）。</summary>
        public static GameManager Instance { get; private set; }

        /// <summary>当前宏观相位。只读窗口；变更统一走 SetPhase / Notify*。</summary>
        public GamePhase Phase { get; private set; } = GamePhase.Boot;

        /// <summary>
        /// 懒加载获取实例。若尚未创建，则造一个 DontDestroyOnLoad 的持久物体并挂本组件。
        /// </summary>
        public static GameManager Ensure()
        {
            if (Instance == null)
            {
                GameObject go = new GameObject("GameManager");
                Instance = go.AddComponent<GameManager>();
                Object.DontDestroyOnLoad(go);
            }
            return Instance;
        }

        private void Awake()
        {
            // 重复实例（理论上不会发生，因为 Ensure 已置 Instance）直接自毁，
            // 保证全局只有一个 GameManager 在跑。
            if (Instance != null && Instance != this)
            {
                Object.Destroy(gameObject);
                return;
            }
            Instance = this;
            Object.DontDestroyOnLoad(gameObject);
            Phase = GamePhase.Boot;
        }

        /// <summary>切换相位并广播 GamePhaseChangedEvent（同值不重复广播）。</summary>
        public void SetPhase(GamePhase phase)
        {
            if (Phase == phase)
            {
                return;
            }
            GamePhase old = Phase;
            Phase = phase;
            EventManager.Publish(new GamePhaseChangedEvent { Old = old, New = phase });
        }

        /// <summary>一局开始 → Playing。</summary>
        public void NotifyRunStarted() => SetPhase(GamePhase.Playing);

        /// <summary>一局结束 → GameOver，并把胜负结果广播给人物表现等订阅者。</summary>
        public void NotifyRunEnded(bool won)
        {
            // CombatBridge 正常只通知一次；仍在这里幂等保护，避免重复结算让
            // Death/胜利表现被从头播放两遍。
            if (Phase == GamePhase.GameOver)
            {
                return;
            }
            SetPhase(GamePhase.GameOver);
            EventManager.Publish(new RunEndedEvent { Won = won });
        }

        /// <summary>进入菜单冻结 → Paused。</summary>
        public void NotifyPaused() => SetPhase(GamePhase.Paused);

        /// <summary>解冻回到游玩 → Playing。</summary>
        public void NotifyResumed() => SetPhase(GamePhase.Playing);

        /// <summary>退出 / 返回主菜单 → MainMenu。</summary>
        public void NotifyReturnedToMenu() => SetPhase(GamePhase.MainMenu);
    }
}
