# 功能闭环规划：从当前切片到可玩 Demo

> 文档版本：v1.0 · 落盘日期：2026-08-08
> 作者：许清楚（Xu，产品经理）
> 工程路径：`F:/AI-project/ancientGame/shuimofeng/shuimofeng`（Unity 2022.3.62f3c1）
> 适用阶段：T3 P0 收尾后 → MVP 可玩 Demo
> **本文档只做规划与梳理，不含任何代码改动。**

---

## 0. 先读这一段（给新手看的三句话）

1. **战斗的"大脑"已经做完了，而且是被测试证明过的。** 纯逻辑内核（`Xianxia.Core` / `Xianxia.Combat`）64/64 + 9/9 + NUnit 88 全绿，数值口径冻结。这部分不用担心。
2. **战斗的"身体"（Unity 表现层）写完了，但没人真正跑起来看过。** 本开发环境**没有 Unity 编译器**，所有 Unity 侧代码只能做静态检查（语法/结构/装配关系），跑不了 PlayMode。目前已经累积了**三批未经运行时验证的改动**。
3. **离"能玩"最缺的不是战斗，是一局游戏的"开始"和"结束"。** 现在玩家血量归零后，游戏里**什么都不会发生**——不结算、不重开、不提示。这是 MVP 的头号缺口。

---

## 1. 现状核实（本次逐文件复查结论）

### 1.1 代码资产盘点

| 层 | 位置 | 文件数 | 状态 |
|---|---|---|---|
| 纯逻辑内核 | `Assets/Scripts/Core/` + `Assets/Scripts/Systems/Combat/` | 33 个 `.cs` | ✅ 有测试护栏 |
| Unity 表现层 | `Assets/_Project/Scripts/Runtime/` | 26 个 `.cs`（含 `Input/` 6 个） | ⚠️ 静态检查过，运行时未验 |
| Editor 工具 | `Assets/_Project/Scripts/Editor/` | 1 个（`ShuimoSceneRebuilder`） | ⚠️ 未验 |
| 静态护栏 | `Assets/Scripts/Systems/Combat/Tests/t1_selfcheck.py`（84 KB）<br>`Assets/Scripts/Systems/Combat/Tests/t3_selfcheck.py`（33 KB）<br>`Assets/_Project/t2_static_check.py`（6 KB） | 3 个 | ✅ 可在本环境跑 |
| 数据 | `Assets/Data/zones.json`（13 KB，6 个 zone）+ `codex.json` | 2 个 | ✅ 已就位 |
| 场景 | `Assets/Scenes/SampleScene.unity` + `T2Slice.unity` | 2 个 | ⚠️ 未验 |
| 美术源图 | `Assets/images/`（3 张 samples + 5 张 heroine，均 1.2–1.7 MB） | 8 张 PNG | ❌ **无 `.meta`，未导入 Unity，非精灵帧，未接线** |

### 1.2 三态诚实标注

**A. 已就绪且被证明（可放心依赖）**

- 战斗内核：伤害结算 `DamageResolver`、调度 `CombatScheduler`、W-CORE 双闸门（0.6s 每源 / 0.2167s 全局）、围攻倍率 2.5294x
- 确定性随机 `PCG32`（`DEFAULT_SEED=20260730`），禁 `System.Random`
- 数值口径冻结：`PlayerHpMax=260` / `d_eff=4.0` / `raw=12` / `CD=0.4s`
- 技能表：4 个已注册（`basic` 普攻 / `circle_burst` 技能1 / `blood_lotus` 技能2 / `dodge_roll` 闪避），槽位已分配
- 敌人 4 类：`blood` / `witch` / `sword` / `alchemy`，含精英修正（HP×2.6 / ATK×1.5 / EXP×2.0）
- 区域数据：6 个 zone 已定义（含 tier 分级、unlock 条件、boss 配置字段）
- `BossController` 纯逻辑：三相位 + 召唤 + 震荡波（**仅逻辑，Unity 未接线**）

**B. 写了但没验证（"薛定谔的代码"——本项目最大风险区）**

| 内容 | 静态护栏结论 | 运行时状态 |
|---|---|---|
| T2 实时切片（移动/普攻/受击/敌人 AI/相机/HUD/斩击特效/世界生成） | t2 静态检查通过；用户 08-05 截图确认过"0 错误 0 警告" | ❌ PlayMode 手感未复验 |
| T3 输入抽象层（`InputBinder` + `GameAction` + `InputBindingProfile` + 键鼠/手柄双源） | t3_selfcheck 9/9 | ❌ 未验 |
| `SkillController`（K/L/右键）、`DodgeController`（Shift/Space） | 9/9 | ❌ 未验 |
| `CombatBridge.SetupT3` 装配 | 9/9 | ❌ 未验 |
| T3 UI（`HudSkillBar` / `HudStatusIcons` / `VfxSkill` / `VfxStatus`） | 9/9 | ❌ 未验 |
| **K/L/右键无响应 Bug 修复** | 代码已确认落地：`WorldBuilder.cs:145` 挂 `InputBinder`、`:574` 挂 `SkillController`、`:575` 挂 `DodgeController`；`CombatBridge` 幂等自愈；护栏复跑 9/9 + 64/64 全绿 | ❌ **修复是否真的生效，完全依赖用户本地 PlayMode 验证** |
| P0-2 死亡结算 / P0-3 胜负 / P0-4 重开（阶段 7+14） | `RunPhase.cs` + `GameOverHud.cs` + `MenuHud` R 重开 / 重新开始；静态代码核查通过 | ❌ 未验 |
| P0-5 主菜单/暂停/退出（阶段 14） | `MainMenuHud.cs` + `PauseMenuHud.cs` + 统一暂停闸门 `IsGameplayBlocked`；静态核查通过 | ❌ 未验 |
| P0-6 操作引导（阶段 14） | `ControlsGuideHud.cs`；静态核查通过 | ❌ 未验 |
| P1-6 玩家成长曲线（阶段 16） | `Progression.cs` 纯逻辑 + NUnit 测试；Python 护栏 9/9 + 64/64 | ❌ Unity 接线待本地验证 |
| P1-2 受击反馈（阶段 18） | `FeedbackClock` / `HitFeedbackDirector` / `CameraShake` / `DamagePopupLayer` / `PlayerHitFlash`；43 条测试 + 静态核查通过 | ❌ 未验 |

**C. 根本没写（真缺口）**

以下均已用全仓 grep 确认为**零命中**或**仅内核侧存在、玩家侧缺失**：

- **玩家死亡后处理**：`gameover` / `respawn` / `restart` / `victory` / `defeat` ~~全仓 **0 命中**~~。~~内核会把 `Player.IsAlive` 置 false，但 Unity 层没有任何响应~~ → **阶段 14 已交付**（`GameOverHud.cs` / 终局闸门 / R 重开），**待用户本地 PlayMode 验证**
- **胜负判定 / 局内结算 / 重开**：~~无~~ → **阶段 7 已交付**（`RunPhase.cs` 状态机 Playing/Won/Lost + `GameOverHud` 结算面板），**待用户本地验证**
- **主菜单 / 暂停 / 退出游戏**：~~无场景、无脚本~~ → **阶段 14 已交付**（`MainMenuHud.cs` / `PauseMenuHud.cs` / 统一暂停闸门），**待用户本地验证**
- **新手引导**：~~HUD 里无任何操作说明文本~~ → **阶段 14 已交付**（`ControlsGuideHud.cs`），**待用户本地验证**
- **音频**：全仓 **0 个 `AudioSource`**（仅相机上有一个 `AudioListener`）
- **存档**：**0 个 `PlayerPrefs`**、0 序列化。（注：`zones.json` 注释已声明 `zone_id` 是存档 key，设计上预留了，但代码未写）
- **玩家成长**：~~内核有 `ExpValue` / `BOSS_EXP_MULT` / tier 掉落分级，但**玩家侧没有任何等级、经验吸收、属性点、掉落、背包**~~ → **阶段 16 已交付**（`Progression.cs` 纯逻辑 + NUnit 测试），**待用户本地验证**
- **区域切换 / 传送**：`ZoneLoader` 与 6 个 zone 数据都在，但 `WorldBuilder` 只构建单区域，无切换入口
- **美术接线**：~~`SpriteFactory` 注释直言"工程里因此一张 png 都没有"，所有可见物（玩家 24×24 方块、敌人 26px 圆点、地块、月牙）均运行时 `Texture2D` 逐像素生成~~ → **阶段 15 已交付**（女主 39 帧水墨精灵接入 Unity），**待用户本地验证**；敌人精灵仍为方块

---

## 2. 清单 A：功能闭环到可玩 Demo 的缺口清单

### 2.1 归属层图例

| 标记 | 含义 | 验证方式 |
|---|---|---|
| 🟢 **纯逻辑** | 可写在 `Xianxia.Core`/`Xianxia.Combat`，零 `UnityEngine` | 本环境写 NUnit + Python 护栏；用户跑 Test Runner 复验 |
| 🔴 **Unity 表现层** | 必须碰 `UnityEngine`，本环境**无法运行验证** | 只能静态检查；**必须**用户本地编译 + PlayMode 验证 |
| 🟡 **混合** | 逻辑判定在内核、表现在 Unity，需两侧配合 | 逻辑侧 NUnit + Unity 侧用户验证 |

### 2.2 P0 — 没有它就不算"能玩"（共 6 项）

| # | 模块 | 现状 | 缺口描述 | 归属层 |
|---|---|---|---|---|
| **P0-1** | **战斗 loop 真能跑通** | 部分 | 代码路径完整（移动→普攻→命中→敌人死亡），但**从未在 PlayMode 跑过**。K/L/右键修复也在此列。这是"验证"缺口，不是"开发"缺口 | 🔴 Unity（**用户验证是唯一出路**） |
| **P0-2** | **玩家死亡 → 游戏结束** | ✅ 已交付 | ~~内核已把玩家纳入接触伤害结算~~ → `RunPhase` 状态机 + `GameOverHud` 结算面板 + 冻结输入 + 死亡表现。**待用户本地 PlayMode 验证** | 🟡 混合 |
| **P0-3** | **胜负判定** | ✅ 已交付 | ~~无~~ → `RunPhase.cs`（Playing/Won/Lost 状态机）。纯逻辑 + NUnit，本环境可自证 | 🟢 纯逻辑 |
| **P0-4** | **重开 / 重试** | ✅ 已交付 | ~~无~~ → "重新开始"按钮 + `WorldBuilder.DestroyGeneratedRoots` + 重置内核。**待用户本地验证** | 🔴 Unity |
| **P0-5** | **主菜单 / 暂停 / 退出** | ✅ 已交付 | ~~无~~ → `MainMenuHud.cs`（开始/退出）+ `PauseMenuHud.cs`（继续/重开/返回主菜单/退出）+ 统一暂停闸门 `IsGameplayBlocked`。**待用户本地验证** | 🔴 Unity |
| **P0-6** | **基础操作引导** | ✅ 已交付 | ~~无~~ → `ControlsGuideHud.cs`（开局静态按键说明 + HUD 常驻小字）。**待用户本地验证** | 🔴 Unity |

> **P0 判读**：6 项全部代码已交付，但其中 5 项为 Unity 表现层，**卡在用户本地验证链上**。本轮通过的环境无法编译/运行 Unity，所有 Unity 侧功能均只做了静态检查。

### 2.3 P1 — 不丢人必需（共 7 项）

| # | 模块 | 现状 | 缺口描述 | 归属层 |
|---|---|---|---|---|
| **P1-1** | **技能/闪避实际生效 + 手感** | 部分 | 代码全在（4 技能已注册、双池灵力/体力已实现），但手感（前摇/后摇/位移/无敌帧长度）**必须靠人手试**。参数在内核可调，试是 Unity 侧的事 | 🟡 混合 |
| **P1-2** | **受击反馈** | ✅ 已交付 | ~~`VfxStatus` / `VfxSlash` / 硬直~~ → `FeedbackClock`（顿帧）+ `CameraShake`（屏震）+ `PlayerHitFlash`（分阵营闪白）+ `DamagePopupLayer`（飘字）。43 条测试。**待用户本地验证** | 🔴 Unity |
| **P1-3** | **音效 / BGM** | **无** | 全仓 0 个 `AudioSource`。最少需要：普攻挥击、命中、受击、死亡、技能×2、BGM×1 = 7 个音源。**内核严禁出现 `AudioSource`**（会破坏对拍），必须走事件 → Unity 侧播放 | 🔴 Unity（内核仅出事件 🟢） |
| **P1-4** | **美术去方块** | 部分 | 女主 39 帧水墨精灵已接入（阶段 15），**待用户本地验证**；敌人仍为方块，需 ImageGen 积分批量出图 | 🔴 Unity |
| **P1-5** | **简单关卡 / 区域** | 部分 | 6 个 zone 数据 + `ZoneLoader` 已在，但只 build 单区域。MVP 建议：**2 个区域 + 1 个安全区**，用传送点串起来，不做开放世界 | 🟡 混合（选区逻辑 🟢 / 切场景 🔴） |
| **P1-6** | **数值成长入口** | ✅ 已交付 | ~~**无（玩家侧）**~~ → `Progression.cs` 纯逻辑（经验→等级→血上限/攻击力）+ NUnit 测试 + Unity 接线。**待用户本地验证** | 🟢 纯逻辑 |
| **P1-7** | **T3 P1 战斗厚度（选做）** | 无 | changelog 已列：3 段连招 / AI 七态 / Lock-on / Debuff 补至 9 种 / 连击增伤 / 受击反应四级。**MVP 只建议做「3 段连招」**，其余留到 Demo 之后。⚠️ 引入硬控或敌人技能化必须重跑 U1 平衡回归 | 🟡 混合 |

### 2.4 P2 — 锦上添花（共 6 项）

| # | 模块 | 现状 | 缺口描述 | 归属层 |
|---|---|---|---|---|
| **P2-1** | **BOSS 战** | 逻辑已写 | `BossController` 三相位/召唤/震荡波都在纯逻辑层，**Unity 未接线**。接线成本不高，性价比高，若时间允许可提到 P1 | 🟡 混合 |
| **P2-2** | **多技能扩展** | 部分 | 现有 2 主动技能。扩到 4–6 个才有 build 感 | 🟢 纯逻辑为主 |
| **P2-3** | **剧情 / 文本** | 无 | `codex.json` 已有词条框架。MVP 阶段一段开场字幕即可 | 🔴 Unity |
| **P2-4** | **存档** | **无** | 0 `PlayerPrefs` / 0 序列化。`zone_id` 作为存档 key 的约定已在数据层预留。单机买断游戏最终必须有，但 Demo 可先不做 | 🟡 混合 |
| **P2-5** | **设置菜单** | 无 | 音量 / 分辨率 / 按键重绑。`InputBindingProfile` 已支持重绑，只差 UI | 🔴 Unity |
| **P2-6** | **PlayMode 自动化冒烟** | 无 | changelog 已列入 T3 P2。**对本项目价值极高**（见 §4 风险），但它本身也需要 Unity 才能跑 | 🔴 Unity |

### 2.5 Steam 上架附加项（非玩法，但上架必须）

| 项 | 现状 | 优先级 |
|---|---|---|
| 游戏图标 / 窗口标题 / 公司名（ProjectSettings） | 未配置 | P1 |
| 分辨率与窗口模式支持 | 未配置 | P1 |
| Build 打包验证（Windows x64 出包能跑） | 未验证 | P0（Demo 交付前） |
| Steam 商店页素材（胶囊图/截图/预告片） | 无 | P2（上架前） |

### 2.6 缺口汇总统计

| 优先级 | 项数 | 其中纯逻辑可自证 🟢 | 其中卡 Unity 验证 🔴/🟡 |
|---|---|---|---|
| P0 | 6 | 1（P0-3） | **5** |
| P1 | 7 | 1（P1-6） | 6 |
| P2 | 6 | 1（P2-2） | 5 |
| **合计** | **19** | **3（16%）** | **16（84%）** |

> **这张表就是本项目的核心结论：84% 的剩余工作，本环境无法自行验证。**

---

## 3. 清单 B：美术替换路线（后期皮，不阻塞功能）

> ### ⚠️ 前置声明
> **本清单是后置任务。当前阶段（功能闭环）不做、不等、不阻塞。**
> 理由：所有可见物走 `SpriteFactory` 统一归口，换皮时只需替换 `SpriteFactory` 的返回值与 `SpriteRenderer.sprite` 赋值点，**不触碰任何战斗逻辑与数值**。功能先跑通，美术随时能换。
> 建议触发时机：**P0 全部完成、且用户本地 PlayMode 验证通过之后**。

### 3.1 三步走

**第一步：AI 出图（可在本环境做，不需要 Unity）**
产出透明底 PNG 精灵帧，按 §3.3 清单批量生成，落到 `Assets/images/` 对应子目录。

**第二步：Unity 接线（必须用户本地做）**
1. 导入 PNG → Texture Type 设 `Sprite (2D and UI)`
2. **`Pixels Per Unit` 必须设为 1**（⚠️ 全工程 PPU=1，`TileUnit=32`，世界坐标 = 像素数。设错会导致角色与地图尺寸完全错位）
3. `Filter Mode` 建议 `Bilinear`（水墨风有渐变晕染，`Point` 会显脏）
4. 多帧图用 Sprite Editor `Slice → Grid By Cell Size` 切帧
5. 改造 `SpriteFactory`：新增贴图加载分支，保留程序化生成作为兜底（找不到贴图时回退方块，**保证不会因为缺图而崩**）
6. 给 Player / Enemy 挂 `Animator` + `AnimatorController`，状态机：`Idle ↔ Walk → Attack → Hurt → Death`
7. 现有控制器（`PlayerController` / `AttackController` / `DodgeController`）**只需增加 `animator.SetTrigger/SetFloat` 调用，不改任何判定逻辑**

**第三步：2D 骨骼升级（远期，可选）**
用 Unity 2D Animation 包做骨骼绑定，替代逐帧图。优点：动作更顺、体积更小、加新动作不用重画。**Demo 阶段完全不需要。**

### 3.2 关键尺寸约定（照抄，别自己改）

| 参数 | 值 | 来源 |
|---|---|---|
| Pixels Per Unit | **1** | `SpriteFactory.cs:55` 注释 |
| 地块边长 | **32** 世界单位 = 32 px | `WorldBuilder.TileUnit = 32` |
| 玩家占位体 | 24×24 → **建议换成 48×64**（约 1.5×2 格，人形比例才对） | `WorldBuilder.PlayerBodySize = 24` |
| 敌人占位体 | 26 圆 → **建议 48×48**（精英自动 ×1.45 缩放，已有逻辑） | `WorldBuilder.EnemyBodySize = 26`，`EnemySpawner.EliteScale = 1.45` |
| 相机正交尺寸 | 352 | `WorldBuilder.CameraOrthoSize` |

> 换算直觉：屏幕纵向可见约 704 世界单位 ≈ 22 个地块。角色 64 高 ≈ 屏幕高度的 1/11，视觉合适。

### 3.3 素材清单（供后续 ImageGen 批量出图）

**统一风格提示词基底**：`中国传统水墨画风格，黑白灰为主色调点缀朱砂红，写意笔触晕染，2D 俯视角游戏精灵，透明背景，无阴影投射`

#### ① 女侠主角 `Assets/images/characters/heroine/`

| 文件名 | 单帧尺寸 | 帧数 | 说明 |
|---|---|---|---|
| `heroine_idle.png` | 48×64 | 4 | 待机呼吸，衣袂轻摆 |
| `heroine_walk_down.png` | 48×64 | 6 | 朝屏幕下方走 |
| `heroine_walk_up.png` | 48×64 | 6 | 背面 |
| `heroine_walk_side.png` | 48×64 | 6 | 侧面（左右翻转复用） |
| `heroine_attack.png` | 96×64 | 6 | 挥剑，横向留出剑势空间 |
| `heroine_hurt.png` | 48×64 | 2 | 受击后仰 |
| `heroine_dodge.png` | 48×64 | 4 | 翻滚，带残影 |
| `heroine_death.png` | 64×64 | 5 | 倒地，末帧化墨消散 |

**小计：8 张图集 / 39 帧**

#### ② 四类小怪 `Assets/images/characters/enemies/`

严格对应内核 kind：`blood`（血傀，赭红）/ `witch`（巫祝，紫）/ `sword`（剑修，青灰）/ `alchemy`（丹修，土金）——配色见 `EnemySpawner.BloodColor` 等常量。

| 文件名模板 | 单帧尺寸 | 帧数 | ×4 类 |
|---|---|---|---|
| `{kind}_idle.png` | 48×48 | 4 | 4 张 |
| `{kind}_walk.png` | 48×48 | 4 | 4 张 |
| `{kind}_attack.png` | 64×48 | 4 | 4 张 |
| `{kind}_death.png` | 48×48 | 4 | 4 张 |

**小计：16 张图集 / 64 帧**

#### ③ 地图瓦片 `Assets/images/tiles/`

对应 `zones.json` 的 4 种 `theme.algo`：`forest` / `volcanic` / `frozen` / `town`

| 文件名模板 | 尺寸 | 数量 | 说明 |
|---|---|---|---|
| `{theme}_ground.png` | 32×32 | 4 | 可平铺地面，**必须四边无缝** |
| `{theme}_rock.png` | 32×32 | 4 | 阻挡物 |
| `{theme}_water.png` | 32×32 | 4 | 阻挡物（水墨留白最出效果） |
| `{theme}_path.png` | 32×32 | 4 | 路面 |

**小计：16 张 / 16 帧**
> ⚠️ 瓦片必须**四边无缝可平铺**，出图时在提示词里强调 `seamless tileable texture`，否则地图会出现明显网格线。

#### ④ 特效贴图 `Assets/images/vfx/`

| 文件名 | 单帧尺寸 | 帧数 | 对应代码 |
|---|---|---|---|
| `vfx_slash.png` | 128×128 | 5 | `VfxSlash`（当前是程序化月牙） |
| `vfx_burst.png` | 160×160 | 6 | `VfxSkill.NewInstance("VfxBurst")` 圆爆 |
| `vfx_lotus.png` | 160×160 | 6 | `VfxSkill.NewInstance("VfxLotus")` 血莲 |
| `vfx_dodge.png` | 96×96 | 4 | `VfxSkill.NewInstance("VfxDodge")` 闪避残影 |
| `vfx_hit.png` | 64×64 | 4 | 命中火花（新增） |

**小计：5 张 / 25 帧**

#### ⑤ UI 素材 `Assets/images/ui/`

| 文件名 | 尺寸 | 数量 | 说明 |
|---|---|---|---|
| `ui_bar_frame.png` | 420×26 | 1 | 血条外框，尺寸对齐 `Hud.HpBarSize` |
| `ui_skill_icon_{1..4}.png` | 64×64 | 4 | 普攻/技能1/技能2/闪避 |
| `ui_status_icon_{1..9}.png` | 32×32 | 9 | Debuff 图标（当前 Debuff 只做了部分，可先出 4 个） |
| `ui_panel_bg.png` | 512×512 | 1 | 九宫格面板底（菜单/结算用） |

**小计：15 张**

#### 总计

| 类别 | 图集数 | 总帧数 |
|---|---|---|
| 主角 | 8 | 39 |
| 小怪 | 16 | 64 |
| 瓦片 | 16 | 16 |
| 特效 | 5 | 25 |
| UI | 15 | 15 |
| **合计** | **60 张图集** | **159 帧** |

### 3.4 现有 8 张 AI 图的处置

`Assets/images/samples/`（3 张）与 `Assets/images/characters/heroine/`（5 张）已有产出，但：
- 均为 1.2–1.7 MB 的**大幅立绘**，非游戏精灵帧
- **无 `.meta` 文件**，说明从未被 Unity 导入
- 未切帧、未透明底处理

**结论：作为风格参考图保留（用于统一后续出图的画风提示词），不作为游戏内素材使用。** 建议移到 `docs/art-reference/` 或保留原地并在文件名前加 `ref_` 前缀，避免误以为已接线。

---

## 4. 🚨 结构性风险：验证断链（本项目头号卡点）

### 4.1 问题陈述

本开发环境**没有 Unity 编译器**。这导致一条硬性断链：

```
本环境能做                          本环境不能做
─────────────────────────────      ─────────────────────────────
✅ 写代码                           ❌ 编译（C# 编译错误无法发现）
✅ 静态结构检查（Python 护栏）        ❌ 运行 PlayMode
✅ 纯逻辑 NUnit 代码编写             ❌ 跑 NUnit（只能用户 Test Runner 跑）
✅ 装配关系人工核对                  ❌ 验证 AddComponent 是否真的生效
✅ AI 出图                          ❌ 验证贴图导入/尺寸/动画
```

### 4.2 为什么这是"头号卡点"而不是"一般不便"

1. **84% 的剩余工作落在不可验证区**（见 §2.6）。这不是边角问题，是主干问题。
2. **K/L Bug 的成因本身就是这条断链造成的。** 那个 Bug 的本质是"`WorldBuilder.BuildPlayer()` 漏挂组件"——**静态护栏 100% 检查不出来**，因为语法完全正确、类型完全正确、9/9 全绿。它只有在 PlayMode 里按下 K 键没反应时才暴露。**同类 Bug 现在还可能潜伏着若干个。**
3. **未验证改动正在累积复利风险。** 目前已堆了三批（T2 U1 平衡修正 / T3 P0 全套 / K-L 修复）。一旦用户跑 PlayMode 报错，问题会同时来自三个批次，**定位成本不是相加，是相乘**——因为无法判断是哪一批引入的，也无法二分回滚。
4. **"护栏全绿"有误导性。** 64/64 + 9/9 证明的是"逻辑内核的数学是对的"和"文件结构符合约定"，**不证明"游戏能跑"**。这两件事之间隔着整个 Unity 运行时。

### 4.3 缓解建议（按性价比排序）

| # | 措施 | 成本 | 收益 |
|---|---|---|---|
| **1** | **立刻做一次"债务清零"验证**：在开发任何新功能前，请用户打开 Unity 跑一遍——编译 0 错 / Test Runner RunAll 全绿 / PlayMode 手动过一遍 WASD+左键+K+L+右键+Shift | 用户 20 分钟 | **最高**。把三批债一次性收敛到一个已知good状态 |
| **2** | **改为小批次交付**：此后每次只交付一个可独立验证的小改动，交付即请用户验证，验完再动下一个 | 节奏变慢 | 把"相乘"的定位成本压回"相加" |
| **3** | **优先做纯逻辑项**（P0-3 胜负状态机、P1-6 成长曲线）：这两项本环境能自证，不占用用户验证预算 | 低 | 在等待验证窗口时不空转 |
| **4** | **建立 PlayMode 自动化冒烟**（P2-6）：让用户跑一个脚本就能验证核心链路，而不是手动点 | 中 | 长期把每次验证从 20 分钟压到 2 分钟 |
| **5** | **为所有 Unity 装配写"自愈 + 显式报错"**：像 `CombatBridge` 幂等自愈那样，组件缺失时 `Debug.LogError` 点名，让用户一看 Console 就知道哪漏了 | 低 | 把"没反应"这种最难查的症状，变成"红字点名" |

### 4.4 一句话结论

> **当前阻碍"功能闭环"的，不是还有多少功能没写，而是已经写完的东西没人能证明它是活的。**
> 建议下一步动作不是开发，是**请用户做一次完整的本地验证，把技术债清零**。

---

## 5. 建议执行顺序（MVP 路线图）

| 阶段 | 内容 | 依赖 |
|---|---|---|
| **第 0 步** | 🚨 **用户本地验证债务清零**（编译 + RunAll + PlayMode 六键手测） | 无 —— **立即可做，且必须先做** |
| 第 1 步 | P0-3 胜负状态机（纯逻辑 + NUnit） | 可与第 0 步并行 |
| 第 2 步 | P0-2 死亡处理 + P0-4 重开 | 依赖第 0 步通过 |
| 第 3 步 | P0-5 主菜单/暂停 + P0-6 操作引导 | 依赖第 2 步 |
| 第 4 步 | P1-6 成长曲线（纯逻辑） + P1-1 手感调参 | 依赖第 0 步 |
| 第 5 步 | P1-2 受击反馈 + P1-3 音效 | 依赖第 3 步 |
| 第 6 步 | **P1-4 美术换皮（清单 B）** | 依赖 P0 全绿 |
| 第 7 步 | P1-5 多区域 + Build 出包验证 | 依赖第 6 步 |
| 第 8 步 | P2 选做（BOSS 接线性价比最高） | 视时间 |

---

## 附录 · 本文档事实来源

所有结论均基于 2026-08-08 对 `F:/AI-project/ancientGame/shuimofeng/shuimofeng` 的逐文件核查：

- 文件清单：`find Assets -name "*.cs"` → 61 个
- 缺失项判定：全仓 `grep -rniE` 零命中（gameover/respawn/restart/victory/defeat、PlayerPrefs、AudioSource）
- 尺寸常量：`WorldBuilder.cs:65,71,74,77`、`SpriteFactory.cs:31-32,55`
- 技能注册：`SkillConfig.cs:361-444`
- 玩家受伤路径：`Encounter.cs:438-447`（`DamageResolver.ResolveContact(e, Player, TouchRange, Events)`）
- 组件装配：`WorldBuilder.cs:145,556,559,574,575`
- 历史状态：`docs/changelog.md`（截至 2026-08-07）

*本文档不含代码改动，未提交 git。*
