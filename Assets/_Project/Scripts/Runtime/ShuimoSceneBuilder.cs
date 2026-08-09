// -----------------------------------------------------------------------------
// ShuimoSceneBuilder.cs —— 场景内容重建的统一入口（asmdef: Xianxia.Unity.T2）
//
// 【它是什么】
// 编辑器「一键清理 + 重建」工具和实际生成逻辑之间的解耦点：工具只负责删旧对象，
// 真正「怎么造世界」由世界生成器（WorldBuilder）注册到 BuildScene 委托上。
//
// 【为什么用静态委托而不是写死】
// 让「谁拥有生成逻辑」和「怎么触发清理」彻底分开：工具不需要知道世界长什么样，
// 世界生成器也不需要知道编辑器菜单长什么样。两边都只认 BuildScene / BuildAll 这一条路。
// -----------------------------------------------------------------------------

using System;

namespace Shuimo
{
    /// <summary>
    /// 场景内容构建器的统一入口。
    ///
    /// 这是编辑器工具「一键清理+重建」和实际游戏构建逻辑之间的解耦点：
    /// 工具只负责「删掉 ShuimoGenerated 标记的旧对象」，然后调用 BuildScene；
    /// 具体的生成逻辑由场景内容的拥有方（WorkBuddy 的代码）挂进来。
    ///
    /// 接入方式（WorkBuddy 在任意构建类里，例如某处 static 初始化）：
    ///     ShuimoSceneBuilder.BuildScene = () => { /* 重建整个世界 */ };
    ///
    /// 如果不想用静态委托，也可以直接把构建逻辑写进 BuildAll()。
    /// 两种方式二选一，保持一致即可。
    /// </summary>
    public static class ShuimoSceneBuilder
    {
        /// <summary>重建场景内容的回调。由实际生成逻辑注册。</summary>
        public static Action BuildScene;

        /// <summary>
        /// 主动触发一次场景内容重建。
        /// 编辑器工具和运行时都用这个方法，保证走同一条路径。
        /// </summary>
        public static void BuildAll()
        {
            if (BuildScene != null)
            {
                BuildScene();
            }
        }
    }
}
