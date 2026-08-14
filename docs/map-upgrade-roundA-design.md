# 地图升级 · 选项 A 设计文档（feature/2.5d，2026-08-14）

> 本文档对应一轮「地图升级」增量：在已有单块矩形竹林（轮次 B/C 产出）之上，
> 补齐**多区域撒点、敌人巡逻 AI、竹剑切割特效**三块缺口。绑骨步骤按用户决策暂跳过，
> 本增量全部走无骨 `SpriteCharacterView` 路径，不依赖 `HAS_2D_BONE_PACKAGE`。

---

## 1. 现状（轮次 B/C 已落地，本增量之前）

| 模块 | 文件 | 状态 |
|------|------|------|
| 竹林程序化生成（20–30 根 + 地面 + 雾 + 深度排序 + 砍竹判定） | `BambooSceneContext.cs` | ✅ 已好 |
| 敌人/NPC 撒点（18 小怪 + 4 NPC，静止 Idle 占位） | `EnemyNpcSpawner.cs` | ⚠️ 静止、无区域 |
| 弧形剑气特效（月牙 + 拖尾，通用） | `VfxSlash.cs` | ✅ 已好，但**未接入砍竹** |
| 砍竹粒子 | `BambooVfx.cs` | ✅ 已好 |

缺口：敌人不动、地图是「一整块」、挥砍没有视觉剑气。

---

## 2. 本轮交付（选项 A 三块）

### 2.1 敌人巡逻 AI —— `EnemyPatrol.cs`（新增）
- 圆形区域内随机游走：到达目标 → 停顿随机时长（waitMin~waitMax）→ 选新目标（圆内均匀采样）。
- 驱动 `CharacterView.PlayState(Walk/Idle)` 与 `SetFacing(dir)`，使无骨 Sprite 朝移动方向翻转。
- 确定性：xorshift32，种子由 Spawner 按「区域 id + 序号」派生，同关卡可复现。
- 推进闸门：由 `EnemyNpcSpawner.Update` 在 `!IsGameplayBlocked` 且 `FeedbackClock.Delta > 0` 帧调用 `Tick(dt)`，顿帧/暂停同步冻结。**不自行读 `Time.deltaTime`**。

### 2.2 多区域撒点 —— `EnemyNpcSpawner.cs`（改造）
- `EnemyNpcSpawnConfig` 新增 `SpawnRegion` 类与 `regions` 列表：每个区域含 `center / radius / enemyCount / npcCount`。
- `SpawnAll` 两条路径：
  - **regions 非空（选项A 主路径）**：遍历 regions，各自用独立种子 `ZoneSeed.CreateRng(zoneSeedId + "_r" + i)` 在**圆内**撒点，敌人挂载 `EnemyPatrol` 且巡逻圈 = 该区域 `center/radius`。
  - **regions 为空（向后兼容）**：退回原「整体方形域」行为，巡逻圈圆心原点、半径取 `spawnAreaHalfExtent`。
- NPC 也挂极慢巡逻（`moveSpeed=28`），让场景更有生气；NPC 不进 harvest 集。

### 2.3 竹剑切割特效 —— `BambooSceneContext.cs`（改造）
- `DetectHarvest` 的 `attackEdge && !blocked` 分支新增：`VfxSlash.Play(player.position, facing, harvestRadius, harvestArcDeg)`。
- 效果：玩家每挥一刀，沿朝向扫出月牙剑气（与判定同半径/张角），叠加 `BambooVfx` 的竹屑粒子，呈现「剑气切割 + 竹断」的打击感。
- 剑气吃 `FeedbackClock.Delta`，顿帧同步冻结（与角色一致，不穿帮）。

---

## 3. 红线（与本工程既有约定一致）

1. 不写 `Time.timeScale`、不写 `FeedbackClock.Frozen`、不引用 `CombatScheduler/RunPhase/DamageResolver`。
2. 不改动任何内核类型；只读取 `PlayerController / AttackController / CombatBridge / BambooSceneContext`。
3. 动画/巡逻推进一律走 `FeedbackClock.Delta`；时钟唯一来源是调用方传入的 `dt`，组件不自行读 `Time.deltaTime`。
4. 巡逻不进战斗内核（无仇恨/追击/伤害），仅表现层游走，符合「2.5D 表现层零内核依赖」原则。

---

## 4. 用户本地验收清单（本环境无 Unity，不可代为编译/跑）

- [ ] **编译 0 error**：打开工程，等重编；`EnemyPatrol.cs` / `EnemyNpcSpawner.cs` / `BambooSceneContext.cs` 无红字。
- [ ] **巡逻可见**：PlayMode 进入竹林，敌人/NPC 在各自区域里来回走动并播放 Walk/Idle，朝向随移动翻转；暂停（ESC）时全员冻结。
- [ ] **多区域**：在 `EnemyNpcSpawnConfig` 资产里填 `regions`（例如 3 个区域，中心错开、半径 400），重进场景，敌人应聚成几簇而非一整片；每个敌人只在自己区域圈内活动。
- [ ] **剑气切割**：挥砍（J 键，默认扇形 harvest）时屏幕出现月牙剑气 + 竹屑；命中竹子触发断裂；顿帧（hitstop）时剑气与角色同步定格。
- [ ] **深度遮挡**：走动到竹子 前后 时，敌人/玩家与竹子的遮挡关系正确（沿用既有 `DepthSortUtility`）。

---

## 5. 后续（本增量未做，待下一轮）

- **真正引入 Unity Tilemap**：当前地图是程序化 primitive + 深度排序，尚未用 Tilemap。若需「铺地纹理/格子感」，可在 `BambooSceneContext` 之外另建 `TilemapRegion` 层，与现有深度排序共存。
- **区域过渡/加载**：多区域目前是「同场景内空间分区」；跨场景/无缝切换需 `WorldBuilder` 配合。
- **NPC 交互**：`InteractableMarker` 已占位，对话/任务系统待接。
- **敌人战斗 AI**：巡逻之上叠加仇恨/追击/攻击，需进战斗内核，本增量刻意不做。
- **绑骨回归**：用户后续把 `HeroineBoneWizardEditor` 生成的骨骼权重调好后，挂 `SpriteSkin` + 加 `HAS_2D_BONE_PACKAGE` 即可切到骨骼视图，本增量完全兼容（巡逻/区域/剑气均不依赖骨骼）。

---

*本文档由主理人齐活林于 2026-08-14 落盘；代码改动对应 `docs/changelog.md` 阶段 37。*
