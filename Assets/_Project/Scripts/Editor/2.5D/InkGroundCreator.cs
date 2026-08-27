// -----------------------------------------------------------------------------
// InkGroundCreator.cs —— 水墨大地「静态地面」一键生成（Editor Only）
//
// 【职责】按 docs/ground-world-design-notes.md 生成覆盖整个活动区
//   （3840×2560 世界单位）的单一大 Plane 地面资产：
//     1. 单 Plane，覆盖 3840×2560（默认 Plane 10×10 → Scale X=384, Z=256, Y=1）
//     2. Rotation 保持 (0,0,0) —— Plane 默认已水平朝上；转 X=-90° 会立成一堵墙
//     3. 材质 Xianxia/Ink/InkGround（复用 MAT_Ink_Ground，宣纸白 Unlit，0 贴图）
//     4. Mesh Collider（角色站得住、不穿地）
//     5. 存 Prefab → Assets/_Project/Prefabs/Environment/InkGround.prefab
//
// 【为什么是脚本而不是手改场景】
//   SampleScene.unity 有 9.7 万行，盲改 YAML 有整场景加载失败的风险。用编辑器 API
//   建 Plane + 存 Prefab，由 Unity 自己完成序列化，零破坏（与 BambooScenePlacer 同套路）。
//
// 【用法】
//   菜单（Unity 顶部）：Shuimo / 2.5D / 地面 / 创建 InkGround Prefab
//   菜单：             Shuimo / 2.5D / 地面 / 放入当前场景
//   批处理（无人值守）：Unity.exe -batchmode -quit -projectPath <proj> \
//                        -executeMethod Shuimo.EditorTools.InkGroundCreator.CreateInkGround
//
// 【红线】纯 Editor 工具；幂等（Prefab 已存在则跳过重建）；不碰运行时组件。
// -----------------------------------------------------------------------------

using System.IO;
using UnityEditor;
using UnityEngine;

namespace Shuimo.EditorTools
{
    public static class InkGroundCreator
    {
        private const string MenuRoot = "Shuimo/2.5D/地面/";
        private const string PrefabDir = "Assets/_Project/Prefabs/Environment";
        private const string PrefabPath = PrefabDir + "/InkGround.prefab";
        private const string MaterialPath = "Assets/_Project/Art/Materials/Ink/MAT_Ink_Ground.mat";
        private const string GroundName = "InkGround";

        // 活动区 = 120×80 格 × 32 TileUnit（docs/ground-world-design-notes.md §3）
        private const float WorldWidth = 3840f;   // X
        private const float WorldDepth = 2560f;   // Z

        // =====================================================================
        // 批处理 / 菜单入口
        // =====================================================================

        [MenuItem(MenuRoot + "创建 InkGround Prefab")]
        public static void CreateInkGround()
        {
            GameObject prefab = BuildPrefab();
            if (prefab == null)
            {
                return;
            }

            Debug.Log($"[InkGroundCreator] 已生成 {PrefabPath}");
            EditorGUIUtility.PingObject(prefab);
            Selection.activeObject = prefab;
        }

        [MenuItem(MenuRoot + "放入当前场景")]
        public static void PlaceInScene()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                prefab = BuildPrefab();
            }

            if (prefab == null)
            {
                return;
            }

            // 场景已有同名地面 → 只选中，不重复放（幂等）
            GameObject existing = GameObject.Find(GroundName);
            if (existing != null)
            {
                Selection.activeGameObject = existing;
                EditorUtility.DisplayDialog("InkGround", "当前场景已存在 InkGround，已选中。", "OK");
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = GroundName;
            instance.transform.position = Vector3.zero;
            Undo.RegisterCreatedObjectUndo(instance, "Place InkGround");
            Selection.activeGameObject = instance;
        }

        /// <summary>
        /// 把 MAT_Ink_Ground 赋给当前场景里 BambooGrove 的
        /// BambooSceneContext.groundMaterial（原来 scene 里是空引用 {fileID: 0}，
        /// 运行时才会兜底建 runtime 材质；接了 asset 材质后竹林脚下同用宣纸白）。
        /// </summary>
        [MenuItem(MenuRoot + "接入竹林地面材质")]
        public static void AssignBambooGroundMaterial()
        {
            string scenePath = "Assets/Scenes/SampleScene.unity";
            UnityEngine.SceneManagement.Scene scene =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!scene.IsValid() || scene.path != scenePath)
            {
                // 当前不是 SampleScene → 显式打开它
                scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath,
                    UnityEditor.SceneManagement.OpenSceneMode.Single);
            }

            if (!scene.IsValid())
            {
                UnityEngine.Debug.LogError($"[InkGroundCreator] 打不开场景：{scenePath}");
                return;
            }

            var ctx = Object.FindObjectOfType<Xianxia.Unity.T2.BambooSceneContext>();
            if (ctx == null)
            {
                UnityEngine.Debug.LogError("[InkGroundCreator] SampleScene 里没有 BambooSceneContext，跳过。");
                return;
            }

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                UnityEngine.Debug.LogError($"[InkGroundCreator] 材质不存在：{MaterialPath}");
                return;
            }

            ctx.groundMaterial = mat;
            UnityEditor.EditorUtility.SetDirty(ctx);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            UnityEngine.Debug.Log("[InkGroundCreator] 已把 MAT_Ink_Ground 赋给 BambooSceneContext.groundMaterial 并保存。");
        }

        /// <summary>
        /// 批处理专用：显式打开 SampleScene → 放地面 → 保存场景。
        /// batchmode 下活动场景是空的 Untitled，不能靠 PlaceInScene 直接落进目标场景。
        /// </summary>
        public static void PlaceInSampleScene()
        {
            if (Application.isPlaying)
            {
                return;
            }

            string scenePath = "Assets/Scenes/SampleScene.unity";
            UnityEngine.SceneManagement.Scene scene =
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath,
                    UnityEditor.SceneManagement.OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                UnityEngine.Debug.LogError($"[InkGroundCreator] 打不开场景：{scenePath}");
                return;
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                prefab = BuildPrefab();
            }

            if (prefab == null)
            {
                return;
            }

            if (GameObject.Find(GroundName) != null)
            {
                UnityEngine.Debug.Log($"[InkGroundCreator] SampleScene 已有 {GroundName}，跳过。");
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = GroundName;
            instance.transform.position = Vector3.zero;

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            UnityEngine.Debug.Log($"[InkGroundCreator] 已把 {GroundName} 放进 SampleScene 并保存。");
        }

        // =====================================================================
        // 核心：建 Plane → 材质 → 碰撞 → 存 Prefab（幂等）
        // =====================================================================

        private static GameObject BuildPrefab()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
            {
                Debug.Log($"[InkGroundCreator] {PrefabPath} 已存在，跳过重建。");
                return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            }

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                Debug.LogError($"[InkGroundCreator] 材质不存在：{MaterialPath}");
                return null;
            }

            // Plane primitive 自带 MeshFilter + MeshRenderer + MeshCollider
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = GroundName;
            ground.transform.position = Vector3.zero;            // y=0 对齐
            ground.transform.rotation = Quaternion.identity;     // 保持水平，勿转 X
            ground.transform.localScale = new Vector3(
                WorldWidth / 10f,   // 384
                1f,
                WorldDepth / 10f);  // 256

            MeshRenderer mr = ground.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.sharedMaterial = mat;
            }

            if (ground.GetComponent<MeshCollider>() == null)
            {
                ground.AddComponent<MeshCollider>();
            }

            EnsureDirectory(PrefabDir);
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(ground, PrefabPath);
            Object.DestroyImmediate(ground);   // 清掉场景里的临时物体
            return saved;
        }

        private static void EnsureDirectory(string dir)
        {
            if (AssetDatabase.IsValidFolder(dir))
            {
                return;
            }

            string parent = Path.GetDirectoryName(dir).Replace('\\', '/');
            string leaf = Path.GetFileName(dir);
            if (!AssetDatabase.IsValidFolder(parent))
            {
                EnsureDirectory(parent);
            }

            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
