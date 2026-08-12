// =============================================================================
// RouteASelfTest.cs
//
// 竹林 2.5D 技术验证 · 轮次 C —— Route A 自测器（Editor Only）
//
// 【职责】一键跑通 docs/bamboo-2_5d-local-verify.md 的核心项，出 PASS/WARN/FAIL 报告：
//   1. GraphicsSettings.asset 的 m_AlwaysIncludedShaders 含三个 Xianxia/Ink/*
//   2. BambooHitFx prefab 存在（Prefabs/ 或 Resources/2.5D/ 任一处）
//   3. 红线 grep：Runtime/2.5D/*.cs 中 Time.timeScale= / CombatScheduler / RunPhase /
//      DamageResolver / RequestHitstop / KickHitstop / Frozen= → 0 命中（注释感知）
//   4. bamboo_ink.fbx 的 ModelImporter.globalScale == 1
//   5. 输出报告（Console + 可选 docs/routeA-selftest-report.md）
//
// 【红线遵守】
//   纯 Editor 工具：不引用内核、不写 Time.timeScale / Scheduler / Frozen、
//   不引用 DamageResolver / CombatScheduler / RunPhase。扫描范围只限
//   Runtime/2.5D/*.cs（内核不在范围内，天然 0 命中）。
//   注意：本文件自身的「红线关键字」仅作为扫描用的正则字符串出现，且本文件位于
//   Editor/ 目录，不在扫描范围内，不会自我命中。
// =============================================================================

using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Shuimo.EditorTools
{
    /// <summary>Route A 真机自测清单的 Editor 一键实现。</summary>
    public static class RouteASelfTest
    {
        /// <summary>菜单入口（与 BambooHitFxPrefabBaker 同菜单层级，序号错开）。</summary>
        [MenuItem("Shuimo/2.5D/运行 Route A 自测", false, 30)]
        public static void Run()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string dataPath = Application.dataPath;

            int pass = 0;
            int warn = 0;
            int fail = 0;
            StringBuilder md = new StringBuilder();
            md.AppendLine("# Route A 自测报告");
            md.AppendLine();
            md.AppendLine("> 由 `Shuimo/2.5D/运行 Route A 自测` 生成。扫描范围仅 `Assets/_Project/Scripts/Runtime/2.5D/*.cs`。");
            md.AppendLine();

            // ---- 检查 1：AlwaysIncludedShaders 含三个 Xianxia/Ink/* ----
            CheckResult c1 = CheckAlwaysIncludedShaders(projectRoot);
            Emit(c1, 1, "AlwaysIncludedShaders（Xianxia/Ink/*）", ref pass, ref warn, ref fail, md);

            // ---- 检查 2：BambooHitFx prefab 存在 ----
            CheckResult c2 = CheckBambooHitFxPrefab(dataPath);
            Emit(c2, 2, "BambooHitFx prefab", ref pass, ref warn, ref fail, md);

            // ---- 检查 3：红线 grep（注释感知） ----
            CheckResult c3 = CheckRedLineGrep(dataPath);
            Emit(c3, 3, "红线 grep（2.5D/）", ref pass, ref warn, ref fail, md);

            // ---- 检查 4：bamboo_ink.fbx globalScale == 1 ----
            CheckResult c4 = CheckInkFbxGlobalScale();
            Emit(c4, 4, "bamboo_ink.fbx globalScale", ref pass, ref warn, ref fail, md);

            // ---- 检查 5：汇总报告 ----
            string summary = string.Format(
                "[RouteASelfTest] 汇总：PASS {0} / WARN {1} / FAIL {2}", pass, warn, fail);
            Debug.Log(summary);
            md.AppendLine("| 汇总 | 结果 |");
            md.AppendLine("|---|---|");
            md.AppendLine(string.Format("| 汇总 | PASS {0} / WARN {1} / FAIL {2} |", pass, warn, fail));
            md.AppendLine();
            md.AppendLine(string.Format("_生成于：{0}_", System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

            // 落盘 docs/routeA-selftest-report.md（可选但默认写，便于回溯）。
            string docsDir = Path.Combine(projectRoot, "docs");
            if (!Directory.Exists(docsDir))
            {
                Directory.CreateDirectory(docsDir);
            }
            string reportPath = Path.Combine(docsDir, "routeA-selftest-report.md");
            File.WriteAllText(reportPath, md.ToString());
            Debug.Log("[RouteASelfTest] 报告已写入：" + reportPath);

            if (fail > 0)
            {
                Debug.LogError("[RouteASelfTest] 存在 FAIL 项，请先消除红线命中 / 资产问题再继续。");
            }
        }

        // =====================================================================
        // 检查项实现
        // =====================================================================

        /// <summary>检查 1：GraphicsSettings.asset 的 m_AlwaysIncludedShaders 是否含三个 Ink Shader。</summary>
        private static CheckResult CheckAlwaysIncludedShaders(string projectRoot)
        {
            string path = Path.Combine(projectRoot, "ProjectSettings", "GraphicsSettings.asset");
            if (!File.Exists(path))
            {
                return CheckResult.Warn(
                    "未找到 ProjectSettings/GraphicsSettings.asset；请确认工程结构。");
            }

            string text = File.ReadAllText(path);
            string[] names =
            {
                "Xianxia/Ink/BambooTrunk",
                "Xianxia/Ink/BambooLeaf",
                "Xianxia/Ink/InkGround",
            };

            List<string> missing = new List<string>();
            for (int i = 0; i < names.Length; i++)
            {
                Shader shader = Shader.Find(names[i]);
                if (shader == null)
                {
                    missing.Add(names[i] + "（Shader.Find 返回 null，工程内未导入，非仅 Always Included 问题）");
                    continue;
                }

                string assetPath = AssetDatabase.GetAssetPath(shader);
                if (string.IsNullOrEmpty(assetPath))
                {
                    missing.Add(names[i] + "（无法解析资产路径）");
                    continue;
                }

                string guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.IsNullOrEmpty(guid) && !text.Contains(guid))
                {
                    missing.Add(names[i] + "（未加入 Always Included Shaders）");
                }
            }

            if (missing.Count == 0)
            {
                return CheckResult.Pass("三个 Xianxia/Ink/* 均已加入 Always Included Shaders。");
            }

            return CheckResult.Warn(
                "缺失项：\n  - " + string.Join("\n  - ", missing) +
                "\n指引：Project Settings → Graphics → Always Included Shaders 添加对应行；" +
                "否则真打包时 Shader.Find 返回 null（Editor PlayMode 不受影响但打包黑屏）。");
        }

        /// <summary>检查 2：BambooHitFx prefab 是否存在（Prefabs/ 或 Resources/2.5D/ 任一处）。</summary>
        private static CheckResult CheckBambooHitFxPrefab(string dataPath)
        {
            string[] candidates =
            {
                Path.Combine(dataPath, "_Project/Scripts/Runtime/2.5D/Prefabs/BambooHitFx.prefab"),
                Path.Combine(dataPath, "Resources/2.5D/BambooHitFx.prefab"),
            };

            List<string> present = new List<string>();
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    present.Add(candidates[i]);
                }
            }

            if (present.Count > 0)
            {
                return CheckResult.Pass("已找到：" + string.Join("；", present));
            }

            return CheckResult.Warn(
                "未找到 BambooHitFx.prefab（Prefabs/ 与 Resources/2.5D/ 均无）。\n" +
                "指引：菜单 Shuimo/2.5D/烘焙 BambooHitFx.prefab（BambooHitFxPrefabBaker）" +
                "烘焙，或提供 Resources/2.5D/ 副本。缺失时 BambooVfx 走运行时构造兜底，不影响功能。");
        }

        /// <summary>
        /// 检查 3：红线 grep（注释感知）。
        /// 逐行剥离 // 单行与 /* */ 块注释后再匹配；仅真实代码 token 判 FAIL。
        /// </summary>
        private static CheckResult CheckRedLineGrep(string dataPath)
        {
            string dir = Path.Combine(dataPath, "_Project/Scripts/Runtime/2.5D");
            if (!Directory.Exists(dir))
            {
                return CheckResult.Warn("未找到目录：" + dir);
            }

            // 红线模式：仅赋值形态的 Frozen 算违规；类型/字段实引用（注释已剥离）算违规。
            Regex[] patterns =
            {
                new Regex(@"Time\.timeScale\s*="),
                new Regex(@"\bCombatScheduler\b"),
                new Regex(@"\bRunPhase\b"),
                new Regex(@"\bDamageResolver\b"),
                new Regex(@"\bRequestHitstop\b"),
                new Regex(@"\bKickHitstop\b"),
                new Regex(@"Frozen\s*=|FeedbackClock\.Frozen\s*="),
            };

            List<string> hits = new List<string>();
            string[] files = Directory.GetFiles(dir, "*.cs", SearchOption.TopDirectoryOnly);
            System.Array.Sort(files);

            for (int f = 0; f < files.Length; f++)
            {
                string[] lines = File.ReadAllLines(files[f]);
                bool inBlock = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string stripped = StripComments(lines[i], ref inBlock);
                    for (int p = 0; p < patterns.Length; p++)
                    {
                        if (patterns[p].IsMatch(stripped))
                        {
                            hits.Add(string.Format(
                                "{0}:{1}", Path.GetFileName(files[f]), i + 1));
                            break;
                        }
                    }
                }
            }

            if (hits.Count == 0)
            {
                return CheckResult.Pass("Runtime/2.5D/*.cs 红线 0 命中（已注释剥离）。");
            }

            return CheckResult.Fail(
                "代码命中红线（必须消除）：\n  - " + string.Join("\n  - ", hits));
        }

        /// <summary>检查 4：bamboo_ink.fbx 的 ModelImporter.globalScale == 1。</summary>
        private static CheckResult CheckInkFbxGlobalScale()
        {
            string fbxPath = "Assets/_Project/Art/Bamboo/bamboo_ink.fbx";
            ModelImporter importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null)
            {
                return CheckResult.Warn(
                    "未导入/未找到资产：" + fbxPath +
                    "（Route A 资产缺失；请先导入 bamboo_ink.fbx）。");
            }

            bool scaleOk = Mathf.Approximately(importer.globalScale, 1.0f);
            bool fileScaleOk = importer.useFileScale == false;
            if (scaleOk && fileScaleOk)
            {
                return CheckResult.Pass(fbxPath + " globalScale == 1 且 useFileScale == false。");
            }

            return CheckResult.Fail(
                string.Format(
                    "{0} 的 globalScale = {1}（应为 1），useFileScale = {2}（应为 false）。\n" +
                    "指引：菜单 Shuimo/2.5D/Reimport Ink Bamboo FBX（BambooInkFbxImportFix 已自动置 1）重新导入。",
                    fbxPath, importer.globalScale, importer.useFileScale));
        }

        // =====================================================================
        // 注释剥离 / 报告输出
        // =====================================================================

        /// <summary>
        /// 剥离一行中的注释，返回仅剩「代码」的文本。跨行的 /* */ 块注释由调用方用
        /// <paramref name="inBlock"/> 状态在多次调用间传递。
        /// </summary>
        /// <param name="raw">原始行。</param>
        /// <param name="inBlock">是否处于块注释中（跨行状态，返回时被更新）。</param>
        /// <returns>剥离注释后的代码文本。</returns>
        private static string StripComments(string raw, ref bool inBlock)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return raw ?? string.Empty;
            }

            StringBuilder sb = new StringBuilder(raw.Length);
            int i = 0;
            int n = raw.Length;
            while (i < n)
            {
                if (inBlock)
                {
                    if (i + 1 < n && raw[i] == '*' && raw[i + 1] == '/')
                    {
                        inBlock = false;
                        i += 2;
                        continue;
                    }

                    i++;
                    continue;
                }

                // 行注释：// 之后整行丢弃。
                if (i + 1 < n && raw[i] == '/' && raw[i + 1] == '/')
                {
                    break;
                }

                // 块注释开始：/* ... 可能跨行。
                if (i + 1 < n && raw[i] == '/' && raw[i + 1] == '*')
                {
                    inBlock = true;
                    i += 2;
                    continue;
                }

                sb.Append(raw[i]);
                i++;
            }

            return sb.ToString();
        }

        /// <summary>单条检查结果。</summary>
        private sealed class CheckResult
        {
            public enum Status { Pass, Warn, Fail }

            public Status Kind { get; private set; }
            public string Detail { get; private set; }

            private CheckResult(Status kind, string detail)
            {
                Kind = kind;
                Detail = detail;
            }

            public static CheckResult Pass(string d) { return new CheckResult(Status.Pass, d); }
            public static CheckResult Warn(string d) { return new CheckResult(Status.Warn, d); }
            public static CheckResult Fail(string d) { return new CheckResult(Status.Fail, d); }
        }

        /// <summary>输出一条检查结果到 Console 与 MD 报告，并更新计数。</summary>
        private static void Emit(
            CheckResult r,
            int index,
            string title,
            ref int pass,
            ref int warn,
            ref int fail,
            StringBuilder md)
        {
            string tag;
            switch (r.Kind)
            {
                case CheckResult.Status.Pass:
                    tag = "PASS";
                    pass++;
                    break;
                case CheckResult.Status.Warn:
                    tag = "WARN";
                    warn++;
                    break;
                default:
                    tag = "FAIL";
                    fail++;
                    break;
            }

            string line = string.Format("[{0}] #{1} {2} — {3}", tag, index, title, r.Detail);
            if (r.Kind == CheckResult.Status.Fail)
            {
                Debug.LogError(line);
            }
            else if (r.Kind == CheckResult.Status.Warn)
            {
                Debug.LogWarning(line);
            }
            else
            {
                Debug.Log(line);
            }

            md.AppendLine(string.Format("| {0} | {1} | {2} |", index, tag, r.Detail.Replace("\n", " ")));
        }
    }
}
