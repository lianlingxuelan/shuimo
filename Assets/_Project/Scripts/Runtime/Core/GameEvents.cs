// -----------------------------------------------------------------------------
// GameEvents.cs —— 首批领域事件 DTO（asmdef: Xianxia.Unity.T2）
//
// 【它是什么】
// 蓝图 0.2 要求的 "EventManager / 事件总线" 的**载荷**部分。每个事件是一个
// 轻量数据类（只有 public 字段，无方法、无逻辑），由 EventManager 负责
// 发布/订阅（见同目录 EventManager.cs）。
//
// 【为什么用 class 而不是 struct】
// EventManager 的泛型总线用 Action<T> 存订阅者，T 为引用类型时订阅/退订的
// 配对最稳（值类型在 += / -+ 链上每次都 boxing 出新实例，Delegate.Remove
// 会静默匹配不上）。事件本就是"传一份快照给别人看"，引用类型完全够用。
//
// 【本周只定义骨架，不全量发布】
// 正魔/背包相关事件（MoralityChanged / InventoryChanged）现在只是占位类型，
// 对应系统要到第 3 / 第 4 周才落地。先把"事件长什么样"钉死，将来发布时
// 不会有人临时瞎起名、字段对不上。
//
// 【为什么放在 Xianxia.Unity.T2.Core】
// 事件总线是运行时基础设施，要跟 GameManager / SceneLoader 同文同宗，
// 直接吃 T2 现有的 UnityEngine / UI 引用即可。单独开 asmdef 只会多一道
// 配置还容易把循环依赖引进来（见 GameOverHud.cs 文件头的同款理由）。
// -----------------------------------------------------------------------------

namespace Xianxia.Unity.T2.Core
{
    /// <summary>全局相位切换（Boot → MainMenu → Playing → Paused → GameOver）。</summary>
    public sealed class GamePhaseChangedEvent
    {
        public GamePhase Old;
        public GamePhase New;
    }

    /// <summary>一局战斗开始（玩家已生成、世界已就绪）。</summary>
    public sealed class RunStartedEvent
    {
    }

    /// <summary>一局战斗结束。won = 胜利 / 失败。</summary>
    public sealed class RunEndedEvent
    {
        public bool Won;
    }

    /// <summary>玩家角色已生成入场。</summary>
    public sealed class PlayerSpawnedEvent
    {
    }

    /// <summary>一只敌人被击杀。</summary>
    public sealed class EnemyKilledEvent
    {
        public int CombatantId;
        public string Name;
    }

    /// <summary>请求存档（由 SaveManager 消费，蓝图第1周落盘）。</summary>
    public sealed class SaveRequestedEvent
    {
    }

    /// <summary>请求读档。</summary>
    public sealed class LoadRequestedEvent
    {
    }

    // ---- 以下为后续周次的占位事件（本周不发布，只先把类型钉死）----

    /// <summary>正魔值变化。第 3 周（正魔系统）真正发布。</summary>
    public sealed class MoralityChangedEvent
    {
        public float Zheng;
        public float Mo;
    }

    /// <summary>背包内容变化。第 4 周（背包桥接）真正发布。</summary>
    public sealed class InventoryChangedEvent
    {
    }
}
