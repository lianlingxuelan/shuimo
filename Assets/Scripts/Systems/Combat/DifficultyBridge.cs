// -----------------------------------------------------------------------------
// DifficultyBridge.cs —— zones.json ⇒ 战斗实体的数值桥（引擎无关）
//
// 【它做三件事】
//   1. 分级缩放：hp_scale = 1 + 0.18·(lv−1)、dmg_scale = 1 + 0.14·(lv−1)
//      精英在 normal 结果上再乘 HP×2.6 / ATK×1.5 / EXP×2.0（main.gd:645-652）
//   2. 词缀：swift(速度×1.35) / ironhide(护甲+12) / blaze(接触伤害×1.25)
//   3. 模型 B 反调：zones.json 里的 atk_mult 只是**种子**，最终 atk_mult_eff
//      由 d_eff 靶子（**玩家** hp_max/65）反解，见 SolveEffectiveAtkMult。
//
// 【模型 B 为什么必须反调】
// 正推（配 atk_mult → 跑一局 → 看死得快不快）在 5 个区 × 60 级的组合上根本收敛不了。
// 反推先锁死"每次该掉多少血"，atk_mult 沦为一个可计算的中间量，
// 于是"这个区难度对不对"变成一次算术校验而不是一场手感玄学。
//
// 【U1 修复：靶子的血是**玩家的**，不是敌人的】
// 模型 B 的推导式 `H/65` 里的 H 自始至终指玩家血上限——A1/A2 两条验收窗口量的是
// 「玩家还能活多少秒」，分子只可能是玩家的血。早先实现误把**敌人** hp_max 喂进
// SolveEffectiveAtkMult，于是靶子退化成「敌人血/65」：一只 30 血的血煞算出
// d_eff≈0.46，被 Difficulty.DamageFloor=1 一把顶成每次固定掉 1 点，
// 玩家单挑要 150+ 秒才倒（目标 35–60s），围攻也完全不构成压力。
// 现在统一改喂 PlayerHpMax，d_eff = 260/65 = 4.0，落回设计窗口。
//
// 副作用（预期且正确）：contactDamage 不再随敌人血量/等级漂移——同一玩家面前
// 每只怪的**有效**每击伤害恒为 playerHp/65，等级差改由「命中频率 + 敌人血厚度
// + atk_mult_eff 反向缩放」体现。等级越高的怪 baseAtk 越大，反解出的 atk_mult_eff
// 就越小，乘积恒定，这是反调模型的定义式行为，不是 bug。
//
// 【禁止事项】不得引用 UnityEngine。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Xianxia.Core;

namespace Xianxia.Combat
{
    /// <summary>
    /// 敌人种类的基础数值。对应 Godot <c>main.gd:37-42</c> 的 <c>ENEMY_SPECS</c>。
    /// 之所以在 Combat 层再定义一份而不是复用 Core：这是**战斗**数值，
    /// 与 zones.json 的区域配置分属两个变更节奏，绑在一起会互相牵连。
    /// </summary>
    public sealed class EnemyBaseStats
    {
        /// <summary>种类 key。</summary>
        public string Kind = CombatConfig.KIND_WITCH;

        /// <summary>中文名。</summary>
        public string DisplayName = string.Empty;

        /// <summary>基础血量。</summary>
        public float Hp = 22.0f;

        /// <summary>基础移动速度（像素/秒）。</summary>
        public float Speed = 95.0f;

        /// <summary>基础接触伤害。</summary>
        public float Damage = 6.0f;

        /// <summary>基础护甲。</summary>
        public float Armor = 1.0f;

        /// <summary>基础经验。</summary>
        public float Exp = 6.0f;

        /// <summary>韧性上限（game_config.gd:70-76 POISE_MAX_BY_KIND）。</summary>
        public float PoiseMax = CombatConfig.POISE_MAX_DEFAULT;

        /// <summary>构造。</summary>
        public EnemyBaseStats() { }

        /// <summary>全字段构造。</summary>
        public EnemyBaseStats(string kind, string displayName, float hp, float speed, float damage, float armor, float exp, float poiseMax)
        {
            Kind = kind;
            DisplayName = displayName;
            Hp = hp;
            Speed = speed;
            Damage = damage;
            Armor = armor;
            Exp = exp;
            PoiseMax = poiseMax;
        }

        /// <summary>浅拷贝（避免多只怪共享同一份被词缀改过的实例）。</summary>
        public EnemyBaseStats Clone()
        {
            return new EnemyBaseStats(Kind, DisplayName, Hp, Speed, Damage, Armor, Exp, PoiseMax);
        }
    }

    /// <summary>
    /// 内置敌人基础数值表。逐值对齐 Godot <c>ENEMY_SPECS</c> 与 <c>POISE_MAX_BY_KIND</c>。
    /// Unity 侧若要改成 ScriptableObject 驱动，只需在启动时覆盖 <see cref="Table"/>。
    /// </summary>
    public static class EnemyBaseTable
    {
        /// <summary>kind ⇒ 基础数值。</summary>
        public static readonly Dictionary<string, EnemyBaseStats> Table = new Dictionary<string, EnemyBaseStats>
        {
            { CombatConfig.KIND_BLOOD,   new EnemyBaseStats(CombatConfig.KIND_BLOOD,   "血煞", 30.0f, 60.0f,  8.0f, 3.0f, 8.0f, 60.0f) },
            { CombatConfig.KIND_WITCH,   new EnemyBaseStats(CombatConfig.KIND_WITCH,   "巫蛊", 22.0f, 95.0f,  6.0f, 1.0f, 6.0f, 24.0f) },
            { CombatConfig.KIND_SWORD,   new EnemyBaseStats(CombatConfig.KIND_SWORD,   "剑修", 28.0f, 80.0f, 10.0f, 4.0f, 7.0f, 34.0f) },
            { CombatConfig.KIND_ALCHEMY, new EnemyBaseStats(CombatConfig.KIND_ALCHEMY, "丹修", 24.0f, 70.0f,  5.0f, 2.0f, 6.0f, 26.0f) },
        };

        /// <summary>按 kind 取基础数值的**副本**。未知 kind 回落到 witch。</summary>
        public static EnemyBaseStats Get(string kind)
        {
            EnemyBaseStats s;
            if (!string.IsNullOrEmpty(kind) && Table.TryGetValue(kind, out s))
            {
                return s.Clone();
            }
            return Table[CombatConfig.KIND_WITCH].Clone();
        }
    }

    /// <summary>
    /// 难度桥：把 zones.json 的区域配置 + 等级 + 随机流，变成一只可战斗的
    /// <see cref="Combatant"/>。
    /// </summary>
    public sealed class DifficultyBridge
    {
        /// <summary>
        /// 反调时假定的玩家防御。默认 0（对拍基线），生产环境由上层写入实际值。
        /// 它只影响 <see cref="SolveEffectiveAtkMult"/> 的 keep 系数。
        /// </summary>
        public float PlayerDef;

        /// <summary>
        /// **平衡基准血量**（不是玩家的实时血上限！），模型 B 的 d_eff 靶子分子
        /// （d_eff = PlayerHpMax / 65）。永久钉死为 260.0f。
        ///
        /// 🚨【N-2 · P1-6 推翻旧口径】这里原本写的是「玩家升级改血上限后，上层必须
        /// 同步写回这里」。那句话**已经作废**，P1-6 之后是**反过来**的：
        /// 上层（<c>CombatBridge.ApplyPlayerDamageModel</c>）现在写入的是常量 260，
        /// **绝不回写玩家实时血上限**。看到这段别再"修复"回去。
        ///
        /// 【为什么反过来了 —— 标尺不能跟着被测量的人一起变】
        /// 这个字段的作用是「假定玩家有多少血」，据此反推怪该打多疼，
        /// 让玩家稳定挨约 4 下死。它是一把**标尺**。
        /// 玩家升级从 260 涨到 494 血，如果标尺跟着涨到 494，
        /// 怪的伤害就同步涨 90% —— 玩家变强的部分被系统原样抵消，白升 9 级。
        /// 所以标尺必须钉死在 1 级基准值 260 上：玩家变强，怪不变，成长才有意义。
        /// 曲线那一侧的同一个 260 写在 <c>ProgressionCurve.BASE_HP_MAX</c>。
        ///
        /// 【为什么不是敌人的 hp_max】见文件头 U1 修复说明：A1/A2 验收窗口量的是玩家
        /// 存活秒数，靶子分子只能是玩家血。用敌人血会让 d_eff 掉到 1 以下被伤害地板吃掉。
        /// </summary>
        public float PlayerHpMax = 260.0f;

        /// <summary>
        /// 基础数值解析器。默认走 <see cref="EnemyBaseTable"/>；
        /// Unity 侧可换成读 ScriptableObject 的实现，内核零改动。
        /// </summary>
        public Func<string, EnemyBaseStats> BaseStatsOf = EnemyBaseTable.Get;

        /// <summary>下一个可用的实体 id。W-CORE 冷却表以它为 key，必须全局唯一。</summary>
        public int NextId = 1;

        /// <summary>取一个新 id。</summary>
        public int AllocId()
        {
            int id = NextId;
            NextId = NextId + 1;
            return id;
        }

        // ---------------------------------------------------------------------
        // 分级缩放公式
        // ---------------------------------------------------------------------

        /// <summary>血量缩放：<c>1 + 0.18·(lv−1)</c>。</summary>
        public static float HpScale(int level)
        {
            return 1.0f + CombatConfig.ENEMY_HP_PER_LEVEL * (level - 1);
        }

        /// <summary>伤害缩放：<c>1 + 0.14·(lv−1)</c>。</summary>
        public static float DmgScale(int level)
        {
            return 1.0f + CombatConfig.ENEMY_DMG_PER_LEVEL * (level - 1);
        }

        /// <summary>把等级夹到 [1, 60]（main.gd 的 clampi）。</summary>
        public static int ClampLevel(int level)
        {
            if (level < CombatConfig.ENEMY_LEVEL_MIN)
            {
                return CombatConfig.ENEMY_LEVEL_MIN;
            }
            return level > CombatConfig.ENEMY_LEVEL_MAX ? CombatConfig.ENEMY_LEVEL_MAX : level;
        }

        // ---------------------------------------------------------------------
        // 模型 B 反调
        // ---------------------------------------------------------------------

        /// <summary>
        /// 反解本区实际生效的 atk_mult。
        ///
        /// <code>
        /// 目标 d_eff = playerHp / 65                            (Difficulty.ComputeDeff)
        /// 实际 d_eff = enemyBaseAtk · atk_mult_eff · keep
        /// keep       = 1 − playerDef / (playerDef + 100)        (Difficulty.MitigationOf)
        /// ⇒ atk_mult_eff = (playerHp / 65) / (enemyBaseAtk · keep)
        /// </code>
        ///
        /// 【<paramref name="enemyBaseAtk"/> 的口径】
        /// 传入的是「已含等级缩放、未含 atk_mult」的伤害，即 <c>base_dmg × dmg_scale</c>。
        /// 把 dmg_scale 折进入参而不是再开一个参数，是为了让这个函数保持
        /// "一个乘积除以另一个乘积"的最简形态——多一个参数就多一处调用方传错的机会。
        /// </summary>
        /// <param name="hpMax">
        /// 参考血量上限（**玩家**）。模型 B 反调 d_eff 的靶子用 <c>playerHp/65</c>，
        /// 调用方应传 <see cref="PlayerHpMax"/>，不要传敌人的 hp_max。
        /// </param>
        /// <param name="enemyBaseAtk">base_dmg × dmg_scale。</param>
        /// <param name="playerDef">玩家防御。</param>
        /// <returns>生效的 atk_mult；入参非法时返回 0。</returns>
        public static float SolveEffectiveAtkMult(float hpMax, float enemyBaseAtk, float playerDef)
        {
            if (enemyBaseAtk <= 0.0f)
            {
                return 0.0f;
            }
            float keep = 1.0f - Difficulty.MitigationOf(playerDef, Difficulty.DefSoftcapK);
            if (keep <= 0.0f)
            {
                return 0.0f;
            }
            float targetDeff = Difficulty.ComputeDeff(hpMax);
            return targetDeff / (enemyBaseAtk * keep);
        }

        // ---------------------------------------------------------------------
        // 随机判定（顺序必须与 Godot spawn_wave 一致，否则同种子长出不同世界）
        // ---------------------------------------------------------------------

        /// <summary>
        /// 精英判定。对齐 <c>main.gd:646</c> 的 <c>rng.randf() &lt; elite_rate</c>。
        /// 注意用 <c>NextFloat()</c> 而非 <c>Chance()</c>：后者在 p≤0 / p≥1 时
        /// **不消耗**随机流，会让同种子下的后续抽取整体错位。
        /// </summary>
        public static bool RollElite(PCG32 rng, float eliteRate)
        {
            if (rng == null)
            {
                return false;
            }
            return rng.NextFloat() < eliteRate;
        }

        /// <summary>
        /// 词缀判定。先掷"是否带词缀"，再等概率从三种里挑一个
        /// （对齐 <c>main.gd:655-659</c>：<c>randf() &lt; affix_rate</c> 后 <c>randi() % 3</c>）。
        /// </summary>
        public static AffixKind RollAffix(PCG32 rng, float affixRate)
        {
            if (rng == null)
            {
                return AffixKind.None;
            }
            if (!(rng.NextFloat() < affixRate))
            {
                return AffixKind.None;
            }
            uint pick = rng.NextUInt() % 3u;
            switch (pick)
            {
                case 0u:
                    return AffixKind.Swift;
                case 1u:
                    return AffixKind.Ironhide;
                default:
                    return AffixKind.Blaze;
            }
        }

        /// <summary>把词缀效果落到实体上（速度改的是 <c>AI.SpeedBase</c>）。</summary>
        public static void ApplyAffix(Combatant c, AffixKind affix)
        {
            if (c == null || affix == AffixKind.None)
            {
                return;
            }
            c.Affix = affix;
            switch (affix)
            {
                case AffixKind.Swift:
                    c.MoveSpeed = c.MoveSpeed * CombatConfig.AFFIX_SWIFT_SPEED_MULT;
                    break;
                case AffixKind.Ironhide:
                    c.Armor = c.Armor + CombatConfig.AFFIX_IRONHIDE_ARMOR_ADD;
                    break;
                case AffixKind.Blaze:
                    c.ContactDamage = c.ContactDamage * CombatConfig.AFFIX_BLAZE_DMG_MULT;
                    break;
            }
        }

        // ---------------------------------------------------------------------
        // 构建实体
        // ---------------------------------------------------------------------

        /// <summary>
        /// 构建一只普通 / 精英敌人。
        /// </summary>
        /// <param name="cfg">zones.json 的 enemies 段。为 null 时用缺省值。</param>
        /// <param name="baseStats">该 kind 的基础数值。</param>
        /// <param name="level">敌人等级（已含 jitter，内部会 clamp 到 [1,60]）。</param>
        /// <param name="rng">随机流，用于精英 / 词缀判定。</param>
        /// <returns>可直接投入 <see cref="Encounter"/> 的实体。</returns>
        public Combatant BuildEnemy(ZoneEnemies cfg, EnemyBaseStats baseStats, int level, PCG32 rng)
        {
            EnemyBaseStats bs = baseStats != null ? baseStats.Clone() : EnemyBaseTable.Get(CombatConfig.KIND_WITCH);

            int lv = ClampLevel(level);
            float hpScale = HpScale(lv);
            float dmgScale = DmgScale(lv);

            float zoneHpMult = cfg != null && cfg.HpMult > 0.0f ? cfg.HpMult : 1.0f;
            float armorAdd = cfg != null ? cfg.ArmorAdd : 0;
            float eliteRate = cfg != null ? cfg.EliteRate : CombatConfig.ELITE_RATE_DEFAULT;
            float affixRate = cfg != null ? cfg.AffixRate : CombatConfig.AFFIX_RATE_DEFAULT;

            // ① normal 数值（设计文档 §3.2）
            float hpMax = bs.Hp * zoneHpMult * hpScale;

            // ② 模型 B 反调：atk_mult 种子作废，用 d_eff 靶子反解真正生效的倍率。
            //    靶子分子是**玩家**血上限（U1 修复），不是这只怪自己的 hpMax。
            float baseAtkScaled = bs.Damage * dmgScale;
            float atkMultEff = SolveEffectiveAtkMult(PlayerHpMax, baseAtkScaled, PlayerDef);
            float contactDamage = baseAtkScaled * atkMultEff;

            Combatant c = Combatant.CreateEnemy(AllocId(), Vec2.Zero, hpMax, contactDamage, bs.Speed);
            c.Kind = bs.Kind;
            c.DisplayName = bs.DisplayName;
            c.Level = lv;
            c.Armor = bs.Armor + armorAdd;
            c.ExpValue = bs.Exp;
            c.PoiseMax = bs.PoiseMax;
            c.Poise = bs.PoiseMax;
            c.AI.Rng = rng;

            // ③ 精英：在 normal 结果上再乘（顺序必须与 Godot 一致）
            if (RollElite(rng, eliteRate))
            {
                c.IsElite = true;
                c.HpMax = c.HpMax * CombatConfig.ELITE_HP_MULT;
                c.Hp = c.HpMax;
                c.ContactDamage = c.ContactDamage * CombatConfig.ELITE_ATK_MULT;
                c.ExpValue = c.ExpValue * CombatConfig.ELITE_EXP_MULT;
            }

            // ④ 词缀
            ApplyAffix(c, RollAffix(rng, affixRate));

            return c;
        }

        /// <summary>
        /// 设计类图签名的重载：从 <paramref name="cfg"/>.Kinds 里按随机流挑一个 kind，
        /// 再走 <see cref="BuildEnemy(ZoneEnemies, EnemyBaseStats, int, PCG32)"/>。
        /// <paramref name="theme"/> 当前仅用于把出生点约束在地图范围内（预留，不改数值）。
        /// </summary>
        public Combatant BuildEnemy(ZoneEnemies cfg, int level, ZoneTheme theme, PCG32 rng)
        {
            string kind = PickKind(cfg, rng);
            Combatant c = BuildEnemy(cfg, BaseStatsOf(kind), level, rng);

            // theme 只用来给一个合法的默认出生点；真正的落点由生成层（Unity 壳 / 测试桩）覆写。
            if (theme != null && theme.Width > 0 && theme.Height > 0)
            {
                c.AI.SpawnPos = c.Position;
            }
            return c;
        }

        /// <summary>
        /// 构建 BOSS。等级 = base_level + boss.level_offset；HP/ATK 再乘 boss 的倍率；
        /// 韧性 ×3.0；AI 换成 <see cref="BossController"/>；不带词缀（设计文档 §3.1）。
        /// </summary>
        /// <param name="cfg">zones.json 的 boss 段。</param>
        /// <param name="zoneEnemies">同区的 enemies 段，用于取 hp_mult / armor_add。</param>
        /// <param name="baseLevel">区域 base_level。</param>
        /// <param name="rng">随机流（BOSS 不摇词缀，仅透传给 AI 的侧向偏移）。</param>
        public Combatant BuildBoss(ZoneBoss cfg, ZoneEnemies zoneEnemies, int baseLevel, PCG32 rng)
        {
            string kind = cfg != null && !string.IsNullOrEmpty(cfg.Kind) ? cfg.Kind : CombatConfig.KIND_BLOOD;
            EnemyBaseStats bs = BaseStatsOf(kind);

            int levelOffset = cfg != null ? cfg.LevelOffset : 3;
            int lv = ClampLevel(baseLevel + levelOffset);
            float hpScale = HpScale(lv);
            float dmgScale = DmgScale(lv);

            float zoneHpMult = zoneEnemies != null && zoneEnemies.HpMult > 0.0f ? zoneEnemies.HpMult : 1.0f;
            float bossHpMult = cfg != null && cfg.HpMult > 0.0f ? cfg.HpMult : 10.0f;
            float bossAtkMult = cfg != null && cfg.AtkMult > 0.0f ? cfg.AtkMult : 1.6f;

            float hpMax = bs.Hp * zoneHpMult * hpScale * bossHpMult;

            // BOSS 同样走模型 B 反调，靶子与杂兵一致取**玩家**血上限（U1 修复），
            // 再乘 boss.atk_mult 作为「首领比杂兵更痛」的显式加成。
            // 这里绝不能拿 BOSS 自己的 hp_max 反调：那会被 hp_mult=10 整整放大十倍，
            // 一次接触伤害就能带走玩家半条命。
            float baseAtkScaled = bs.Damage * dmgScale;
            float atkMultEff = SolveEffectiveAtkMult(PlayerHpMax, baseAtkScaled, PlayerDef);
            float contactDamage = baseAtkScaled * atkMultEff * bossAtkMult;

            Combatant c = new Combatant();
            c.Id = AllocId();
            c.Faction = Faction.Enemy;
            c.Kind = kind;
            c.DisplayName = cfg != null && !string.IsNullOrEmpty(cfg.DisplayName) ? cfg.DisplayName : "首领";
            c.BossId = cfg != null && !string.IsNullOrEmpty(cfg.BossId) ? cfg.BossId : string.Empty;
            c.Level = lv;
            c.IsBoss = true;
            c.HpMax = hpMax;
            c.Hp = hpMax;
            c.Armor = bs.Armor;
            c.ContactDamage = contactDamage;
            c.ExpValue = bs.Exp * CombatConfig.BOSS_EXP_MULT;
            c.PoiseMax = bs.PoiseMax * CombatConfig.BOSS_POISE_MULT;
            c.Poise = c.PoiseMax;

            BossController boss = new BossController(c);
            boss.SpeedBase = bs.Speed;
            boss.Rng = rng;
            c.AI = boss;

            return c;
        }

        /// <summary>
        /// 从 <c>cfg.Kinds</c> 里等概率挑一个 kind（对齐 <c>main.gd:612</c> 的 <c>randi() % size</c>）。
        /// 列表为空时回落到 witch。
        /// </summary>
        public static string PickKind(ZoneEnemies cfg, PCG32 rng)
        {
            if (cfg == null || cfg.Kinds == null || cfg.Kinds.Count == 0)
            {
                return CombatConfig.KIND_WITCH;
            }
            if (rng == null)
            {
                return cfg.Kinds[0];
            }
            int idx = (int)(rng.NextUInt() % (uint)cfg.Kinds.Count);
            return cfg.Kinds[idx];
        }

        /// <summary>
        /// 按 zone.base_level + jitter 掷一个敌人等级（对齐 <c>main.gd:599-600</c>）。
        /// </summary>
        public static int RollLevel(int baseLevel, PCG32 rng)
        {
            int jitter = 0;
            if (rng != null)
            {
                jitter = rng.NextRangeInt(CombatConfig.ENEMY_LEVEL_JITTER_MIN, CombatConfig.ENEMY_LEVEL_JITTER_MAX);
            }
            return ClampLevel(baseLevel + jitter);
        }

        /// <summary>
        /// 校验：给定敌人打在玩家身上的实际 d_eff 是否落在模型 B 的有效区间
        /// <c>[playerHp/73.8, playerHp/58.3]</c> 内。逐区数值验收用。
        ///
        /// 【窗口分母是玩家血】U1 修复后与 <see cref="SolveEffectiveAtkMult"/> 同口径：
        /// 这条校验回答的是「玩家会不会死得太快/太慢」，和敌人自己有多少血无关。
        /// </summary>
        public bool IsDeffInTarget(Combatant enemy)
        {
            if (enemy == null)
            {
                return false;
            }
            float keep = 1.0f - Difficulty.MitigationOf(PlayerDef, Difficulty.DefSoftcapK);
            float deff = enemy.ContactDamage * keep;
            return Difficulty.IsDeffInTarget(deff, PlayerHpMax);
        }
    }
}
