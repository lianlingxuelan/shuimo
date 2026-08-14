# Route A 自测报告

> 由 `Shuimo/2.5D/运行 Route A 自测` 生成。扫描范围仅 `Assets/_Project/Scripts/Runtime/2.5D/*.cs`。

| 1 | WARN | 缺失项：   - Xianxia/Ink/BambooTrunk（未加入 Always Included Shaders）   - Xianxia/Ink/BambooLeaf（未加入 Always Included Shaders）   - Xianxia/Ink/InkGround（未加入 Always Included Shaders） 指引：Project Settings → Graphics → Always Included Shaders 添加对应行；否则真打包时 Shader.Find 返回 null（Editor PlayMode 不受影响但打包黑屏）。 |
| 2 | WARN | 未找到 BambooHitFx.prefab（Prefabs/ 与 Resources/2.5D/ 均无）。 指引：菜单 Shuimo/2.5D/烘焙 BambooHitFx.prefab（BambooHitFxPrefabBaker）烘焙，或提供 Resources/2.5D/ 副本。缺失时 BambooVfx 走运行时构造兜底，不影响功能。 |
| 3 | PASS | Runtime/2.5D/*.cs 红线 0 命中（已注释剥离）。 |
| 4 | PASS | Assets/_Project/Art/Bamboo/bamboo_ink.fbx globalScale == 1 且 useFileScale == false。 |
| 汇总 | 结果 |
|---|---|
| 汇总 | PASS 2 / WARN 2 / FAIL 0 |

_生成于：2026-08-14 00:03:13_
