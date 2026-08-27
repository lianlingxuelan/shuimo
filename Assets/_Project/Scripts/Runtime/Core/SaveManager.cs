// -----------------------------------------------------------------------------
// SaveManager.cs —— JSON 存档读写框架（asmdef: Xianxia.Unity.T2）
//
// 【它是什么】
// 蓝图 0.2 要求的 "SaveManager"。本周先交"装裱存档雏形"——一个 JSON 落地
// 框架 + 存档点注册表的骨架，真实存档内容（玩家进度 / 正魔 / 背包）随各系统
// 周次逐步填充。第 5 周的"装裱存档点"、第 10 周的"云端/本地多周目继承"都
// 复用本框架，不另起炉灶。
//
// 【为什么 JSON 落 Application.persistentDataPath】
// 这是 Unity 平台无关的持久目录（Win/Mac/移动端都在），无需关心绝对路径。
// JsonUtility 是引擎内置、零依赖，契合"不引第三方包"的红线；其限制
// （不序列化 Dictionary、只认 public 字段）我们用 List&lt;SaveStat&gt; 规避，
// 多键值需求走这个扁平列表即可。
//
// 【为什么没有 ResetStatics】
// SaveManager 不持有任何可写静态字段——路径是计算属性（每调用现算），
// 缓冲区都是方法内的局部变量。没有跨 PlayMode 存活的状态，自然无需复位钩子。
// 将来若加了静态缓存，再按 Bootstrap 的纪律补 ResetStatics。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Xianxia.Unity.T2.Core
{
    /// <summary>单个存档键值（规避 JsonUtility 不序列化 Dictionary 的限制）。</summary>
    [Serializable]
    public sealed class SaveStat
    {
        public string key;
        public float value;
    }

    /// <summary>
    /// 一份存档数据。本周只填"骨架字段"，真实内容随各周次追加。
    /// slot 用于多存档位（第 10 周多周目继承会用到）。
    /// </summary>
    [Serializable]
    public sealed class SaveData
    {
        public int version = 1;
        public string slot = "default";
        public string zoneId;
        public int zoneVisits;
        public int playerLevel;
        public float playerHp;
        public float playerHpMax;
        /// <summary>已游玩秒数（由 CombatBridge.ElapsedTime 等汇总，本周预留）。</summary>
        public float playTime;
        /// <summary>UTC ticks，读档时可换算成人类可读时间。</summary>
        public long savedAtTicks;
        /// <summary>任意扩展数值（五行/正魔/做减法"失去"等将来塞这里）。</summary>
        public List<SaveStat> stats = new List<SaveStat>();
    }

    /// <summary>JSON 存档读写。纯静态工具，无状态。</summary>
    public static class SaveManager
    {
        private static string SavePath(string slot)
        {
            string safe = string.IsNullOrEmpty(slot) ? "default" : slot;
            return Path.Combine(Application.persistentDataPath, "save_" + safe + ".json");
        }

        /// <summary>是否存在该存档位。</summary>
        public static bool HasSave(string slot = "default")
        {
            return File.Exists(SavePath(slot));
        }

        /// <summary>
        /// 写入存档。自动盖 savedAtTicks。路径/IO 异常不吞不抛——交给上层决定
        /// （本周是雏形，调用方少，直接让异常冒泡比静默丢档更安全）。
        /// </summary>
        public static void Save(SaveData data)
        {
            if (data == null)
            {
                return;
            }
            data.savedAtTicks = DateTime.UtcNow.Ticks;
            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(SavePath(data.slot), json);
        }

        /// <summary>读取存档；不存在返回 null。</summary>
        public static SaveData Load(string slot = "default")
        {
            string path = SavePath(slot);
            if (!File.Exists(path))
            {
                return null;
            }
            string json = File.ReadAllText(path);
            return JsonUtility.FromJson<SaveData>(json);
        }

        /// <summary>删除存档位（装裱点耗尽 / 新游戏覆盖时）。</summary>
        public static void Delete(string slot = "default")
        {
            string path = SavePath(slot);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
