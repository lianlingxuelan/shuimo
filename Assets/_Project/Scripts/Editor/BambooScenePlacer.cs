// -----------------------------------------------------------------------------
// BambooScenePlacer.cs —— 竹林「打开工程即存在」自动放置（feature/2.5d，轮次 D/E）
//
// 菜单入口（Unity 顶部）：
//   Shuimo / 2.5D / 放置竹林 BambooSceneContext          ← 手动往当前场景加一个竹林宿主
//   Shuimo / 2.5D / 移除竹林 BambooSceneContext          ← 移除（先 Unload 还原雾，再删宿主）
//   Shuimo / 2.5D / 自动放置竹林（默认勾选）             ← 开关：开时，打开工程/重新编译后
//                                                        自动把竹林加进 SampleScene，零手动操作
//
// 【为什么是脚本自动放置，而不是直接手改 SampleScene.unity】
//   SampleScene.unity 有 9.7 万行，且结尾有 SceneRoots 注册表。盲改 YAML 一旦
//   笔误会让整场景加载失败，而本交付环境没有 Unity 无法验证。用编辑器 API 添加
//   组件由 Unity 自己完成序列化（自动处理 SceneRoots / 字段默认值 / 引用），
//   零破坏风险——这也是本项目 Shuimo/2.5D/ 菜单（Apply Ink Bamboo Materials、
//   烘焙 BambooHitFx.prefab）的一贯做法。
//
//   BambooSceneContext.OnEnable 在 PlayMode 下会自动调 Load() 生成竹林，
//   所以「组件一挂上场景、进入 PlayMode，竹子就出现」。本工具只负责把组件挂到
//   常驻 GameObject、并把真·水墨 bamboo_ink.fbx 套好材质接上。用户打开工程即可见
//   一个 BambooGrove 宿主，进 PlayMode 即见竹林，无需任何手动点击。
//
// 【自动放置的边界（避免「删了又回」的纠缠）】
//   - 仅对 SampleScene 生效，不污染其他场景；
//   - 已存在则跳过（幂等）；
//   - 不在 PlayMode 下操作场景；编辑态也不强制生成（避免「双份竹林」）；
//   - 通过 EditorPrefs 记住「用户是否关闭自动放置」：用户若手动 Remove 并关掉开关，
//     之后不再自动加回来。想恢复，重新勾选开关即可。
//
// 【BambooSceneContext 是什么】
//   运行时程序化生成竹林的组件（PlayMode 下自动生成 20–30 根水墨竹 + 地面 + 雾）。
//   优先用套好水墨材质的 bamboo_ink.fbx（路线 A，最高保真、零 primitives/格子）；
//   fbx 缺失时才退回 primitives + 运行时 Ink 材质兜底（路线 B）。
//
// 【红线】纯 Editor 工具：不碰 Combat/**、WorldBuilder、不写 timeScale /
// Scheduler / RunPhase，不引用 DamageResolver，不写 FeedbackClock.Frozen。
// -----------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Xianxia.Unity.T2;

namespace Shuimo.EditorTools
{
    /// <summary>
    /// 把 <see cref="BambooSceneContext"/> 放进 / 移出当前场景的编辑器菜单，
    /// 并支持「打开工程即自动放置」。
    /// </summary>
    public static class BambooScenePlacer
    {
        private const string MenuPlace = "Shuimo/2.5D/放置竹林 BambooSceneContext";
        private const string MenuAssignHeroRock = "Shuimo/2.5D/接入 Blender 太湖石到当前竹林";
        private const string MenuRemove = "Shuimo/2.5D/移除竹林 BambooSceneContext";
        private const string MenuToggle = "Shuimo/2.5D/自动放置竹林";
        private const string HostName = "BambooGrove";
        private const string FbxPath = "Assets/_Project/Art/Bamboo/Generated/InkBambooHero.fbx";
        private const string HeroRockFbxPath = "Assets/_Project/Art/Rocks/Generated/InkScholarRock_v1.fbx";
        private const string HeroRockSpritePath = "Assets/_Project/Resources/Environments/ink_scholar_rock_wash_v1.png";
        private const string BambooClumpSpritePath = "Assets/_Project/Resources/Environments/ink_bamboo_clump_v1.png";
        private const string HarvestBambooSpritePath = "Assets/_Project/Resources/Environments/ink_harvest_bamboo_v1.png";
        private const string BambooStumpSpritePath = "Assets/_Project/Resources/Environments/ink_bamboo_stump_v1.png";
        private const string YoungBambooSpritePath = "Assets/_Project/Resources/Environments/ink_young_bamboo_v1.png";
        private const string BambooShootsSpritePath = "Assets/_Project/Resources/Environments/ink_bamboo_shoots_v1.png";
        private const string TargetSceneName = "SampleScene";
        private const string AutoKey = "Shuimo.2_5D.AutoBamboo";

        // =====================================================================
        // 自动放置：打开工程 / 重新编译后由 Unity 触发
        // =====================================================================

        [InitializeOnLoadMethod]
        private static void AutoPlaceOnLoad()
        {
            // 延迟到编辑器空闲、场景已加载后再尝试，避免时序问题。
            EditorApplication.delayCall += TryAutoPlace;
        }

        private static void TryAutoPlace()
        {
            if (Application.isPlaying)
            {
                return;
            }

            // 仅对 SampleScene 生效，避免污染其他场景。
            if (SceneManager.GetActiveScene().name != TargetSceneName)
            {
                return;
            }

            // 用户关闭了自动放置 → 不再自动加。
            if (!EditorPrefs.GetBool(AutoKey, true))
            {
                return;
            }

            // 已存在则升级为当前的英雄竹丛资源（幂等），不再保留历史 FBX 引用。
            BambooSceneContext existing = Object.FindObjectOfType<BambooSceneContext>();
            if (existing != null)
            {
                ConfigureGeneratedHeroModel(existing);
                return;
            }

            PlaceInternal(true);
        }

        // =====================================================================
        // 菜单：手动放置 / 移除 / 开关
        // =====================================================================

        [MenuItem(MenuPlace)]
        public static void Place()
        {
            PlaceInternal(false);
        }

        private static void PlaceInternal(bool silent)
        {
            BambooSceneContext existing = Object.FindObjectOfType<BambooSceneContext>();
            if (existing != null)
            {
                ConfigureGeneratedHeroModel(existing);
                Selection.activeGameObject = existing.gameObject;
                if (!silent)
                {
                    EditorUtility.DisplayDialog(
                        "竹林",
                        "当前场景已存在 BambooSceneContext，已为你选中。\n如需重建请先『移除竹林』再『放置竹林』。",
                        "OK");
                }
                return;
            }

            GameObject go = new GameObject(HostName);
            BambooSceneContext ctx = go.AddComponent<BambooSceneContext>();

            // 若工程里已有 bamboo_ink.fbx：先程序化套水墨材质（确保是真·水墨而非灰模/粉红），
            // 再接成 bambooModelPrefab（路线 A，最高保真，零 primitives/格子）。
            // fbx 缺失或套材质失败时，组件退回 primitives + 运行时 Ink 材质兜底（路线 B）。
            bool usedModel = ConfigureGeneratedHeroModel(ctx);

            Selection.activeGameObject = go;
            EditorUtility.SetDirty(go);

            // 不在编辑态强制 Load()：竹林改为 PlayMode 时由 OnEnable 生成，
            // 避免运行时子物体被序列化进场景、重载后 OnEnable 再生成一次导致「双份竹林」。

            if (silent)
            {
                Debug.Log(string.Format(
                    "[Shuimo/2.5D] 已自动放置竹林（{0}）。打开工程即生效，无需手动操作；保存场景后持久化。",
                    usedModel ? "已接入 bamboo_ink 模型" : "走 primitives 兜底"));
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "竹林",
                    string.Format(
                        "[Shuimo/2.5D] 已放置 BambooSceneContext（{0}）。\n保存场景后持久化；" +
                        "进入 PlayMode 或当前 Scene 视图即可看到竹林。\n（已开启『自动放置竹林』，下次打开工程会自动出现。）",
                        usedModel ? "已接入 bamboo_ink 模型" : "走 primitives 兜底"),
                    "OK");
            }
        }

        /// <summary>把旧场景里遗留的 bamboo_ink 引用无损切换到本轮生成的英雄竹丛。</summary>
        private static bool ConfigureGeneratedHeroModel(BambooSceneContext context)
        {
            if (context == null)
            {
                return false;
            }

            ConfigureHeroScholarRock(context);
            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            if (fbx == null)
            {
                return false;
            }

            BambooInkImporter.ApplyToModel(FbxPath);
            bool changed = context.bambooModelPrefab != fbx
                || context.forcePrimitiveBamboo
                || !context.bambooModelIncludesLeafClusters;
            context.bambooModelPrefab = fbx;
            context.forcePrimitiveBamboo = false;
            context.bambooModelIncludesLeafClusters = true;
            if (changed)
            {
                EditorUtility.SetDirty(context);
                Debug.Log("[Shuimo/2.5D] 已把 BambooGrove 更新为 InkBambooHero 正式竹丛。");
            }
            return true;
        }

        /// <summary>把本地 Blender 导出的太湖石接入场景宿主，不触碰用户现有对象。</summary>
        private static void ConfigureHeroScholarRock(BambooSceneContext context)
        {
            EnsureSpriteImport(HeroRockSpritePath);
            EnsureSpriteImport(BambooClumpSpritePath);
            EnsureSpriteImport(HarvestBambooSpritePath);
            EnsureSpriteImport(BambooStumpSpritePath);
            EnsureSpriteImport(YoungBambooSpritePath);
            EnsureSpriteImport(BambooShootsSpritePath);
            GameObject rock = AssetDatabase.LoadAssetAtPath<GameObject>(HeroRockFbxPath);
            if (context == null || rock == null || context.heroRockModelPrefab == rock)
            {
                return;
            }

            context.heroRockModelPrefab = rock;
            EditorUtility.SetDirty(context);
            Debug.Log("[Shuimo/2.5D] 已接入 Blender 太湖石：道路右侧主景。 ");
        }

        private static void EnsureSpriteImport(string assetPath)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null || importer.textureType == TextureImporterType.Sprite)
            {
                return;
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100.0f;
            importer.alphaIsTransparency = true;
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        [MenuItem(MenuAssignHeroRock)]
        public static void AssignHeroRockToCurrentGrove()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("[Shuimo/2.5D] 请先退出 Play 模式，再接入太湖石引用。 ");
                return;
            }

            BambooSceneContext context = Object.FindObjectOfType<BambooSceneContext>();
            if (context == null)
            {
                Debug.LogError("[Shuimo/2.5D] 当前场景没有 BambooSceneContext，无法接入太湖石。 ");
                return;
            }

            ConfigureHeroScholarRock(context);
            EditorUtility.SetDirty(context);
        }

        [MenuItem(MenuRemove)]
        public static void Remove()
        {
            BambooSceneContext existing = Object.FindObjectOfType<BambooSceneContext>();
            if (existing == null)
            {
                EditorUtility.DisplayDialog("竹林", "当前场景没有 BambooSceneContext。", "OK");
                return;
            }

            // 先让组件自行 Unload（还原雾设置、销毁生成的子树），再删宿主 GameObject。
            existing.Unload();
            Object.DestroyImmediate(existing.gameObject);
            Debug.Log("[Shuimo/2.5D] 已移除竹林（含还原场景雾设置）。");
        }

        // EditorPrefs 开关：菜单项带勾选状态，用户可随时关闭自动放置。
        [MenuItem(MenuToggle)]
        private static void ToggleAuto()
        {
            bool next = !EditorPrefs.GetBool(AutoKey, true);
            EditorPrefs.SetBool(AutoKey, next);
            Debug.Log(string.Format("[Shuimo/2.5D] 自动放置竹林已{0}。", next ? "开启" : "关闭"));
        }

        [MenuItem(MenuToggle, true)]
        private static bool ToggleAutoValidate()
        {
            Menu.SetChecked(MenuToggle, EditorPrefs.GetBool(AutoKey, true));
            return true;
        }
    }
}
