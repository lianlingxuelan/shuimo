// -----------------------------------------------------------------------------
// ZoneData.cs —— 区域数据结构 + 字符串散列辅助（引擎无关）
//
// 与 Assets/Data/zones.json（= godot/data/zones.json 的副本）逐字段对应。
// 本文件是**纯 POCO**：不带任何序列化框架的特性标注，JSON 依赖全部收敛在
// ZoneLoader.cs 一个文件里。这样将来若 Unity 侧要换 Newtonsoft / MessagePack，
// 只需替换 Loader，数据结构与业务代码零改动。
//
// 【禁止事项】不得引用 UnityEngine。必须能被 dotnet test 独立编译。
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace Xianxia.Core
{
    /// <summary>单个区域的完整定义。zone_id 是存档 key，一经确定不得改名。</summary>
    public sealed class ZoneData
    {
        /// <summary>唯一标识，也是存档 key 与种子派生的输入。</summary>
        public string ZoneId { get; set; }

        /// <summary>中文显示名。</summary>
        public string DisplayName { get; set; }

        /// <summary>中文描述文案。</summary>
        public string Desc { get; set; }

        /// <summary>菜单排序序号。</summary>
        public int Order { get; set; }

        /// <summary>安全区标记：true 表示不刷怪，且种子不随访问次数变化。</summary>
        public bool IsSafe { get; set; }

        /// <summary>主城标记。全局唯一。</summary>
        public bool IsHub { get; set; }

        /// <summary>掉落分级。0 = 安全区不参与分级；战斗区 1..5。与 base_level 解耦。</summary>
        public int Tier { get; set; }

        /// <summary>推荐等级区间 [min, max]。</summary>
        public int[] RecLevel { get; set; }

        /// <summary>敌人等级基准。</summary>
        public int BaseLevel { get; set; }

        /// <summary>解锁条件。</summary>
        public ZoneUnlock Unlock { get; set; }

        /// <summary>地形与美术主题。</summary>
        public ZoneTheme Theme { get; set; }

        /// <summary>敌人配置。</summary>
        public ZoneEnemies Enemies { get; set; }

        /// <summary>BOSS 配置。安全区为 null。</summary>
        public ZoneBoss Boss { get; set; }

        /// <summary>遭遇战次数区间 [min, max]。</summary>
        public int[] EncounterCount { get; set; }

        /// <summary>本区专属材料 id。空串表示无。</summary>
        public string ExclusiveMaterial { get; set; }

        /// <summary>NPC 列表。战斗区通常为空。</summary>
        public List<ZoneNpc> Npcs { get; set; }
    }

    /// <summary>
    /// 解锁条件。四项**全部**满足才解锁。
    /// level / realm_rank 为 -1 表示不校验；kills_in 为空表示不校验；boss_cleared 为空表示不校验。
    /// </summary>
    public sealed class ZoneUnlock
    {
        /// <summary>最低角色等级，-1 = 不校验。</summary>
        public int Level { get; set; }

        /// <summary>最低境界档位（0 基），-1 = 不校验。</summary>
        public int RealmRank { get; set; }

        /// <summary>前置击杀数要求：zone_id ⇒ 需要的击杀数。</summary>
        public Dictionary<string, int> KillsIn { get; set; }

        /// <summary>前置 BOSS 通关要求：需要已清的 zone_id 列表。</summary>
        public List<string> BossCleared { get; set; }
    }

    /// <summary>地形生成与美术主题。</summary>
    public sealed class ZoneTheme
    {
        /// <summary>地图尺寸 [w, h]，单位 tile。</summary>
        public int[] Size { get; set; }

        /// <summary>地形算子族：forest | volcanic | frozen | town。town 走手绘布局，忽略 operators。</summary>
        public string Algo { get; set; }

        /// <summary>调色板。</summary>
        public ZonePalette Palette { get; set; }

        /// <summary>水面占比目标。</summary>
        public float WaterRate { get; set; }

        /// <summary>岩石占比目标。</summary>
        public float RockRate { get; set; }

        /// <summary>按序应用的算子链。</summary>
        public List<ZoneOperator> Operators { get; set; }

        /// <summary>装饰物配置。</summary>
        public ZoneDecor Decor { get; set; }

        /// <summary>天气：none | snow | ash | bloodmist。</summary>
        public string Weather { get; set; }

        /// <summary>地图宽（tile）。Size 缺失时返回 0。</summary>
        public int Width
        {
            get { return Size != null && Size.Length > 0 ? Size[0] : 0; }
        }

        /// <summary>地图高（tile）。Size 缺失时返回 0。</summary>
        public int Height
        {
            get { return Size != null && Size.Length > 1 ? Size[1] : 0; }
        }
    }

    /// <summary>十六进制颜色字符串（#RRGGBB）。Core 层不做颜色解析，交给渲染层。</summary>
    public sealed class ZonePalette
    {
        /// <summary>地面主色。</summary>
        public string Ground { get; set; }

        /// <summary>地面次色（做斑驳感）。</summary>
        public string Ground2 { get; set; }

        /// <summary>水面色。</summary>
        public string Water { get; set; }

        /// <summary>岩石色。</summary>
        public string Rock { get; set; }

        /// <summary>点缀 / 高光色。</summary>
        public string Accent { get; set; }
    }

    /// <summary>
    /// 地形算子。不同 op 用到的字段不同，未用字段保持默认值。
    /// op ∈ noise_blob | vein | scatter | lake | ring | grove；target ∈ rock | water。
    /// </summary>
    public sealed class ZoneOperator
    {
        /// <summary>算子类型。</summary>
        public string Op { get; set; }

        /// <summary>作用目标图层：rock | water。</summary>
        public string Target { get; set; }

        /// <summary>数量（vein / lake / ring / grove）。</summary>
        public int Count { get; set; }

        /// <summary>长度区间 [min, max]（vein）。</summary>
        public int[] Len { get; set; }

        /// <summary>半径区间 [min, max]（lake / ring / grove）。</summary>
        public int[] Radius { get; set; }

        /// <summary>密度（scatter / grove）。</summary>
        public float Density { get; set; }

        /// <summary>噪声缩放（noise_blob）。</summary>
        public float Scale { get; set; }

        /// <summary>叠加权重（noise_blob）。</summary>
        public float Weight { get; set; }

        /// <summary>来源图层（ring 以某图层为圆心种子）。</summary>
        public string Source { get; set; }

        /// <summary>环宽（ring）。</summary>
        public int Thickness { get; set; }
    }

    /// <summary>装饰物。</summary>
    public sealed class ZoneDecor
    {
        /// <summary>装饰种类：lantern | bamboo | ember | icespike | swordstone | ...</summary>
        public string Kind { get; set; }

        /// <summary>铺设密度。</summary>
        public float Density { get; set; }
    }

    /// <summary>敌人配置。</summary>
    public sealed class ZoneEnemies
    {
        /// <summary>同屏常驻敌人数量。</summary>
        public int Count { get; set; }

        /// <summary>可刷出的敌人种类 key 列表。</summary>
        public List<string> Kinds { get; set; }

        /// <summary>血量倍率。</summary>
        public float HpMult { get; set; }

        /// <summary>
        /// 攻击倍率。★ 这是模型 B 的**种子值，不是最终生效值**：
        /// 每区先锁 d_eff = hp_max/65，再反推该区真正要用的 atk_mult。
        /// </summary>
        public float AtkMult { get; set; }

        /// <summary>护甲平坦加值（整数加值，非 0..1 减伤率）。</summary>
        public int ArmorAdd { get; set; }

        /// <summary>精英怪出现概率。</summary>
        public float EliteRate { get; set; }

        /// <summary>词缀附加概率。</summary>
        public float AffixRate { get; set; }
    }

    /// <summary>BOSS 配置。</summary>
    public sealed class ZoneBoss
    {
        /// <summary>BOSS 唯一 id，也是通关记录的 key。</summary>
        public string BossId { get; set; }

        /// <summary>中文显示名。</summary>
        public string DisplayName { get; set; }

        /// <summary>沿用哪个敌人种类的外观 / AI。</summary>
        public string Kind { get; set; }

        /// <summary>等级相对 base_level 的偏移。</summary>
        public int LevelOffset { get; set; }

        /// <summary>血量倍率。</summary>
        public float HpMult { get; set; }

        /// <summary>攻击倍率。</summary>
        public float AtkMult { get; set; }

        /// <summary>常规 roll 之外的额外掉落。</summary>
        public ZoneBossDropExtra DropExtra { get; set; }
    }

    /// <summary>BOSS 额外掉落。</summary>
    public sealed class ZoneBossDropExtra
    {
        /// <summary>保底材料：material_id ⇒ 数量。</summary>
        public Dictionary<string, int> Materials { get; set; }

        /// <summary>true 表示本次掉落的装备品质吃该区上限。</summary>
        public bool EquipmentQualityCap { get; set; }
    }

    /// <summary>NPC 定义（仅安全区有）。</summary>
    public sealed class ZoneNpc
    {
        /// <summary>NPC 唯一 id。</summary>
        public string NpcId { get; set; }

        /// <summary>中文显示名。</summary>
        public string DisplayName { get; set; }

        /// <summary>立绘 / 精灵图 key。</summary>
        public string SpriteKey { get; set; }

        /// <summary>站位 tile 坐标 [x, y]。</summary>
        public int[] Tile { get; set; }

        /// <summary>提供的服务：shop | forge | realm，空串表示纯对话。</summary>
        public string Service { get; set; }

        /// <summary>任务链，按序解锁。</summary>
        public List<ZoneQuestLink> QuestChain { get; set; }

        /// <summary>无任务可交互时的闲聊对白 id，空串表示没有。</summary>
        public string IdleDialogue { get; set; }

        /// <summary>站位 X（tile）。</summary>
        public int TileX
        {
            get { return Tile != null && Tile.Length > 0 ? Tile[0] : 0; }
        }

        /// <summary>站位 Y（tile）。</summary>
        public int TileY
        {
            get { return Tile != null && Tile.Length > 1 ? Tile[1] : 0; }
        }
    }

    /// <summary>NPC 任务链的一环。</summary>
    public sealed class ZoneQuestLink
    {
        /// <summary>任务 id。</summary>
        public string QuestId { get; set; }

        /// <summary>接取时播放的对白 id。</summary>
        public string Offer { get; set; }

        /// <summary>交付时播放的对白 id。</summary>
        public string TurnIn { get; set; }
    }

    /// <summary>zones.json 的解析结果：全部区域 + 按 id / order 的索引。</summary>
    public sealed class ZoneDatabase
    {
        /// <summary>数据文件的 _version 字段。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>按 order 升序排列的全部区域。</summary>
        public List<ZoneData> Zones { get; private set; }

        private readonly Dictionary<string, ZoneData> _byId;

        /// <summary>用已解析的区域列表构造索引。</summary>
        public ZoneDatabase(List<ZoneData> zones)
        {
            Zones = zones ?? new List<ZoneData>();
            Zones.Sort(delegate (ZoneData a, ZoneData b) { return a.Order.CompareTo(b.Order); });
            _byId = new Dictionary<string, ZoneData>(Zones.Count);
            for (int i = 0; i < Zones.Count; i++)
            {
                _byId[Zones[i].ZoneId] = Zones[i];
            }
        }

        /// <summary>按 id 取区域，不存在返回 null。</summary>
        public ZoneData Get(string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId))
            {
                return null;
            }
            ZoneData z;
            return _byId.TryGetValue(zoneId, out z) ? z : null;
        }

        /// <summary>该区是否为安全区。未知 id 视为非安全区。</summary>
        public bool IsSafe(string zoneId)
        {
            ZoneData z = Get(zoneId);
            return z != null && z.IsSafe;
        }

        /// <summary>主城 zone_id。找不到返回 null。</summary>
        public string HubZoneId()
        {
            for (int i = 0; i < Zones.Count; i++)
            {
                if (Zones[i].IsHub)
                {
                    return Zones[i].ZoneId;
                }
            }
            return null;
        }
    }

    /// <summary>字符串散列。用于区域种子派生，必须与 Godot 逐位一致。</summary>
    public static class GodotHash
    {
        /// <summary>
        /// Godot <c>String.hash()</c> / GDScript 全局 <c>hash("...")</c> 的等价实现。
        ///
        /// 【★ 与初版规格的一处订正】
        /// 任务书原文写的是「Godot hash() 对字符串用 FNV-1a」，但查 Godot 源码
        /// （core/string/ustring.cpp <c>String::hash()</c>）实际是 **djb2**：
        /// <code>hashv = 5381; while (c) hashv = ((hashv &lt;&lt; 5) + hashv) + c;</code>
        /// 若用 FNV-1a，同一个 zone_id 会得到完全不同的种子，Unity 生成的地图与
        /// Godot 原型对不上，「保留原型玩法」的前提就断了。因此这里以 djb2 为准，
        /// FNV-1a 仍保留在 <see cref="Fnv1a32"/> 供其它用途。
        ///
        /// 遍历单位是 **Unicode 码点**（Godot 内部是 char32_t），因此这里显式处理
        /// 代理对。zone_id 目前全是 ASCII，但 npc_id / 材料 id 未必永远是。
        /// </summary>
        public static uint Djb2(string s)
        {
            uint hashv = 5381u;
            if (string.IsNullOrEmpty(s))
            {
                return hashv;
            }

            for (int i = 0; i < s.Length; i++)
            {
                uint c = s[i];
                // 合并代理对为单个码点，与 char32_t 遍历对齐。
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    c = (uint)char.ConvertToUtf32(s[i], s[i + 1]);
                    i++;
                }
                unchecked
                {
                    hashv = ((hashv << 5) + hashv) + c;   // hashv * 33 + c
                }
            }
            return hashv;
        }

        /// <summary>FNV-1a 32 位（对 UTF-8 字节流）。非 Godot 种子路径，供通用散列使用。</summary>
        public static uint Fnv1a32(string s)
        {
            uint hash = 2166136261u;
            if (string.IsNullOrEmpty(s))
            {
                return hash;
            }
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
            unchecked
            {
                for (int i = 0; i < bytes.Length; i++)
                {
                    hash ^= bytes[i];
                    hash *= 16777619u;
                }
            }
            return hash;
        }
    }
}
