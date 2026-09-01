using System.IO;
using UnityEditor;
using UnityEngine;

namespace Shuimo.EditorTools
{
    /// <summary>从正在运行的 Game 画面保存白衣角色的本地验证截图。</summary>
    public static class WhiteHeroineGameCapture
    {
        public const string CapturePath = "Library/WhiteHeroineVerification.png";

        [MenuItem("Shuimo/2.5D/保存白衣角色验证截图", false, 224)]
        public static void Capture()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[WhiteHeroineCapture] 请先进入 Play 模式后再截图。");
                return;
            }

            string absolutePath = Path.GetFullPath(CapturePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            ScreenCapture.CaptureScreenshot(absolutePath, 1);
            Debug.Log("[WhiteHeroineCapture] 已请求截图：" + absolutePath);
        }
    }
}
