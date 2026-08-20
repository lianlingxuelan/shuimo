// -----------------------------------------------------------------------------
// BambooEditorPreview.cs —— 编辑态预览竹林 + 水墨氛围（feature/2.5d）
//
// 痛点：BambooSceneContext.OnEnable 故意只在 PlayMode 生成（避免把运行时子物体
// 序列化进场景导致「双份竹林」）。但这样在编辑器 Scene 视图里永远看不到竹林 /
// 水墨地面 / 天空远山，只能盲改。
//
// 本工具提供两个菜单，让你**不进 PlayMode** 也能看到成品：
//   Shuimo / 2.5D / 预览竹林(编辑器)      ← 生成竹林 + 水墨地面 + 雾 + 氛围层
//   Shuimo / 2.5D / 清除竹林预览          ← 清掉预览（Unload + 删氛围 + 关雾）
//
// 【重要】预览是临时性的：生成的子物体若被你保存进场景，下一次域重载 _loaded 会
// 复位而子物体还在，进入 Play 时 Load() 会先清掉旧根再重建（已做幂等保护）。
// 但为避免意外序列化，建议预览完就点「清除竹林预览」再保存。
// -----------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;
using Xianxia.Unity.T2;

namespace Shuimo.EditorTools
{
    public static class BambooEditorPreview
    {
        [MenuItem("Shuimo/2.5D/预览竹林(编辑器)")]
        static void Preview()
        {
            if (Application.isPlaying) return;

            // 复用场景里已有的 BambooSceneContext（如自动放置的那个），避免双份宿主。
            BambooSceneContext ctx = Object.FindObjectOfType<BambooSceneContext>();
            if (ctx == null)
            {
                var go = new GameObject("BambooPreviewHost");
                ctx = go.AddComponent<BambooSceneContext>();
            }
            ctx.Load();

            // 同步注入水墨氛围层（天空 + 远山 + 雾）。
            if (Object.FindObjectOfType<AtmosphereLayer>() == null)
            {
                var ago = new GameObject("AtmosphereLayer");
                ago.AddComponent<AtmosphereLayer>().Build();
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log("[Shuimo/2.5D] 编辑器预览已生成（竹林 + 水墨地面 + 天空/远山/雾）。退出前请「清除竹林预览」。");
        }

        [MenuItem("Shuimo/2.5D/清除竹林预览")]
        static void Clear()
        {
            var ctx = Object.FindObjectOfType<BambooSceneContext>();
            if (ctx != null) ctx.Unload();

            var atm = Object.FindObjectOfType<AtmosphereLayer>();
            if (atm != null) Object.DestroyImmediate(atm.gameObject);

            RenderSettings.fog = false;
            Debug.Log("[Shuimo/2.5D] 编辑器预览已清除，雾已关闭。");
        }
    }
}
