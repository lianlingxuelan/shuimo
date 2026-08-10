# P2-1 BOSS 战 增量 PRD（提级 P1 · 最后一公里接线）

> 版本：v1.0　｜　作者：许清楚（产品经理）　｜　主理人：齐活林
> 范围：把已就绪的 BOSS 内核与桥接层**接到玩家面前**，并拍板三项产品决策。
> 本文档**不含任何代码改动**。所有引用的符号、行号、数值均已在
> `F:/AI-project/ancientGame/shuimofeng/shuimofeng` 实测核实，见 §8 附录。

---

## 0. TL;DR（给架构师的四句话）

1. **这是接线，不是造轮子**。三阶段 FSM、召唤、冲击波、数值桥接、事件出口、音效、zones.json 配置**全部已就绪**。真实缺口只有 5 处，见 §8.2。
2. **决策 1 —— BOSS 在初始波清空后 1.5s 现身；胜利 = 「BOSS 已了结 且 场上无存活敌人」**。采用 **A′ 方案**：在 `Encounter` 侧引入 `BossPending` 虚拟敌人计数抑制早判，**`RunPhase.cs` 零改动**，RP01–RP11 共 11 条 NUnit 护栏原样全绿。理由与竞态论证见 §4.1。
3. **决策 2 —— 顶部中央专属 BOSS 血条（带 65%/30% 阶段刻度）+ 出场横幅 + 阶段横幅 + P3「震」读条**。顶部中央经实测是唯一空闲区，与左上玩家面板、底部技能栏均无重叠。**冲击波不做假前摇预警**，理由见 §4.2.4。
4. **决策 3 —— 不做美术、不做掉落、不做区域解锁**。本期只保证 `zone_youhuang` 竹魈王全流程可玩，见 §6。

> ⚠️ **本 PRD 最关键的一条**：`BossPending` 一旦置位却没有 BOSS 进场，这一局将**永远无法判胜**（玩家清完所有怪站在空场里，游戏不结束）。这是本期唯一的软锁风险，防护要求写在 §4.1.4，验收项 AC-06 专门回归它。

---

## 1. 产品目标

### 1.1 为什么现在做 BOSS 战

项目已闭合 P0-1..P0-6 与 P1-2/P1-3/P1-6（打击感、音频、成长线），**一局游戏现在"打得完、有回报"，但"没有高潮"**。当前 `zone_youhuang` 的全部内容就是清掉一波同质杂兵然后弹胜利结算——15 分钟的体验和第 1 分钟完全一致，缺少一个**段落终点**。

而 BOSS 的内核**早已在跑**：`BossController` 的三阶段 FSM、`DifficultyBridge.BuildBoss()` 的数值桥接、`Encounter` 的召唤收编与冲击波结算、`CombatEventsUnity` 的四条音效出口、`zones.json` 里 5 个区域的完整 boss 段——**全部就绪且互相接通**。唯一的问题是：**没有任何一处代码把它们启动起来**。`SpawnBoss` 全仓零调用，`BossPhaseChanged` 全仓零订阅，`ShockwaveFxPrefab` 全仓零装配。

本期要补的就是这条**从"内核会打 BOSS"到"玩家能打到 BOSS"的最后一公里**。

### 1.2 三个正交目标

| # | 目标 | 衡量口径 |
|---|---|---|
| G-1 | **让 BOSS 真的出场** | 在 `zone_youhuang` 清完初始波后，竹魈王必然出现，且出现前不会误弹胜利结算 |
| G-2 | **让玩家看懂 BOSS 战** | 出场、当前血量、所处阶段、下一次冲击波何时到——四件事全部有明确视觉表达 |
| G-3 | **不破坏任何既有交付** | `RunPhase.cs` 零改动；U1 平衡口径与 P0-3 的 11 条 NUnit 护栏逐条不变 |

### 1.3 这一版刻意不做什么

本期是**接线任务**，不是内容扩张。数值已冻结（`BuildBoss` 的 HP×10 / ATK×1.6 / 韧性×3.0 / EXP×3.0 不动），阶段阈值已冻结（0.65 / 0.30 不动），召唤与冲击波节拍已冻结（8.0s / 6.0s / 半径 200 不动）。范围边界见 §6。

---

## 2. 用户故事

| # | 故事 |
|---|---|
| US-1 | 作为玩家，我希望**清完杂兵后有一个真正的对手压轴登场**，这样这一局才有"打完了"的成就感，而不是怪没了就结束。 |
| US-2 | 作为玩家，我希望**BOSS 出场时我能立刻知道它是谁**（名号、体型不同于杂兵），这样我才会本能地紧张起来。 |
| US-3 | 作为玩家，我希望**随时看得到 BOSS 还剩多少血**，这样我才能判断该继续拼还是该拉开风筝。 |
| US-4 | 作为玩家，我希望**BOSS 变强的那一刻有明确提示**，这样我挨了更重的一下时知道是"它进阶了"，而不是"我变菜了"。 |
| US-5 | 作为玩家，我希望**三阶段的冲击波不是凭空掉血**——哪怕躲不掉，我也要知道它按什么节奏来，好提前站远。 |
| US-6 | 作为玩家，我希望**杀掉 BOSS 之后这一局立刻干净地结束**，而不是还要满地图找漏网的小怪。 |
| US-7 | 作为玩家，我希望**在安全区不会莫名其妙冒出 BOSS**，也不会因为没有 BOSS 就卡在"打不完"的局里。 |

---

## 3. 现状与缺口（实测）

### 3.1 已就绪（既成事实，本期不重新设计）

| 模块 | 位置 | 状态 |
|---|---|---|
| 三阶段 FSM | `Assets/Scripts/Systems/Combat/BossController.cs` | ✅ P1 ratio>0.65 / P2 >0.30 / P3 ≤0.30，只降不升；进位式冷却复位防节拍漂移 |
| 数值桥接 | `DifficultyBridge.BuildBoss()` | ✅ HP×10 / ATK×1.6 / 韧性×3.0 / EXP 走 `BOSS_EXP_MULT`（**数值冻结**） |
| 召唤收编 | `Encounter.CollectSummons` :1045 | ✅ 含 `MaxEnemies=32` 上限保护 |
| 冲击波结算 | `Encounter.ResolveBossShockwave` :1071 | ✅ 在位移之后结算，中心取本步移动后位置 |
| BOSS 生成 | `CombatController.SpawnBoss(Vector2)` :423 | ✅ 含召唤委托接线与 `bossPrefab` 视图挂载 |
| 事件出口 | `CombatEventsUnity` :156/:173/:186 | ✅ 四条音效已接，`BossPhaseChanged` 回调已暴露 |
| 区域配置 | `Assets/Data/zones.json` | ✅ 5 个战斗区各有完整 boss 段，`ZoneLoader` 已校验 |

### 3.2 真实缺口（本期要补的全部内容）

| # | 缺口 | 位置 |
|---|---|---|
| GAP-1 | boss 配置被硬编码传 `null`（T2 切片期刻意关闭，:910-911 有注释说明） | `CombatBridge.cs:912` |
| GAP-2 | `bossTemplate` 传 `null` | `WorldBuilder.cs:729` |
| GAP-3 | `SpawnBoss` **全仓零调用** | — |
| GAP-4 | `BossPhaseChanged` **零订阅者**；无阶段提示、无 BOSS 血条 | — |
| GAP-5 | `ShockwaveFxPrefab` **零装配**，冲击波无视觉 | `CombatEventsUnity.cs:40` |

---

## 4. 三项产品决策

### 4.1 决策 1：BOSS 出场时机与胜负判定

#### 4.1.1 结论

> **BOSS 在初始波杂兵清空后 1.5s 现身。**
> **本局胜利条件 = 「BOSS 已进场并被击杀」且「场上无任何存活敌人（含 BOSS 召唤物）」。**
> **实现路径采用 A′：在 `Encounter` 侧抑制早判，`RunPhase.cs` 一个字节都不改。**

#### 4.1.2 冲突复述

`RunPhaseTracker.Evaluate` 的胜利规则是 `_armed && aliveEnemyCount <= 0`（`RunPhase.cs:220`），且状态机只能从 `Playing` 单向落定到 `Won`/`Lost`，幂等闸门永不回退（:189-192）。若采用经典的「清完杂兵 → BOSS 现身」节奏，杂兵归零那一瞬间 `aliveEnemyCount == 0`，**同一步就判 Won**，胜利结算弹出，BOSS 再出场也没有意义。

#### 4.1.3 推荐方案 A′ 及其论证

**核心洞察**：`RunPhase.cs` 里有**两个** `Evaluate` 重载——
- 纯查询三参重载 `Evaluate(bool, bool, int)`（:187）是**唯一的判定逻辑所在**，被 RP01–RP11 共 11 条 NUnit 用例逐条锁死；
- 便捷重载 `Evaluate(Encounter)`（:239）**只是帮忙把三个数从战场里取出来**，本身不含任何判定逻辑。

而 `Encounter` 第 ⑦ 步调用的正是便捷重载（`Encounter.cs:511`）。

**因此"抑制早判"完全可以在 `Encounter` 侧完成，不需要碰 `RunPhase.cs`。** 具体方向（最终形态由架构师裁量）：

- `Encounter` 引入 `BossPending`（bool），语义 = **"本局承诺还会出现一只 BOSS，但它还没进场"**；
- 第 ⑦ 步改为向**纯查询重载**传入 `AliveEnemyCount + (BossPending ? 1 : 0)`。

这个"虚拟 +1 敌人"在语义上完全自洽：BOSS 确实是**这一局还欠玩家的一只敌人**。

**为什么 A′ 优于直接改 RunPhase 语义：**

| 维度 | A′（Encounter 侧） | 直接改 RunPhase |
|---|---|---|
| `RunPhase.cs` diff | **0 行** | 需改判定分支或加参数 |
| RP01–RP11 护栏 | **原样全绿，无需改用例** | 三参重载签名或语义变动 → 用例需同步改，护栏失去"未被改动过"的证明力 |
| P0-3 幂等/布防/同归于尽语义 | 逐条不变 | 需重新论证 |
| 布防（`_armed`）是否受影响 | 天然生效：`BossPending` 从建场即为 true，第一步 `aliveEnemyCount ≥ 1` 就已布防 | 需重新推演 |
| 胜利条件表达 | 自动成立，无需第二处判定 | 需额外维护 |

**⚠️ 置位时机必须是「建场即置位」，不是「清完杂兵才置位」——这是本方案最关键的风险点：**

若等到"剩余杂兵 ≤ 1"或"杂兵归零"才置位，存在**零跨越竞态**：莲、阵、冲击波都是范围伤害，`aliveEnemyCount` 完全可能在一步之内从 3 直接跳到 0，`Evaluate` 在同一步的第 ⑦ 步就判胜；而 Unity 侧的 `SpawnBoss` 最早要到下一帧 `Update` 才可能执行。**一旦 `Won` 落定，幂等闸门永久锁死，BOSS 再也没有意义。** 从建场即置位则根本不存在这个窗口。

**清位时机**：`SpawnBoss` 成功把 boss `Encounter.Add` 进场之后，立即清 `false`。

#### 4.1.4 软锁防护（硬要求）

`BossPending` 置位却永远没有 BOSS 进场 = 这一局永远无法判胜。必须保证：

- **R-1** 仅当「非安全区」且「`zone.boss` 配置非空」且「`controller` 已收到非 null 的 boss 配置」三者同时成立时才置位；
- **R-2** 安全区（`is_safe=true`，如 `zones.json` 第 1 个区域 boss 为 `null`）必须恒为 `false`；
- **R-3** `Encounter.Clear()` / `RunState.Reset()` 路径必须一并复位 `BossPending`（`Encounter.cs:366` 已有 `RunState.Reset()` 调用点，就近处理）；
- **R-4** 建议加一道兜底：若置位后超过 N 秒仍无 BOSS 进场，强制清位并打 `Debug.LogError`。N 取值见 §7 待确认问题 Q-1。

#### 4.1.5 出场演出口径

- **触发**：初始波（`SpawnFirstWave` 生成的那批）全部清空；
- **节奏**：静默 1.5s → 名号横幅 → 竹魈王落地；
- **落点**：玩家当前朝向前方约 **260 世界单位**。取值理由：> `AI_STRIKE_DIST`(160) 保证不贴脸直接开打，< 相机半高 `CameraOrthoSize`(352) 保证一定在画面内可见。

#### 4.1.6 为什么不选 B / C

- **方案 B（开局同时在场）**：实现最省（约 3 行），但代价不可接受。10 倍血、1.6 倍攻的竹魈王从开局就压着打，玩家在清杂兵阶段就要同时吃 P2 召唤——召唤物与初始波叠加会持续逼近 `MaxEnemies=32`，难度曲线直接崩掉；且完全没有"BOSS 战"的段落感，US-1 无法达成。省下的 3 行不值得。
- **方案 C（血量/击杀数阈值触发）**：同样有 §4.1.3 描述的零跨越竞态（击杀数达标那一步可能恰好也是清空那一步），**并不比 A′ 省事**；而且"打着打着 BOSS 突然冒出来"缺乏因果可读性，玩家不知道自己做了什么招来了它。

---

### 4.2 决策 2：BOSS 战的可读性

#### 4.2.0 屏幕占用实测（参考分辨率 1920×1080，`CanvasScaler match=0.5`）

| 区域 | 占用者 | 实测坐标 |
|---|---|---|
| 左上 | `Hud` 玩家面板（区域名 / 血 / 气力 / CD / 经验 / 等级） | anchor(0,1), pos(28,-24), 420×150 → **x∈[28,448]**，距顶 24–174 |
| 底部中央 | `HudSkillBar` | **y∈[34,110]**（`BottomMargin`34 + `CellSize`76）🚫 不得侵占 |
| 底部中央偏上 | Combo 文字 | y∈[124,168]，宽 420 |
| **顶部中央** | **空闲** | ← **BOSS 血条落位** |

#### 4.2.1 出场提示：**要**

- 名号横幅「**竹魈王**」+ 副题「幽篁竹海 · 主」（`display_name` 直接读 `zones.json`，不硬编码）；
- 位置：屏幕中央偏上，anchor(0.5,0.5)，y ≈ +120；
- 时长 1.5s（0.3 淡入 / 0.9 停留 / 0.3 淡出）；
- 🚫 硬约束：横幅任何部分不得落进 y < 200 的底部区域。

#### 4.2.2 BOSS 专属血条：**要**

- **位置**：顶部中央，anchor(0.5,1)，anchoredPosition (0,-40)，尺寸 **720×28**；
- **不重叠证明**：水平区间 x∈[600,1320]，左上玩家面板止于 x=448，**600 > 448**，安全；垂直方向距顶 40–68，远离底部技能栏；
- 血条上方 22px 一行：BOSS 名号 + 当前阶段标记；
- **★ 血条上必须画 65% / 30% 两条阶段刻度线**——让玩家能预判"还有多久进下一阶段"。这是 BOSS 战可读性的核心，也是 US-3 与 US-4 的交汇点；
- 仅在 BOSS 存活期间显示，BOSS 死亡后 0.6s 淡出；
- 小怪头顶血条**维持原样不动**，靠"位置 + 尺寸 + 刻度线"三重差异与 BOSS 血条区分。

#### 4.2.3 阶段切换呈现：**要**

- 订阅 `CombatEventsUnity.BossPhaseChanged`（当前零订阅者，即 GAP-4）；
- **P2** → 横幅「二阶 · 唤魈」，血条主色转琥珀；
- **P3** → 横幅「三阶 · 震山」，血条主色转朱红 + 常驻微脉动；
- 音效 `sfx_boss_phase` 已在 `CombatEventsUnity:160` 自动播放，**不需要额外接**；
- ⚠️ **连跳保证**：内核在一次扣血同时跨两个阈值时**只抛 P3 一次**（`BossController.cs:136-140` 注释明确）。因此 UI **不需要**自己做去重，但也**绝不能假设一定会收到 P2**——血条配色等状态必须按"当前阶段"直接赋值，而不是按"上一阶段 +1"递推。

#### 4.2.4 P3 冲击波预警：**不做假前摇，做「节拍可预期 + 事后诚实表达」**

**这是本决策里最需要拍板的一条。**

内核冲击波是**即时结算、无前摇**：`BossController.RunP3Abilities` 冷却归零的同一步直接置 `ShockwavePending` 并抛 `OnShockwave`，`Encounter` 在同一步的第 ④ 步就已 `ResolveBossShockwave` 把伤害扣完。数值与时序均已冻结。

**结论：不画"假预警圈"。**

理由：如果画一圈 0.5s 的红色预警圈然后才表现伤害，等于向玩家承诺"你还有 0.5s 可以跑"——**而内核根本不给这 0.5s，血在预警出现的同一步就已经掉了**。这种"预警与结算不一致"比没有预警更糟，它会让玩家笃定判定有 bug。这条原则与 `CombatEventsUnity:84-90` 既有的处理完全同构：`applied=false` 时**什么都不播**，因为"播了就等于告诉玩家我挨打了，但血没掉，反馈与结果不一致，比没有反馈更糟"。

**替代方案两条，均不动内核时序：**

1. **节拍可预期（解决 US-5）**：进入 P3 后，BOSS 血条下方出现一条 **6s 循环的「震」读条**，读满即震。玩家据此可预判并主动拉开距离——冲击波半径 200，而 `AI_LEASH_DIST` 是 480，**跑得开**。这把"被偷袭"转化为"考验站位"，是正向的技巧空间。
2. **事后诚实表达**：`ShockwaveFxPrefab`（GAP-5）装配为一圈**瞬间铺满全尺寸**的冲击环 + 屏震，0.35s 淡出。它表达的是"**已经震过了**"，而不是"要震了"。

> ⚠️ **特效尺度契约（易踩，验收要量）**：`CombatEventsUnity.SpawnFx` 执行 `localScale = Vector3.one * radius`，而 `radius = BOSS_P3_SHOCK_RADIUS = 200`。因此预制体必须**按半径 1 世界单位建模**（`CombatEventsUnity.cs:190-191` 注释已约定），否则会被放大 200 倍变成满屏白幕。

---

### 4.3 决策 3：本期范围边界

#### 4.3.1 本期做（= §3.2 的 5 个缺口）

1. GAP-1 `CombatBridge:912` 传入真实 boss 配置
2. GAP-2 `WorldBuilder:729` 传入 `bossTemplate`
3. GAP-3 触发 `SpawnBoss` + `BossPending` 抑制早判（决策 1）
4. GAP-4 BOSS 血条 + 出场横幅 + 阶段横幅（决策 2）
5. GAP-5 `ShockwaveFxPrefab` 装配 + P3「震」读条（决策 2）

#### 4.3.2 本期**不做**（明确排除）

| 不做项 | 理由 |
|---|---|
| **BOSS 专属美术精灵** | 需 ImageGen 积分。本期复用 `enemyPrefab`，用 `SpriteFactory` 按 `kind=witch` 既有配色上色 + 体型放大 ~1.6× 来区分。美术另立条目。 |
| **`drop_extra` 掉落结算** | `zones.json` 已配 `mat_bamboo_marrow×2` 与 `equipment_quality_cap`，但**当前无背包/材料系统**，结算出来无处可落。 |
| **`boss_cleared` 解锁下一区域** | 属区域流程与存档范畴，不是战斗接线。另立条目。 |
| **其余 4 个区域 BOSS 联调** | 本期只验收 `zone_youhuang` 竹魈王。焰魈/玄冰蛟/剑冢守灵/归墟魔尊的配置已在 `zones.json` 就位，接线代码天然通用，但**不纳入本期验收**。 |
| **BOSS 专属 BGM / 战斗音乐切换** | 属 P1-3 音频范畴，且需新音源。 |
| **召唤物差异化** | 维持内核现状（当前 zone 的 normal 敌人降 1 级），不新增召唤物种类。 |
| **PlayMode 自动化测试** | 本环境无 Unity、无 dotnet，无法执行。BOSS 战的 PlayMode 覆盖另立条目。 |
| **BOSS 韧性/受击表现特化** | 韧性×3.0 已在内核生效，表现层沿用 `HitFeedbackDirector` 现有分档。 |

---

## 5. 需求池

### P0（必须有，缺一则本期不成立）

| # | 需求 | 对应 |
|---|---|---|
| P0-1 | `CombatBridge` 必须把 `zones.json` 的真实 boss 配置注入 `SetZoneConfig`，不再传 `null` | GAP-1 |
| P0-2 | `WorldBuilder` 必须传入非 null 的 `bossTemplate` | GAP-2 |
| P0-3 | 初始波清空后必须触发 `SpawnBoss`，落点符合 §4.1.5 | GAP-3 |
| P0-4 | 必须实现 `BossPending` 早判抑制，且 **`RunPhase.cs` 零改动** | 决策 1 |
| P0-5 | 必须实现 §4.1.4 全部四条软锁防护（R-1..R-4） | 决策 1 |
| P0-6 | 必须有顶部中央 BOSS 血条，且不侵占 `y∈[34,110]` 与左上面板 | 决策 2 |
| P0-7 | 击杀 BOSS 且场上无敌人后，必须正常弹出胜利结算且只弹一次 | US-6 |

### P1（应该有，影响体验完整度）

| # | 需求 | 对应 |
|---|---|---|
| P1-1 | BOSS 出场名号横幅（读 `display_name`，不硬编码） | §4.2.1 |
| P1-2 | 阶段切换横幅 + 血条配色变化（按当前阶段直接赋值，不递推） | §4.2.3 |
| P1-3 | `ShockwaveFxPrefab` 装配，可见半径 = 200 世界单位 | GAP-5 |
| P1-4 | BOSS 血条上的 65% / 30% 阶段刻度线 | §4.2.2 |
| P1-5 | P3「震」6s 循环读条 | §4.2.4 |

### P2（锦上添花，可延后）

| # | 需求 |
|---|---|
| P2-1 | 冲击波屏震强度分档 |
| P2-2 | BOSS 死亡的镜头停顿 / 慢镜（见 §7 Q-2） |
| P2-3 | BOSS 体型放大的落地缓冲动画 |

---

## 6. 验收标准

> 本环境**无 Unity、无 dotnet**，故验收项按可执行方式分为三类：
> **【静】** = 可静态核验（读码 / grep）　**【测】** = 需 NUnit　**【手】** = 需 Unity 手动验收

| # | 类型 | 验收项 | 通过判据 |
|---|---|---|---|
| AC-01 | 【静】 | `CombatBridge.cs` 注入真实 boss 配置 | `SetZoneConfig` 第 2 参数不再是字面量 `null` |
| AC-02 | 【静】 | `WorldBuilder.cs` 传入 bossTemplate | `BindScene` 第 4 参数不再是字面量 `null` |
| AC-03 | 【静】 | `SpawnBoss` 存在真实调用点 | 全仓 grep `SpawnBoss` ≥ 1 个非定义、非注释的调用 |
| AC-04 | 【静】 | `BossPhaseChanged` 存在订阅者 | 全仓 grep 出现 `BossPhaseChanged +=` |
| AC-05 | 【静】 | **`RunPhase.cs` 未被改动** | 该文件 `git diff` 为空 |
| AC-06 | 【测】 | 早判抑制生效 + 软锁回归 | 新增用例：①`BossPending=true` 且 `aliveEnemyCount=0` → 仍 `Playing`；②清位后同样输入 → `Won`；③安全区路径 `BossPending` 恒 false 且清场后能正常判胜 |
| AC-07 | 【测】 | 存量护栏全绿 | RP01–RP11 共 11 条**原样**通过，用例文件无改动 |
| AC-08 | 【手】 | BOSS 按时出场 | `zone_youhuang` 清完初始波后 1.5±0.3s 内竹魈王出现；**出现前不弹胜利结算** |
| AC-09 | 【手】 | 安全区不出 BOSS | 安全区清场后正常判胜，无 BOSS、无卡死 |
| AC-10 | 【手】 | 数值正确 | 竹魈王 HP = 同级 normal ×10；等级 = `base_level`3 + `level_offset`2 = **5** |
| AC-11 | 【手】 | 血条不重叠 | 顶部中央血条与左上面板、底部技能栏**零像素重叠**；1920×1080 与 1280×720 两档均需验 |
| AC-12 | 【手】 | 阶段刻度准确 | 血条 65% / 30% 刻度线与实际阶段切换时刻一致（误差 < 1 帧） |
| AC-13 | 【手】 | 周期技能节拍 | P2 每 8s 召唤 2 只；P3 每 6s 一次冲击波；「震」读条读满与特效**同帧** |
| AC-14 | 【手】 | 冲击波尺度 | 特效可见半径 ≈ 200 世界单位（±5%），**不得满屏** |
| AC-15 | 【手】 | 连跳只提示一次 | 秒杀跨阶（P1→P3）时阶段横幅只出现一次且内容为「三阶」 |
| AC-16 | 【手】 | 胜利闭环 | 击杀竹魈王 → 场上无敌人（含召唤物）→ 弹胜利结算，且只弹一次 |
| AC-17 | 【手】 | 判负优先 | 玩家在 BOSS 战中死亡 → 判负，**不弹**胜利结算 |
| AC-18 | 【手】 | 重开正常 | 重开一局后 BOSS 流程可完整复现（`BossPending` 已复位） |

---

## 7. 待确认问题

| # | 问题 | 影响 | 建议 |
|---|---|---|---|
| Q-1 | `BossPending` 兜底超时（R-4）取值？ | 软锁最后一道防线 | 建议 10s。需架构师确认是否有比超时更确定的信号 |
| Q-2 | BOSS 死亡到胜利结算之间要不要留 ~1s 镜头停顿？ | 收尾仪式感 | 倾向要，但属 P2，可延后 |
| Q-3 | P2 长时间僵持会不会把 `MaxEnemies=32` 堆满？ | 召唤静默失败 | 内核已容忍 `SpawnMinion` 返回 null，需确认这个"静默失败"在产品上可接受 |
| Q-4 | 竹魈王 5 级 vs 玩家 1 级起步的难度落差 | 首次 BOSS 战挫败感 | 需确认本期是否默认玩家已达 `rec_level`；否则可能需要引导 |
| Q-5 | P0-4 场景重载路径下 `Encounter` 是否必然重建？ | 决定 R-3 是否充分 | 需架构师确认重载路径 |
| Q-6 | 超宽屏（21:9）下 `match=0.5` 顶部血条表现 | 布局兼容 | 低优先，可在 AC-11 之外单独抽查 |
| Q-7 | 冲击波是否会命中 BOSS 自己的召唤物？ | 影响 P3 观感与难度 | 需读 `ResolveBossShockwave` 的阵营过滤确认，本期不改行为，只需明确预期 |

---

## 8. 附录：核实记录

### 8.1 核实环境

- 工程根：`F:/AI-project/ancientGame/shuimofeng/shuimofeng`
- 核实方式：`find` / `grep` / `Read` / `python -c` 直读 `zones.json`
- **未触碰** `F:/AI-project/xianxia-rpg/`（废弃 Godot/Web 原型仓）
- 环境无 Unity、无 dotnet，**未编译、未跑 PlayMode**

### 8.2 关键符号实测位置

> ⚠️ **注意：`CombatController.cs` 与 `CombatEventsUnity.cs` 实际位于 `Assets/Scripts/Systems/Combat/Unity/`，不在 `Assets/_Project/Scripts/Runtime/`。** 侦察清单中标注的路径与实际不符，架构师改动时请以本表为准。

| 符号 | 实测路径 | 行号 |
|---|---|---|
| `BossController` | `Assets/Scripts/Systems/Combat/BossController.cs` | 全文件 |
| `RunPhaseTracker.Evaluate(bool,bool,int)` | `Assets/Scripts/Systems/Combat/RunPhase.cs` | 187 |
| `RunPhaseTracker.Evaluate(Encounter)` | 同上 | 239 |
| 胜利判定分支 | 同上 | 220 |
| `Encounter.AliveEnemyCount` | `Assets/Scripts/Systems/Combat/Encounter.cs` | 370 |
| `Encounter` 第 ⑦ 步判定调用 | 同上 | 511 |
| `CollectSummons` / `ResolveBossShockwave` | 同上 | 1045 / 1071 |
| `Encounter.SpawnMinion` | 同上 | 1137 |
| `MaxEnemies = 32` | 同上 | 125 |
| `CombatController.SpawnBoss` | `Assets/Scripts/Systems/Combat/Unity/CombatController.cs` | 423 |
| `CombatController.BindScene` | 同上 | 167 |
| `CombatEventsUnity.ShockwaveFxPrefab` | `Assets/Scripts/Systems/Combat/Unity/CombatEventsUnity.cs` | 40 |
| `BossPhaseChanged` 声明 | 同上 | 69 |
| `OnBossPhase` / `OnSummon` / `OnShockwave` | 同上 | 156 / 173 / 186 |
| `CombatBridge.InjectZoneConfig`（GAP-1） | `Assets/_Project/Scripts/Runtime/CombatBridge.cs` | 912 |
| `WorldBuilder.BuildCombat`（GAP-2） | `Assets/_Project/Scripts/Runtime/WorldBuilder.cs` | 729 |
| `HudSkillBar` 布局常量 | `Assets/_Project/Scripts/Runtime/HudSkillBar.cs` | 31 / 34 / 37 |
| `Hud` 玩家面板定位 | `Assets/_Project/Scripts/Runtime/Hud.cs` | 206-208 |
| `RunPhaseTests` RP01–RP11 | `Assets/Scripts/Systems/Combat/Tests/RunPhaseTests.cs` | 433 行 |

### 8.3 数值实测（`CombatConfig.cs`，本期全部冻结）

| 常量 | 值 | 行号 |
|---|---|---|
| `BOSS_PHASE_P2_THRESHOLD` | 0.65 | 73 |
| `BOSS_PHASE_P3_THRESHOLD` | 0.30 | 76 |
| `BOSS_P2_SPEED_MULT` / `BOSS_P2_ATK_MULT` | 1.25 / 1.0 | 85 / 88 |
| `BOSS_P2_SUMMON_CD` / `BOSS_P2_SUMMON_N` | 8.0s / 2 | 91 / 94 |
| `BOSS_P3_ATK_MULT` / `BOSS_P3_SPEED_MULT` | 1.40 / 1.40 | 97 / 100 |
| `BOSS_P3_SHOCK_CD` / `BOSS_P3_SHOCK_RADIUS` | 6.0s / 200.0 | 103 / 106 |
| `BOSS_POISE_MULT` / `BOSS_EXP_MULT` | 3.0 / 3.0 | 109 / 112 |
| `BOSS_SUMMON_LEVEL_OFFSET` | -1 | 115 |
| `TOUCH_RANGE` / `AI_STRIKE_DIST` / `AI_LEASH_DIST` | 44 / 160 / 480 | 29 / 39 / 36 |
| `CameraOrthoSize`（`WorldBuilder.cs`） | 352.0 | 71 |

### 8.4 `zones.json` 实测（6 区域，1 安全 + 5 战斗）

| zone_id | is_safe | base_level | boss |
|---|---|---|---|
| （第 1 区） | true | — | `null` |
| **`zone_youhuang`（幽篁竹海）** | false | **3** | **`boss_zhuxiaowang` 竹魈王**，kind=witch，level_offset=**2**，hp_mult=10.0，atk_mult=1.6，drop_extra=`mat_bamboo_marrow×2` + `equipment_quality_cap` |
| （第 3 区） | false | — | `boss_yanxiao` 焰魈，blood，+3，12.0，1.7 |
| （第 4 区） | false | — | `boss_xuanbingjiao` 玄冰蛟，witch，+3，14.0，1.8 |
| （第 5 区） | false | — | `boss_jianzhongshouling` 剑冢守灵，sword，+3，16.0，1.9 |
| （第 6 区） | false | — | `boss_guixumozun` 归墟魔尊，blood，+4，20.0，2.0 |

### 8.5 缺口实测证据（grep 结果）

- `SpawnBoss`：仅 2 处命中 —— 定义（`CombatController.cs:423`）与一条注释（`CombatBridge.cs:911`），**零真实调用** ✅ 确认 GAP-3
- `BossPhaseChanged`：仅 3 处命中，全在 `CombatEventsUnity.cs`（声明 :69、判空 :162、调用 :164），**零外部订阅** ✅ 确认 GAP-4
- `ShockwaveFxPrefab`：仅 2 处命中，全在 `CombatEventsUnity.cs`（声明 :40、使用 :192），**零装配** ✅ 确认 GAP-5
