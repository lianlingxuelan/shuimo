using System.IO;
using UnityEditor;
using UnityEngine;

namespace Shuimo.EditorTools
{
    /// <summary>
    /// 修复 Blender 导出的水墨竹林 FBX 在 Unity 里"一丢丢"（缩小约 100 倍）的问题。
    ///
    /// 根因：Blender 5.x 以米为单位写出顶点坐标（竹子 5~10、地面 50×50），
    /// 但 FBX 头部声明 UnitScaleFactor=100。Unity 导入器默认按文件单位换算，
    /// 把模型整体除以 100，竹子因此比一个网格还小。
    ///
    /// 本后处理在导入时强制关闭文件单位换算（useFileScale=false，globalScale=1），
    /// 让 1 原始单位 = 1 Unity 米，几何按真实尺寸进入场景，无需任何手动设置。
    ///
    /// 配合菜单「Shuimo > 2.5D > Reimport Ink Bamboo FBX」可对已导入的模型一键重导。
    /// </summary>
    public sealed class BambooInkFbxImportFix : AssetPostprocessor
    {
        private const string TargetFileName = "bamboo_ink.fbx";

        private void OnPreprocessModel()
        {
            if (Path.GetFileName(assetPath) != TargetFileName)
            {
                return;
            }

            var importer = assetImporter as ModelImporter;
            if (importer == null)
            {
                return;
            }

            importer.useFileScale = false;
            importer.globalScale = 1f;

            Debug.Log("[Shuimo/2.5D] " + assetPath + " 已按 1:1 导入（关闭 FBX 文件单位换算）。");
        }

        /// <summary>
        /// 一键重导 FBX：写好后处理器后点一次，即可让已拖进项目的模型按正确尺寸刷新。
        /// 菜单：Shuimo > 2.5D > Reimport Ink Bamboo FBX
        /// </summary>
        [MenuItem("Shuimo/2.5D/Reimport Ink Bamboo FBX")]
        public static void ReimportInkBamboo()
        {
            const string fbxPath = "Assets/_Project/Art/Bamboo/bamboo_ink.fbx";
            if (File.Exists(fbxPath))
            {
                AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);
                Debug.Log("[Shuimo/2.5D] 已重新导入 " + fbxPath + "，竹子应按真实尺寸出现。");
            }
            else
            {
                EditorUtility.DisplayDialog("Reimport Ink Bamboo",
                    "未找到 " + fbxPath + "。请确认 FBX 已放在 Assets/_Project/Art/Bamboo/ 目录下。",
                    "确定");
            }
        }
    }
}
