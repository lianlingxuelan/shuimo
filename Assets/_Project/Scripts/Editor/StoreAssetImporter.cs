// -----------------------------------------------------------------------------
// StoreAssetImporter.cs —— 把已导入的 Unity 商店资源生成可用的运行时 prefab
// （feature/2.5d，轮次：商店资源接入）
//
// 触发：菜单 Shuimo/Store/Generate Prefabs（用户在 Unity 里点一次即可）。
// 用途：把裸 FBX（Char_Feng 角色、VVayToyek 国风剑）转成带材质/动画控制器的
//       prefab，并落到 _Project/Resources/{Enemies,Weapons}/ 下，使运行时能用
//       Resources.Load 直接拿到，零运行时 Editor 依赖。
//
// 【设计红线】
//   1. 本文件是 Editor-only（namespace Shuimo.EditorTools，依赖 Xianxia.Unity.T2.Editor
//      asmdef）。绝不引用任何运行时战斗内核，也不在打包后参与。
//   2. 只「新增」资源文件，绝不删除/覆盖用户已有的任何资源（含原图、HeroineBone、
//      boss/witch/elder/musician 占位）。生成目标是独立的新文件名。
//   3. 自动把 FBX 按游戏单位缩放（角色 ~220、剑 ~200），消除运行时比例猜测。
//
// 【为什么用 Resources 而非拖引用】
//   运行时 EnemyNpcSpawner / PlayerWeaponRig 无法在某个具体 ScriptableObject 资产里
//   预设这些新 prefab 引用（那需要手改资产或额外 Editor 接线）。统一约定放 Resources
//   下，运行时按固定字符串路径 Load，最省事、最稳。
// -----------------------------------------------------------------------------

using System.IO;
using UnityEditor;
using UnityEngine;

namespace Shuimo.EditorTools
{
    /// <summary>把已导入的商店 FBX 转成带材质/动画的运行时 prefab。</summary>
    public static class StoreAssetImporter
    {
        private const string FengFbx = "Assets/Char_Feng/Fbx/Feng.fbx";
        private const string FengController = "Assets/Char_Feng/Animation/Feng.controller";
        private const string FengTexture = "Assets/Char_Feng/Fbx/Material_Pbr_Diffuse.jpg";

        private const string SwordPackRoot = "Assets/VVayToyek/中式玄幻剑合集包1";

        // 中文目录名 → 生成的 prefab 英文名（Resources.Load 用英文名，避免路径歧义）。
        private static readonly (string dir, string prefab)[] Swords = new[]
        {
            ("桃木剑", "TaomuSword"),
            ("窥月剑", "KuiyueSword"),
            ("阳魂剑", "YanghunSword"),
            ("阴魄剑", "YinpoSword"),
        };

        [MenuItem("Shuimo/Store/Generate Prefabs")]
        public static void GeneratePrefabs()
        {
            EnsureDir("Assets/_Project/Resources/Enemies");
            EnsureDir("Assets/_Project/Resources/Weapons");
            AssetDatabase.Refresh();

            int ok = 0;
            if (GenerateFeng()) ok++;
            foreach (var s in Swords)
            {
                if (GenerateSword(s.dir, s.prefab)) ok++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(string.Format(
                "[StoreAssetImporter] 商店 prefab 生成完成：成功 {0}/5（FengEnemy + 4 把剑）。" +
                "已放入 Resources，运行时可直接 Load。失败项见上方 Warn。", ok));
        }

        // ---------------------------------------------------------------------
        // 工具
        // ---------------------------------------------------------------------

        private static void EnsureDir(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        private static float MeasureHeight(GameObject go)
        {
            Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return 0.0f;
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++)
            {
                b.Encapsulate(rs[i].bounds);
            }
            return Mathf.Abs(b.size.y) > 1e-4f ? b.size.y : Mathf.Max(b.size.x, b.size.z);
        }

        private static float MeasureLongest(GameObject go)
        {
            Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return 0.0f;
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++)
            {
                b.Encapsulate(rs[i].bounds);
            }
            return Mathf.Max(b.size.x, b.size.y, b.size.z);
        }

        /// <summary>给 MeshRenderer 套一个带贴图的标准材质（Built-in RP 原生支持）。</summary>
        private static void ApplyTextureMaterial(GameObject inst, string texPath)
        {
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            MeshRenderer mr = inst.GetComponentInChildren<MeshRenderer>(true);
            if (mr == null) return;

            if (tex != null)
            {
                Material m = new Material(Shader.Find("Standard"));
                m.mainTexture = tex;
                m.name = "MAT_StoreImport_Runtime";
                // 水墨场景偏素，降一点光滑度避免塑料感。
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.25f);
                mr.sharedMaterial = m;
            }
            else
            {
                Debug.LogWarning("[StoreAssetImporter] 贴图缺失，保留 FBX 自带材质：" + texPath);
            }
        }

        // ---------------------------------------------------------------------
        // Feng 角色
        // ---------------------------------------------------------------------

        private static bool GenerateFeng()
        {
            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FengFbx);
            if (fbx == null)
            {
                Debug.LogWarning("[StoreAssetImporter] 找不到 " + FengFbx + "，跳过 FengEnemy。");
                return false;
            }

            GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            if (inst == null) return false;

            // 自动缩放：量包围盒高度，拉到 ~220 单位（与竹林/小怪尺度一致）。
            float h = MeasureHeight(inst);
            if (h > 1e-3f)
            {
                float s = 220.0f / h;
                inst.transform.localScale = new Vector3(s, s, s);
            }

            // 动画控制器（Feng.controller 含 Idle/Walk/Death/Boxing 等）。
            RuntimeAnimatorController ctrl =
                AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(FengController);
            Animator anim = inst.GetComponent<Animator>() ?? inst.AddComponent<Animator>();
            if (ctrl != null) anim.runtimeAnimatorController = ctrl;

            ApplyTextureMaterial(inst, FengTexture);

            string outPath = "Assets/_Project/Resources/Enemies/FengEnemy.prefab";
            PrefabUtility.SaveAsPrefabAsset(inst, outPath);
            Object.DestroyImmediate(inst);
            Debug.Log("[StoreAssetImporter] 已生成 " + outPath);
            return true;
        }

        // ---------------------------------------------------------------------
        // 国风剑
        // ---------------------------------------------------------------------

        private static bool GenerateSword(string dirName, string prefabName)
        {
            string dir = SwordPackRoot + "/" + dirName;
            string fbxPath = dir + "/" + dirName + ".fbx";
            string pngPath = dir + "/" + dirName + "贴图.png";

            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbx == null)
            {
                Debug.LogWarning("[StoreAssetImporter] 找不到 " + fbxPath + "，跳过 " + prefabName + "。");
                return false;
            }

            GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            if (inst == null) return false;

            // 自动缩放：取最长边，拉到 ~70 单位（约为角色身高 220 的 1/3，避免 40 米大刀）。
            float len = MeasureLongest(inst);
            if (len > 1e-3f)
            {
                float s = 70.0f / len;
                inst.transform.localScale = new Vector3(s, s, s);
            }

            ApplyTextureMaterial(inst, pngPath);

            string outPath = "Assets/_Project/Resources/Weapons/" + prefabName + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(inst, outPath);
            Object.DestroyImmediate(inst);
            Debug.Log("[StoreAssetImporter] 已生成 " + outPath);
            return true;
        }
    }
}
