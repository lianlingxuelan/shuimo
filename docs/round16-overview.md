# 第 16 轮推进总览 · P1-6 玩家成长曲线

> 执行：automation-1786170865072「仙侠RPG-Unity-自动推进」第 2 轮
> 时间：2026-08-09 03:0x–04:0x　主理人：齐活林
> 工程根：`F:/AI-project/ancientGame/shuimofeng/shuimofeng`

---

## 一句话

给玩家装上了**等级成长线**，并在这个过程中挖出并修掉了一个会让「升级完全失效」的存量隐患。

---

## 为什么选这个题

用户重点清单里的 P0-5（主菜单/ESC）、P0-6（操作引导）、PlayMode 场景名，**在上一轮已全部交付**（changelog 阶段 14）；美术阶段 E 的女主 39 帧也已接入 Unity（阶段 15，磁盘核实 39 张 PNG 在位）。

剩下两类候选：

| 候选 | 判断 |
|---|---|
| 敌人/NPC 精灵批量出图 | ❌ 需消耗 ImageGen 积分。**无人值守时段不擅自烧积分**，留给用户在场拍板 |
| P1-6 成长曲线（纯逻辑） | ✅ 采纳。`feature-closure-plan.md` §4.3 明确建议「优先做本环境能自证的纯逻辑项，不占用用户验证预算」 |

---

## 交付内容

### 数值口径

等级上限 **10**，1 级加成恒为 0。

| 等级 | 1 | 2 | 3 | 5 | 10 |
|---|---|---|---|---|---|
| 血上限 | 260 | 286 | 312 | 364 | 494 |
| raw | 12 | 13 | 14 | 16 | 21 |

`HpMax = 260 + 26×(L-1)`，`AtkBonus = +1×(L-1)`（**加法，不构成伤害乘区**）。升级 `Hp += ΔHpMax`（不回满，缺口恒定）。**局内成长、重开清零**。满级累计经验 790，约 20 杀到 5 级。

### 文件清单

**新增（3）**

| 路径 | 字节 | 说明 |
|---|---|---|
| `Assets/Scripts/Systems/Combat/Progression.cs` | 27336 | 纯逻辑成长内核，零 UnityEngine |
| `Assets/Scripts/Systems/Combat/Tests/ProgressionTests.cs` | 26028 | NUnit 纯逻辑测试 |
| `Assets/_Project/Scripts/Runtime/Tests/P1_6_ProgressionIntegrationTests.cs` | 25712 | 集成/PlayMode 测试 |

**修改（6）**

`CombatBridge.cs`（Q-1 钉死基准 + 成长接线 + 注释推翻）、`DifficultyBridge.cs`（语义注释推翻重写）、`CombatController.cs`（防踩坑注释）、`AttackController.cs`（普攻 raw 注入）、`Hud.cs`（等级/经验显示）、`t3_selfcheck.py`（登记新文件）

**文档（4）**

`unity-p1-6-progression-prd.md`、`unity-p1-6-progression-architecture.md`、`progression-class-diagram.mermaid`、`progression-sequence-diagram.mermaid`

---

## 🚨 本轮最大发现：反调靶子回写 = 玩家白升级

`CombatBridge.ApplyPlayerDamageModel()`（**会反复刷新**）原本写：

```csharp
Encounter.Bridge.PlayerHpMax = p.HpMax > 0.0f ? p.HpMax : PlayerHpMax;  // ❌
```

把玩家**实时**血上限回写进 `DifficultyBridge.PlayerHpMax` —— 而它正是模型 B 的 **d_eff 靶子分子**（`d_eff = PlayerHpMax / 65`）。

**后果链**：升级 → HpMax 涨 → d_eff 分子涨 → 怪伤害同步上调 → **玩家变强的部分被系统原样抵消，白升 9 级**，且 d_eff 从冻结值 4.0 漂移。

**裁定**：该字段语义正式改为「**平衡基准血量**」，永久钉死 260。

> 它是一把**标尺**。标尺不能跟着被测量的人一起变。

因 1 级时两者同值，**对存量 64+88 条断言严格位等价**；改动前 2 级即漂移，改动后任何等级下 d_eff 恒为 4.0。

同时**推翻重写**（非叠加）三处过时注释——原文正主动指引后人改回实时回写。新注释明写「看到这段别再"修复"回去」。

---

## 团队各环节抓到的问题

| 角色 | 发现 | 价值 |
|---|---|---|
| PM 许清楚 | Q-1 反调靶子回写隐患 | ⭐ 本轮最高价值，否则整个 P1-6 等于没做 |
| 架构师 高见远 | `Combatant.Level` 已被**敌人区域等级**占用，复用即语义污染 | 避免一次深层返工 |
| 架构师 高见远 | 玩家有**两条互不引用的 raw 源**，只改技能表 = 升级后普攻毫无变化 | 避免"需求做了一半"的隐性缺陷 |
| QA 严过关 | `34.999999f` 在 float32 下被舍入为**精确 35.0f**（距 35.0 仅 1e-6 < 半个 ULP 3.81e-6），断言必失败 | 修掉一条红灯 |
| QA 严过关 | PI-06 两侧传同一常量 → 相等断言**恒真**，生产代码改坏也不会红 | ⭐ 修掉一条假绿哨兵 |
| 主理人 + QA | 架构「坑 4」前提不成立：`Combatant.Hp` 透传到 `WCore` **裸字段，无钳制** | 文档纠偏，实现无需返工 |

---

## 质量关卡（主理人亲自复跑，未采信 agent 回传）

| 检查 | 结果 |
|---|---|
| `t3_selfcheck.py` | **9/9 PASS**（33 文件；15 纯逻辑零 UnityEngine；类型宇宙 76 .cs 全解析） |
| `t1_selfcheck.py` | **64/64 PASS**，围攻 4.300 / 单挑 1.700 → **倍率 2.5294x 未动** |
| `Bridge.PlayerHpMax =` 赋值点 | 仅 2 处（常量 + 构建期一次性） |
| 注释订正后可执行代码 | `CombatBridge.cs:531/532` 两行原样保留、顺序未变 |
| AC 覆盖率 | 39/39 有归属无真空（AC-39 需 CI 补跑） |

---

## ⚠️ 诚实边界

本环境**无 Unity、无 dotnet** —— 全部改动**未经编译、未跑过任何 NUnit**。

`t3` 的跨文件类型解析是最强静态信号（机器校验"没臆造 API"），但**不等于编译通过**：它不校验重载匹配、可访问性、泛型约束。

---

## 待用户本地验证（5 项）

1. Unity 2022.3 打开工程，确认 **0 error**
2. Test Runner → EditMode 跑 `ProgressionTests`
3. PlayMode 四项：杀怪涨经验 → 升级血条变长 → **升级后普攻伤害变大**（这条专验"两条 raw 源"那个坑）→ 按 R 重开等级归 1
4. 补跑存量 **88 条 NUnit**（AC-39，本环境无法执行）
5. `P1_6_ProgressionIntegrationTests` 里 `GameplaySceneName = "SampleScene"` 是**占位值**，需改成真实场景名，否则相关用例会 `Assert.Ignore` 静默跳过

---

## 下一轮候选

1. 用户本地验证反馈（若有报错 → BugFix 快捷路径，最高优先）
2. **P1-2 受击反馈**（hitstop / 屏震 / 受击闪白 / 伤害飘字）—— 纯表现层，不碰数值，风险低
3. 敌人/NPC 精灵批量 —— **需 ImageGen 积分，等用户在场确认**
4. F-1 多波次永久锁 Won（现状单波次安全）
5. `unity-t3-prd.md §4.4` 文档滞后（写"灵力 20"，实际扣**体力 25**）
