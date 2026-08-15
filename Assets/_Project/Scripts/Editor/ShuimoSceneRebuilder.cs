using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Shuimo;

namespace Shuimo.EditorTools
{
    /// <summary>
    /// 通用场景清理+重建工具。
    ///
    /// 模仿 Codex 在古梦月项目里的 HomeUILayoutBuilder 思路，但做成通用版：
    /// 不绑定任何具体 UI 或玩法，只负责「清理由代码生成的对象」+「触发重建」，
    /// 让你在改代码后不用退出 Unity 编辑器就能一键重置场景。
    ///
    /// 菜单入口（Unity 顶部）：
    ///     Shuimo > Scene > Clean And Rebuild         ← 重建场景对象 + 强制重编译脚本
    ///     Shuimo > Scene > Clean Generated Objects    ← 只清理，不重建
    ///     Shuimo > Scene > Force Recompile (Clean Build) ← 只重编译脚本程序集（不动场景）
    ///     Shuimo > Scene > Reload Scene               ← 从磁盘重载当前场景（丢弃所有未保存改动）
    ///
    /// 关于「Clean And Rebuild」里那句"重编译"：
    ///   它调用 CompilationPipeline.RequestScriptCompilation 让 Unity 把全部脚本程序集重新编译一遍，
    ///   专治这一类问题——你刚 pull 了别人的修复、但 Unity 还在跑旧 dll，表现就是"代码改了却没生效"。
    ///   注意：编辑器内只能重编译脚本，不能删 Library；若怀疑 Library 缓存彻底坏了，
    ///   请关掉 Unity、手动删除项目根目录的 Library/ 文件夹再重开（最彻底的 clean）。
    ///
    /// 清理规则：只删除场景里挂了 ShuimoGenerated 标记的根节点。
    /// 相机、灯光、场景自带对象、手工摆放的对象一律保留，不会被误删。
    /// </summary>
    public static class ShuimoSceneRebuilder
    {
        [MenuItem("Shuimo/Scene/Clean And Rebuild")]
        public static void CleanAndRebuild()
        {
            // 未保存的场景改动先提示，避免误删后无法恢复。
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            CleanGeneratedObjects();
            ShuimoSceneBuilder.BuildAll();

            // 顺带强制重编译脚本程序集：确保你刚 pull 的修复真的被重新编译、加载，
            // 而不是还跑着上一版旧 dll（这正是"代码改了却没生效 / 玩家还是动不了"的常见元凶）。
            ForceRecompile();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log("[Shuimo] 场景已清理并重建，脚本已请求重编译。");
        }

        /// <summary>
        /// 强制重新编译全部脚本程序集（等价于许多引擎的 "Clean &amp; Rebuild" 里的编译环节）。
        /// 当你刚 pull 了别人的改动、怀疑 Unity 还在跑旧 dll 时，用它能确保新代码被编译并加载。
        /// 仅重建脚本程序集；不删 Library（删 Library 需关掉 Unity 手动操作，见类注释）。
        /// </summary>
        [MenuItem("Shuimo/Scene/Force Recompile (Clean Build)")]
        public static void ForceRecompile()
        {
            Debug.Log("[Shuimo] 请求强制重编译脚本程序集（Clean Build）…");
            Debug.LogWarning("[Shuimo] 重编译期间若 Console 出现红色脚本错误，程序集不会更新、游戏会继续跑旧逻辑——请先清掉报错再重试。");

#if UNITY_2023_1_OR_NEWER
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation(
                UnityEditor.Compilation.RequestScriptCompilationOptions.CleanBuildCache);
#else
            // Unity 2022.x 的等价重载：触发一次全量重编译。CleanBuildCache 选项在 2022.3 也支持，
            // 但为兼容更低版本这里走简单重载（pull 之后源文件已变更，Unity 会判定程序集过期并重编）。
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
#endif

            AssetDatabase.Refresh();
        }

        [MenuItem("Shuimo/Scene/Clean Generated Objects")]
        public static void CleanOnly()
        {
            int count = CleanGeneratedObjects();
            Debug.Log("[Shuimo] 已清理 " + count + " 个由代码生成的对象。");
        }

        /// <summary>从磁盘重载当前场景。会丢弃场景里所有未保存的手工改动。</summary>
        [MenuItem("Shuimo/Scene/Reload Scene")]
        public static void ReloadScene()
        {
            Scene active = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(active.path))
            {
                Debug.LogWarning("[Shuimo] 当前场景未保存到磁盘，无法重载。请先保存场景。");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            EditorSceneManager.OpenScene(active.path);
            Debug.Log("[Shuimo] 场景已从磁盘重载。");
        }

        // ======================= 核心清理逻辑 =======================

        private static int CleanGeneratedObjects()
        {
            var toDestroy = new List<GameObject>();

            // 场景根节点层级（包括未激活的对象）。
            Scene active = SceneManager.GetActiveScene();
            foreach (GameObject root in active.GetRootGameObjects())
            {
                // 只删挂 ShuimoGenerated 标记的根节点。
                // 用 GetComponentsInChildren 但要小心：如果某父节点已带标记，
                // 它的所有子节点也一起被删，所以收集时跳过已被父节点覆盖的。
                if (root.GetComponent<ShuimoGenerated>() != null)
                {
                    toDestroy.Add(root);
                }
            }

            // 从最外层删除，避免索引失效。
            int destroyed = 0;
            for (int i = toDestroy.Count - 1; i >= 0; i--)
            {
                GameObject go = toDestroy[i];
                if (go == null)
                {
                    continue;
                }

                Object.DestroyImmediate(go);
                destroyed++;
            }

            return destroyed;
        }
    }
}
