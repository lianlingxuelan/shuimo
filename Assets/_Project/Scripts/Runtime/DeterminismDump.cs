// -----------------------------------------------------------------------------
// DeterminismDump.cs —— 确定性快照导出（asmdef: Xianxia.Unity.T2，PRD Q13）
//
// 【它解决什么问题】
// 「同种子同世界」是一条没法靠肉眼验收的性质：两次生成的竹海看起来都是竹海。
// 所以把世界压成一份纯文本指纹——种子、地形直方图、逐格散列、怪群花名册——
// 生成两次 diff 一下，一致就是一致，不一致会精确告诉你是地形错位还是怪群错位。
//
// 【为什么散列而不是 dump 全部 9600 格】
// 全量 dump 会产生几十 KB 噪音，diff 出来一屏红根本读不了。逐格滚动散列
// 把地形压成一个 uint，出问题时再看直方图和分块散列定位到区域。
//
// 【导出位置】
// Editor 下写到工程根的 Logs/，跟着工程走、方便直接 git diff；
// 打包运行时写到 persistentDataPath（Editor 下的 Application.dataPath 在包里不存在）。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Xianxia.Combat;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>确定性快照导出器。挂在 Combat 节点上。</summary>
    [DisallowMultipleComponent]
    public sealed class DeterminismDump : MonoBehaviour
    {
        [Header("导出")]
        [Tooltip("世界生成完毕后自动导出一次。关掉可改用右键菜单手动导出。")]
        [SerializeField] private bool dumpOnBuild = true;

        // ---------------------------------------------------------------------
        // T3-T05 确定性护栏
        //
        // 【legacyReportOnly 存在的唯一理由】
        // T1/T2 的基线快照是**逐字节**比对的。只要报告里多出一行，diff 就红成一片，
        // "确定性有没有泄漏"这个问题立刻被淹没在噪声里。
        // 所以 T3 的新字段不是无条件追加，而是走一个独立段落 [t3]，
        // 并且在基线模式下整段不输出 —— 于是基线跑出来的文件与 T2 时代逐字节相同。
        //
        // 【为什么默认是自动判定而不是手动勾】
        // 手动开关意味着"忘了勾"就会污染基线。默认跟随 CombatBridge 的
        // BaselineMode / T3Enabled：T3 没装配就不可能有 T3 字段可写，
        // 这个判断本身就是正确答案，不需要人来记。
        // ---------------------------------------------------------------------

        [Tooltip("强制只输出 T2 口径的段落，不追加 [t3]。留空时按 CombatBridge 是否装配 T3 自动判定。")]
        [SerializeField] private bool legacyReportOnly;

        /// <summary>最近一次导出的文件绝对路径，供 HUD / 日志显示。</summary>
        public string LastDumpPath { get; private set; }

        /// <summary>最近一次导出的正文，供不便读文件的场合（如自动化）直接取用。</summary>
        public string LastDumpText { get; private set; }

        /// <summary>是否在世界生成后自动导出。</summary>
        public bool DumpOnBuild
        {
            get { return dumpOnBuild; }
            set { dumpOnBuild = value; }
        }

        /// <summary>强制只输出 T2 口径段落。</summary>
        public bool LegacyReportOnly
        {
            get { return legacyReportOnly; }
            set { legacyReportOnly = value; }
        }

        /// <summary>
        /// 本次导出是否应当省略 [t3] 段落。
        /// 强制开关优先；否则按 T3 是否真的装配了来判定。
        /// </summary>
        /// <returns>true = 只输出 T2 口径段落。</returns>
        public bool ResolveLegacyOnly()
        {
            if (legacyReportOnly)
            {
                return true;
            }
            CombatBridge bridge = GetComponent<CombatBridge>();
            return bridge == null || bridge.BaselineMode || !bridge.T3Enabled;
        }

        /// <summary>
        /// 生成并写出快照。右键组件菜单也能手动触发，方便「改一行地形代码 → 立刻 diff」。
        /// </summary>
        [ContextMenu("导出确定性快照")]
        public void Dump()
        {
            Dump(ResolveLegacyOnly());
        }

        /// <summary>
        /// 生成并写出快照，显式指定报告口径。
        /// </summary>
        /// <param name="legacyOnly">true = 只输出 T2 口径段落（与 T1/T2 基线逐字节可比）。</param>
        public void Dump(bool legacyOnly)
        {
            string text = BuildReport(legacyOnly);
            LastDumpText = text;

            string dir = Application.isEditor
                ? Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Logs")
                : Application.persistentDataPath;

            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string file = string.Format("determinism_{0}_v{1}.txt",
                    WorldBuilder.Zone != null ? WorldBuilder.Zone.ZoneId : "unknown", Bootstrap.ZoneVisits);
                LastDumpPath = Path.Combine(dir, file);
                File.WriteAllText(LastDumpPath, text, new UTF8Encoding(false));
                Debug.Log("[T2] 确定性快照已导出：" + LastDumpPath);
            }
            // 捕获面刻意开大：写盘可能因权限、只读目录、路径过长等多种原因失败，
            // 而这是个诊断工具——它挂掉不该连带把游戏一起拖下水。
            catch (System.Exception e)
            {
                // 导出失败不该让游戏挂掉——它是诊断工具，不是玩法。
                LastDumpPath = null;
                Debug.LogWarning("[T2] 快照写盘失败（" + e.Message + "），正文已保留在 LastDumpText。");
            }
        }

        /// <summary>由 <see cref="CombatBridge"/> 在世界与怪群都就绪后调用。</summary>
        public void OnWorldReady()
        {
            if (dumpOnBuild)
            {
                Dump();
            }
        }

        // ---------------------------------------------------------------------
        // 报告正文
        // ---------------------------------------------------------------------

        /// <summary>生成报告正文（口径自动判定）。</summary>
        /// <returns>报告文本。</returns>
        public string BuildReport()
        {
            return BuildReport(ResolveLegacyOnly());
        }

        /// <summary>
        /// 生成报告正文。
        /// </summary>
        /// <param name="legacyOnly">true = 不追加 [t3] 段落。</param>
        /// <returns>报告文本。</returns>
        public string BuildReport(bool legacyOnly)
        {
            StringBuilder sb = new StringBuilder(4096);
            CultureInfo inv = CultureInfo.InvariantCulture;

            sb.AppendLine("# 幽篁竹海 T2 切片 · 确定性快照");
            sb.AppendLine("# 两次生成 diff 应完全一致；不一致处即为确定性泄漏点。");
            sb.AppendLine();

            // --- 种子 ---
            sb.AppendLine("[seed]");
            sb.AppendLine("zone_id     = " + (WorldBuilder.Zone != null ? WorldBuilder.Zone.ZoneId : "?"));
            sb.AppendLine("is_safe     = " + (WorldBuilder.Zone != null && WorldBuilder.Zone.IsSafe));
            sb.AppendLine("visits      = " + Bootstrap.ZoneVisits.ToString(inv));
            sb.AppendLine("derived     = " + WorldBuilder.Seed.ToString(inv));
            sb.AppendLine("base_level  = " + (WorldBuilder.Zone != null ? WorldBuilder.Zone.BaseLevel : 0).ToString(inv));
            sb.AppendLine();

            // --- 地形 ---
            sb.AppendLine("[terrain]");
            sb.AppendLine("size_tiles  = " + WorldBuilder.Width.ToString(inv) + " x " + WorldBuilder.Height.ToString(inv));
            sb.AppendLine("size_units  = " + WorldBuilder.WorldWidth.ToString("F0", inv) + " x " + WorldBuilder.WorldHeight.ToString("F0", inv));

            TileKind[] grid = WorldBuilder.Grid;
            if (grid == null)
            {
                sb.AppendLine("grid        = <未生成>");
            }
            else
            {
                int[] hist = new int[4];
                for (int i = 0; i < grid.Length; i++)
                {
                    hist[(int)grid[i]]++;
                }
                float total = grid.Length > 0 ? grid.Length : 1;
                sb.AppendLine("cells       = " + grid.Length.ToString(inv));
                sb.AppendLine(string.Format(inv, "ground      = {0} ({1:P2})", hist[0], hist[0] / total));
                sb.AppendLine(string.Format(inv, "ground2     = {0} ({1:P2})", hist[1], hist[1] / total));
                sb.AppendLine(string.Format(inv, "water       = {0} ({1:P2})", hist[2], hist[2] / total));
                sb.AppendLine(string.Format(inv, "rock        = {0} ({1:P2})", hist[3], hist[3] / total));
                sb.AppendLine("hash_all    = " + Djb2(grid, 0, grid.Length).ToString(inv));

                // 分四象限再各散列一次：整图散列只能告诉你"不一样"，
                // 象限散列能告诉你"哪半边不一样"，定位成本差一个数量级。
                int halfW = WorldBuilder.Width / 2;
                int halfH = WorldBuilder.Height / 2;
                sb.AppendLine("hash_q_bl   = " + QuadHash(grid, 0, 0, halfW, halfH).ToString(inv));
                sb.AppendLine("hash_q_br   = " + QuadHash(grid, halfW, 0, WorldBuilder.Width, halfH).ToString(inv));
                sb.AppendLine("hash_q_tl   = " + QuadHash(grid, 0, halfH, halfW, WorldBuilder.Height).ToString(inv));
                sb.AppendLine("hash_q_tr   = " + QuadHash(grid, halfW, halfH, WorldBuilder.Width, WorldBuilder.Height).ToString(inv));
            }
            sb.AppendLine();

            // --- 玩家 ---
            sb.AppendLine("[player]");
            sb.AppendLine(string.Format(inv, "spawn       = ({0:F2}, {1:F2})",
                WorldBuilder.PlayerSpawn.x, WorldBuilder.PlayerSpawn.y));
            sb.AppendLine(string.Format(inv, "hp_max      = {0:F2}", CombatBridge.PlayerHpMax));
            sb.AppendLine(string.Format(inv, "def         = {0:F2}", CombatBridge.PlayerDef));
            sb.AppendLine(string.Format(inv, "atk_raw     = {0:F2}", AttackController.AttackRaw));
            sb.AppendLine();

            // --- 怪群 ---
            sb.AppendLine("[enemies]");
            EnemySpawner spawner = GetComponent<EnemySpawner>();
            List<Combatant> wave = spawner != null ? spawner.LastWave : null;
            if (wave == null || wave.Count == 0)
            {
                sb.AppendLine("<空>");
            }
            else
            {
                sb.AppendLine("count       = " + wave.Count.ToString(inv));
                sb.AppendLine("# idx kind lv hp_max contact armor poise elite affix pos");
                for (int i = 0; i < wave.Count; i++)
                {
                    Combatant c = wave[i];
                    sb.AppendLine(string.Format(inv,
                        "{0,3} {1,-8} {2,2} {3,9:F4} {4,8:F4} {5,6:F2} {6,6:F1} {7} {8,-8} ({9:F2}, {10:F2})",
                        i, c.Kind, c.Level, c.HpMax, c.ContactDamage, c.Armor, c.PoiseMax,
                        c.IsElite ? "E" : "-", c.Affix, c.Position.X, c.Position.Y));
                }
            }
            sb.AppendLine();

            // --- 内核状态 ---
            CombatController ctrl = GetComponent<CombatController>();
            sb.AppendLine("[kernel]");
            if (ctrl != null && ctrl.Encounter != null)
            {
                sb.AppendLine("combatants  = " + ctrl.Encounter.Combatants.Count.ToString(inv));
                sb.AppendLine("alive_enemy = " + ctrl.Encounter.AliveEnemyCount.ToString(inv));
                sb.AppendLine("touch_range = " + ctrl.Encounter.TouchRange.ToString("F2", inv));
                sb.AppendLine("fixed_step  = " + CombatScheduler.FixedStep.ToString("F6", inv));
            }
            else
            {
                sb.AppendLine("<内核未初始化>");
            }

            // --- T3 战斗深化（基线模式下整段不输出）---
            if (!legacyOnly)
            {
                sb.AppendLine();
                AppendT3Section(sb, inv);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 追加 [t3] 段落：双资源池 / 连击 / 动作机 / 技能 CD / 状态清单 + 全部整数帧常量。
        ///
        /// 【为什么要把常量也写进快照】
        /// 「同种子同世界」在 T3 之后多了一层含义：同一份配置。若有人把
        /// DODGE_IFRAME_LEN 从 12 改成 13，战斗手感全变、但地形散列一个字节都不动。
        /// 把配置指纹一起打进快照，这类改动才会在 diff 里显形。
        /// </summary>
        /// <param name="sb">输出缓冲。</param>
        /// <param name="inv">不变文化（避免逗号小数点在不同区域设置下漂移）。</param>
        private void AppendT3Section(StringBuilder sb, CultureInfo inv)
        {
            sb.AppendLine("[t3]");

            CombatBridge bridge = GetComponent<CombatBridge>();
            if (bridge == null)
            {
                sb.AppendLine("<未装配 CombatBridge>");
                return;
            }

            sb.AppendLine("enabled     = " + bridge.T3Enabled);
            sb.AppendLine("baseline    = " + bridge.BaselineMode);
            sb.AppendLine(string.Format(inv, "qi          = {0:F2} / {1:F2}", bridge.QiCurrent, bridge.QiMax));
            sb.AppendLine(string.Format(inv, "stamina     = {0:F2} / {1:F2}", bridge.StaminaCurrent, bridge.StaminaMax));
            sb.AppendLine(string.Format(inv, "combo       = {0} x{1:F3}", bridge.Combo, bridge.ComboMult));
            sb.AppendLine("action      = " + bridge.PlayerAction + (bridge.PlayerIframeActive ? " [iframe]" : string.Empty));
            sb.AppendLine("status      = " + bridge.PlayerStatusSummary());

            Combatant p = bridge.Player;
            SkillRuntime skills = p != null ? p.Skills : null;
            sb.AppendLine("# slot id cd_remain cd_frames qi_cost stam_cost raw range arc frames");
            for (int i = 0; i < SkillTable.SlotCount; i++)
            {
                SkillDef def = bridge.SkillOf((IntentSlot)i);
                if (def == null)
                {
                    sb.AppendLine(string.Format(inv, "{0,4} <空>", i));
                    continue;
                }
                int remain = skills != null ? skills.CdRemain(i) : 0;
                sb.AppendLine(string.Format(inv,
                    "{0,4} {1,-18} {2,3} {3,4} {4,6:F1} {5,6:F1} {6,6:F1} {7,6:F1} {8,6:F1} {9}/{10}/{11}",
                    i, def.Id, remain, def.CooldownFrames, def.QiCost, def.StaminaCost,
                    def.Raw, def.Range, def.ArcDeg,
                    def.Frames.Startup, def.Frames.Active, def.Frames.Recovery));
            }

            sb.AppendLine("# 配置指纹（整数帧 / 与帧率无关）");
            sb.AppendLine("buffer_f    = " + SkillConfig.INPUT_BUFFER_FRAMES.ToString(inv));
            sb.AppendLine("soft_aim    = " + SkillConfig.SOFT_AIM_MAX_DEG.ToString("F1", inv) + " deg");
            sb.AppendLine("combo_reset = " + SkillConfig.COMBO_RESET_FRAMES.ToString(inv));
            sb.AppendLine(string.Format(inv, "qi_pool     = max {0:F0} regen {1:F1}/s ooc x{2:F1}@{3}f lock {4}f",
                SkillConfig.QI_MAX, SkillConfig.QI_REGEN, SkillConfig.QI_OOC_MULT,
                SkillConfig.QI_OOC_FRAMES, SkillConfig.QI_REGEN_LOCK_FRAMES));
            sb.AppendLine(string.Format(inv, "stam_pool   = max {0:F0} regen {1:F1}/s ooc x{2:F1}@{3}f lock {4}f",
                SkillConfig.STAM_MAX, SkillConfig.STAM_REGEN, SkillConfig.STAM_OOC_MULT,
                SkillConfig.STAM_OOC_FRAMES, SkillConfig.STAM_REGEN_LOCK_FRAMES));
            sb.AppendLine(string.Format(inv, "dodge       = dist {0:F1} cost {1:F0} cd {2}f {3}/{4}/{5} iframe {6}+{7}",
                SkillConfig.DODGE_DISTANCE, SkillConfig.DODGE_STAMINA_COST, SkillConfig.DODGE_CD_FRAMES,
                SkillConfig.DODGE_STARTUP_FRAMES, SkillConfig.DODGE_ACTIVE_FRAMES, SkillConfig.DODGE_RECOVERY_FRAMES,
                SkillConfig.DODGE_IFRAME_START, SkillConfig.DODGE_IFRAME_LEN));
            sb.AppendLine(string.Format(inv, "burn        = {0}f tick {1}f dmg {2:F1}",
                StatusConfig.BURN_DURATION_FRAMES, StatusConfig.BURN_TICK_FRAMES, StatusConfig.BURN_TICK_DAMAGE));
            sb.AppendLine(string.Format(inv, "poison      = {0}f tick {1}f dmg {2:F1} max {3}",
                StatusConfig.POISON_DURATION_FRAMES, StatusConfig.POISON_TICK_FRAMES,
                StatusConfig.POISON_TICK_DAMAGE, StatusConfig.POISON_MAX_STACKS));
            sb.AppendLine(string.Format(inv, "slow        = {0}f mult {1:F2}",
                StatusConfig.SLOW_DURATION_FRAMES, StatusConfig.SLOW_SPEED_MULT_DELTA));
            sb.AppendLine(string.Format(inv, "break_def   = {0}f mult {1:F2}",
                StatusConfig.BREAK_DEF_DURATION_FRAMES, StatusConfig.BREAK_DEF_ARMOR_MULT_DELTA));
        }

        /// <summary>对整块格子做 djb2 滚动散列（与 Core 的字符串散列同族，便于人工比对）。</summary>
        private static uint Djb2(TileKind[] grid, int from, int to)
        {
            uint h = 5381u;
            unchecked
            {
                for (int i = from; i < to; i++)
                {
                    h = ((h << 5) + h) + (uint)grid[i];
                }
            }
            return h;
        }

        /// <summary>对一个矩形子区域散列。</summary>
        private static uint QuadHash(TileKind[] grid, int x0, int y0, int x1, int y1)
        {
            uint h = 5381u;
            unchecked
            {
                for (int y = y0; y < y1; y++)
                {
                    int rowBase = y * WorldBuilder.Width;
                    for (int x = x0; x < x1; x++)
                    {
                        h = ((h << 5) + h) + (uint)grid[rowBase + x];
                    }
                }
            }
            return h;
        }
    }
}
