# 女主水墨精灵接入 · 本地 Unity 验收与校准清单

> 对应设计：`docs/unity-e-heroine-sprite-integration.md`（美术阶段 E）
> 工程根：`F:/AI-project/ancientGame/shuimofeng/shuimofeng/`
> 编码环境无 Unity / dotnet，以下全部需在**本地 Unity 2022.3** 编译后执行。

---

## 0. 首次打开 Unity 前的确认

39 张帧图已从 `Assets/images/characters/heroine/` **移动**到
`Assets/StreamingAssets/characters/heroine/`。

- `Assets/StreamingAssets/characters/heroine/*.png` → **39 张**
- `Assets/images/characters/heroine/` 仍保留 5 张源大图 + 1 张 `_preview_heroine_sample.png`（素材归档，与运行时无关）
- 39 张帧图**没有 `.meta`**，这是正常的：StreamingAssets 里的文件不作为资产导入，也不需要 GUID

> Unity 打开后会为 `StreamingAssets` 目录本身生成 `.meta`，属正常现象，请一并提交。

---

## 1. 编译

Unity 编辑器打开工程，等待编译完成。预期 **0 error / 0 warning**（新增代码不引入任何新依赖包，`Xianxia.Unity.T2.asmdef` 未改动）。

若报错，优先检查：

| 报错关键字 | 可能原因 |
|-----------|---------|
| `ImageConversion` / `LoadImage` 找不到 | asmdef 的 `noEngineReferences` 被人改成了 `true`（应为 `false`） |
| `HeroineAnimator` 找不到 | 新文件没被 Unity 识别，重启编辑器或 Reimport `_Project/Scripts/Runtime` |

---

## 2. EditMode 单元测试

`Window → General → Test Runner → EditMode → Xianxia.Unity.T2.Tests → Run All`

新增 `HeroineFramesTests`（16 个断言方法）应**全绿**。它只测清单表的数据自洽性，不依赖磁盘与场景。

若 `Table_MatchesDesignManifest` 或 `FrameCounts_SumTo39` 红了，说明有人改了 `HeroineFrames._table` 而没同步帧文件。

---

## 3. PlayMode 人工验收（8 项）

| # | 操作 | 期望 |
|---|------|------|
| ① | 进 PlayMode | 玩家是**水墨女主**（不是蓝方块），站立呼吸，idle 4 帧循环约 1 秒/轮 |
| ② | WASD 移动 | 上 / 下 / 左右 三套走路帧正确切换；**向左走时角色水平翻转**（flipX） |
| ③ | 挥剑（J / 左键） | attack 6 帧播完自动回落 idle/walk；**角色不发生水平位移** ← 若位移见 §4 |
| ④ | 挨打 / 闪避（Shift / Space） | hurt 2 帧；dodge 4 帧 |
| ⑤ | 死亡 | death 5 帧播完**停在末帧**，不循环、不回落 idle |
| ⑥ | 打开暂停菜单（Esc） | 动画**冻结在当前帧**（不是继续播、也不是跳回 idle） |
| ⑦ | 菜单 Clean And Rebuild ×5 | Profiler → Memory 里 Texture Memory **不持续增长** |
| ⑧ | **fallback 验证**：把 `StreamingAssets/characters/heroine` 临时改名，重进 PlayMode | 显示 40×40 **蓝方块 + 朝向条**；Console 有 Warning；**无 NullReferenceException**。验完改回来 |

第 ⑧ 项是本次改动最重要的一条：它证明"精灵是升级项而非必需品"，PRD P0-11「clone 即跑」的精神没被破坏。

---

## 4. pivot 校准（设计 §5 的 U2 / U3，唯一需要目测定的参数）

代码已把 pivot 参数化，**校准只需改常量表里的一个浮点数，不动任何逻辑**。

文件：`Assets/_Project/Scripts/Runtime/HeroineFrames.cs`

```csharp
private static readonly Vector2 FootPivot   = new Vector2(0.50f, 0.28f);
private static readonly Vector2 AttackPivot = new Vector2(0.25f, 0.28f);  // ← U2
private static readonly Vector2 DeathPivot  = new Vector2(0.50f, 0.22f);  // ← U3
```

### U2 · Attack（96×64 画布）

**判据**：站着别动，按住 J 连续挥剑，只盯**角色身体**（不看剑）。

| 现象 | 结论 | 改法 |
|------|------|------|
| 身体稳定不动，只有剑挥出去 | ✅ 当前 `0.25f` 正确 | 不用改 |
| 挥剑瞬间身体**向左跳**约半个身位，收招又跳回来 | 画布里身体是居中的 | `AttackPivot` 的 x 改成 `0.50f` |
| 身体向右跳 | 身体更靠右 | x 试 `0.10f` 左右 |

> 原理：attack 画布 96 宽，是 idle（48 宽）的两倍。pivot 是"世界坐标锚在图的哪个位置"，
> 只要 attack 的锚点没落在身体中轴上，切帧的一刹那身体就会整体横移。

### U3 · Death（64×64 画布）

**判据**：让玩家死一次，看倒地帧。

| 现象 | 改法 |
|------|------|
| 倒地后身体**陷进地面** | `DeathPivot` 的 y 调大（0.22 → 0.30） |
| 倒地后**浮在空中** | y 调小（0.22 → 0.15） |

改完保存，Unity 自动重编译，重进 PlayMode 即可看到效果（39 帧是运行时读盘，**不需要重新导入资产**）。

---

## 5. 本次改动的边界（回归时重点看这里）

**已改**

- `Assets/_Project/Scripts/Runtime/SpriteFactory.cs` — 新增 PNG 加载 API；顺带修掉 `SolidRect`/`Circle` 的缓存 key 碰撞
- `Assets/_Project/Scripts/Runtime/WorldBuilder.cs` — 仅 `BuildPlayer()` 尾部新增精灵接线
- 新增 `HeroineFrames.cs` / `HeroineAnimator.cs` / `Tests/HeroineFramesTests.cs`

**一行未改（红线）**

- `PlayerController.cs` / `AttackController.cs` / `DodgeController.cs` / `CombatBridge.cs`
- `Xianxia.Unity.T2.asmdef`
- `WorldBuilder.PlayerBodySize` 仍为 **40**（它是"逻辑体宽"，服务于攻击半径/边界钳制/朝向条距离；
  视觉尺寸看 `PoseDef.FrameW/FrameH`。**谁也不许为了对齐视觉去改它**）

**顺带修掉的历史 bug**：`SolidRect` 原本的缓存 key 是 `"rect_" + key`，不含 w/h/fill —— 
先调 `SolidRect("player",40,40,蓝)` 再调 `SolidRect("player",20,20,红)` 会**静默**拿回 40×40 的蓝块。
本次改成 `rect|<key>|<w>x<h>|<RRGGBBAA>`（`Circle` 同理加 fill/outline/outlinePx）。
所有调用点签名不变，无需改动调用方。
