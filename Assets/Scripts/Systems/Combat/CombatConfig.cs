// -----------------------------------------------------------------------------
// CombatConfig.cs —— 战斗常量总表（引擎无关）
//
// 全部数值逐值对齐 godot/scripts/game_config.gd。这里是「手感」的唯一真源：
// 改动任何一个常量都会改变 Godot 原型验收过的战斗节奏，必须同步改
// Tests/t1_selfcheck.py 的 §常量区 并重跑对拍。
//
// 【为什么不放 ScriptableObject】
// ScriptableObject 是 UnityEngine 类型，一旦引入，内核就再也无法 headless 编译
// 与 Python 对拍。数值调优期可以在 Unity 侧做一个 ScriptableObject 覆写层，
// 但它必须住在 Combat/Unity/ 里，通过赋值把数写进内核的可变字段，而不是反过来。
//
// 【禁止事项】不得引用 UnityEngine。
// -----------------------------------------------------------------------------

namespace Xianxia.Combat
{
    /// <summary>战斗内核的全部常量。对齐 <c>game_config.gd</c> / <c>main.gd</c>。</summary>
    public static class CombatConfig
    {
        // =====================================================================
        // 判定尺度（game_config.gd:38-44，由 HITBOX_SCALE = 2.0 派生）
        // =====================================================================

        /// <summary>视觉缩放 → 世界判定的换算系数。</summary>
        public const float HITBOX_SCALE = 2.0f;

        /// <summary>接触伤害判定半径 = 22 × HITBOX_SCALE = 44。敌人→玩家接触伤害的唯一阈值。</summary>
        public const float TOUCH_RANGE = 44.0f;

        // =====================================================================
        // W10 · AI 三态（game_config.gd:175-183）
        // =====================================================================

        /// <summary>牵引距离。超出即脱战回 PATROL。</summary>
        public const float AI_LEASH_DIST = 480.0f;

        /// <summary>进入 STRIKE 的距离阈值。</summary>
        public const float AI_STRIKE_DIST = 160.0f;

        /// <summary>CHASE 侧向偏移的最大角度（±deg）。让多只怪不叠成一条直线。</summary>
        public const float AI_CHASE_OFFSET_DEG = 35.0f;

        /// <summary>CHASE 重新掷偏移角的冷却（秒）。</summary>
        public const float AI_CHASE_REDIR_CD = 1.2f;

        /// <summary>STRIKE 蓄力时长（秒）。此期间敌人不移动，是玩家的反应窗口。</summary>
        public const float AI_STRIKE_WINDUP = 0.35f;

        /// <summary>STRIKE 突进时长（秒）。</summary>
        public const float AI_STRIKE_DASH = 0.25f;

        /// <summary>STRIKE 突进期间的速度倍率。</summary>
        public const float AI_STRIKE_SPEED_MULT = 3.2f;

        /// <summary>STRIKE 突进期间的接触伤害倍率。</summary>
        public const float AI_STRIKE_DMG_MULT = 1.5f;

        /// <summary>一次 STRIKE 结束后的冷却（秒）。</summary>
        public const float AI_STRIKE_CD = 3.5f;

        /// <summary>PATROL 回锚时的速度倍率（enemy.gd:277 的 0.3）。</summary>
        public const float AI_PATROL_SPEED_MULT = 0.3f;

        /// <summary>PATROL 判定「已回到出生点」的距离（enemy.gd:274 的 20.0）。</summary>
        public const float AI_PATROL_ANCHOR_RADIUS = 20.0f;

        // =====================================================================
        // W9 · BOSS 三阶段（game_config.gd:165-172）
        // =====================================================================

        /// <summary>P1→P2 的血量比例阈值。</summary>
        public const float BOSS_PHASE_P2_THRESHOLD = 0.65f;

        /// <summary>P2→P3 的血量比例阈值。</summary>
        public const float BOSS_PHASE_P3_THRESHOLD = 0.30f;

        /// <summary>
        /// 阶段阈值表 [0.65, 0.30]，与 <c>game_config.gd:165</c> 逐值对齐。
        /// 用 static readonly 而非 const（C# 数组不能 const）；调用方**只读**。
        /// </summary>
        public static readonly float[] BOSS_PHASE_THRESHOLDS = { BOSS_PHASE_P2_THRESHOLD, BOSS_PHASE_P3_THRESHOLD };

        /// <summary>P2 速度倍率。</summary>
        public const float BOSS_P2_SPEED_MULT = 1.25f;

        /// <summary>P2 攻击倍率（不提升，仅提速 + 召唤）。</summary>
        public const float BOSS_P2_ATK_MULT = 1.0f;

        /// <summary>P2 召唤冷却（秒）。</summary>
        public const float BOSS_P2_SUMMON_CD = 8.0f;

        /// <summary>P2 单次召唤数量。</summary>
        public const int BOSS_P2_SUMMON_N = 2;

        /// <summary>P3 攻击倍率。</summary>
        public const float BOSS_P3_ATK_MULT = 1.40f;

        /// <summary>P3 速度倍率。</summary>
        public const float BOSS_P3_SPEED_MULT = 1.40f;

        /// <summary>P3 冲击波冷却（秒）。</summary>
        public const float BOSS_P3_SHOCK_CD = 6.0f;

        /// <summary>P3 冲击波半径（像素）。</summary>
        public const float BOSS_P3_SHOCK_RADIUS = 200.0f;

        /// <summary>BOSS 韧性倍率（enemy.gd:149 的 poise_max *= 3.0）。</summary>
        public const float BOSS_POISE_MULT = 3.0f;

        /// <summary>BOSS 经验倍率（main.gd:707 的 exp * 3.0）。</summary>
        public const float BOSS_EXP_MULT = 3.0f;

        /// <summary>R5：BOSS 召唤的小怪相对自身等级的偏移（当前 zone 的 normal 降一级）。</summary>
        public const int BOSS_SUMMON_LEVEL_OFFSET = -1;

        // =====================================================================
        // W8 · 敌人分级（game_config.gd:145-162 + main.gd:52-55）
        // =====================================================================

        /// <summary>敌人血量的每级增幅（main.gd:52）。</summary>
        public const float ENEMY_HP_PER_LEVEL = 0.18f;

        /// <summary>敌人伤害的每级增幅（game_config.gd:147，M6 由 0.08 提到 0.14）。</summary>
        public const float ENEMY_DMG_PER_LEVEL = 0.14f;

        /// <summary>敌人等级下限（main.gd:54）。</summary>
        public const int ENEMY_LEVEL_MIN = 1;

        /// <summary>敌人等级上限（main.gd:55）。</summary>
        public const int ENEMY_LEVEL_MAX = 60;

        /// <summary>敌人等级相对 zone.base_level 的抖动下界（game_config.gd:149）。</summary>
        public const int ENEMY_LEVEL_JITTER_MIN = -1;

        /// <summary>敌人等级相对 zone.base_level 的抖动上界。</summary>
        public const int ENEMY_LEVEL_JITTER_MAX = 2;

        /// <summary>精英出现概率缺省值。</summary>
        public const float ELITE_RATE_DEFAULT = 0.22f;

        /// <summary>精英血量倍率（M6 由 2.4 提到 2.6）。</summary>
        public const float ELITE_HP_MULT = 2.6f;

        /// <summary>精英攻击倍率。</summary>
        public const float ELITE_ATK_MULT = 1.5f;

        /// <summary>精英经验倍率。</summary>
        public const float ELITE_EXP_MULT = 2.0f;

        /// <summary>词缀附加概率缺省值。</summary>
        public const float AFFIX_RATE_DEFAULT = 0.20f;

        /// <summary>词缀 swift「迅捷」：移动速度倍率。</summary>
        public const float AFFIX_SWIFT_SPEED_MULT = 1.35f;

        /// <summary>
        /// 词缀 ironhide「坚甲」：护甲平坦加值。
        /// Godot 里配的是 0.12，spawn_wave 落地时 <c>× 100.0</c>（main.gd:663），
        /// 即实际 +12 护甲。这里直接写最终值 12，避免把那次 ×100 的隐式换算带进 Unity。
        /// </summary>
        public const float AFFIX_IRONHIDE_ARMOR_ADD = 12.0f;

        /// <summary>词缀 blaze「炽焰」：接触伤害倍率。</summary>
        public const float AFFIX_BLAZE_DMG_MULT = 1.25f;

        // =====================================================================
        // R-14 · 霸体 / 硬直 / 击退（game_config.gd:69-99）
        // =====================================================================

        /// <summary>韧性上限缺省值。</summary>
        public const float POISE_MAX_DEFAULT = 30.0f;

        /// <summary>韧性回复速度（点/秒）。</summary>
        public const float POISE_REGEN = 20.0f;

        /// <summary>受击后多久开始回韧（秒）。</summary>
        public const float POISE_REGEN_DELAY = 2.0f;

        /// <summary>
        /// 击退摩擦减速度（像素/秒²）。
        /// 击退距离 dist 对应的初速 v0 = sqrt(2 · KNOCKBACK_FRICTION · dist)，
        /// 这样策划只需配「退多远」，不必理解速度与摩擦的耦合。
        /// </summary>
        public const float KNOCKBACK_FRICTION = 1400.0f;

        /// <summary>击退速度衰减到该值以下即归零（像素/秒）。</summary>
        public const float KNOCKBACK_STOP_SPEED = 1.0f;

        /// <summary>破韧时施加的默认硬直时长（秒）。</summary>
        public const float POISE_BREAK_HITSTUN = 0.20f;

        /// <summary>破韧时施加的默认击退距离（像素）。对齐 KNOCKBACK_DIST["skill_j"] = 24 × 2。</summary>
        public const float POISE_BREAK_KNOCKBACK_DIST = 48.0f;

        // =====================================================================
        // 减法承伤模型（gear.md / enemy.gd:488-494）
        // =====================================================================

        /// <summary>敌人承伤下限。再厚的甲也至少掉 1 点，杜绝「免疫悬崖」。</summary>
        public const float ENEMY_DAMAGE_FLOOR = 1.0f;

        // =====================================================================
        // 敌人基础数值表（main.gd:37-42 ENEMY_SPECS）
        // =====================================================================

        /// <summary>敌人种类 key：血煞。</summary>
        public const string KIND_BLOOD = "blood";

        /// <summary>敌人种类 key：巫蛊。</summary>
        public const string KIND_WITCH = "witch";

        /// <summary>敌人种类 key：剑修。</summary>
        public const string KIND_SWORD = "sword";

        /// <summary>敌人种类 key：丹修。</summary>
        public const string KIND_ALCHEMY = "alchemy";
    }
}
