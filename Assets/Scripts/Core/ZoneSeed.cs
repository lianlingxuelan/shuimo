// -----------------------------------------------------------------------------
// ZoneSeed.cs —— 区域随机种子派生（引擎无关，移植自 godot/scripts/zone_db.gd:352-371）
//
// 【规则】
//   安全区：seed = hash(zone_id)              —— 坊市必须每次进都长一模一样
//   战斗区：seed = hash(zone_id) ^ (visits × ZONE_SEED_MIX)
//
// 【为什么战斗区要混访问次数】
// 玩家反复刷同一张图，如果地形每次都相同，第三趟就变成走过场。混入 visits 让
// 每次进入都是新地图，但「第 N 次进入某区」的结果是确定的 —— 存档回读、
// bug 复现、回归测试都能精确重放。这是「随机」与「确定」的正确折中。
//
// 【为什么乘子是 2654435761】
// 2^32 / φ 的取整（Knuth 黄金比散列常数）。它与 2^32 互质且比特分布良好，
// 连续的 visits（0,1,2,3…）乘上去后高低位都会剧烈翻动，异或进 hash 不会
// 出现「相邻两次访问的种子只差几个 bit ⇒ 地图长得像」的退化。
//
// 【禁止事项】不得引用 UnityEngine。必须能被 dotnet test 独立编译。
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace Xianxia.Core
{
    /// <summary>区域种子派生与访问计数。</summary>
    public static class ZoneSeed
    {
        /// <summary>种子混合乘子，对齐 <c>GameConfig.ZONE_SEED_MIX</c>。</summary>
        public const long ZoneSeedMix = 2654435761L;

        /// <summary>
        /// 派生区域种子。
        /// 返回 long 而不是 int/uint：<c>visits × 2654435761</c> 在 visits ≥ 2 时
        /// 就会溢出 32 位，GDScript 那边是 64 位整数运算，必须跟着用 64 位。
        /// </summary>
        /// <param name="zoneId">区域 id。</param>
        /// <param name="isSafe">是否安全区。安全区忽略 visits。</param>
        /// <param name="visits">此前已进入过的次数（0 = 首次进入）。</param>
        public static long Derive(string zoneId, bool isSafe, int visits)
        {
            long h = GodotHash.Djb2(zoneId);   // uint32 无符号提升到 long，与 GDScript hash() 的返回域一致
            if (isSafe)
            {
                return h;
            }
            unchecked
            {
                return h ^ (visits * ZoneSeedMix);
            }
        }

        /// <summary>用派生出的种子造一条 PCG32 流。</summary>
        public static PCG32 CreateRng(string zoneId, bool isSafe, int visits)
        {
            return new PCG32((ulong)Derive(zoneId, isSafe, visits));
        }
    }

    /// <summary>
    /// 访问次数账本 + 种子发放。对应 zone_db.gd 的 <c>visit_count</c> / <c>next_seed</c>。
    /// 需要随存档一起持久化，否则读档后重进同一区会拿到已经用过的种子。
    /// </summary>
    public sealed class ZoneSeedTracker
    {
        private readonly Dictionary<string, int> _visits = new Dictionary<string, int>();

        /// <summary>查询某区已进入过的次数。</summary>
        public int VisitsOf(string zoneId)
        {
            int n;
            return _visits.TryGetValue(zoneId, out n) ? n : 0;
        }

        /// <summary>
        /// 取种子并把访问计数 +1（真正进入区域时调用）。
        /// 安全区同样计数，只是种子不受计数影响。
        /// </summary>
        public long NextSeed(string zoneId, bool isSafe)
        {
            int n = VisitsOf(zoneId);
            _visits[zoneId] = n + 1;
            return ZoneSeed.Derive(zoneId, isSafe, n);
        }

        /// <summary>只看不取：预览下一次进入会拿到的种子，不改计数（UI 预览 / 测试用）。</summary>
        public long PeekSeed(string zoneId, bool isSafe)
        {
            return ZoneSeed.Derive(zoneId, isSafe, VisitsOf(zoneId));
        }

        /// <summary>直接设定访问次数（读档用）。</summary>
        public void SetVisits(string zoneId, int visits)
        {
            _visits[zoneId] = visits < 0 ? 0 : visits;
        }

        /// <summary>导出全部计数（存档用）。</summary>
        public Dictionary<string, int> Export()
        {
            return new Dictionary<string, int>(_visits);
        }

        /// <summary>清空（新游戏）。</summary>
        public void Clear()
        {
            _visits.Clear();
        }
    }
}
