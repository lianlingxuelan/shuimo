using UnityEditor;
using UnityEngine;
using Xianxia.Unity.T2;

namespace Shuimo.EditorTools
{
    /// <summary>确保白衣 cutout 骨骼角色有一个可被 WorldBuilder 加载的 Resources Prefab。</summary>
    public static class WhiteHeroineCutoutPrefabBuilder
    {
        private const string PrefabPath = "Assets/_Project/Resources/HeroineWhiteCutout.prefab";

        [InitializeOnLoadMethod]
        private static void EnsureOnLoad()
        {
            EditorApplication.delayCall += EnsurePrefab;
        }

        [MenuItem("Shuimo/2.5D/生成白衣 Cutout 骨骼Prefab", false, 223)]
        public static void EnsurePrefab()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
            {
                return;
            }

            GameObject root = new GameObject("HeroineWhiteCutout");
            // 保留 root Renderer，既兼容玩家受击组件，也不参与实际绘制。
            root.AddComponent<SpriteRenderer>();
            root.AddComponent<WhiteHeroineCutoutView>();
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            Debug.Log("[WhiteHeroineCutout] 已生成白衣分层骨骼 Prefab。");
        }
    }
}
