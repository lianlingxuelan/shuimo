// -----------------------------------------------------------------------------
// P1_2_HitFeedbackTests.cs —— P1-2「受击反馈四件套」EditMode 验收套件
//
// 【验证状态 —— 先说清楚，免得被误读为"已通过"】
// 编写环境**没有 Unity、没有 dotnet**，本文件**未经编译、未经运行**。
// 下面所有断言都是静态审查的产物，等待在本地 Unity 里跑一次才算数：
//   Window → General → Test Runner → EditMode → Xianxia.Unity.T2.Tests → Run All
//
// ★★★ 本套件中有 1 条用例是**预期失败**的，它不是写错了，是在指认一个真实缺陷：
//     Frozen_StaysReleased_WhenDisabledDirectorStillReceivesEvent()
//     详见该用例上方的注释与 docs/p1-2-qa-report.md 的 [P1-01]。
//     跑出红色是**正确结果**，请不要"修测试"，要修源码。
//
// 【测试范围】
// 只测能在 EditMode 下确定性求值的部分：
//   · FeedbackClock 静态闸门的读数契约与复位契约
//   · HitFeedbackDirector 的 Frozen 生命周期（启用/禁用/销毁/显式收敛）
//   · DamagePopupLayer 的池容量、合并、清空、"冻结但不清除"（A-7/A-8 的飘字半边）
//   · CameraShake × CameraFollow 的"权威中心只读不写"与回正无漂移（A-6/R-02）
//   · HitFeedbackConfig 的跨文件常量不变量（含与 HudSkillBar / CombatView 的对齐）
//   · CombatEventsT3Unity 探针字段的存续（防"删闪白顺手删探针"的回归）
//
// 【不在本文件测什么，以及为什么 —— EditMode 的三条硬约束】
//   1. **Update / LateUpdate 不会被驱动。** AddComponent 会立刻跑 Awake/OnEnable，
//      但此后引擎不再推进任何一帧。因此"顿帧 0.05s 后自动解冻""飘字 0.7s 后回收"
//      "屏震 sin 波衰减到 0"这类**依赖时间推进**的行为一律无法在此断言。
//      硬写会得到一条永远不动的假绿。它们属于 PlayMode / 本地目视。
//   2. **Time.frameCount 在 EditMode 下不推进。** HitFeedbackDirector.TickHitstop
//      有一句 `if (Time.frameCount == _stopKickFrame) return;`（起帧不扣）。
//      在 EditMode 里手工连调 KickHitstop→LateUpdate，帧号恒等 ⇒ 永远不递减。
//      所以本文件对 hitstop 只测**显式收敛路径**（ClearAll / OnDisable / OnDestroy），
//      不测倒计时自然归零。
//   3. **GetComponentInChildren&lt;T&gt;() 默认跳过未激活对象。**
//      DamagePopupLayer.BuildSlot 末尾 `text.gameObject.SetActive(false)`，
//      12 个槽位建出来就是熄灭的。取它们**必须** GetComponentsInChildren&lt;Text&gt;(true)。
//      本工程已在此处踩过两次，全文统一用带 includeInactive 的重载。
//
// 【★ TearDown 的复位责任 —— 本文件自己绝不能变成下一个 MENU09】
// FeedbackClock.Frozen 与 HitFeedbackConfig.FeedbackIntensity 都是 static 可写状态，
// 而 [RuntimeInitializeOnLoadMethod] **不会**在 EditMode 测试里触发。
// 任何一条用例把它们改脏之后不还原，后面所有用例（乃至同一 Editor 会话里别的套件）
// 都会读到脏值。TearDown 无条件复位两者，这是本文件的第一纪律。
//
// 【禁止事项】
//   · 不反射读写生产代码的私有字段来"造"状态（探针存续检查只做 API 存在性）。
//   · 不写"两侧取自同一来源"的等值断言（恒真假绿）。凡是断言，
//     左右两侧必须来自**不同的**责任方，或与字面量常量比较。
// -----------------------------------------------------------------------------

using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Xianxia.Combat;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2.Tests
{
    /// <summary>P1-2 受击反馈四件套 EditMode 验收。</summary>
    [TestFixture]
    public sealed class P1_2_HitFeedbackTests
    {
        // =====================================================================
        // 夹具
        // =====================================================================

        /// <summary>本用例创建的全部根对象，TearDown 里统一销毁。</summary>
        private System.Collections.Generic.List<GameObject> _spawned;

        [SetUp]
        public void SetUp()
        {
            _spawned = new System.Collections.Generic.List<GameObject>();

            // 进用例前先把静态闸门摆到干净状态，避免受上一条用例（或别的套件）影响。
            FeedbackClock.Frozen = false;
            HitFeedbackConfig.FeedbackIntensity = 1.0f;
        }

        [TearDown]
        public void TearDown()
        {
            // ★★★ 第一纪律：无条件复位所有 static 可写状态。
            //     漏了这一句，本文件就会亲手制造下一个 MENU09 式的跨用例污染。
            FeedbackClock.Frozen = false;
            HitFeedbackConfig.FeedbackIntensity = 1.0f;

            if (_spawned != null)
            {
                for (int i = 0; i < _spawned.Count; i++)
                {
                    if (_spawned[i] != null)
                    {
                        Object.DestroyImmediate(_spawned[i]);
                    }
                }
                _spawned.Clear();
            }
        }

        /// <summary>建一个受管理的空 GameObject（TearDown 会销毁它）。</summary>
        private GameObject NewGo(string name)
        {
            GameObject go = new GameObject(name);
            _spawned.Add(go);
            return go;
        }

        /// <summary>
        /// 建一台带 MainCamera 标签的正交相机。
        /// DamagePopupLayer.TryPlace 走 Camera.main 做世界→屏幕投影，
        /// 没有它 Push 会静默丢弃，所有飘字用例都会退化成"什么都没发生"的假绿。
        /// </summary>
        private Camera NewMainCamera()
        {
            GameObject go = NewGo("TestMainCamera");
            go.tag = "MainCamera";
            Camera cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 6.0f;
            go.transform.position = new Vector3(0.0f, 0.0f, -100.0f);
            return cam;
        }

        /// <summary>造一个内核战斗单位。字段全公开，无需反射。</summary>
        private static Combatant NewCombatant(int id, Faction faction, float hpMax, float hp)
        {
            Combatant c = new Combatant();
            c.Id = id;
            c.Faction = faction;
            c.HpMax = hpMax;
            c.Hp = hp;
            c.Position = new Vec2(0.0f, 0.0f);
            return c;
        }

        /// <summary>
        /// 数一个飘字层里"正在显示"的槽位数。
        /// ★ 必须带 includeInactive: true —— 12 个槽位建出来就是未激活的，
        ///   不带的话恒返回 0，用例会全部假绿。
        /// </summary>
        private static int CountActivePopups(DamagePopupLayer layer)
        {
            Text[] all = layer.GetComponentsInChildren<Text>(true);
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].gameObject.activeSelf)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>取第一个正在显示的槽位文本。没有则返回 null。</summary>
        private static string FirstActivePopupText(DamagePopupLayer layer)
        {
            Text[] all = layer.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].gameObject.activeSelf)
                {
                    return all[i].text;
                }
            }
            return null;
        }

        // =====================================================================
        // 一、FeedbackClock —— 静态闸门的读数与复位契约
        // =====================================================================

        /// <summary>
        /// 冻结时 Delta 必须**精确**为 0。
        /// 这是整个 P1-2 的地基：所有表现层组件都靠这个 0 定格。
        /// 与字面量 0 比较，不与 Time.deltaTime 比较（那是同源恒真）。
        /// </summary>
        [Test]
        public void FeedbackClock_Delta_IsExactlyZero_WhenFrozen()
        {
            FeedbackClock.Frozen = true;

            Assert.AreEqual(0.0f, FeedbackClock.Delta,
                "FeedbackClock.Frozen 为 true 时 Delta 必须精确为 0，否则顿帧期间"
                + "角色/特效/相机/飘字仍会推进，四件套的定格全部失效。");
        }

        /// <summary>未冻结时 Delta 不得为负 —— 负 dt 会让所有寿命计时倒着走。</summary>
        [Test]
        public void FeedbackClock_Delta_IsNonNegative_WhenNotFrozen()
        {
            FeedbackClock.Frozen = false;

            Assert.GreaterOrEqual(FeedbackClock.Delta, 0.0f,
                "未冻结时 Delta 不得为负，负值会让飘字寿命 / 屏震年龄倒着走，永不结束。");
        }

        /// <summary>
        /// ResetStatics() 必须能把卡住的闸门放开。
        /// 这是 FeedbackClock 文件头写死的"三重保险第 1 道"，也是
        /// 「Enter Play Mode without Domain Reload」下防开局即冻结的唯一手段。
        /// </summary>
        [Test]
        public void FeedbackClock_ResetStatics_ReleasesStuckGate()
        {
            FeedbackClock.Frozen = true;

            FeedbackClock.ResetStatics();

            Assert.IsFalse(FeedbackClock.Frozen,
                "ResetStatics() 必须把 Frozen 复位为 false。它是 Domain Reload 关闭时"
                + "防止上一局的顿帧状态跨局存活、导致下一局开局即全局冻结的唯一闸门。");
        }

        /// <summary>
        /// ResetStatics 必须挂着 SubsystemRegistration 这一档的
        /// [RuntimeInitializeOnLoadMethod]。挂晚了（如 AfterSceneLoad）会留出
        /// "第一个 Awake 已经读到脏 Frozen"的窗口；不挂则整条保险失效。
        /// </summary>
        [Test]
        public void FeedbackClock_ResetStatics_IsHookedAtSubsystemRegistration()
        {
            MethodInfo mi = typeof(FeedbackClock).GetMethod(
                "ResetStatics", BindingFlags.Public | BindingFlags.Static);

            Assert.IsNotNull(mi, "FeedbackClock.ResetStatics() 不存在，静态复位保险 1/3 已丢失。");

            RuntimeInitializeOnLoadMethodAttribute attr =
                (RuntimeInitializeOnLoadMethodAttribute)System.Attribute.GetCustomAttribute(
                    mi, typeof(RuntimeInitializeOnLoadMethodAttribute));

            Assert.IsNotNull(attr,
                "ResetStatics 缺少 [RuntimeInitializeOnLoadMethod]，"
                + "Domain Reload 关闭时静态 Frozen 会跨 PlayMode 存活 → 开局即全局冻结。");

            Assert.AreEqual(RuntimeInitializeLoadType.SubsystemRegistration, attr.loadType,
                "复位必须挂在 SubsystemRegistration（最早一档）。挂在更晚的档位会留出"
                + "'第一个 Awake 已经读到脏 Frozen'的窗口。");
        }

        // =====================================================================
        // 二、HitFeedbackDirector —— Frozen 生命周期与防死锁
        //
        // 【为什么这一节只测显式收敛，不测倒计时】见文件头 EditMode 约束第 2 条：
        // Time.frameCount 不推进 ⇒ TickHitstop 的"起帧不扣"分支恒成立 ⇒ 永不递减。
        // =====================================================================

        /// <summary>命中会置起静态闸门 —— 这是后面所有"能不能放开"用例的前提。</summary>
        [Test]
        public void Director_OnHitFeedback_RaisesFrozenGate()
        {
            GameObject host = NewGo("Director");
            HitFeedbackDirector dir = host.AddComponent<HitFeedbackDirector>();
            Combatant enemy = NewCombatant(1, Faction.Enemy, 22.0f, 12.0f);

            dir.OnHitFeedback(null, enemy, 10.0f);

            Assert.IsTrue(FeedbackClock.Frozen,
                "一次有效命中之后 FeedbackClock.Frozen 应为 true（hitstop 已起）。"
                + "为 false 说明 KickHitstop 被去重/冷却/强度系数误吃掉了。");
        }

        /// <summary>ClearAll() 必须放开闸门 —— 暂停 / 终局 / 拆线都靠它。</summary>
        [Test]
        public void Director_ClearAll_ReleasesFrozenGate()
        {
            GameObject host = NewGo("Director");
            HitFeedbackDirector dir = host.AddComponent<HitFeedbackDirector>();
            Combatant enemy = NewCombatant(1, Faction.Enemy, 22.0f, 12.0f);
            dir.OnHitFeedback(null, enemy, 10.0f);

            dir.ClearAll();

            Assert.IsFalse(FeedbackClock.Frozen,
                "ClearAll() 之后 Frozen 必须为 false。它是暂停/终局/拆线三条路径的共同收敛点，"
                + "留 true 就是暂停面板背后整个画面被永久按住。");
        }

        /// <summary>
        /// 禁用组件必须放开闸门（保险 2/3 的 OnDisable 分支）。
        /// 组件一禁用 LateUpdate 就不再执行，_stopRemain 永远没人递减 ——
        /// 此时若 Frozen 还是 true，画面与死机无异。
        /// </summary>
        [Test]
        public void Director_Disable_ReleasesFrozenGate()
        {
            GameObject host = NewGo("Director");
            HitFeedbackDirector dir = host.AddComponent<HitFeedbackDirector>();
            Combatant enemy = NewCombatant(1, Faction.Enemy, 22.0f, 12.0f);
            dir.OnHitFeedback(null, enemy, 10.0f);
            Assert.IsTrue(FeedbackClock.Frozen, "前置条件不成立：命中后应处于顿帧中。");

            dir.enabled = false;   // 触发 OnDisable → ClearAll → ClearHitstop

            Assert.IsFalse(FeedbackClock.Frozen,
                "Director 被禁用后 Frozen 必须放开。禁用之后 LateUpdate 不再执行，"
                + "倒计时永远没人递减，Frozen 留 true = 表现层永久冻结。");
        }

        /// <summary>
        /// 销毁宿主对象必须放开闸门（保险 2/3 的 OnDestroy 分支）。
        /// 覆盖"换场景 / 按 R 重开时正卡在顿帧里"这条路径。
        /// </summary>
        [Test]
        public void Director_DestroyHost_ReleasesFrozenGate()
        {
            GameObject host = NewGo("Director");
            HitFeedbackDirector dir = host.AddComponent<HitFeedbackDirector>();
            Combatant enemy = NewCombatant(1, Faction.Enemy, 22.0f, 12.0f);
            dir.OnHitFeedback(null, enemy, 10.0f);
            Assert.IsTrue(FeedbackClock.Frozen, "前置条件不成立：命中后应处于顿帧中。");

            Object.DestroyImmediate(host);
            _spawned.Remove(host);

            Assert.IsFalse(FeedbackClock.Frozen,
                "销毁 Director 宿主后 Frozen 必须放开。销毁后再没有任何人递减倒计时，"
                + "下一局开局就会全局冻结（FeedbackClock 文件头点名的失效模式）。");
        }

        /// <summary>
        /// 重新启用组件时必须先把闸门摆干净（保险 2/3 的 OnEnable 分支）。
        /// 防的是"上一次禁用时留下的脏 Frozen 被下一次启用继承"。
        /// </summary>
        [Test]
        public void Director_Enable_ClearsStaleFrozenGate()
        {
            GameObject host = NewGo("Director");
            HitFeedbackDirector dir = host.AddComponent<HitFeedbackDirector>();
            dir.enabled = false;

            // 模拟"闸门被外部留成脏值"，然后重新启用。
            FeedbackClock.Frozen = true;
            dir.enabled = true;   // 触发 OnEnable → ClearHitstop

            Assert.IsFalse(FeedbackClock.Frozen,
                "OnEnable 必须清掉可能残留的脏 Frozen。这是 Domain Reload 关闭时"
                + "静态字段跨 PlayMode 存活的兜底（对标 MainMenuHud.SkipOnNextLoad 的前车之鉴）。");
        }

        /// <summary>
        /// ★★★ 预期失败 —— 这条用例在指认一个真实缺陷，不是测试写错了。
        ///
        /// 【场景】C# 委托**不认识** MonoBehaviour 的 enabled / activeInHierarchy。
        /// CombatBridge 的退订只发生在 OnDestroy(TeardownHitFeedback)，所以只要
        /// Director 被"禁用但未销毁"（组件 enabled=false，或宿主 SetActive(false)），
        /// 订阅关系依然在：内核继续 Tick → HitFeedback 事件照常打到 OnHitFeedback →
        /// KickHitstop 把 FeedbackClock.Frozen 置 true → 而 LateUpdate 已经不执行了，
        /// _stopRemain 永远没人递减 ⇒ **整个表现层永久冻结**。
        ///
        /// 【当前源码为什么挡不住】HitFeedbackDirector.OnHitFeedback（:309-341）
        /// 只判了 defender==null 与 _bridge.IsGameplayBlocked，**没有**任何
        /// isActiveAndEnabled 守卫；OnEnemyDied（:356-378）同理。
        ///
        /// 【建议修法】两个入口各加一行：`if (!isActiveAndEnabled) return;`
        /// 详见 docs/p1-2-qa-report.md [P1-01]。
        /// 路由：Engineer 改源码。**不要**为了变绿而删掉这条用例。
        /// </summary>
        [Test]
        public void Frozen_StaysReleased_WhenDisabledDirectorStillReceivesEvent()
        {
            GameObject host = NewGo("Director");
            HitFeedbackDirector dir = host.AddComponent<HitFeedbackDirector>();
            Combatant enemy = NewCombatant(1, Faction.Enemy, 22.0f, 12.0f);

            dir.enabled = false;   // OnDisable → ClearAll，此刻闸门是干净的
            Assert.IsFalse(FeedbackClock.Frozen, "前置条件不成立：禁用后闸门本应是干净的。");

            // 委托不认 enabled，事件照样打进来。
            dir.OnHitFeedback(null, enemy, 10.0f);

            Assert.IsFalse(FeedbackClock.Frozen,
                "【P1-01 死锁风险】Director 已禁用（LateUpdate 不再执行、倒计时无人递减），"
                + "但 OnHitFeedback 仍把 FeedbackClock.Frozen 置成了 true —— 这会导致表现层"
                + "永久冻结。修法：在 OnHitFeedback / OnEnemyDied 入口各加一行 "
                + "`if (!isActiveAndEnabled) return;`（HitFeedbackDirector.cs:309 与 :356）。");
        }

        /// <summary>
        /// 强度系数为 0（无障碍全关）时不得冻任何一帧。
        /// R-09 语义："整体调弱"必须能一路调到"完全关闭"。
        /// </summary>
        [Test]
        public void Director_ZeroIntensity_NeverFreezes()
        {
            HitFeedbackConfig.FeedbackIntensity = 0.0f;

            GameObject host = NewGo("Director");
            HitFeedbackDirector dir = host.AddComponent<HitFeedbackDirector>();
            Combatant enemy = NewCombatant(1, Faction.Enemy, 22.0f, 12.0f);

            dir.OnHitFeedback(null, enemy, 10.0f);

            Assert.IsFalse(FeedbackClock.Frozen,
                "FeedbackIntensity = 0 等价于'关闭反馈'，此时不该产生任何顿帧。"
                + "为 true 说明 KickHitstop 的 sec<=0 早退分支没生效。");
        }

        /// <summary>
        /// 击杀强调同样受全局强度系数约束（R-09）。
        /// 固定档最容易被写成"不看系数"，那会让无障碍全关失效。
        /// </summary>
        [Test]
        public void Director_ZeroIntensity_KillEmphasisAlsoSilent()
        {
            HitFeedbackConfig.FeedbackIntensity = 0.0f;

            GameObject host = NewGo("Director");
            HitFeedbackDirector dir = host.AddComponent<HitFeedbackDirector>();
            Combatant enemy = NewCombatant(2, Faction.Enemy, 22.0f, 0.0f);

            dir.OnEnemyDied(enemy);

            Assert.IsFalse(FeedbackClock.Frozen,
                "FeedbackIntensity = 0 时击杀强调也必须完全静默，"
                + "否则'无障碍全关'会漏掉 R-07 这一档。");
        }

        /// <summary>defender 为 null 时必须安全早退，不得起顿帧、不得抛异常。</summary>
        [Test]
        public void Director_NullDefender_IsSafeNoop()
        {
            GameObject host = NewGo("Director");
            HitFeedbackDirector dir = host.AddComponent<HitFeedbackDirector>();

            Assert.DoesNotThrow(delegate { dir.OnHitFeedback(null, null, 10.0f); },
                "defender 为 null 时 OnHitFeedback 不得抛异常（DOT/环境伤害结算可能传 null）。");

            Assert.IsFalse(FeedbackClock.Frozen,
                "空受击方不该产生任何顿帧。");
        }

        /// <summary>
        /// HpMax 为 0（实体刚构造完还没填数值）不得算出 NaN。
        /// NaN 会一路污染到屏震幅度，相机位置一旦变成 NaN 就再也回不来。
        /// </summary>
        [Test]
        public void Director_ZeroHpMax_DoesNotProduceNaN()
        {
            GameObject host = NewGo("Director");
            HitFeedbackDirector dir = host.AddComponent<HitFeedbackDirector>();
            Combatant broken = NewCombatant(3, Faction.Enemy, 0.0f, 0.0f);

            Assert.DoesNotThrow(delegate { dir.OnHitFeedback(null, broken, 5.0f); },
                "HpMax = 0 时分档不得抛异常（除零保护在 Grade() 里）。");

            // 闸门是 bool，NaN 污染的直接可观测后果是"顿帧起不来或起得莫名其妙"。
            // 这里只断言流程走通且状态可收敛。
            dir.ClearAll();
            Assert.IsFalse(FeedbackClock.Frozen,
                "HpMax = 0 的脏输入之后，ClearAll 仍必须能把闸门收干净。");
        }

        // =====================================================================
        // 三、DamagePopupLayer —— 池、合并、A-7/A-8 的飘字半边
        // =====================================================================

        /// <summary>
        /// 池必须建满 PopupCapacity 个常驻槽位，且初始全部熄灭。
        /// ★ 本用例是 includeInactive 陷阱的正面示范：不带 true 会数出 0。
        /// </summary>
        [Test]
        public void Popup_Build_CreatesCapacitySlots_AllInactive()
        {
            GameObject host = NewGo("PopupLayer");
            DamagePopupLayer layer = host.AddComponent<DamagePopupLayer>();

            Text[] all = layer.GetComponentsInChildren<Text>(true);

            Assert.AreEqual(HitFeedbackConfig.PopupCapacity, all.Length,
                "飘字池必须建出 HitFeedbackConfig.PopupCapacity 个常驻 Text 槽位。"
                + "数量不符说明 Build() 的循环上界与容量常量脱钩了。"
                + "（若这里数到 0，八成是漏了 GetComponentsInChildren 的 includeInactive:true。）");

            Assert.AreEqual(0, CountActivePopups(layer),
                "刚建好时不该有任何槽位处于显示状态。");
        }

        /// <summary>飘字 Canvas 必须压在 HUD(100) 之上，否则瞬时信息会被血条盖住。</summary>
        [Test]
        public void Popup_Canvas_SortsAboveHud()
        {
            GameObject host = NewGo("PopupLayer");
            DamagePopupLayer layer = host.AddComponent<DamagePopupLayer>();

            Canvas canvas = layer.GetComponentInChildren<Canvas>(true);

            Assert.IsNotNull(canvas, "飘字层必须自带独立 Canvas（不与 HUD 共用，避免每帧连带重建血条/技能栏）。");
            Assert.Greater(canvas.sortingOrder, 100,
                "飘字 Canvas 的 sortingOrder 必须高于 Hud 的 100，否则伤害数字会被血条压住。");
        }

        /// <summary>非法伤害值（0 / 负 / NaN / Inf）一律不得点亮槽位（A-10）。</summary>
        [Test]
        public void Popup_RejectsInvalidDamage()
        {
            NewMainCamera();
            GameObject host = NewGo("PopupLayer");
            DamagePopupLayer layer = host.AddComponent<DamagePopupLayer>();

            layer.Push(1, Vector3.zero, 0.0f, Color.white, false);
            layer.Push(2, Vector3.zero, -5.0f, Color.white, false);
            layer.Push(3, Vector3.zero, float.NaN, Color.white, false);
            layer.Push(4, Vector3.zero, float.PositiveInfinity, Color.white, false);

            Assert.AreEqual(0, CountActivePopups(layer),
                "0 / 负数 / NaN / Infinity 伤害都不得产生飘字：A-10 要求飘字恒为非负整数，"
                + "而 NaN 一旦写进 anchoredPosition，该槽位会永久失效。");
        }

        /// <summary>正常伤害要点亮一条，且文本是四舍五入后的整数（A-10）。</summary>
        [Test]
        public void Popup_Push_ShowsRoundedIntegerText()
        {
            NewMainCamera();
            GameObject host = NewGo("PopupLayer");
            DamagePopupLayer layer = host.AddComponent<DamagePopupLayer>();

            layer.Push(1, Vector3.zero, 12.4f, Color.white, false);

            Assert.AreEqual(1, CountActivePopups(layer),
                "一次合法 Push 应恰好点亮一个槽位。为 0 通常是 Camera.main 缺失导致 TryPlace 失败。");
            Assert.AreEqual("12", FirstActivePopupText(layer),
                "A-10：飘字必须是四舍五入后的整数字符串，12.4 应显示为 '12'。");
        }

        /// <summary>
        /// 合并窗口内同目标的多次结算累加进同一条，而不是各占一个槽位。
        /// 断言 "20+14=34" 与字面量比较，不与任何被测量自身比较。
        /// </summary>
        [Test]
        public void Popup_SameTargetWithinWindow_MergesIntoOneSlot()
        {
            NewMainCamera();
            GameObject host = NewGo("PopupLayer");
            DamagePopupLayer layer = host.AddComponent<DamagePopupLayer>();

            layer.Push(7, Vector3.zero, 20.0f, Color.white, false);
            layer.Push(7, Vector3.zero, 14.0f, Color.white, false);

            Assert.AreEqual(1, CountActivePopups(layer),
                "合并窗口内同一目标的两次命中必须累加进同一条飘字，"
                + "各占一条会让连击时数字糊成一团。");
            Assert.AreEqual("34", FirstActivePopupText(layer),
                "合并后的文本应是两次伤害之和（20 + 14 = 34）。");
        }

        /// <summary>不同目标不得被合并 —— 否则伤害会算到别人头上。</summary>
        [Test]
        public void Popup_DifferentTargets_DoNotMerge()
        {
            NewMainCamera();
            GameObject host = NewGo("PopupLayer");
            DamagePopupLayer layer = host.AddComponent<DamagePopupLayer>();

            layer.Push(1, Vector3.zero, 10.0f, Color.white, false);
            layer.Push(2, Vector3.zero, 10.0f, Color.white, false);

            Assert.AreEqual(2, CountActivePopups(layer),
                "不同受击方必须各占一条飘字。合并成一条意味着把 A 的伤害显示到了 B 身上。");
        }

        /// <summary>
        /// ★ 池耗尽：同屏飘字数**永不**超过 PopupCapacity。
        /// 清场时一秒十几个飘字是常态，超限会把屏幕糊死。
        /// </summary>
        [Test]
        public void Popup_PoolNeverExceedsCapacity_UnderBurst()
        {
            NewMainCamera();
            GameObject host = NewGo("PopupLayer");
            DamagePopupLayer layer = host.AddComponent<DamagePopupLayer>();

            // 每个 target 都不同 ⇒ 全部走"取新槽位"分支，逼满环形池再溢出一圈。
            for (int i = 0; i < HitFeedbackConfig.PopupCapacity * 3; i++)
            {
                layer.Push(100 + i, Vector3.zero, 5.0f + i, Color.white, false);
            }

            Assert.LessOrEqual(CountActivePopups(layer), HitFeedbackConfig.PopupCapacity,
                "同屏飘字数不得超过 HitFeedbackConfig.PopupCapacity（定长环形池的硬上限）。"
                + "超出说明环形游标没有正确覆盖最旧的槽位。");
        }

        /// <summary>
        /// 【A-7 飘字半边】FreezeAll(true) 是"冻结"，绝不能顺手把飘字清掉。
        /// 菜单暂停常发生在"刚打出一个大数字想看清楚"的时刻，清掉就是信息损失。
        /// </summary>
        [Test]
        public void Popup_FreezeAll_KeepsSlotsAlive_A7()
        {
            NewMainCamera();
            GameObject host = NewGo("PopupLayer");
            DamagePopupLayer layer = host.AddComponent<DamagePopupLayer>();

            layer.Push(1, Vector3.zero, 10.0f, Color.white, false);
            layer.Push(2, Vector3.zero, 20.0f, Color.white, false);
            layer.Push(3, Vector3.zero, 30.0f, Color.white, false);
            Assert.AreEqual(3, CountActivePopups(layer), "前置条件不成立：应有 3 条飘字在显示。");

            layer.FreezeAll(true);

            Assert.AreEqual(3, CountActivePopups(layer),
                "A-7：菜单暂停时飘字必须'冻结但不清除'。数量变少说明 FreezeAll 误走了清除路径。");
        }

        /// <summary>
        /// 【A-8 飘字半边】ClearAll() 必须把 12 条全部熄灭，结算面板上零残留。
        /// </summary>
        [Test]
        public void Popup_ClearAll_LeavesNothingOnScreen_A8()
        {
            NewMainCamera();
            GameObject host = NewGo("PopupLayer");
            DamagePopupLayer layer = host.AddComponent<DamagePopupLayer>();

            for (int i = 0; i < 5; i++)
            {
                layer.Push(200 + i, Vector3.zero, 10.0f + i, Color.white, false);
            }
            Assert.Greater(CountActivePopups(layer), 0, "前置条件不成立：应有飘字在显示。");

            layer.ClearAll();

            Assert.AreEqual(0, CountActivePopups(layer),
                "A-8：终局收敛后结算面板上不得残留任何飘字。");
        }

        /// <summary>
        /// 冻结中再 ClearAll，必须既清干净、又能继续接受新飘字。
        ///
        /// 【本用例证明力的边界 —— 说清楚免得被当成更强的保证】
        /// Push() 本身**不读** _frozen，所以这里只能证明"清空后 Push 仍能点亮槽位"，
        /// **不能**证明 ClearAll 真的把 _frozen 解开了。后者要看解冻后飘字是否继续
        /// 推进寿命，而寿命推进依赖 LateUpdate —— EditMode 不驱动它。
        /// 真正的"解冻后接着播"归 PlayMode / 本地目视，见 QA 报告 A-7 条目。
        /// </summary>
        [Test]
        public void Popup_ClearAll_WhileFrozen_StillAcceptsNewPush()
        {
            NewMainCamera();
            GameObject host = NewGo("PopupLayer");
            DamagePopupLayer layer = host.AddComponent<DamagePopupLayer>();

            layer.Push(1, Vector3.zero, 10.0f, Color.white, false);
            layer.FreezeAll(true);
            layer.ClearAll();

            Assert.AreEqual(0, CountActivePopups(layer), "ClearAll 应先清空。");

            layer.Push(2, Vector3.zero, 15.0f, Color.white, false);

            Assert.AreEqual(1, CountActivePopups(layer),
                "ClearAll 之后飘字层必须仍能接受新的 Push 并点亮槽位。收不进来说明 ClearAll "
                + "把池本身弄坏了（例如误清了 _slots 或让游标越界）。");
        }

        /// <summary>
        /// 组件被禁用时必须自己收干净 —— 否则那 12 个 Text 会永久挂在屏幕上
        /// （禁用后 LateUpdate 不再执行，寿命永远走不完）。
        /// </summary>
        [Test]
        public void Popup_Disable_ClearsEverything()
        {
            NewMainCamera();
            GameObject host = NewGo("PopupLayer");
            DamagePopupLayer layer = host.AddComponent<DamagePopupLayer>();

            layer.Push(1, Vector3.zero, 10.0f, Color.white, false);
            Assert.AreEqual(1, CountActivePopups(layer), "前置条件不成立。");

            layer.enabled = false;   // 触发 OnDisable → ClearAll

            Assert.AreEqual(0, CountActivePopups(layer),
                "飘字层被禁用后必须自行清空。留着的话 LateUpdate 已不执行，"
                + "这些数字会永久停在屏幕上。");
        }

        // =====================================================================
        // 四、CameraShake × CameraFollow —— A-6 / R-02 无累计漂移
        // =====================================================================

        /// <summary>建一台装好 Follow + Shake 的相机。注意顺序：Follow 必须先加。</summary>
        private void BuildShakeRig(out CameraFollow follow, out CameraShake shake, out GameObject go)
        {
            go = NewGo("ShakeCam");
            go.transform.position = new Vector3(5.0f, 3.0f, -100.0f);
            go.AddComponent<Camera>();

            // CameraShake.Awake 会 GetComponent<CameraFollow>() 兜底，
            // 所以 Follow 必须先于 Shake 挂上，否则拿不到引用。
            follow = go.AddComponent<CameraFollow>();
            shake = go.AddComponent<CameraShake>();
            shake.Configure(follow);
        }

        /// <summary>
        /// ★ A-6 / R-02 核心：连续 20 次屏震之后，权威中心 BaseCenter 一位不动。
        /// 这条直接验证"屏震只读 BaseCenter、绝不回写"这个不变量 ——
        /// 一旦屏震回写了中心，累计漂移就无法避免。
        /// 断言用的是**开震之前捕获的快照**，不是任何被测量的当前值。
        /// </summary>
        [Test]
        public void CameraShake_NeverWritesBackAuthoritativeCenter_A6()
        {
            CameraFollow follow;
            CameraShake shake;
            GameObject go;
            BuildShakeRig(out follow, out shake, out go);

            Vector3 center0 = follow.BaseCenter;

            for (int i = 0; i < 20; i++)
            {
                shake.Kick(HitFeedbackConfig.ShakeAmpPlayerHeavy, HitFeedbackConfig.ShakeDurPlayerHeavy);
                shake.StopAndRecenter();
            }

            Assert.AreEqual(center0.x, follow.BaseCenter.x, 1e-5f,
                "连续 20 次屏震后 CameraFollow.BaseCenter.x 发生了变化 —— 屏震回写了权威中心，"
                + "这会累积成 R-02 明令禁止的相机漂移。");
            Assert.AreEqual(center0.y, follow.BaseCenter.y, 1e-5f,
                "连续 20 次屏震后 CameraFollow.BaseCenter.y 发生了变化 —— 同上，屏震污染了跟随基准。");
        }

        /// <summary>
        /// ★ A-6：屏震收敛后相机必须精确回到起震前的位置，不留残余偏移。
        /// </summary>
        [Test]
        public void CameraShake_RecentersExactly_AfterRepeatedKicks_A6()
        {
            CameraFollow follow;
            CameraShake shake;
            GameObject go;
            BuildShakeRig(out follow, out shake, out go);

            Vector3 pos0 = go.transform.position;

            for (int i = 0; i < 20; i++)
            {
                shake.Kick(HitFeedbackConfig.ShakeAmpEnemyHeavy, HitFeedbackConfig.ShakeDurEnemyHeavy);
                shake.StopAndRecenter();
            }

            Assert.AreEqual(pos0.x, go.transform.position.x, 1e-5f,
                "20 次屏震回正后相机 x 没有回到原位，存在累计漂移（A-6 一票否决项）。");
            Assert.AreEqual(pos0.y, go.transform.position.y, 1e-5f,
                "20 次屏震回正后相机 y 没有回到原位，存在累计漂移（A-6 一票否决项）。");
            Assert.AreEqual(pos0.z, go.transform.position.z, 1e-5f,
                "屏震不得改变相机 Z（取景距离），动了会改变正交裁剪范围。");
        }

        /// <summary>StopAndRecenter 之后必须处于"没有抖动在进行"的状态。</summary>
        [Test]
        public void CameraShake_StopAndRecenter_EndsShaking()
        {
            CameraFollow follow;
            CameraShake shake;
            GameObject go;
            BuildShakeRig(out follow, out shake, out go);

            shake.Kick(0.3f, 0.2f);
            Assert.IsTrue(shake.IsShaking, "前置条件不成立：Kick 之后应处于抖动中。");

            shake.StopAndRecenter();

            Assert.IsFalse(shake.IsShaking,
                "StopAndRecenter() 之后不得仍处于抖动中。A-7/A-8 要求暂停面板与结算界面上镜头静止回正。");
        }

        /// <summary>非法参数的 Kick 必须被丢弃，不得进入抖动状态。</summary>
        [Test]
        public void CameraShake_RejectsNonPositiveKick()
        {
            CameraFollow follow;
            CameraShake shake;
            GameObject go;
            BuildShakeRig(out follow, out shake, out go);

            shake.Kick(0.0f, 0.2f);
            Assert.IsFalse(shake.IsShaking, "幅度为 0 的 Kick 应被丢弃（FeedbackIntensity=0 时会走到这里）。");

            shake.Kick(0.3f, 0.0f);
            Assert.IsFalse(shake.IsShaking, "时长为 0 的 Kick 应被丢弃，否则会除零得到 NaN 偏移。");

            shake.Kick(-1.0f, 0.2f);
            Assert.IsFalse(shake.IsShaking, "负幅度的 Kick 应被丢弃。");
        }

        /// <summary>
        /// 组件禁用时必须回正（禁用后 LateUpdate 不再执行，
        /// 相机会永久停在最后那一帧的偏移位上）。
        /// </summary>
        [Test]
        public void CameraShake_Disable_Recenters()
        {
            CameraFollow follow;
            CameraShake shake;
            GameObject go;
            BuildShakeRig(out follow, out shake, out go);

            Vector3 pos0 = go.transform.position;
            shake.Kick(0.3f, 0.2f);

            shake.enabled = false;   // 触发 OnDisable → StopAndRecenter

            Assert.IsFalse(shake.IsShaking, "禁用后不得仍处于抖动中。");
            Assert.AreEqual(pos0.x, go.transform.position.x, 1e-5f,
                "屏震组件被禁用后相机必须回正，否则会永久停在偏移位上。");
        }

        /// <summary>
        /// ClampPoint 必须是纯函数：反复调用不得改变权威中心。
        /// CameraShake 每帧都要调它，一旦有副作用就是每帧一次的漂移源。
        /// </summary>
        [Test]
        public void CameraFollow_ClampPoint_IsPure()
        {
            CameraFollow follow;
            CameraShake shake;
            GameObject go;
            BuildShakeRig(out follow, out shake, out go);

            Vector3 center0 = follow.BaseCenter;

            for (int i = 0; i < 20; i++)
            {
                follow.ClampPoint(new Vector3(i * 3.0f, -i * 2.0f, -100.0f));
            }

            Assert.AreEqual(center0.x, follow.BaseCenter.x, 1e-5f,
                "ClampPoint 是只读纯函数，调用它不得改动 BaseCenter。有副作用即为每帧漂移源。");
            Assert.AreEqual(center0.y, follow.BaseCenter.y, 1e-5f,
                "同上（y 分量）。");
        }

        // =====================================================================
        // 五、HitFeedbackConfig —— 跨文件常量不变量
        //
        // 【为什么这些不是恒真断言】每条断言的左右两侧来自**不同责任方**
        // （本表 vs HudSkillBar / CombatView，或两个独立调校的常量），
        // 任何一方单独改动都会打破关系并被这里抓住。
        // =====================================================================

        /// <summary>
        /// A-9：HUD 避让的落点必须真的高于技能栏顶边。
        /// 左侧是飘字表的常量，右侧是技能栏自己的两个常量 —— 不同文件、不同责任方。
        /// </summary>
        [Test]
        public void Config_PopupHudSafeY_ClearsSkillBarTop_A9()
        {
            float barTop = HudSkillBar.BottomMargin + HudSkillBar.CellSize;

            Assert.Greater(HitFeedbackConfig.PopupHudSafeY, barTop,
                "A-9：PopupHudSafeY 必须高于 HudSkillBar.BottomMargin + CellSize（技能栏顶边 "
                + barTop.ToString() + "），否则'避让'会把飘字抬到技能栏内部，等于没避。");
        }

        /// <summary>轻重两档的敌人闪白时长必须真的分得开，且与内核 fallback 同源。</summary>
        [Test]
        public void Config_EnemyHeavyFlash_LongerThanDefaultFlash()
        {
            Assert.Greater(CombatView.DefaultFlashDuration, 0.0f,
                "CombatView.DefaultFlashDuration 必须为正 —— 敌人轻击档直接取它。");
            Assert.Greater(HitFeedbackConfig.FlashDurEnemyHeavy, CombatView.DefaultFlashDuration,
                "敌人重击闪白必须比轻击（= CombatView.DefaultFlashDuration）更长，"
                + "否则轻重两档在画面上无法区分。");
        }

        /// <summary>阵营阈值必须真的分成两套（共用一条线会让敌人侧分档退化成常量）。</summary>
        [Test]
        public void Config_HeavyRatio_DiffersByFaction()
        {
            Assert.AreNotEqual(HitFeedbackConfig.HeavyRatio(true), HitFeedbackConfig.HeavyRatio(false),
                "玩家与敌人的重击阈值必须分成两套：敌人基准 Hp 约 22、玩家 260，相差约 11.8 倍，"
                + "共用一条线会让敌人侧的轻/重分档退化成常量。");
        }

        /// <summary>玩家侧第三档（"要命"）必须严格高于重击档（"疼"），US-4 要求可单独感知。</summary>
        [Test]
        public void Config_PlayerCrushingTier_AbovePlayerHeavyTier()
        {
            Assert.Greater(HitFeedbackConfig.ShakeHeavyRatioPlayer, HitFeedbackConfig.HeavyRatioPlayer,
                "US-4：玩家屏震的'要命'档阈值必须高于'疼'档，否则第三档永远够不着或永远命中。");
            Assert.Greater(HitFeedbackConfig.ShakeAmpPlayerHeavy, HitFeedbackConfig.ShakeAmpPlayer,
                "'要命'档的屏震幅度必须大于普通档，否则分档没有任何可感知差异。");
        }

        /// <summary>顿帧上限必须罩得住所有档位 × 最大强度系数，否则上限形同虚设。</summary>
        [Test]
        public void Config_StopMaxSeconds_CoversAllTiers()
        {
            Assert.GreaterOrEqual(HitFeedbackConfig.StopMaxSeconds,
                HitFeedbackConfig.StopPlayer * HitFeedbackConfig.ScaleMax,
                "StopMaxSeconds 必须罩得住'玩家档 × ScaleMax'，否则玩家重击会被上限硬截，分档失真。");

            Assert.GreaterOrEqual(HitFeedbackConfig.StopMaxSeconds,
                HitFeedbackConfig.KillStopSeconds * HitFeedbackConfig.IntensityMax,
                "StopMaxSeconds 必须罩得住'击杀档 × IntensityMax'，否则 R-07 在高强度设置下被截断。");
        }

        /// <summary>去重窗口必须严格短于起始冷却，否则两道闸的语义会互相吞没。</summary>
        [Test]
        public void Config_DedupeWindow_ShorterThanCooldown()
        {
            Assert.Less(HitFeedbackConfig.StopDedupeWindow, HitFeedbackConfig.StopCooldown,
                "同帧去重窗口必须远短于 hitstop 起始冷却：前者管'一次范围技能的多段伤害'，"
                + "后者管'连击不要每下都顿'。窗口 ≥ 冷却会让两道闸的语义互相吞没。");
        }

        /// <summary>飘字三段生命周期的时刻必须严格递增，否则某一段会被压成零长。</summary>
        [Test]
        public void Config_PopupPhases_AreStrictlyOrdered()
        {
            Assert.Less(HitFeedbackConfig.PopupPopInEnd, HitFeedbackConfig.PopupRiseEnd,
                "弹入结束必须早于上飘结束，否则上飘段长度为负，riseK 计算失稳。");
            Assert.Less(HitFeedbackConfig.PopupRiseEnd, HitFeedbackConfig.PopupLifetime,
                "上飘结束必须早于寿命终点，否则淡出段长度为零，飘字会硬切消失。");
            Assert.Less(HitFeedbackConfig.PopupMergeWindow, HitFeedbackConfig.PopupLifetime,
                "合并窗口必须短于寿命，否则一条飘字直到消失前都在吸收新伤害，数字永远读不完。");
        }

        /// <summary>强度系数必须双端可钳 —— 上端防冻死画面，下端支持无障碍全关。</summary>
        [Test]
        public void Config_Intensity_ClampsBothEnds()
        {
            HitFeedbackConfig.FeedbackIntensity = 99.0f;
            Assert.AreEqual(HitFeedbackConfig.IntensityMax, HitFeedbackConfig.Intensity(), 1e-5f,
                "Intensity() 必须把上溢钳到 IntensityMax，否则 FeedbackIntensity=99 会直接把画面"
                + "冻死 StopMaxSeconds 那么久。");

            HitFeedbackConfig.FeedbackIntensity = -5.0f;
            Assert.AreEqual(HitFeedbackConfig.IntensityMin, HitFeedbackConfig.Intensity(), 1e-5f,
                "Intensity() 必须把下溢钳到 IntensityMin（0），负系数会让顿帧时长变负。");
        }

        /// <summary>ScaleOf 的输出必须恒落在 [ScaleMin, ScaleMax]，且随占比单调不减。</summary>
        [Test]
        public void Config_ScaleOf_IsClampedAndMonotonic()
        {
            for (int f = 0; f < 2; f++)
            {
                bool isPlayer = f == 0;
                float prev = float.NegativeInfinity;

                for (int i = 0; i <= 10; i++)
                {
                    float ratio = i / 10.0f;
                    float s = HitFeedbackConfig.ScaleOf(ratio, isPlayer);

                    Assert.GreaterOrEqual(s, HitFeedbackConfig.ScaleMin - 1e-5f,
                        "ScaleOf(" + ratio + ", isPlayer=" + isPlayer + ") 低于 ScaleMin，"
                        + "会让屏震幅度与顿帧时长塌到不可感知。");
                    Assert.LessOrEqual(s, HitFeedbackConfig.ScaleMax + 1e-5f,
                        "ScaleOf(" + ratio + ", isPlayer=" + isPlayer + ") 超过 ScaleMax，"
                        + "会让单次命中的顿帧突破设计上限。");
                    Assert.GreaterOrEqual(s, prev - 1e-5f,
                        "ScaleOf 必须随伤害占比单调不减：ratio=" + ratio + " 处出现回落，"
                        + "意味着'打得更重反而反馈更弱'。");
                    prev = s;
                }
            }
        }

        /// <summary>飘字池容量必须与 PRD 的"同屏 ≤12 条"一致。</summary>
        [Test]
        public void Config_PopupCapacity_MatchesSpec()
        {
            Assert.AreEqual(12, HitFeedbackConfig.PopupCapacity,
                "PRD 规定同屏飘字上限为 12 条。改动此值需同步修订 PRD 与架构文档 §2。");
        }

        // =====================================================================
        // 六、回归护栏 —— 删 PlayHitFlash 那一刀不许伤到探针
        // =====================================================================

        /// <summary>
        /// CombatEventsT3Unity 的两个测试探针必须存续。
        /// 本轮删掉了该文件里那次多余的 view.PlayHitFlash()（重复触发源），
        /// 这条用例守住"顺手把旁边的探针一起删了"这类回归 —— 已有测试在断言它们。
        /// 只做 API 存在性检查，不反射读值。
        /// </summary>
        [Test]
        public void Regression_T3Probes_StillExist()
        {
            System.Type t = typeof(CombatEventsT3Unity);

            Assert.IsNotNull(t.GetProperty("SkillHitCount"),
                "CombatEventsT3Unity.SkillHitCount 探针丢失 —— 已有技能命中用例在断言它。"
                + "删除 PlayHitFlash 时不得连带删除探针。");
            Assert.IsNotNull(t.GetProperty("LastSkillDamage"),
                "CombatEventsT3Unity.LastSkillDamage 探针丢失 —— 已有技能伤害用例在断言它。");
        }

        /// <summary>
        /// 闪白派发的唯一入口签名必须保持 (Color, float, float)。
        /// Director 与 PlayerHitFlash 两条路径共用这一套参数，签名一旦分叉，
        /// 敌我两侧的闪白就会长得不一样。
        /// </summary>
        [Test]
        public void Regression_FlashSignatures_StayAligned()
        {
            System.Type[] sig = new System.Type[] { typeof(Color), typeof(float), typeof(float) };

            Assert.IsNotNull(typeof(CombatView).GetMethod("PlayHitFlash", sig),
                "CombatView.PlayHitFlash(Color,float,float) 丢失 —— Director 的敌人侧派发依赖它。");
            Assert.IsNotNull(typeof(PlayerHitFlash).GetMethod("Play", sig),
                "PlayerHitFlash.Play(Color,float,float) 丢失或签名改变 —— 它必须与 "
                + "CombatView.PlayHitFlash 逐字一致，否则敌我两侧闪白参数会分叉。");
            Assert.IsNotNull(typeof(PlayerHitFlash).GetMethod("StopAndRestore"),
                "PlayerHitFlash.StopAndRestore() 丢失 —— Director.ClearAll 依赖它做终局收敛（A-8）。");
        }
    }
}
