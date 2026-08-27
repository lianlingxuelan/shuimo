using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Shuimo.EditorTools
{
    /// <summary>
    /// 2.5D 水墨竹林一键套材质工具(feature/2.5d 分支)。
    ///
    /// 背景:Blender 导出的 BambooInk.fbx 只带几何,不带水墨节点材质,
    /// Unity 导入后是灰白 Standard 材质。本工具按物体命名把三个
    /// 水墨 Shader(Xianxia/Ink/...)自动套到对应 MeshRenderer 上:
    ///     *_trunk_* / *_node_* / *_branch_* -> BambooTrunk(墨色渐变)
    ///     *_leaf_*                          -> BambooLeaf(半透明晕染)
    ///     Ground                            -> InkGround(纸纹淡墨)
    ///     Fog_*                             -> InkFog(淡墨薄雾)
    ///
    /// 菜单入口(Unity 顶部):
    ///     Shuimo > 2.5D > Apply Ink Bamboo Materials   ← 对选中的 FBX 根节点套用
    ///
    /// 材质会按需创建并保存到 Assets/_Project/Art/Materials/Ink/,可反复点(幂等)。
    /// </summary>
    public static class BambooInkImporter
    {
        private const string InkFolder = "Assets/_Project/Art/Materials/Ink";

        [MenuItem("Shuimo/2.5D/Apply Ink Bamboo Materials")]
        public static void ApplyInkBamboo()
        {
            var roots = Selection.gameObjects;
            if (roots == null || roots.Length == 0)
            {
                EditorUtility.DisplayDialog("Apply Ink Bamboo",
                    "请先在场景/Hierarchy 中选中导入的 BambooInk FBX 根节点(可多选)。", "OK");
                return;
            }

            EnsureInkFolder();
            int applied = 0;
            foreach (GameObject root in roots)
            {
                applied += ApplyRecursively(root.transform);
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[Shuimo/2.5D] 已为 " + applied + " 个 MeshRenderer 套用水墨材质。");
        }

        /// <summary>
        /// 对工程内的 FBX 模型资产（而非场景实例）套水墨材质。
        /// 供 <see cref="BambooScenePlacer"/> 在「自动放置竹林」时调用，
        /// 保证用的是真·水墨模型而非灰模/粉红，更不是 primitives 格子。
        /// 幂等：材质已存在则复用并刷新默认值。等价于在 Project 窗口选中该 fbx 后
        /// 点菜单 Shuimo/2.5D/Apply Ink Bamboo Materials。
        /// </summary>
        /// <param name="assetPath">FBX 资产路径，如 Assets/_Project/Art/Bamboo/bamboo_ink.fbx。</param>
        public static void ApplyToModel(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (model == null)
            {
                Debug.LogWarning("[Shuimo/2.5D] ApplyToModel 找不到资产： " + assetPath);
                return;
            }

            EnsureInkFolder();
            int applied = ApplyRecursively(model.transform);
            AssetDatabase.SaveAssets();
            Debug.Log(string.Format(
                "[Shuimo/2.5D] 已为 {0} 的 {1} 个 MeshRenderer 套用水墨材质。", assetPath, applied));
        }

        // ======================= 递归套用 =======================

        private static int ApplyRecursively(Transform node)
        {
            int applied = 0;
            var mr = node.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                Material mat = MaterialFor(node.name);
                if (mat != null)
                {
                    mr.sharedMaterial = mat;
                    mr.shadowCastingMode = (mat.name.Contains("Leaf") || mat.name.Contains("Fog"))
                        ? UnityEngine.Rendering.ShadowCastingMode.Off
                        : UnityEngine.Rendering.ShadowCastingMode.On;
                    applied++;
                }
            }

            for (int i = 0; i < node.childCount; i++)
            {
                applied += ApplyRecursively(node.GetChild(i));
            }

            return applied;
        }

        // ======================= 材质解析 =======================

        private static Material MaterialFor(string objectName)
        {
            if (objectName == null)
            {
                return null;
            }

            if (objectName.Contains("Ground"))
            {
                return GetOrCreate("MAT_Ink_Ground", "Xianxia/Ink/InkGround", ApplyGroundDefaults);
            }

            if (objectName.Contains("Fog"))
            {
                return GetOrCreate("MAT_Ink_Fog", "Xianxia/Ink/BambooLeaf", ApplyFogDefaults);
            }

            if (objectName.Contains("leaf") || objectName.Contains("Leaf"))
            {
                return GetOrCreate("MAT_Ink_Leaf", "Xianxia/Ink/BambooLeaf", ApplyLeafDefaults);
            }

            // trunk / node / branch 统一用竹竿水墨渐变(节点/枝是深墨段)
            if (objectName.Contains("trunk") || objectName.Contains("node") || objectName.Contains("branch"))
            {
                return GetOrCreate("MAT_Ink_Trunk", "Xianxia/Ink/BambooTrunk", ApplyTrunkDefaults);
            }

            return null;
        }

        private static Material GetOrCreate(string matName, string shaderName, System.Action<Material> setDefaults)
        {
            string path = InkFolder + "/" + matName + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    Debug.LogError("[Shuimo/2.5D] 找不到 Shader: " + shaderName +
                        "。请确认 Assets/_Project/Shaders/Ink 已导入。");
                    return null;
                }

                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }

            setDefaults(mat);
            return mat;
        }

        private static void ApplyTrunkDefaults(Material m)
        {
            m.SetColor("_InkBottom", new Color(0.05f, 0.09f, 0.06f, 1f));
            m.SetColor("_InkMid", new Color(0.11f, 0.17f, 0.10f, 1f));
            m.SetColor("_InkTop", new Color(0.17f, 0.25f, 0.15f, 1f));
            m.SetFloat("_GradStart", 0f);
            m.SetFloat("_GradEnd", 10f);
            m.SetFloat("_NoiseScale", 12f);
            m.SetFloat("_NoiseStrength", 0.20f);
            m.SetFloat("_StrokeScale", 8f);
            m.SetFloat("_StrokeStrength", 0.15f);
            m.SetFloat("_EdgeInk", 0.35f);
            m.SetFloat("_Roughness", 0.6f);
        }

        private static void ApplyLeafDefaults(Material m)
        {
            m.SetColor("_LeafColor", new Color(0.15f, 0.22f, 0.13f, 1f));
            m.SetFloat("_Alpha", 0.92f);
            m.SetFloat("_EdgeFade", 0.55f);
            m.SetFloat("_NoiseScale", 16f);
            m.SetFloat("_NoiseStrength", 0.25f);
        }

        private static void ApplyGroundDefaults(Material m)
        {
            m.SetColor("_PaperColor", new Color(0.95f, 0.94f, 0.89f, 1f));
            m.SetColor("_InkColor", new Color(0.86f, 0.85f, 0.80f, 1f));
            m.SetFloat("_BlotScale", 3f);
            m.SetFloat("_BlotStrength", 0.22f);
            m.SetFloat("_FineScale", 60f);
            m.SetFloat("_FineStrength", 0.08f);
        }

        private static void ApplyFogDefaults(Material m)
        {
            // 淡墨薄雾:复用 BambooLeaf 的透明混合,颜色换成纸色
            m.SetColor("_LeafColor", new Color(0.90f, 0.90f, 0.86f, 1f));
            m.SetFloat("_Alpha", 0.18f);
            m.SetFloat("_EdgeFade", 1f);
            m.SetFloat("_NoiseScale", 6f);
            m.SetFloat("_NoiseStrength", 0.1f);
        }

        // ======================= 目录 =======================

        private static void EnsureInkFolder()
        {
            string folder = "Assets/_Project/Art/Materials";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets/_Project/Art", "Materials");
            }

            if (!AssetDatabase.IsValidFolder(InkFolder))
            {
                AssetDatabase.CreateFolder(folder, "Ink");
            }
        }
    }
}
