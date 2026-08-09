# 精灵帧规划文档（art-sprite-plan）

> **状态**：草案 v0.1 · 编写者：软件工程师（Alex）· 不阻塞功能闭环，独立推进。
> **依据**：`docs/feature-closure-plan.md` §3「清单 B：美术替换路线」。
> **本文件性质**：仅规划，**不触发任何 AI 出图**（出图调用走后续任务）。

---

## 1. 项目现状

| 项 | 当前状态 |
|---|---|
| 游戏中所有可见物（地块 / 玩家 / 敌人 / 剑气 / HUD） | 全部由 `Assets/_Project/Scripts/Runtime/SpriteFactory.cs` **运行时程序化生成**（白方块 / 上色圆点 / 月牙），无任何静态美术资源 |
| `Assets/images/samples/` | 3 张大幅水墨参考样品（女侠立绘 / 妖魔小怪 / 山水场景），**约 1.6 MB/张**，均未生成 `.meta`，未切帧，未透明底 |
| `Assets/images/characters/heroine/` | 5 张女主全身单帧图（"游戏角色精灵..."、"游戏角色精灵帧..."），同样是大幅立绘，**与上面一样未导入、未切帧、未透明底** |
| `docs/feature-closure-plan.md` §3.4 结论 | "作为风格参考图保留，**不作为游戏内素材使用**" |

**关键结论**：当前 `Assets/images/` 下的 8 张 PNG **全部是风格参考**，没有任何一张是可直接挂到 `SpriteRenderer` 的精灵帧。要做换皮必须**重新按本文件规划的尺寸/网格切帧生成**，而不是直接拿现成的图。

---

## 2. 统一风格提示词基底（出图时全用这一段作为基底）

> **直接引用** `docs/feature-closure-plan.md` L178（已与原作者对齐，不再修改）：
>
> `中国传统水墨画风格，黑白灰为主色调点缀朱砂红，写意笔触晕染，2D 俯视角游戏精灵，透明背景，无阴影投射`

针对每个分类的**后缀约束**（按文档要求叠加在基底之后）：

* **瓦片**追加：`seamless tileable texture，四边可平铺无缝`（必加，否则地图拼出明显网格线）
* **角色 / 敌人**追加：`俯视角，全身体型完整居中，下方留 8px 安全边`（避免脚底/头顶被切）
* **特效**追加：`居中向外辐射，alpha 通道透明`（粒子图都按这个出）
* **UI**追加：`纯色墨韵边，alpha 通道透明，白底不留`（图标底板统一）

---

## 3. Pixels Per Unit 与角色高度基准

| 参数 | 值 | 来源 |
|---|---|---|
| **Pixels Per Unit（PPU）** | **1** | `SpriteFactory.cs:55` 注释 + `WorldBuilder.TileUnit = 32` → 像素数 = 世界单位 |
| 地块边长 | 32 像素 | `WorldBuilder.TileUnit` |
| **角色推荐身高** | **64 像素**（约屏幕纵向高度 1/11） | `feature-closure-plan.md` L174 |
| 玩家占位体 | 24×24 → **建议换成 48×64** | `feature-closure-plan.md` L170 |
| 敌人占位体 | 26 圆 → **建议换成 48×48**（精英自动 ×1.45 缩放，已有逻辑） | `feature-closure-plan.md` L171 |
| 相机正交尺寸 | 352 | `WorldBuilder.CameraOrthoSize` |
| 屏幕纵向可见 | 约 704 世界单位 ≈ 22 个地块 | `feature-closure-plan.md` L174 |

> **绝对不能改 PPU**。本工程全 PPU=1，改了所有现有坐标立刻错位。

---

## 4. 精灵帧清单

> **写作约定**：
> - 单帧尺寸 = `宽 × 高`（像素），已含帧间距的最终贴图尺寸按「帧数 × 单帧宽」计算；
> - 帧数列 = 该图集总帧数；
> - **参考 kind/theme** 列绑死内核字符串字面量（`kind` 取自 `EnemySpawner.ColorOf(kind)` 的入参；`theme` 取自 `WorldBuilder.Theme`），用于提示词里点名配色。

### ① 女主角 `Assets/images/characters/heroine/`

> **8 张图集 / 39 帧**（与 `feature-closure-plan.md` L180–193 对齐）。

| 文件名 | 单帧尺寸 | 帧数 | 说明 |
|---|---|---|---|
| `heroine_idle.png` | 48×64 | 4 | 待机呼吸，衣袂轻摆 |
| `heroine_walk_down.png` | 48×64 | 6 | 朝屏幕下方走 |
| `heroine_walk_up.png` | 48×64 | 6 | 背面 |
| `heroine_walk_side.png` | 48×64 | 6 | 侧面，**左右翻转复用** |
| `heroine_attack.png` | 96×64 | 6 | 挥剑，横向留出剑势空间 |
| `heroine_hurt.png` | 48×64 | 2 | 受击后仰 |
| `heroine_dodge.png` | 48×64 | 4 | 翻滚，**带残影**（前 2 帧实 + 后 2 帧半透明） |
| `heroine_death.png` | 64×64 | 5 | 倒地，末帧化墨消散 |

> **⚠️ 与任务简报的差异**：简报列了 `attack1/attack2/skill/dodge/hit`，**本文档沿用 feature-closure-plan.md 的命名**（`attack`/`hurt`/`dodge`/`death`）。原因：
> 1. `attack1/attack2` 在当前战斗逻辑里没有分支（`AttackController` 是单段判定），强行拆两张没意义；
> 2. `skill` 与 `attack` 视觉上都是"挥剑"动作的变体，分开两张反而提高维护成本；
> 3. `hit` 对应文档的 `hurt`（命中后仰），是同一回事；
> **建议在阶段 B 出样品时按文档命名做，样片落地后若用户觉得需要细分再补。**

### ② 敌人 `Assets/images/characters/enemies/`

> **16 张图集 / 64 帧**（4 类 × 4 动作 × 4 帧）。
>
> ⚠️ **任务简报里的路径是 `Assets/images/enemies/`，本文档以 feature-closure-plan.md L195 的 `Assets/images/characters/enemies/` 为准**——主角在 `characters/heroine/`，把敌人放在同级 `characters/enemies/` 维护性更好。

`{kind}` ∈ {`blood`, `witch`, `sword`, `alchemy`}（对应 `EnemySpawner.ColorOf` 入参）。
**文件名模板**：`{kind}_{action}.png`（每张图集一份）。

| 文件名 | 单帧尺寸 | 帧数 | 说明 | 参考 kind |
|---|---|---|---|---|
| `{kind}_idle.png` | 48×48 | 4 | 待机，敌种静止时呼吸 | `blood` 赭红 / `witch` 紫 / `sword` 青灰 / `alchemy` 土金 |
| `{kind}_walk.png` | 48×48 | 4 | 朝玩家走，2 方向（向下/向上），侧面通过 Transform 翻转 | 同上 |
| `{kind}_attack.png` | 64×48 | 4 | 攻击前摇 + 出手 + 收招（**注意宽出 16px 是给手臂/武器空间**） | 同上 |
| `{kind}_death.png` | 48×48 | 4 | 倒地消散，末帧化墨 | 同上 |

**小计：4 类 × 4 动作 = 16 张 / 64 帧。**

> **⚠️ 与任务简报的差异**：简报给的是 `idle/attack/death/hit` 4 类×4 动作，本文按文档给的是 `idle/walk/attack/death` 4 类×4 动作。原因：
> 1. `hit` 在敌人侧目前仅 1 帧白闪（`EnemySpawner` 受击无骨骼状态切换），独立出 4 帧太奢侈；
> 2. `walk` 是敌人移动态的必备动作，缺失则玩家看见敌人在飘。
> **同样建议阶段 B 出样品时按本文档走。**

### ③ 地块 `Assets/images/tiles/`

> **16 张 / 16 帧**（4 主题 × 4 种地块 × 1 张平铺）。
> ⚠️ **任务简报的输出路径 `Assets/images/tiles/` 与本文一致**，但简报说"4 主题 × ground/ground2/water/rock"，本文按文档用 `ground/rock/water/path`——`ground2` 在 `WorldBuilder` 里是「随机地面变体」（≈70% 概率 ground、30% 用 ground2），底图相同即可；`path` 是路面变体，也是瓦片层里必备的一项。

`{theme}` ∈ {`forest`, `volcanic`, `frozen`, `town`}（对应 `WorldBuilder.Theme`）。

| 文件名模板 | 尺寸 | 数量 | 说明 | 参考 theme |
|---|---|---|---|---|
| `{theme}_ground.png` | 32×32 | 4 | 可平铺地面，**必须四边无缝** | `forest` 林木土黄 / `volcanic` 焦岩黑红 / `frozen` 雪白蓝灰 / `town` 青瓦灰砖 |
| `{theme}_rock.png` | 32×32 | 4 | 阻挡物 | 同上 |
| `{theme}_water.png` | 32×32 | 4 | 阻挡物（**水墨留白出效果**） | 同上 |
| `{theme}_path.png` | 32×32 | 4 | 路面，**必须四边无缝** | 同上 |

**小计：16 张 / 16 帧。**

### ④ 特效 `Assets/images/vfx/`

> **5 张 / 25 帧**（与 `feature-closure-plan.md` L222–232 对齐）。
> ⚠️ **任务简报的特效名 `slash/arrange/lotus/levelup/damage` 与文档的 `slash/burst/lotus/dodge/hit` 不一致**——文档与 `VfxSkill.NewInstance(...)` 代码硬绑定（详见 `Assets/_Project/Scripts/Runtime/VfxSkill.cs`），所以本文沿用文档名。

| 文件名 | 单帧尺寸 | 帧数 | 说明 | 对应代码 |
|---|---|---|---|---|
| `vfx_slash.png` | 128×128 | 5 | 月牙剑气弧线 | `VfxSlash`（当前是程序化月牙） |
| `vfx_burst.png` | 160×160 | 6 | 圆爆 | `VfxSkill.NewInstance("VfxBurst")` |
| `vfx_lotus.png` | 160×160 | 6 | 血莲 | `VfxSkill.NewInstance("VfxLotus")` |
| `vfx_dodge.png` | 96×96 | 4 | 闪避残影 | `VfxSkill.NewInstance("VfxDodge")` |
| `vfx_hit.png` | 64×64 | 4 | 命中火花（新增） | 攻击命中点用 |

### ⑤ UI / 技能图标 `Assets/images/ui/`

> **15 张**（与 `feature-closure-plan.md` L234–243 对齐）。
> 任务简报让"数量自定"——按文档落地。

| 文件名 | 尺寸 | 数量 | 说明 |
|---|---|---|---|
| `ui_bar_frame.png` | 420×26 | 1 | 血条外框，**尺寸对齐 `Hud.HpBarSize`** |
| `ui_skill_icon_1.png` | 64×64 | 1 | 普攻图标（剑） |
| `ui_skill_icon_2.png` | 64×64 | 1 | 技能 1（斩） |
| `ui_skill_icon_3.png` | 64×64 | 1 | 技能 2（阵） |
| `ui_skill_icon_4.png` | 64×64 | 1 | 闪避图标（闪） |
| `ui_status_icon_1.png` | 32×32 | 1 | Debuff 1（中毒/灼烧等） |
| `ui_status_icon_2.png` | 32×32 | 1 | Debuff 2 |
| `ui_status_icon_3.png` | 32×32 | 1 | Debuff 3 |
| `ui_status_icon_4.png` | 32×32 | 1 | Debuff 4（**先做这 4 个**——文档注明"Debuff 只做了部分"） |
| `ui_panel_bg.png` | 512×512 | 1 | 九宫格面板底（菜单/结算用） |

### 总计

| 类别 | 图集数 | 总帧数 |
|---|---|---|
| 主角 | 8 | 39 |
| 小怪 | 16 | 64 |
| 瓦片 | 16 | 16 |
| 特效 | 5 | 25 |
| UI | 10 | 15 |
| **合计** | **55 张图集** | **159 帧** |

> 较 `feature-closure-plan.md` L254 的 **60 张图集** 少 5 张，是 UI 部分去掉了"预留 9 个 debuff 中的 5 个"（文档原文："可先出 4 个"），落地按 4 个做。如后续 debuff 系统扩展，按相同命名追加即可。

---

## 5. 实现路线

### 阶段 A：风格基准（✅ 已完成，本批任务）

> **目标**：把现有 3 张参考样品调亮，作为后续批量出图的"基准配色 + 基准明度"参照。
>
> **输入**：`Assets/images/samples/` 下 3 张原图。
> **输出**：`Assets/images/samples_brightened/` 下 3 张 `{原名}_brightened.png`。
> **工具**：`tools/brighten_samples.py`（Pillow，无 AI 调用）。
>
> **关键发现**：实测原图都是亮宣纸底（mean 171~197 / median 204~240），**不是暗底**。所以全局 gamma 提亮是错的处方（会把留白拍成死白），正确的做法是 **shadow-weighted lift**：仅抬暗部，纸白几乎不动。
>
> **最终调亮参数**（经参数扫描选优，三张图全部满足"不过曝 / 不压黑 / 墨区加权斜率 ≥ 0.98"）：

| 参数 | 值 | 含义 |
|---|---|---|
| `mode` | `shadow` | 暗部加权提亮 |
| `gamma` | **1.30** | 暗部提亮强度（>1 抬暗部） |
| `shadow_focus` | **2.0** | 提亮量随亮度衰减指数（越大越只管暗部） |
| `contrast` | **1.16** | 对比度系数（端点保护，4v(1−v) 权重，0/1 端点不动） |
| `pivot` | 0.50 | 对比支点（固定中灰，**禁止用中位亮度**，否则会压黑） |
| `knee` | 0.95 | 高光软限幅拐点（渐近压缩，永远到不了 1.0） |

**效果量化**：

| 图 | mean Δ | p05 Δ | 暗部<64 Δ | 纯白占比 Δ | 墨区加权斜率 |
|---|---|---|---|---|---|
| 女侠立绘 | +6.82 | +7.0 | −1.6pp | −0.001pp | 1.011 |
| 妖魔小怪 | +5.08 | +4.0 | −0.8pp | −0.002pp | 1.117 |
| 山水场景 | +3.56 | +7.0 | −0.7pp | −0.002pp | 1.052 |

> 墨区加权斜率 > 1 表示相邻灰阶差被放大，**晕染层次不仅没丢还略微加强**，是这次调亮的关键成功指标。

### 阶段 B：女主角验证样品（下一批任务）

> 1. 按 §4 ① 的尺寸与提示词生成 **idle × 1 张 + attack × 1 张**两张样品（共 10 帧）。
> 2. 同步运行 `brighten_samples.py` 把样品也调亮一次，比对看风格是否与参考一致。
> 3. 由用户目检通过后才进入阶段 C。

### 阶段 C：批量生成剩余帧

> 1. 把 §4 ① ② ③ ④ ⑤ 全部清单按提示词 + 主题色块（kind/theme 表）批量送 ImageGen。
> 2. 每次生成完一类，统一跑 `brighten_samples.py`（仅 ①/②/④ 角色类需要，瓦片和 UI 不需要——它们不需要调亮）。
> 3. 命名前缀校验：每个输出文件按 §4 表格命名（不是 ImageGen 原始长名），命名错位的直接报错重出。

### 阶段 D：SpriteFactory 改造

> **目标**：让运行时优先加载静态贴图，找不到时回退到程序化生成，**永远不能因缺图崩**。
>
> **改动思路**（不展开代码，仅留接口轮廓）：
>
> 1. 在 `SpriteFactory` 新增 **贴图加载分支**：
>    ```text
>    TryLoadStatic(key)  // 走 Resources.Load 或 AssetDatabase 加载预制 PNG
>        成功 → 返回 Sprite（带缓存）
>        失败 → 回退到既有的程序化生成（保持现状）
>    ```
> 2. **缓存 key 必须包含尺寸 + 颜色**——这是上次 `SpriteFactory` 热重载/复用踩过的坑（见风险章节）。**任何"按颜色刷贴图"的接口（`SolidRect`、`Circle`、`Crescent`）的 cache key 都不能只看 `key` 字符串，必须把 `width × height × color` 拼进去**。
> 3. 现有 5 个公开方法（`WhiteTile`、`SolidRect`、`Circle`、`Crescent`、`UiPixel`）的签名保持兼容，新增贴图加载只是内部多一个分支。
> 4. 给 Player / Enemy 挂 `Animator` + `AnimatorController`，状态机：`Idle ↔ Walk → Attack → Hurt → Death`。
> 5. 现有控制器（`PlayerController` / `AttackController` / `DodgeController`）**只增 `animator.SetTrigger/SetFloat` 调用，不改任何判定逻辑**——这是 `feature-closure-plan.md` L159 的硬性约束。

---

## 6. 风险与待确认

### 风险 R1：大幅立绘不能当精灵帧用

`Assets/images/samples/` 3 张 + `Assets/images/characters/heroine/` 5 张，全部 1.2~1.7 MB 的大幅参考图，**不能直接当精灵帧**。原因：
- 尺寸远大于 64px（这些图基本是 1024×1024），导入后世界尺寸会变成 1024 世界单位 ≈ 14 格地块，**角色和地图完全错位**；
- 无透明通道（PNG 不带 alpha），需要重新抠图；
- 未切帧，单张无法表达动画。

**处置**：保留作为风格参考，阶段 B 按 §4 ① 尺寸重新生成。

### 风险 R2：AI 生成需消耗 credits

本批**没有**触发任何 ImageGen。下游任务（阶段 B/C）每张图都会触发 AI 生成，按经验值：

| 类别 | 张数 | 估算 credits | 说明 |
|---|---|---|---|
| 主角（39 帧 / 8 图集） | 8 | ~64 | 每张样品要试 2~3 版 |
| 小怪（64 帧 / 16 图集） | 16 | ~128 | 4 类 × 4 动作要各试 2 版 |
| 瓦片（16 张） | 16 | ~48 | 平铺类废稿率较高 |
| 特效（25 帧 / 5 图集） | 5 | ~30 | 透明底抠图易失败 |
| UI（15 张） | 10 | ~20 | 图标类简单，1~2 版就能过 |
| **小计** | **55** | **~290 credits** | |

> **注意**：以上是粗估，实际可能 ±30%。阶段 B 出 2 张样品后应让用户决定是否接受风格，再批量放行。

### 风险 R3：热重载 / 缓存 key 教训

**问题回顾**（见 `SpriteFactory.cs` L86–113 的 `SolidRect`）：当前 key 只用字符串（`"rect_" + key`），**没有把 width / height / color 拼进去**。这意味着 `SolidRect("player", 24, 24, red)` 和 `SolidRect("player", 48, 64, blue)` 在 PlayMode 热重载时会拿到**同一张 24×24 红色**缓存，第二次调用的玩家体变成了错误尺寸。

**本规划的对策**：
- §5 阶段 D 改造 `SpriteFactory` 时，所有按颜色刷贴图的接口必须把 `w × h × rgba` 拼进 cache key；
- 在文档里把这条作为后续工程的硬约束（已经写在 §5 阶段 D 第 2 点了）；
- 改造完后写一个 NUnit 测试：连续两次不同尺寸同 key 调用，验证返回的 Sprite 尺寸正确。

### 待确认 Q1：敌人 path 在 `characters/enemies/` 还是 `enemies/`？

任务简报给的路径是 `Assets/images/enemies/`，本文按文档用的 `Assets/images/characters/enemies/`（与主角同级）。需要团队确认。

### 待确认 Q2：女主动作命名（attack1/attack2/skill vs attack）

详见 §4 ① 的"⚠️ 与任务简报的差异"。需要产品/设计拍板。

### 待确认 Q3：瓦片 ground2 vs path

`WorldBuilder` 里 ground2 是 ground 的随机变体，不需要单独贴图；path 是路面变体。建议把 ground2 删掉，把 path 留下。详见 §4 ③ 的表前说明。

---

## 7. 附录：本次调亮输出文件绝对路径

```
F:/AI-project/ancientGame/shuimofeng/shuimofeng/Assets/images/samples_brightened/中国传统水墨画风格_仙侠女侠角色立绘_黑白灰为主色调点缀朱砂_2026-08-08T01-49-38_brightened.png
F:/AI-project/ancientGame/shuimofeng/shuimofeng/Assets/images/samples_brightened/中国传统水墨画风格_仙侠妖魔小怪敌人_水墨笔触晕染_黑白灰为_2026-08-08T01-49-39_brightened.png
F:/AI-project/ancientGame/shuimofeng/shuimofeng/Assets/images/samples_brightened/中国传统水墨画风格_仙侠山水地图场景_远山云雾缭绕_水墨笔触_2026-08-08T01-49-39_brightened.png
```

工具脚本绝对路径：`F:/AI-project/ancientGame/shuimofeng/shuimofeng/tools/brighten_samples.py`

---

## 8. 文档变更记录

| 日期 | 版本 | 变更 |
|---|---|---|
| 2026-08-08 | v0.1 | 首版：基于 feature-closure-plan.md §3 展开 + 阶段 A 调亮验证完成 + 待确认 3 项 |