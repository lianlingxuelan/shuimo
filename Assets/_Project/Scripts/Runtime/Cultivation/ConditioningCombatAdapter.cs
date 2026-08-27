// -----------------------------------------------------------------------------
// ConditioningCombatAdapter.cs —— 调理系统对战斗表现层的最小接线
//
// 战斗内核仍以原始 SkillDef.BeginCast 结算；这里只在“确实已成功施放”的 T3 事件
// 到来后，按本局调理把刚写入的 CD 改为对应帧数。这样不把 Unity 背包类型倒灌进内核。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Combat;

namespace Xianxia.Unity.T2
{
    /// <summary>将当前调理转译为战斗侧的局内加成。</summary>
    [DisallowMultipleComponent]
    public sealed class ConditioningCombatAdapter : MonoBehaviour
    {
        private CombatBridge _bridge;
        private ConditioningController _conditioning;
        private CombatEventsT3Unity _subscribedEvents;
        private float _nextResolveTime;

        private void Update()
        {
            if (_subscribedEvents != null)
            {
                return;
            }
            if (Time.unscaledTime < _nextResolveTime)
            {
                return;
            }
            _nextResolveTime = Time.unscaledTime + 0.25f;
            BindIfReady();
        }

        private void OnDestroy()
        {
            if (_subscribedEvents != null)
            {
                _subscribedEvents.SkillCast -= OnSkillCast;
                _subscribedEvents = null;
            }
        }

        private void BindIfReady()
        {
            if (_bridge == null)
            {
                _bridge = FindObjectOfType<CombatBridge>();
            }
            if (_conditioning == null)
            {
                _conditioning = GetComponent<ConditioningController>();
            }
            CombatEventsT3Unity eventsT3 = _bridge != null ? _bridge.EventsT3 : null;
            if (eventsT3 == null || eventsT3 == _subscribedEvents)
            {
                return;
            }
            if (_subscribedEvents != null)
            {
                _subscribedEvents.SkillCast -= OnSkillCast;
            }
            _subscribedEvents = eventsT3;
            _subscribedEvents.SkillCast += OnSkillCast;
        }

        private void OnSkillCast(Combatant caster, SkillDef def, Vector2 facing)
        {
            if (_bridge == null || _conditioning == null || _conditioning.ActiveKind != ConditioningKind.QingQi)
            {
                return;
            }
            if (caster == null || caster != _bridge.Player || def == null || def.Action == ActionKind.Dodge || caster.Skills == null)
            {
                return;
            }

            IntentSlot slot;
            if (!TryFindSlot(def, out slot))
            {
                return;
            }
            int adjusted = ConditioningRules.AdjustCooldownFrames(def.CooldownFrames, _conditioning.ActiveKind);
            caster.Skills.SetCd((int)slot, adjusted);
        }

        private bool TryFindSlot(SkillDef def, out IntentSlot slot)
        {
            for (int i = 0; i < SkillTable.SlotCount; i++)
            {
                IntentSlot candidate = (IntentSlot)i;
                SkillDef current = _bridge.SkillOf(candidate);
                if (current == def || (current != null && current.Id == def.Id))
                {
                    slot = candidate;
                    return true;
                }
            }
            slot = IntentSlot.Basic;
            return false;
        }
    }
}
