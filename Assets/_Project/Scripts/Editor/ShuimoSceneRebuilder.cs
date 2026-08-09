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
    ///     Shuimo > Scene > Clean And Rebuild   ← 清理 + 重建
    ///     Shuimo > Scene > Clean Generated      ← 只清理，不重建
    ///     Shuimo > Scene > Reload Scene         ← 从磁盘重载当前场景（丢弃所有未保存改动）
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

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log("[Shuimo] 场景已清理并重建。");
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
