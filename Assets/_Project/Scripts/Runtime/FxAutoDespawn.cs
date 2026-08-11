// -----------------------------------------------------------------------------
// FxAutoDespawn.cs —— 一次性特效自毁组件（asmdef: Xianxia.Unity.T2）
//
// 【补的是哪个洞】
// CombatEventsUnity.SpawnFx 只负责 Instantiate，**没有任何回收逻辑**。
// BOSS 打到 P3 之后每 6 秒放一次冲击波，一场十分钟的战斗就是 100 个特效对象
// 永久挂在 FxRoot 下面，谁也不会去清 —— 这是标准的对象泄漏，
// 表现为长局越打越卡，而 Profiler 里只看到 GameObject 数量在缓慢爬升。
//
// 【为什么用 Destroy 而不是对象池】
// 冲击波的发射频率是 6 秒一次，池化收益趋近于零，却要引入一整套池的生命周期管理
// （谁归还、场景重载怎么办、池空了怎么扩）。这里的正确工程判断是：
// 不为一秒钟 0.17 次的分配去背一个池的复杂度。
//
// 【计时用 OnEnable 而不是 Start】
// 将来若真接了对象池，复用取出时只会触发 OnEnable、不会再触发 Start，
// 写在 OnEnable 里到时候一行都不用改。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 挂在一次性特效实例上：启用后经过 <see cref="LifeSeconds"/> 秒自动销毁自身。
    /// 由 <c>WorldBuilder.BuildShockwaveFxTemplate</c> 装到模板上，
    /// <c>Instantiate</c> 出来的每一个副本都自带这个组件。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FxAutoDespawn : MonoBehaviour
    {
        /// <summary>
        /// 存活时长（秒）。默认取 <see cref="BossFlowConfig.FxLifeSeconds"/>。
        /// 允许 Inspector 覆盖，方便调特效时单独拉长看清楚。
        /// </summary>
        public float LifeSeconds = BossFlowConfig.FxLifeSeconds;

        // 已存活时长。用 unscaledDeltaTime 之外的普通 deltaTime —— 见 Update 注释。
        private float _age;

        private void OnEnable()
        {
            // 每次启用都从头计时。对象池复用时这一行就是"重置寿命"。
            _age = 0.0f;
        }

        private void Update()
        {
            // 用 Time.deltaTime（受 timeScale 影响）而不是 unscaledDeltaTime：
            // 玩家暂停时特效应当跟着定格，否则暂停界面上会看到一圈冲击波自顾自地消失。
            // 注意本项目的暂停走的是 CombatBridge 的 Scheduler.Paused（逻辑层不推进），
            // timeScale 保持 1，所以这里实际等价于墙钟 —— 但语义上写 deltaTime 才是对的，
            // 将来若改用 timeScale 暂停，这里无需修改。
            _age += Time.deltaTime;

            if (_age >= LifeSeconds)
            {
                Destroy(gameObject);
            }
        }
    }
}
