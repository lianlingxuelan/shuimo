# 竹林 2.5D · 本地 Unity 自测清单（Round B + Route A 真实资产）

> 用途：用户在本地 Unity 2022.3.62f3c1 跑一遍，确认 `feature/2.5d` 上的本轮代码 + 新增真实水墨资产工作正常。
> 本环境无 Unity，无法编译 / PlayMode，下列全部需**用户本地执行**。
> 关联文档：`docs/bamboo-2_5d-roundB-design.md`、`docs/changelog.md`（阶段 29）、`docs/branch-merge-plan.md` §8。

## 0. 当前磁盘状态（主理人已核实）

- 分支：`feature/2.5d`（本地领先 origin 3 commit；`main` = b7c3ad7）
- Round B 代码已提交：`BambooSceneContext.cs`(50258B)、`BambooVfx.cs`(34047B)、`Editor/BambooHitFxPrefabBaker.cs`(14418B)、`docs/bamboo-2_5d-roundB-design.md`。
- **新增未提交资产（用户本地放入，即「路线 A」真实资产）**：
  - `Art/Bamboo/bamboo_ink.fbx` + 导入修复 `Editor/BambooInkFbxImportFix.cs`
  - `Shaders/Ink/{BambooTrunk,BambooLeaf,InkGround}.shader`（ShaderLab 名 `Xianxia/Ink/*`）
  - `Art/Materials/Ink/MAT_Ink_{Trunk,Leaf,Ground,Fog}.mat`
  - 另：`Editor/BambooInkImporter.cs`（GoodX 生成，Editor-only）
- `ProjectSettings` 中**尚未**注册 Always Included Shaders（见步骤 5）。

## A. 编译与资产导入（PlayMode 前）

1. 打开工程，等全部导入完成，**Console 0 error**（新 shader / Editor 脚本必须能编过）。
2. 选 `bamboo_ink.fbx` → Import Settings 中 **Scale Factor = 1**、模型约 **5~10 单位高**（不是 0.05~0.1）。Console 应有日志
   `[Shuimo/2.5D] ... 已按 1:1 导入`。
   - 若仍是缩小 100 倍 → 菜单 `Shuimo > 2.5D > Reimport Ink Bamboo FBX` 重导。
3. `Shaders/Ink/*.shader` 无编译错误；ShaderLab 名 = `Xianxia/Ink/BambooTrunk`、`Xianxia/Ink/BambooLeaf`、`Xianxia/Ink/InkGround`。
4. 逐个打开 `MAT_Ink_*.mat`：Inspector 的 **Shader 字段应显示 `Xianxia/Ink/*`**（非 Missing / 粉色）。若丢引用 → 重新指到对应 shader。
5. **注册 Always Included Shaders**：`Project Settings → Graphics → Always Included Shaders` 加入上面三个 `Xianxia/Ink/*`。
   否则打包后 `Shader.Find` 返回 null（Editor PlayMode 不受影响，但**真打包会黑**）。
6. 确认 SampleScene / Bootstrap 已接入 `BambooSceneContext`（竹林在 Play 时生成）。

## B. PlayMode 运行检查

7. **竹林生成**：Play → 竹丛按确定性种子撒点（同 seed 同布局），数量符合预期。
8. **遮挡 / 深度排序**：相机看向竹丛，近竿正确遮挡远竿（深度轴已修正为朝相机 **-Z**）；走位验证排序稳定。
9. **砍竹特效**：移到竹竿旁触发攻击 → `AttackController.SwingCount` 增长沿 → 90° 扇形 `DetectHarvest` → `BambooVfx.OnHit` 晃动 / 断裂 + 粒子生成。
10. **顿帧同步**：命中时 `SyncFxPause(frozen)` 让粒子随 `FeedbackClock.Frozen` 暂停；**全程无 `Time.timeScale`**（不应出现全局慢放 / 跳帧）。
11. **相机**：`IsometricCameraRig.orthographicSize` 被 BSC 重配 ≈352，取景覆盖竹丛。

## C. 粒子转正（BambooHitFx prefab）

12. 菜单 `Shuimo > 2.5D > 烘焙 BambooHitFx.prefab` → 生成 `Prefabs/BambooHitFx.prefab`，内含粒子系统。
    可选「含 Resources 副本」使其落到 `Resources/2.5D/BambooHitFx` 供 `Resources.Load` 命中。
13. 验证 `SpawnFx` 三级取用：① Inspector 拖 prefab → ② `Resources.Load("2.5D/BambooHitFx")` → ③ `BuildRuntimeFx` 兜底。
    命中任一级都应看到砍竹粒子。

## D. 红线回归（防回潮）

14. grep 红线（2.5D + Editor 目录）：
    - `Time.timeScale` → 0
    - `CombatScheduler` / `RunPhase` / `DamageResolver` 改动 → 0
    - `FeedbackClock.Frozen` 直写 → 仅 `HitFeedbackDirector`（BSC 只读、经 `SyncFxPause` 同步）
    - `IsGameplayBlocked` 读取点 → 仅 `BambooSceneContext.cs:431`
    - `ShuimoGenerated.Mark` 代码引用 → 0（BSC 根节点刻意不挂）
15. `git diff main..feature/2.5d -- '*.cs'` → 确认零战斗内核 C# 改动（只有 2.5D / Editor 新增 + 文档 + 资产）。

## E. 提交与合并债（本地执行）

16. 上述未提交资产（`Art/`、`Shaders/`、`Editor/*.cs`、`.meta`）必须先 commit 到 `feature/2.5d`，否则合并会丢。
17. 合并 `feature/2.5d` → `main` 按 `docs/branch-merge-plan.md` §8：`CombatBridge.cs` 有 1 处冲突（-1366），`Bootstrap.cs` 禁用 `-X ours/theirs`，**手工解**。
18. 合并后 PlayMode 再跑一遍 B 组，确认 2D 可玩版未被破坏。

## 通过判据

A~D 全部无 error / 异常 **且** E 完成 → Round B + Route A 资产闭环，可推进 **Round C（Spine 角色）** 或敌人 / NPC 美术。

## 已知未决（非阻塞）

- **D3 软碰撞**：竹子 `isTrigger`，玩家可穿模（软碰撞推出未实现），待拍板补 / 放弃。
- **护栏 .py**（`t1/t3/bosspending`）被 `.gitignore` 排除，clone 即丢，待拍板是否入库。
- **Round C（Spine 角色）** 待做。
