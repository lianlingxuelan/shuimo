// -----------------------------------------------------------------------------
// ShuimoGenerated.cs —— 场景内容生成器的「标记组件」（asmdef: Xianxia.Unity.T2）
//
// 【它是什么】
// 一个空组件，专门用来给「由代码在运行时 / 编辑器里生成」的 GameObject 打标记。
// 编辑器的一键清理工具只删带这个标记的根节点，从而绝不误伤相机、灯光、
// 以及场景里手工摆放的对象。
//
// 【为什么用一个组件而不是记住名字】
// 用名字匹配既脆弱又难维护；挂一个标记组件，清理逻辑就成了纯「有无标记」的判断，
// 与对象叫什么、层级在哪完全解耦。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Shuimo
{
    /// <summary>
    /// 场景重建工具的标记组件。
    ///
    /// 用途：所有「由代码在运行时/编辑器里生成」的根节点，都应该挂上这个空组件。
    /// 编辑器工具 ShuimoSceneRebuilder 扫描场景时，只删除带这个标记的根节点，
    /// 从而保证相机、灯光、以及场景里手工放置的对象永远不会被误删。
    ///
    /// 使用方法（WorkBuddy 在生成对象时）：
    ///     GameObject go = new GameObject("Shuimo_ZoneRoot");
    ///     go.AddComponent<ShuimoGenerated>();   // ← 加这一行标记
    ///
    /// 或者用一个快捷方法：
    ///     ShuimoGenerated.Mark(go);
    ///
    /// 注意：这个组件是纯标记，不包含任何运行时逻辑，也不应该被序列化成数据。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShuimoGenerated : MonoBehaviour
    {
        /// <summary>给一个 GameObject 打上「由代码生成」标记，幂等。</summary>
        public static void Mark(GameObject go)
        {
            if (go != null && go.GetComponent<ShuimoGenerated>() == null)
            {
                go.AddComponent<ShuimoGenerated>();
            }
        }
    }
}
