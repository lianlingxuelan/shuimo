// =============================================================================
// BambooHitFxPrefabBaker.cs
//
// 竹林 2.5D 技术验证 · 轮次 B —— BambooHitFx.prefab 烘焙器（Editor Only）
//
// 【为什么需要这个工具】
// 任务要求交付 Assets/_Project/Scripts/Runtime/2.5D/Prefabs/BambooHitFx.prefab。
// ParticleSystem 的序列化体量极大（MainModule / EmissionModule / ShapeModule /
// SizeOverLifetime / ColorOverLifetime / ForceOverLifetime / Renderer …
// 每个模块几十上百个字段，还带 AnimationCurve、Gradient、MinMaxCurve 的嵌套结构），
// 手写 YAML 既无法在无 Unity 的环境下校验，也极易因为字段缺失/版本差异导致
// prefab 打不开或参数被重置。
//
// 因此这里换一条更可靠的路：让 Unity 自己序列化。
// 本工具调用运行时的 BambooVfx.CreateFxTemplate(...) 在场景里搭出层级，
// 修好材质引用后用 PrefabUtility.SaveAsPrefabAsset 存盘。
// 好处是「代码兜底的特效」和「烘焙出的 prefab」共用同一份构造实现，
// 参数永远一致，不会出现两套配置各跑各的。
//
// 【用法】菜单栏：
//   Shuimo/2.5D/烘焙 BambooHitFx.prefab              → 存到 Prefabs/ 目录
//   Shuimo/2.5D/烘焙 BambooHitFx.prefab（含 Resources 副本）
//                                                    → 额外存一份到 Resources/2.5D/，
//                                                      让 BambooVfx 的 Resources 兜底分支生效
//
// 烘焙完把 BambooHitFx.prefab 拖到 BambooVfx.inkLeafPrefab 字段即可
//（或什么都不做：BambooVfx 会先试 Inspector 引用，再试 Resources，最后才运行时构造）。
//
// 【红线遵守】
//   本文件是纯 Editor 资产生成工具：
//   不碰 Combat/**、不碰 WorldBuilder/SpriteFactory/HeroineAnimator/AttackController，
//   不写 Time.timeScale / Scheduler / RunPhase / CombatScheduler，
//   不引用 DamageResolver，不写 FeedbackClock.Frozen。
// =============================================================================

using System.IO;
using UnityEditor;
using UnityEngine;
using Xianxia.Unity.T2;

namespace Shuimo.EditorTools
{
    /// <summary>
    /// 把 <see cref="BambooVfx"/> 的运行时特效层级烘焙成正式 prefab 资产。
    /// </summary>
    public static class BambooHitFxPrefabBaker
    {
        /// <summary>prefab 目标目录（任务指定路径）。</summary>
        private const string PrefabDir = "Assets/_Project/Scripts/Runtime/2.5D/Prefabs";

        /// <summary>prefab 目标路径。</summary>
        private const string PrefabPath = PrefabDir + "/BambooHitFx.prefab";

        /// <summary>粒子共用材质资产路径（必须是持久化资产，否则存进 prefab 会丢）。</summary>
        private const string MaterialPath = PrefabDir + "/MAT_BambooHitFx.mat";

        /// <summary>Resources 副本目录，对应 BambooVfx.FxResourcePath = "2.5D/BambooHitFx"。</summary>
        private const string ResourcesDir = "Assets/_Project/Resources/2.5D";

        /// <summary>Resources 副本路径。</summary>
        private const string ResourcesPrefabPath = ResourcesDir + "/BambooHitFx.prefab";

        /// <summary>烘焙时使用的竹干半径，与 BambooSceneContext.trunkRadius 默认值保持一致。</summary>
        private const float DefaultRadius = 14.0f;

        /// <summary>特效根节点名（去掉运行时后缀，作为正式资产名）。</summary>
        private const string FxRootName = "BambooHitFx";

        /// <summary>
        /// 默认深度轴 = 指向相机 = -Z。
        /// 依据：WorldBuilder.SetupCamera 把相机放在 z = -100，所以朝向相机的方向是 -Z。
        /// 与 BambooSceneContext.depthSign 默认值 -1 一致。
        /// </summary>
        private static readonly Vector3 DefaultDepthAxis = new Vector3(0.0f, 0.0f, -1.0f);

        // =====================================================================
        // 菜单入口
        // =====================================================================

        /// <summary>只烘焙到 Prefabs/ 目录。</summary>
        [MenuItem("Shuimo/2.5D/烘焙 BambooHitFx.prefab", false, 20)]
        public static void BakePrefab()
        {
            GameObject asset = Bake(PrefabPath);
            if (asset == null)
            {
                return;
            }

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            Debug.Log(
                "[BambooHitFxPrefabBaker] 已烘焙：" + PrefabPath +
                "\n把它拖到 BambooVfx.inkLeafPrefab 字段即可启用 prefab 分支。",
                asset);
        }

        /// <summary>烘焙到 Prefabs/，并额外复制一份到 Resources/2.5D/ 启用 Resources 兜底。</summary>
        [MenuItem("Shuimo/2.5D/烘焙 BambooHitFx.prefab（含 Resources 副本）", false, 21)]
        public static void BakePrefabWithResourcesCopy()
        {
            GameObject asset = Bake(PrefabPath);
            if (asset == null)
            {
                return;
            }

            GameObject copy = Bake(ResourcesPrefabPath);
            if (copy == null)
            {
                return;
            }

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            Debug.Log(
                "[BambooHitFxPrefabBaker] 已烘焙：\n  " + PrefabPath +
                "\n  " + ResourcesPrefabPath +
                "\nResources 副本就位后，BambooVfx 即使不填 inkLeafPrefab 也会自动 Load 到它。",
                asset);
        }

        /// <summary>删除烘焙产物（prefab + Resources 副本 + 材质），方便重来一遍。</summary>
        [MenuItem("Shuimo/2.5D/清除 BambooHitFx 烘焙产物", false, 22)]
        public static void ClearBakedAssets()
        {
            int removed = 0;
            removed += DeleteIfExists(PrefabPath) ? 1 : 0;
            removed += DeleteIfExists(ResourcesPrefabPath) ? 1 : 0;
            removed += DeleteIfExists(MaterialPath) ? 1 : 0;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[BambooHitFxPrefabBaker] 已清除 " + removed + " 个烘焙产物。");
        }

        // =====================================================================
        // 核心流程
        // =====================================================================

        /// <summary>
        /// 在指定路径烘焙一份 BambooHitFx prefab。
        /// </summary>
        /// <param name="assetPath">目标资产路径（Assets/ 开头）。</param>
        /// <returns>烘焙成功返回 prefab 资产，失败返回 null。</returns>
        public static GameObject Bake(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogError("[BambooHitFxPrefabBaker] assetPath 为空，烘焙中止。");
                return null;
            }

            string dir = Path.GetDirectoryName(assetPath);
            if (!EnsureFolder(dir))
            {
                Debug.LogError("[BambooHitFxPrefabBaker] 目录创建失败：" + dir);
                return null;
            }

            Material sharedMat = EnsureBakedMaterial();
            if (sharedMat == null)
            {
                Debug.LogError(
                    "[BambooHitFxPrefabBaker] 无法创建粒子材质（Sprites/Default 与 " +
                    "Legacy Shaders/Particles/Alpha Blended 都找不到），烘焙中止。");
                return null;
            }

            GameObject root = null;
            GameObject saved = null;

            try
            {
                // 复用运行时同一份构造实现，保证参数一致。
                root = BambooVfx.CreateFxTemplate(
                    FxRootName, DefaultRadius, DefaultDepthAxis, sharedMat);

                if (root == null)
                {
                    Debug.LogError("[BambooHitFxPrefabBaker] CreateFxTemplate 返回 null，烘焙中止。");
                    return null;
                }

                PrepareForBake(root, sharedMat);

                saved = PrefabUtility.SaveAsPrefabAsset(root, assetPath);
            }
            finally
            {
                // 无论成败都别把临时对象留在场景里。
                if (root != null)
                {
                    Object.DestroyImmediate(root);
                }
            }

            if (saved == null)
            {
                Debug.LogError("[BambooHitFxPrefabBaker] SaveAsPrefabAsset 失败：" + assetPath);
                return null;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return saved;
        }

        /// <summary>
        /// 存盘前的整理：
        /// 1) 把所有粒子停下来并清空，避免把「正在播放的中间态」烘进 prefab；
        /// 2) playOnAwake 置 true —— BambooVfx.SpawnFx 走 prefab 分支时是直接 Instantiate，
        ///    虽然它也会补一刀 Play，但资产本身自洽更好（手动拖进场景也能看到效果）；
        /// 3) 把运行时临时材质（HideFlags.DontSave，存不进 prefab）替换成持久化材质资产。
        /// </summary>
        /// <param name="root">待烘焙的层级根节点。</param>
        /// <param name="sharedMat">持久化的粒子材质资产。</param>
        private static void PrepareForBake(GameObject root, Material sharedMat)
        {
            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null)
                {
                    continue;
                }

                ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Clear(false);

                ParticleSystem.MainModule main = ps.main;
                main.playOnAwake = true;
            }

            ParticleSystemRenderer[] renderers =
                root.GetComponentsInChildren<ParticleSystemRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                ParticleSystemRenderer psr = renderers[i];
                if (psr == null)
                {
                    continue;
                }

                Material cur = psr.sharedMaterial;
                // 只要不是磁盘上的资产（null / 运行时 new 出来的 DontSave 材质），
                // 一律换成烘焙材质，否则 prefab 存出来会是空材质 → 粉红。
                if (cur == null || !EditorUtility.IsPersistent(cur))
                {
                    psr.sharedMaterial = sharedMat;
                }
            }
        }

        /// <summary>
        /// 取得（必要时创建）粒子用的持久化材质资产。
        /// 与运行时兜底一致：优先 Sprites/Default，退到 Legacy 粒子 shader。
        /// </summary>
        /// <returns>材质资产；shader 都找不到时返回 null。</returns>
        private static Material EnsureBakedMaterial()
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (existing != null)
            {
                return existing;
            }

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            }
            if (shader == null)
            {
                return null;
            }

            if (!EnsureFolder(PrefabDir))
            {
                return null;
            }

            Material mat = new Material(shader);
            mat.name = "MAT_BambooHitFx";
            AssetDatabase.CreateAsset(mat, MaterialPath);
            AssetDatabase.SaveAssets();

            return AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        }

        // =====================================================================
        // 小工具
        // =====================================================================

        /// <summary>
        /// 逐级确保 AssetDatabase 里存在该文件夹（AssetDatabase.CreateFolder 不递归）。
        /// </summary>
        /// <param name="folder">形如 "Assets/A/B/C" 的目录（可含反斜杠，内部会归一化）。</param>
        /// <returns>目录存在或创建成功返回 true。</returns>
        private static bool EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return false;
            }

            string normalized = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(normalized))
            {
                return true;
            }

            string[] parts = normalized.Split('/');
            if (parts.Length == 0 || parts[0] != "Assets")
            {
                return false;
            }

            string cur = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(cur, parts[i]);
                    if (string.IsNullOrEmpty(guid))
                    {
                        return false;
                    }
                }
                cur = next;
            }

            return AssetDatabase.IsValidFolder(normalized);
        }

        /// <summary>存在则删除该资产。</summary>
        /// <param name="assetPath">资产路径。</param>
        /// <returns>确实删掉了返回 true。</returns>
        private static bool DeleteIfExists(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            if (AssetDatabase.LoadAssetAtPath<Object>(assetPath) == null)
            {
                return false;
            }

            return AssetDatabase.DeleteAsset(assetPath);
        }
    }
}
