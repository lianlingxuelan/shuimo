// -----------------------------------------------------------------------------
// ZoneLoader.cs —— zones.json 解析（引擎无关，Newtonsoft 实现）
//
// 【依赖收敛】
// 整个 Core 层只有这一个文件引用 JSON 库。ZoneData.cs 保持纯 POCO，
// 字段名映射靠下面的 SnakeCaseContractResolver 而不是逐字段打特性。好处：
//   1. 换序列化框架只改这一个文件
//   2. 新增字段时不会漏标注特性（漏标 = 静默读成默认值，是最难查的一类 bug）
//
// 【Unity 2022 适配说明】
// 原版用 System.Text.Json，但 Unity 2022 的 .NET Standard 2.1 运行时**不自带**
// System.Text.Json。改为 Newtonsoft.Json（com.unity.nuget.newtonsoft-json，
// Unity 官方包），该包在 2022 开箱即用，且对注释 / 尾随逗号 / 字符串数字更宽容。
//
// 【禁止事项】不得引用 UnityEngine。必须能被 dotnet test 独立编译。
//   （注意：dotnet test 环境下需通过 NuGet 引入 Newtonsoft.Json 包。）
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Xianxia.Core
{
    /// <summary>zones.json 读取器。</summary>
    public static class ZoneLoader
    {
        /// <summary>相对 Unity 工程根的默认数据路径。</summary>
        public const string DefaultRelativePath = "Assets/Data/zones.json";

        private static readonly JsonSerializerSettings Settings = CreateSettings();

        private static JsonSerializerSettings CreateSettings()
        {
            JsonSerializerSettings s = new JsonSerializerSettings();
            s.ContractResolver = SnakeCaseContractResolver.Instance;
            s.Formatting = Formatting.None;
            // Newtonsoft 默认即允许注释、尾随逗号、从字符串读数字，无需逐项开关。
            return s;
        }

        /// <summary>从磁盘读取并解析。</summary>
        public static ZoneDatabase LoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("zones.json 路径为空", "path");
            }
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("找不到区域数据文件: " + path, path);
            }
            // 显式指定 UTF-8：zones.json 含大量中文，交给运行时默认编码猜是灾难。
            string json = File.ReadAllText(path, new UTF8Encoding(false));
            return LoadFromJson(json);
        }

        /// <summary>从 JSON 文本解析。</summary>
        public static ZoneDatabase LoadFromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentException("zones.json 内容为空", "json");
            }

            ZonesFile file = JsonConvert.DeserializeObject<ZonesFile>(json, Settings);
            if (file == null || file.Zones == null || file.Zones.Count == 0)
            {
                throw new InvalidDataException("zones.json 解析结果为空，检查 zones 数组是否存在");
            }

            Validate(file.Zones);

            ZoneDatabase db = new ZoneDatabase(file.Zones);
            db.SchemaVersion = file.Version;
            return db;
        }

        /// <summary>
        /// 结构性校验。只查「一定是配置写错」的硬错误，不查平衡性数值。
        /// 数据错误必须在加载期炸掉，不能拖到玩家进图后才表现为「地图空白」。
        /// </summary>
        private static void Validate(List<ZoneData> zones)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            int hubCount = 0;

            for (int i = 0; i < zones.Count; i++)
            {
                ZoneData z = zones[i];
                if (string.IsNullOrEmpty(z.ZoneId))
                {
                    throw new InvalidDataException("第 " + i + " 个区域缺少 zone_id");
                }
                if (!ids.Add(z.ZoneId))
                {
                    throw new InvalidDataException("zone_id 重复: " + z.ZoneId + "（它是存档 key，必须唯一）");
                }
                if (z.Theme == null)
                {
                    throw new InvalidDataException(z.ZoneId + " 缺少 theme");
                }
                if (z.Theme.Width <= 0 || z.Theme.Height <= 0)
                {
                    throw new InvalidDataException(z.ZoneId + " 的 theme.size 非法");
                }
                if (z.Enemies == null)
                {
                    throw new InvalidDataException(z.ZoneId + " 缺少 enemies");
                }
                if (z.IsHub)
                {
                    hubCount++;
                }
                if (!z.IsSafe && z.Boss == null)
                {
                    throw new InvalidDataException(z.ZoneId + " 是战斗区但没有 boss 配置");
                }
                if (z.Unlock == null)
                {
                    z.Unlock = new ZoneUnlock { Level = -1, RealmRank = -1 };
                }
                if (z.Npcs == null)
                {
                    z.Npcs = new List<ZoneNpc>();
                }
                if (z.Theme.Operators == null)
                {
                    z.Theme.Operators = new List<ZoneOperator>();
                }
            }

            if (hubCount != 1)
            {
                throw new InvalidDataException("必须且只能有 1 个 is_hub 区域，实际 " + hubCount + " 个");
            }
        }

        /// <summary>顶层文件结构。下划线开头的 key 靠显式 [JsonProperty] 映射。</summary>
        private sealed class ZonesFile
        {
            [JsonProperty("_version")]
            public int Version { get; set; }

            [JsonProperty("zones")]
            public List<ZoneData> Zones { get; set; }
        }

        /// <summary>
        /// PascalCase ⇒ snake_case 命名策略（Newtonsoft 版）。
        /// 只在大写字母前插下划线，因此 <c>Ground2</c> ⇒ <c>ground2</c>（不是 ground_2），
        /// 与 zones.json 的实际字段名一致。ZoneData 上无需任何特性即可正确映射。
        /// </summary>
        private sealed class SnakeCaseContractResolver : DefaultContractResolver
        {
            public static readonly SnakeCaseContractResolver Instance = new SnakeCaseContractResolver();

            protected override string ResolvePropertyName(string propertyName)
            {
                if (string.IsNullOrEmpty(propertyName))
                {
                    return propertyName;
                }
                StringBuilder sb = new StringBuilder(propertyName.Length + 6);
                for (int i = 0; i < propertyName.Length; i++)
                {
                    char c = propertyName[i];
                    if (char.IsUpper(c))
                    {
                        if (i > 0)
                        {
                            sb.Append('_');
                        }
                        sb.Append(char.ToLowerInvariant(c));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                return sb.ToString();
            }
        }
    }
}
