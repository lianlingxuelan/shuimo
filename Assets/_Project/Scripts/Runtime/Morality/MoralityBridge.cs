// -----------------------------------------------------------------------------
// MoralityBridge.cs —— 正魔 / 性格 系统的 Unity 桥接层（asmdef: Xianxia.Unity.T2.Morality）
//
// 【它是什么】
// 把纯逻辑内核（Xianxia.Morality / Xianxia.Personality）接到战斗表现层：
//   1. 订阅 CombatBridge 的战斗事件（CastRequested / EnemyDied），按"克己 vs 拔刀"
//      推进正魔值与五性格；
//   2. 把正魔值换算的防御加成落到玩家 Armor（合法承伤路径，不碰内核推进权）；
//   3. 魔道技能触发反噬 → 走 Player.ApplyEnemyDamage（红线：不直改 HP，由内核承伤）；
//   4. 发布 MoralityChangedEvent / PersonalityChangedEvent 给 HUD 与未来系统；
//   5. 代码内搭极简墨条 + 性格面板（零美术 prefab）。
//
// 【红线守则不破】
//   · 反噬掉血只调 Player.ApplyEnemyDamage，绝不自己改 Hp；
//   · 防御只写玩家 Armor 公有字段（内核承伤模型本来就吃它），不碰 WCore / DamageResolver；
//   · 攻击倍率本周不动——玩家攻击走 AttackController.AttackRaw（不走 Combatant.Atk），
//     挂钩需要武器 / 技能系统接入，留 [⏳]（见 changelog 阶段81）。
//
// 【用法】把本组件挂到 CombatBridge 所在 GameObject 上即可（自动找 CombatBridge）。
// -----------------------------------------------------------------------------

using System;
using UnityEngine;
using UnityEngine.UI;
using Xianxia.Combat;
using Xianxia.Combat.UnityBridge;
using Xianxia.Morality;
using Xianxia.Personality;
using Xianxia.Unity.T2;
using Xianxia.Unity.T2.Core;

namespace Xianxia.Unity.T2.Morality
{
    /// <summary>正魔 / 性格 桥接器。单例式挂在 CombatBridge 同体。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(220)]
    public sealed class MoralityBridge : MonoBehaviour
    {
        [SerializeField] private CombatBridge combatBridge;
        [SerializeField] private MoralityPersonalityConfig configAsset;

        private MoralityManager _morality;
        private PersonalityProfile _personality;
        private MoralityConfig _mCfg;
        private PersonalityConfig _pCfg;
        private Combatant _player;
        private float _baseArmor;
        private System.Random _rng;
        private IntentSlot _lastCastSlot = IntentSlot.Basic;
        private MoralityHud _hud;
        private bool _inited;

        private void Start()
        {
            combatBridge = combatBridge ?? FindObjectOfType<CombatBridge>();
            _mCfg = configAsset != null ? configAsset.ToMoralityConfig() : new MoralityConfig();
            _pCfg = configAsset != null ? configAsset.ToPersonalityConfig() : new PersonalityConfig();
            _morality = new MoralityManager(_mCfg);
            _personality = new PersonalityProfile(_pCfg);
            _rng = new System.Random();
            BuildHud();
            InitIfReady();
        }

        private void Update()
        {
            if (!_inited)
            {
                InitIfReady();
            }
        }

        private void InitIfReady()
        {
            if (_inited || combatBridge == null)
            {
                return;
            }
            Combatant p = combatBridge.Player;
            CombatEventsUnity ev = combatBridge.CombatEvents;
            if (p == null || ev == null)
            {
                return;
            }
            _player = p;
            _baseArmor = p.Armor;
            ev.EnemyDied += OnEnemyDied;
            combatBridge.CastRequested += OnCast;
            _inited = true;
            PublishAndApply();
        }

        // ---------------------------------------------------------------------
        // 战斗事件 → 正魔 / 性格
        // ---------------------------------------------------------------------

        private void OnCast(IntentSlot slot)
        {
            if (!_inited)
            {
                return;
            }
            _lastCastSlot = slot;

            if (slot == IntentSlot.Skill1 || slot == IntentSlot.Skill2)
            {
                // 魔道技能 / 杀招：反噬检定（贪婪额外放大伤害）
                if (!_morality.IsEnchanted)
                {
                    float pmExtra = _personality.ComputeCombat().BacklashExtra;
                    BacklashResult res = BacklashResolver.Resolve(
                        _mCfg, _morality.Mo, _player.HpMax, () => (float)_rng.NextDouble());
                    if (res.Triggered)
                    {
                        // 红线：反噬掉血只走内核承伤，不直改 Hp
                        _player.ApplyEnemyDamage(res.Damage * (1.0f + pmExtra));
                    }
                }
                // 魔道值本身在"击杀"时结算（蓝图：杀招 + 击杀 = 魔道涨）
            }
            else if (slot == IntentSlot.Basic)
            {
                // 克己普攻：正道稳 + 悟性+
                _morality.ApplyRestrainedHit();
                _personality.RegisterRestrainedHit();
            }
            // Dodge：中性，不影响正魔 / 性格
            PublishAndApply();
        }

        private void OnEnemyDied(Combatant e)
        {
            if (!_inited || e == null || !e.IsEnemy)
            {
                return;
            }
            bool heavy = _lastCastSlot == IntentSlot.Skill1 || _lastCastSlot == IntentSlot.Skill2;
            if (heavy)
            {
                // 杀招 / 重击 + 击杀 = 魔道涨 + 冲动升 / 隐忍降 / 冷静升
                _morality.ApplyHeavyKill();
                _personality.RegisterKill(true);
            }
            else
            {
                // 克己求存：不额外涨正魔（普攻时已涨），仅冷静+
                _personality.RegisterKill(false);
            }
            PublishAndApply();
        }

        // ---------------------------------------------------------------------
        // 换算 → 玩家 / 事件 / HUD
        // ---------------------------------------------------------------------

        private void PublishAndApply()
        {
            // 正道高 → 防高：对玩家 Armor 加法护甲（合法承伤路径）。
            // 魔道高时 zheng 低 → 护甲自然趋零（呼应"魔道防御低"）。
            float zf = _mCfg.ZhengMax > 0f ? _morality.Zheng / _mCfg.ZhengMax : 0f;
            if (_player != null)
            {
                _player.Armor = _baseArmor + _mCfg.DefenseBonusMax * zf;
            }

            EventManager.Publish(new MoralityChangedEvent
            {
                Zheng = _morality.Zheng,
                Mo = _morality.Mo
            });
            EventManager.Publish(new PersonalityChangedEvent
            {
                Wuxing = _personality.Wuxing,
                Chongdong = _personality.Chongdong,
                Yinren = _personality.Yinren,
                Lengjing = _personality.Lengjing,
                Tanlan = _personality.Tanlan
            });

            if (_hud != null)
            {
                _hud.Refresh(_morality.Route, _morality.Zheng, _morality.Mo,
                    new[] { _personality.Wuxing, _personality.Chongdong, _personality.Yinren,
                            _personality.Lengjing, _personality.Tanlan });
            }
        }

        // ---------------------------------------------------------------------
        // HUD 构建（代码内搭，零美术 prefab）
        // ---------------------------------------------------------------------

        private void BuildHud()
        {
            if (_hud != null)
            {
                return;
            }
            GameObject canvasGo = new GameObject("MoralityCanvas");
            canvasGo.transform.SetParent(transform, false);
            canvasGo.layer = LayerMask.NameToLayer("UI");

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 110; // 压在 Hud(100) 之上

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = Hud.ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            _hud = canvasGo.AddComponent<MoralityHud>();
            _hud.Build(canvasGo.transform);
        }

        // ---------------------------------------------------------------------
        // 退订（谁订阅谁退订，与 CombatBridge.OnDestroy 同款约定）
        // ---------------------------------------------------------------------

        private void OnDestroy()
        {
            if (combatBridge != null)
            {
                if (combatBridge.CombatEvents != null)
                {
                    combatBridge.CombatEvents.EnemyDied -= OnEnemyDied;
                }
                combatBridge.CastRequested -= OnCast;
            }
        }
    }
}
