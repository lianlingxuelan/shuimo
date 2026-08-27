// -----------------------------------------------------------------------------
// EventManager.cs —— 类型化事件总线（asmdef: Xianxia.Unity.T2）
//
// 【它解决什么】
// 蓝图 0.2 要求的 "EventManager"。让各系统**彻底解耦**：发布者只管
// EventManager.Publish(evt)，订阅者只管 EventManager.Subscribe<T>(handler)，
// 两边互不知道对方存在。将来正魔系统发布 MoralityChangedEvent、背包系统
// 发布 InventoryChangedEvent，都不用回头改任何调用方。
//
// 【为什么是静态类 + Dictionary<Type, Delegate>】
// 本工程已有大量静态事件（CombatScheduler.Stepped、RunPhaseTracker.PhaseChanged
// 等），但都是"一对一"委托。我们要的是"一对多、跨系统"，标准做法就是一个
// 类型→多播委托的字典。泛型 Subscribe/Publish 做类型抹除与还原，外层调用
// 全程类型安全，内部只有一个 object 化的委托表。
//
// 【★ 本静态字典必须有复位钩子 —— 本工程第三次栽在同一类坑的同源约束】
// Bootstrap.cs / CombatScheduler 都因为 "关闭 Domain Reload 后 static 跨
// PlayMode 存活" 出过黑屏 / 冻结事故。EventManager 的 _handlers 也是可写
// static：如果不清，第 2 次进 PlayMode 时旧订阅链（指向已销毁的面板/组件）
// 仍在表里，发布事件会回调到死对象 → MissingReferenceException，且可能把
// 同事件的后续订阅者一起带走。所以必须在 SubsystemRegistration（最早一档）
// 把表清空；订阅全在运行时（CombatBridge.Start 等）重新挂上，清空零副作用。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Xianxia.Unity.T2.Core
{
    /// <summary>
    /// 全局事件总线。静态、线程不安全的单进程总线（Unity 主线程够用）。
    /// 典型用法：
    /// <code>
    /// EventManager.Subscribe&lt;EnemyKilledEvent&gt;(OnEnemyKilled);
    /// EventManager.Publish(new EnemyKilledEvent { CombatantId = id });
    /// </code>
    /// </summary>
    public static class EventManager
    {
        /// <summary>类型 → 多播委托。唯一的可写静态状态，必须在 ResetStatics 清空。</summary>
        private static readonly Dictionary<Type, Delegate> _handlers = new Dictionary<Type, Delegate>();

        /// <summary>订阅类型为 <typeparamref name="T"/> 的事件。</summary>
        public static void Subscribe<T>(Action<T> handler)
        {
            if (handler == null)
            {
                return;
            }
            Type t = typeof(T);
            Delegate del;
            if (_handlers.TryGetValue(t, out del))
            {
                _handlers[t] = (Action<T>)del + handler;
            }
            else
            {
                _handlers[t] = handler;
            }
        }

        /// <summary>退订。与 Subscribe 严格对称；退到空则把该类型的键整个移除。</summary>
        public static void Unsubscribe<T>(Action<T> handler)
        {
            if (handler == null)
            {
                return;
            }
            Type t = typeof(T);
            if (!_handlers.TryGetValue(t, out Delegate del))
            {
                return;
            }
            Delegate combined = (Action<T>)del - handler;
            if (combined == null)
            {
                _handlers.Remove(t);
            }
            else
            {
                _handlers[t] = combined;
            }
        }

        /// <summary>发布一个事件给所有订阅者。无订阅者时是安全的 no-op。</summary>
        public static void Publish<T>(T evt)
        {
            if (evt == null)
            {
                return;
            }
            if (_handlers.TryGetValue(typeof(T), out Delegate del))
            {
                ((Action<T>)del)?.Invoke(evt);
            }
        }

        /// <summary>清空全部订阅。一般只由 ResetStatics 在域重载时调。</summary>
        public static void Clear()
        {
            _handlers.Clear();
        }

        /// <summary>
        /// 域重载 / 每次进 PlayMode 最早的一档复位。不清会跨 PlayMode 残留死订阅。
        /// 与 Bootstrap.ResetStatics 取同一档（SubsystemRegistration）以保持纪律一致。
        /// </summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _handlers.Clear();
        }
    }
}
