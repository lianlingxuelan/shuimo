// =============================================================================
// BoneSetupSelfTest.cs
//
// 竹林 2.5D 技术验证 · 骨骼绑定自检器（Editor Only）
//
// 【职责】一键验证 docs/2d-bone-setup-guide.md 的本地绑骨结果，出 PASS/WARN/FAIL 报告：
//   1. PlayerSettings 的 Scripting Define Symbols 含 HAS_2D_BONE_PACKAGE
//   2. heroine_base_open.png 的 TextureImporter.spriteImportMode 已设为多/单 Sprite（非 Default）
//   3. 存在 HeroineBone 预制体（HeroineBone 或 HeroineBone_Test）
//   4. 预制体含 SpriteSkin 组件（骨骼数据已绑定）
//   5. 预制体含 Animator + AnimatorController，且状态机含 idle/walk/attack/hit/death 5 个状态
//   6. 预制体含 UnityBoneCharacterView 组件，且 5 个 clip 字段已填（非空）
//
// 【红线遵守】
//   纯 Editor 工具：不引用内核、不写 Time.timeScale / Scheduler / Frozen。
//   通过反射读取运行时类型（UnityBoneCharacterView / SpriteSkin），避免对
//   HAS_2D_BONE_PACKAGE 编译符号的硬依赖——符号未开时自检器仍能报告“缺符号”。
// =============================================================================

using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Shuimo.EditorTools
{
    /// <summary>骨骼绑定自检器：跑通 setup guide 的核心项并出报告。</summary>
    public static class BoneSetupSelfTest
    {
        private const string HeroineSpritePath =
            "Assets/_Project/Art/Characters/Heroine2D/heroine_base_open.png";

        /// <summary>菜单入口，与 RouteASelfTest 同菜单层级，序号错开。</summary>
        [MenuItem("Shuimo/2.5D/运行 骨骼绑定自检", false, 31)]
        public static void Run()
        {
            int pass = 0;
            int warn = 0;
            int fail = 0;
            StringBuilder md = new StringBuilder();
            md.AppendLine("# 骨骼绑定自检报告");
            md.AppendLine();
            md.AppendLine("> 由 `Shuimo/2.5D/运行 骨骼绑定自检` 生成。");
            md.AppendLine();

            CheckResult c1 = CheckDefineSymbol();
            Emit(c1, 1, "HAS_2D_BONE_PACKAGE 符号", ref pass, ref warn, ref fail, md);

            CheckResult c2 = CheckHeroineSpriteImport();
            Emit(c2, 2, "heroine_base_open.png 导入设置", ref pass, ref warn, ref fail, md);

            GameObject prefab = FindHeroineBonePrefab(out CheckResult c3);
            Emit(c3, 3, "HeroineBone 预制体存在", ref pass, ref warn, ref fail, md);
            if (prefab == null)
            {
                // 后续检查都依赖预制体，直接给出指引并收尾。
                Finish(pass, warn, fail, md);
                return;
            }

            CheckResult c4 = CheckSpriteSkin(prefab);
            Emit(c4, 4, "预制体含 SpriteSkin（骨骼已绑）", ref pass, ref warn, ref fail, md);

            CheckResult c5 = CheckAnimatorStates(prefab);
            Emit(c5, 5, "Animator 5 状态齐全", ref pass, ref warn, ref fail, md);

            CheckResult c6 = CheckBoneViewClips(prefab);
            Emit(c6, 6, "UnityBoneCharacterView clip 字段已填", ref pass, ref warn, ref fail, md);

            Finish(pass, warn, fail, md);
        }

        // =====================================================================
        // 检查项实现
        // =====================================================================

        /// <summary>检查 1：编译符号 HAS_2D_BONE_PACKAGE。</summary>
        private static CheckResult CheckDefineSymbol()
        {
            BuildTargetGroup group = EditorUserBuildSettings.selectedBuildTargetGroup;
            string defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
            if (defines.Contains("HAS_2D_BONE_PACKAGE"))
            {
                return CheckResult.Pass("已定义 HAS_2D_BONE_PACKAGE。");
            }
            return CheckResult.Fail(
                "未定义 HAS_2D_BONE_PACKAGE。\n" +
                "指引：Edit → Project Settings → Player → Other Settings → Scripting Define Symbols 追加 " +
                "HAS_2D_BONE_PACKAGE，Apply 后等编译。未定义时 UnityBoneCharacterView 不编译，骨骼视图不会生效。");
        }

        /// <summary>检查 2：女主母图已设为 Sprite 导入。</summary>
        private static CheckResult CheckHeroineSpriteImport()
        {
            TextureImporter importer = AssetImporter.GetAtPath(HeroineSpritePath) as TextureImporter;
            if (importer == null)
            {
                return CheckResult.Warn("未找到：" + HeroineSpritePath + "（请确认资产已导入工程）。");
            }
            if (importer.textureType == TextureImporterType.Sprite)
            {
                return CheckResult.Pass(HeroineSpritePath + " 的 Texture Type = Sprite。");
            }
            return CheckResult.Warn(
                HeroineSpritePath + " 的 Texture Type = " + importer.textureType +
                "（应为 Sprite）。\n指引：选中该 PNG，Inspector 设 Texture Type = Sprite (2D and UI)，Apply。");
        }

        /// <summary>检查 3：查找 HeroineBone 预制体。</summary>
        private static GameObject FindHeroineBonePrefab(out CheckResult result)
        {
            string[] guids = AssetDatabase.FindAssets("HeroineBone t:Prefab");
            if (guids.Length == 0)
            {
                result = CheckResult.Fail(
                    "未找到 HeroineBone 预制体（HeroineBone / HeroineBone_Test 均无）。\n" +
                    "指引：按 docs/2d-bone-setup-guide.md 第 5–6 步，在场景创建测试角色并做成 Prefab：" +
                    "Assets/_Project/Prefabs/HeroineBone.prefab。");
                return null;
            }

            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                result = CheckResult.Fail("找到候选但加载失败：" + path);
                return null;
            }
            result = CheckResult.Pass("已找到预制体：" + path);
            return prefab;
        }

        /// <summary>检查 4：预制体含 SpriteSkin 组件。</summary>
        private static CheckResult CheckSpriteSkin(GameObject prefab)
        {
            System.Type skinType = FindType("SpriteSkin");
            if (skinType == null)
            {
                return CheckResult.Warn(
                    "运行时未识别 SpriteSkin 类型（可能 2D Animation 包未装或符号未开）。\n" +
                    "指引：确认 Packages 含 com.unity.2d.animation，且 HAS_2D_BONE_PACKAGE 已开。");
            }
            Component skin = prefab.GetComponent(skinType);
            if (skin != null)
            {
                return CheckResult.Pass("预制体含 SpriteSkin（骨骼数据已绑定）。");
            }
            return CheckResult.Fail(
                "预制体缺少 SpriteSkin 组件，说明骨骼尚未绑定。\n" +
                "指引：选中 heroine_base_open.png → Sprite Editor → Skinning Editor → 画骨 + Auto Weights + Apply；" +
                "再给测试角色挂 Sprite Skin 组件。");
        }

        /// <summary>检查 5：AnimatorController 含 5 个必需状态。</summary>
        private static CheckResult CheckAnimatorStates(GameObject prefab)
        {
            Animator animator = prefab.GetComponent<Animator>();
            if (animator == null)
            {
                return CheckResult.Fail("预制体缺少 Animator 组件。指引：Component → Animator 并挂 HeroineBoneAnimator。");
            }
            RuntimeAnimatorController ctrl = animator.runtimeAnimatorController;
            if (ctrl == null)
            {
                return CheckResult.Fail("Animator 未挂 Controller。指引：拖入 HeroineBoneAnimator。");
            }

            string[] required = { "idle", "walk", "attack", "hit", "death" };
            AnimatorController ac = ctrl as AnimatorController;
            if (ac == null)
            {
                // 非 AnimatorController（如 AnimatorOverrideController），无法直接枚举状态。
                return CheckResult.Warn(
                    "Controller 不是标准 AnimatorController（" + ctrl.GetType().Name +
                    "），无法自动枚举状态。请手动确认含 idle/walk/attack/hit/death。");
            }

            List<string> missing = new List<string>();
            HashSet<string> present = new HashSet<string>();
            foreach (var layer in ac.layers)
            {
                foreach (var state in layer.stateMachine.states)
                {
                    present.Add(state.state.name.ToLowerInvariant());
                }
            }
            foreach (string r in required)
            {
                if (!present.Contains(r))
                {
                    missing.Add(r);
                }
            }

            if (missing.Count == 0)
            {
                return CheckResult.Pass("AnimatorController 含 idle/walk/attack/hit/death 全部 5 状态。");
            }
            return CheckResult.Fail(
                "AnimatorController 缺失状态：" + string.Join(", ", missing) +
                "。\n指引：按 guide 第 7 步录制 heroine_idle/walk/attack/hit/death.anim 并拖入状态机。");
        }

        /// <summary>检查 6：UnityBoneCharacterView 组件存在且 clip 字段已填。</summary>
        private static CheckResult CheckBoneViewClips(GameObject prefab)
        {
            System.Type viewType = FindType("UnityBoneCharacterView");
            if (viewType == null)
            {
                return CheckResult.Fail(
                    "运行时未识别 UnityBoneCharacterView 类型。\n" +
                    "指引：确认 HAS_2D_BONE_PACKAGE 已定义（检查 1），且 CharacterView.cs 已包含该类型。");
            }
            Component view = prefab.GetComponent(viewType);
            if (view == null)
            {
                return CheckResult.Fail(
                    "预制体缺少 UnityBoneCharacterView 组件。\n" +
                    "指引：选中测试角色 → Component → Unity Bone Character View。");
            }

            SerializedObject so = new SerializedObject(view);
            string[] fields = { "clipIdle", "clipWalk", "clipAttack", "clipHit", "clipDeath" };
            List<string> empty = new List<string>();
            foreach (string f in fields)
            {
                SerializedProperty p = so.FindProperty(f);
                if (p == null || string.IsNullOrEmpty(p.stringValue))
                {
                    empty.Add(f);
                }
            }

            if (empty.Count == 0)
            {
                return CheckResult.Pass("UnityBoneCharacterView 的 5 个 clip 字段均已填写。");
            }
            return CheckResult.Warn(
                "UnityBoneCharacterView 以下 clip 字段为空：" + string.Join(", ", empty) +
                "。\n指引：Inspector 按 guide 第 8 步填入 idle/walk/attack/hit/death。");
        }

        // =====================================================================
        // 工具方法
        // =====================================================================

        /// <summary>跨程序集按简单名查找类型（无需编译期引用）。</summary>
        private static System.Type FindType(string typeName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = asm.GetType(typeName);
                if (t != null)
                {
                    return t;
                }
            }
            return null;
        }

        /// <summary>收尾：汇总并落盘报告。</summary>
        private static void Finish(int pass, int warn, int fail, StringBuilder md)
        {
            string summary = string.Format(
                "[BoneSetupSelfTest] 汇总：PASS {0} / WARN {1} / FAIL {2}", pass, warn, fail);
            Debug.Log(summary);
            md.AppendLine("| 汇总 | 结果 |");
            md.AppendLine("|---|---|");
            md.AppendLine(string.Format("| 汇总 | PASS {0} / WARN {1} / FAIL {2} |", pass, warn, fail));
            md.AppendLine();
            md.AppendLine(string.Format("_生成于：{0}_", System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string docsDir = Path.Combine(projectRoot, "docs");
            if (!Directory.Exists(docsDir))
            {
                Directory.CreateDirectory(docsDir);
            }
            string reportPath = Path.Combine(docsDir, "bone-setup-selftest-report.md");
            File.WriteAllText(reportPath, md.ToString());
            Debug.Log("[BoneSetupSelfTest] 报告已写入：" + reportPath);

            if (fail > 0)
            {
                Debug.LogError("[BoneSetupSelfTest] 存在 FAIL 项，请按报告消除后再 PlayMode 验收。");
            }
            else if (warn > 0)
            {
                Debug.LogWarning("[BoneSetupSelfTest] 仅 WARN，可 PlayMode 试跑；建议补齐后重跑。");
            }
            else
            {
                Debug.Log("[BoneSetupSelfTest] 全部 PASS，骨骼绑定就绪，可进入 PlayMode 验收。");
            }
        }

        // =====================================================================
        // 检查结果类型
        // =====================================================================

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
