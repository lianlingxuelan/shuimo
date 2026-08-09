# P1-2 受击反馈 —— QA 静态审查报告

| 项 | 值 |
|---|---|
| 审查人 | 严过关（QA） |
| 工程根 | `F:/AI-project/ancientGame/shuimofeng/shuimofeng` |
| 被测范围 | 新建 6 个 + 修改 10 个（三批次） |
| 测试代码 | `Assets/_Project/Scripts/Runtime/Tests/P1_2_HitFeedbackTests.cs`（43 条用例） |
| 轮次 | Round 1 / 上限 2 |

## ⚠️ 交付性质声明

本环境**无 Unity、无 dotnet**，因此：

- **没有跑过任何一条测试**。本报告全部结论来自逐行静态审查。
- 测试文件**未经编译**。若本地编译报错，属预期内的一次性修补，不代表结论失效。
- 本报告**不出现"测试通过"字样**。所有验收项只有三种结论：
  「静态判定通过」「静态判定不通过」「待本地目视」。

---

## 一、核心结论：`FeedbackClock.Frozen` 卡 true 的排查

这是本次审查的第一优先项。结论：**主路径三重保险完整，未发现可在正式游戏中触发的卡死路径；
但存在 1 条纵深防御缺口（P1-01），建议补一行守卫。**

### 1.1 置位点全集（只有 2 处，均在 Director）

| 位置 | 触发条件 |
|---|---|
| `HitFeedbackDirector.cs:554` `KickHitstop` | 一次通过去重与冷却的命中 |
| `HitFeedbackDirector.cs:676` `KickKillEmphasis` | 一次击杀强调 |

### 1.2 复位点全集（6 处，覆盖每一条退出路径）

| 位置 | 覆盖的异常路径 | 判定 |
|---|---|---|
| `FeedbackClock.cs:99-103` `ResetStatics` + `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` | Domain Reload 关闭时 static 跨 PlayMode 存活 | ✅ 且档位正确（最早一档，早于任何 Awake） |
| `HitFeedbackDirector.cs:191-199` `OnEnable → ClearHitstop` | 组件重新启用时继承脏值 | ✅ |
| `HitFeedbackDirector.cs:201-212` `OnDisable → ClearAll` | 禁用后 LateUpdate 停摆 | ✅ 走 ClearAll 而非 ClearHitstop，屏震一并回正 |
| `HitFeedbackDirector.cs:214-219` `OnDestroy → ClearHitstop` | 销毁 / 换场景 | ✅ |
| `HitFeedbackDirector.cs:560-568` `TickHitstop` 兜底 | `_stopRemain` 被别的路径清零但闸门没跟上 | ✅ 状态与闸门强制一致 |
| `CombatBridge.cs:585` `TeardownHitFeedback` 末尾 | 拆线（含按 R 重开） | ✅ |

### 1.3 逐条排查「可能卡 true」的异常路径

| # | 路径 | 结论 |
|---|---|---|
| 1 | Director 被 Destroy 时正在顿帧 | **安全**。Unity 销毁激活对象时必先 `OnDisable` 再 `OnDestroy`，两处都清。 |
| 2 | 场景切换 | **安全**。场景卸载触发 OnDisable/OnDestroy；`CombatBridge.OnDestroy → TeardownHitFeedback` 再补一次。 |
| 3 | `OnDisable` 与 `OnDestroy` 调用顺序 | **无关紧要**。两者都是幂等清零，任意顺序结果一致；且期间不会有事件触发。 |
| 4 | PlayMode 退出 | **安全**。退出即销毁，同路径 1。 |
| 5 | Domain Reload 关闭时 static 跨局存活 | **安全**。`RuntimeInitializeOnLoadMethod` 在**每次**进入 PlayMode 都会执行（与 Domain Reload 开关无关），`SubsystemRegistration` 档位早于任何 Awake。 |
| 6 | 顿帧中被暂停 / 终局 | **安全**。`LateUpdate` 第一件事就是 `ApplyPauseConvergence()`，其内 `ClearHitstop()`（:803）先于一切。 |
| 7 | `_stopRemain` 用错时间源导致死锁 | **安全**。`:581` 是 `Time.deltaTime`，非 `FeedbackClock.Delta`（主理人已核实，复核一致）。 |
| 8 | `_stopKickFrame` 起帧不扣导致永不递减 | **正式运行安全**（`Time.frameCount` 每帧递增）。但**EditMode 下帧号不推进**，是测试陷阱而非产品缺陷 —— 已在测试文件头写明，本套件不测倒计时自然归零。 |
| 9 | **Director 被禁用但事件订阅仍在** | ⚠️ **P1-01，见下**。这是唯一找到的缺口。 |

### 1.4 P1-01 的机理

C# 委托**不认识** `MonoBehaviour.enabled` / `activeInHierarchy`。
退订只发生在 `CombatBridge.OnDestroy → TeardownHitFeedback`（`:568-569`），
所以「Director 被禁用但未销毁」这个窗口里，订阅关系依然有效：

```
内核继续 Tick → CombatEventsUnity.HitFeedback 触发
  → Director.OnHitFeedback（无 isActiveAndEnabled 守卫，:309）
    → KickHitstop → FeedbackClock.Frozen = true   （:554）
      → 但 LateUpdate 已不执行，_stopRemain 无人递减
        → 表现层永久冻结
```

**可达性的诚实评估**：我在全仓未找到任何一处把 Director 或其宿主
（与 `CombatBridge` 同体）设为 disabled / inactive 的代码，因此
**当前版本不可触发**。定级 P1 而非 P0 正是基于这一点 —— 它是纵深防御缺口，
不是现网故障。但「全局 static + 唯一递减者是自己的 LateUpdate」这个结构，
配一行守卫的成本是零，而漏掉的后果是画面死机级别。

---

## 二、PRD 验收项逐条判定

| 项 | 内容 | 结论 | 依据 |
|---|---|---|---|
| A-6 | 连续受击后相机无累计漂移 | ✅ **静态判定通过** | `CameraFollow._center` 为权威中心；`CameraShake` 全程只读 `BaseCenter`（:180）、只写 `transform.position`（:188），从不回写 `_center`。`CameraFollow.LateUpdate` 每帧用 `_center` 重写 transform（:145），抖动不会进入 SmoothDamp 起点。**数学上无累积项**。 |
| A-6 | 屏震回零 | ✅ **静态判定通过** | `OffsetAt` 的 `k = 1 - age/duration`，`age ≥ duration ⇒ k = 0 ⇒ offset == Vector3.zero`（:199-203）。回零是公式结论，不依赖清理代码。 |
| A-6 | 边缘钳制不吃掉回位 | ✅ **静态判定通过** | `ClampPoint` 是纯函数（:157-160）。基准点与抖动点走**同一个**钳制，抖动结束时 `ClampPoint(base+0) ≡ ClampPoint(base)`，与无抖动时逐位相同。世界小于视口时走硬居中（:176-179）分支，同样对两者一致 —— 表现为"贴边不抖"，非漂移。 |
| A-7 | 菜单暂停：hitstop 结束 | ✅ **静态判定通过** | `ApplyPauseConvergence:803` `ClearHitstop()`。 |
| A-7 | 菜单暂停：相机回正 | ✅ **静态判定通过** | `:805-808` `_shake.StopAndRecenter()`。 |
| A-7 | 菜单暂停：飘字**冻结不清除** | ✅ **静态判定通过** | `:812-818` `menuPaused` 分支走 `FreezeAll(true)`；`DamagePopupLayer.FreezeAll` 只写 `_frozen`（:309-312），不碰任何槽位。 |
| A-7 | **恢复后飘字能接着播** | ✅ **静态判定通过** | 关键在 `LateUpdate:238-241` 的无条件 `_popup.FreezeAll(false)`。恢复后 `ApplyPauseConvergence` 返回 false，执行流落到这一句。`Age` 在冻结期间不推进（`dt=0` 早退，:197-201），也从未被重置 —— 语义正确。<br>**执行顺序也对**：Director(120) 早于 DamagePopupLayer(215)，解冻发生在飘字推进之前，不丢帧。 |
| A-8 | 终局：hitstop 结束 + 相机回正 | ✅ **静态判定通过** | 同 A-7 前两条（共用分支）。 |
| A-8 | 终局：飘字**清干净** | ✅ **静态判定通过** | `:820-825` `!menuPaused` 分支走 `_popup.ClearAll()`，12 个槽位全部 `SetActive(false)`（:537-540）。 |
| A-8 | 终局：玩家闪白还原 | ✅ **静态判定通过** | `ClearAll:402-405` `_playerFlash.StopAndRestore()`。 |
| A-9 | 飘字全程不遮技能栏 | ✅ **静态判定通过**（数值） | `DamagePopupLayer:677` 引用 `HudSkillBar.BottomMargin(34) + CellSize(76) = 110`，`PopupHudSafeY = 120 > 110`。飘字诞生后只向上移动（`riseK ≥ 0`），一次钳制即全程有效。<br>⚠️ 边缘情形见 P2-03。 |
| A-10 | 飘字为非负整数、无 NaN | ✅ **静态判定通过** | 三道闸：`Push:232` `dmg<=0` 拒；`:240-243` NaN/Inf 拒；`ApplyText:509-514` 再钳一次 `Max(0, RoundToInt)`。 |
| R-01 | 并发只取最长 / 起始冷却 0.15s | ✅ **静态判定通过** | `PassDedupe`（:504-519）+ `KickHitstop:533` 冷却 + `:550` `Mathf.Max` 取最长（不累加）。 |
| R-02 | 屏震并发取最强 | ✅ **静态判定通过** | `CameraShake.Kick:124-127` 只比幅度，更弱者整个丢弃。 |
| R-05 | 暂停期间不新起反馈 | ✅ **静态判定通过** | `OnHitFeedback:321-324` 与 `OnEnemyDied:372-375` 双入口拦截。 |
| R-07 | 击杀强调 | ✅ **静态判定通过** | `KickKillEmphasis` 刻意绕过 `StopCooldown` 的论证成立（致命伤会先占用冷却），且仍走 `Mathf.Max` 不累加、仍推 `_lastStopAt` 保住后续冷却。 |
| R-08 | 濒死加强 | ✅ **静态判定通过** | `Grade:461` 读的是**结算后**血量，语义正确（把玩家打进濒死的那一下就加强）。闪白拉长（:492）、屏震加幅（:629），不加深不拉长时长 —— 与注释一致。 |
| R-09 | 全局强度可一路调到 0 | ✅ **静态判定通过** | 常规命中经 `ScaleOf` 吃系数；击杀档单独取 `Intensity()`（:661）并在 `k<=0` 时全静默。两条路径都覆盖。 |
| R-03 | 敌白 / 玩家朱砂闪白 | 🟡 **待本地目视** | 配色裁定单点（`Grade:464-486`）静态正确；实际染色效果、峰值观感需目视。 |
| R-04 | 飘字三段动画观感 | 🟡 **待本地目视** | 三段时刻常量关系已由测试守住；"弹入过冲是否自然"只能目视。 |
| R-06 | 连续强度系数手感 | 🟡 **待本地目视** | 数值单调性已由 `Config_ScaleOf_IsClampedAndMonotonic` 守住；手感需实测。 |
| R-10~R-13 | 观感类需求 | 🟡 **待本地目视** | — |
| B-x | 编译通过 / Test Runner 全绿 | 🟡 **待本地执行** | 本环境无 Unity / dotnet，无法编译。 |

---

## 三、问题清单（按 P0/P1/P2 分级）

### P0 —— 无

**未发现 P0 级缺陷。** 时间源、静态复位三重保险、暂停三态收敛、相机漂移、
事件订阅对称性、探针存续，逐项复核均正确。

### P1-01 · Director 禁用后仍会置起全局冻结闸门（死锁纵深缺口）

- **文件:行号**：`HitFeedbackDirector.cs:309`（`OnHitFeedback`）、`:356`（`OnEnemyDied`）
- **现象**：两个事件入口均无 `isActiveAndEnabled` 守卫。C# 委托不认 `enabled`，
  而退订只在 `OnDestroy`。若 Director 被禁用但未销毁，事件仍会打进来并把
  `FeedbackClock.Frozen` 置 true，而此时 `LateUpdate` 已停摆、`_stopRemain`
  无人递减 → **表现层永久冻结**。
- **可达性**：当前版本无调用方禁用它，**不可触发**。属纵深防御缺口。
- **建议修法**：两处入口各加一行（放在 `defender == null` 判空之后、
  `ResolveDependencies()` 之前）：
  ```csharp
  if (!isActiveAndEnabled) { return; }
  ```
- **对应用例**：`Frozen_StaysReleased_WhenDisabledDirectorStillReceivesEvent()`
  —— **当前预期为红**。修复后转绿。
- **路由**：**Engineer 改源码**

### P1-02 · `HitFeedbackConfig.FeedbackIntensity` 是无复位保险的可写 static

- **文件:行号**：`HitFeedbackConfig.cs:64`
- **现象**：`public static float FeedbackIntensity = 1.0f;` 是全仓第二个全局可写
  static，但**没有** `FeedbackClock.ResetStatics` 那样的
  `[RuntimeInitializeOnLoadMethod]` 复位钩子。Domain Reload 关闭时它会跨 PlayMode
  存活；任何测试或调试代码把它改成 0 之后不还原，下一局所有受击反馈会静默全关，
  且**没有任何报错**——排查成本极高。这与 `MainMenuHud.SkipOnNextLoad` 导致
  MENU09 假红是同一类结构。
- **建议修法**：仿 `FeedbackClock.ResetStatics` 加一个
  `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]`
  的复位方法，把 `FeedbackIntensity` 恢复为 `1.0f`（若将来要做玩家设置持久化，
  改为从设置源重新加载）。
- **测试侧已自保**：本套件 `SetUp`/`TearDown` 无条件复位它。
- **路由**：**Engineer 改源码**

### P2-01 · 飘字环形游标会覆盖"正在播且刚合并过"的槽位

- **文件:行号**：`DamagePopupLayer.cs:266-267`
- **现象**：取新槽位时直接 `_slots[_cursor]`，**不检查 `Alive`、也不优先复用空闲槽位**。
  文件头声称"游标转一圈自动覆盖最旧的那条"，但该不变量被合并逻辑打破了：
  `Push` 的合并分支会把 `merged.Age = 0`（:260）让老槽位重新变年轻。
  于是可能出现「11 个槽位空闲、游标却指向那条刚合并过的活跃飘字」，
  把它当场顶掉 —— 玩家看到一个正在上飘的数字突然变成另一个目标的数字（闪烁 / 跳变），
  且已累计的伤害数丢失。
- **严重度**：纯视觉、低频（需要池近满 + 合并交错），不崩溃、不卡死，故 P2。
- **建议修法**：取槽位时先线性扫一遍找 `!Alive` 的空闲槽；全满时再退化为
  "选 `Age` 最大的那条"覆盖。池长 12，多一次线性扫描代价可忽略，
  且与 `FindMergeTarget` 已有的线性扫描风格一致。
- **为什么没有配套用例**：复现需要"槽位年龄分化"，而年龄推进依赖 `LateUpdate`，
  EditMode 不驱动。硬造会写出一条永远不会红的假绿用例，故**不写**，
  仅在此记录，留待 PlayMode 阶段覆盖。
- **路由**：**Engineer 改源码**（低优先，可排入后续批次）

### P2-02 · `Push` 在投影失败前就推进了游标

- **文件:行号**：`DamagePopupLayer.cs:266-277`
- **现象**：`_cursor` 在 `TryPlace` 之前就自增；投影失败时 `return`，
  该槽位未被使用但游标已跳过。相机未就绪的头几帧会空转游标。
- **影响**：无可见后果（被跳过的槽位仍然可用，只是使用顺序变了）。
- **建议修法**：把 `_cursor` 自增挪到 `TryPlace` 成功之后。一行位置调整。
- **路由**：**Engineer 改源码**（可选，洁癖级）

### P2-03 · 小画布下 HUD 避让可能把飘字抬出可视区

- **文件:行号**：`DamagePopupLayer.cs:668` 与 `:682-685`
- **现象**：先算 `maxY`（视口上界，含 `risePx` 预留），再执行
  `if (y <= barTop) y = PopupHudSafeY;`。后一步是**硬赋值**，不再受 `maxY` 约束。
  当画布高度很小（`r.height - pad - risePx < 120`）时，飘字会被抬到可视区之外。
- **影响**：仅在极端小分辨率 / 极端窄画布下出现；设计分辨率 1920×1080 不触发。
- **建议修法**：末尾补一次钳制 `y = Mathf.Min(y, maxY);`。
- **路由**：**Engineer 改源码**（低优先）

### P2-04 · 6 个新建文件缺少 `.meta`

- **文件**：`FeedbackClock.cs`、`HitFeedbackConfig.cs`、`HitFeedbackDirector.cs`、
  `CameraShake.cs`、`DamagePopupLayer.cs`、`PlayerHitFlash.cs`
- **现象**：同目录既有文件均有 `.meta`，这 6 个没有。
- **影响**：Unity 首次导入会自动生成，**不影响本地运行**；但若这些 `.meta`
  未随代码一起提交，团队其他成员 / CI 会拿到不同的 GUID。
- **建议**：本地 Unity 打开一次后，把生成的 `.meta` 一并纳入版本控制。
- **路由**：**NoOne**（工具链自动处理，仅提醒提交）

---

## 四、专项复核：删除 `CombatEventsT3Unity` 那次 `PlayHitFlash` 的影响面

| 检查项 | 结论 |
|---|---|
| 探针 `SkillHitCount++` 是否保留 | ✅ 在 `CombatEventsT3Unity.cs:143` |
| 探针 `LastSkillDamage = dmg` 是否保留 | ✅ 在 `:144` |
| 探针初始化是否保留 | ✅ `:90` 与 `:95` |
| 是否有测试在断言"技能命中会闪白" | ✅ **没有**。全仓 `PlayHitFlash` 调用点仅剩 `CombatEventsUnity.cs:118`（内核 fallback）与 `HitFeedbackDirector.cs:750`（Director 派发），测试目录零命中。 |
| 删除是否留下孤儿 | ✅ 无。原调用处 `:146-151` 留有说明性注释，解释了"它是重复触发源"。 |

**结论：删除安全，无回归面。路由 NoOne。**
已补 `Regression_T3Probes_StillExist()` 与 `Regression_FlashSignatures_StayAligned()`
两条护栏，防止后续批次顺手删掉探针或改动闪白签名。

---

## 五、自查：本套件是否存在"假绿"断言

主理人点名的反模式是「两侧传同一来源的等值断言」。逐条自查结果：

| 风险点 | 处理 |
|---|---|
| `FeedbackClock.Delta == Time.deltaTime`（未冻结时） | **未写**。这是典型同源恒真（`Delta` 的 getter 就是返回 `Time.deltaTime`）。改为只断言冻结时 `Delta == 0f`（与字面量比较）+ 未冻结时 `Delta >= 0`。 |
| `HeavyRatio(true) == HeavyRatioPlayer` | **未写**。函数体就是返回该常量，恒真。改为断言 `HeavyRatio(true) != HeavyRatio(false)`（两套阈值必须真的分开）。 |
| 相机回正断言 | 用**开震前捕获的快照**与震后状态比较，且跨 `CameraShake` / `CameraFollow` 两个责任方，非同源。 |
| `Popup_ClearAll_WhileFrozen_StillAcceptsNewPush` | 初稿的失败消息声称能证明"_frozen 被解开"，但 `Push` 根本不读 `_frozen` —— **已修正**消息与文档注释，明确标注证明力边界，把"解冻后接着播"移交 PlayMode。 |
| 所有配置常量断言 | 均为跨文件 / 跨常量的关系断言（如 `PopupHudSafeY > HudSkillBar.BottomMargin + CellSize`），任一方改动都会变红。 |

---

## 六、测试套件构成（43 条）

| 分组 | 条数 | 覆盖 |
|---|---|---|
| FeedbackClock 闸门契约 | 4 | 读数为 0、非负、复位、`RuntimeInitializeOnLoadMethod` 档位 |
| Director Frozen 生命周期 | 10 | 置位 / ClearAll / 禁用 / 销毁 / 重启用 / **P1-01 缺陷用例** / 零强度 ×2 / 空受击方 / 零 HpMax |
| DamagePopupLayer | 11 | 池容量 + `includeInactive` 陷阱、层级、脏输入、整数化、合并、不误合、**池耗尽不超限**、A-7 冻结不清、A-8 清干净、禁用自清 |
| CameraShake × CameraFollow | 6 | **A-6 权威中心不被回写**、**A-6 20 次回正无漂移**、停止、非法参数、禁用回正、`ClampPoint` 纯函数 |
| HitFeedbackConfig 不变量 | 10 | A-9 避让高度、闪白分档、阵营阈值、玩家第三档、顿帧上限、去重<冷却、飘字三段序、强度双端钳、`ScaleOf` 单调有界、容量 |
| 回归护栏 | 2 | T3 探针存续、闪白签名对齐 |

### 本地运行须知

1. `Window → General → Test Runner → EditMode → Xianxia.Unity.T2.Tests → Run All`
2. **预期 42 绿 / 1 红**。唯一的红是
   `Frozen_StaysReleased_WhenDisabledDirectorStillReceivesEvent`，
   它在指认 P1-01。**请修源码，不要删用例。**
3. 若出现批量红且都指向"数不到组件"，先检查是否有
   `GetComponentsInChildren<T>()` 漏了 `includeInactive: true`
   —— 本工程已在此栽过两次，本套件内已统一处理。

---

## 七、智能路由判定汇总

| 路由 | 条数 | 明细 |
|---|---|---|
| **Engineer 改源码** | **5** | P1-01（死锁守卫）、P1-02（Intensity 复位钩子）、P2-01（游标覆盖活跃槽位）、P2-02（游标提前自增）、P2-03（小画布避让越界） |
| **QA 改测试** | **1** | `Popup_ClearAll_WhileFrozen_StillAcceptsNewPush` 的失败消息过度声称 —— **本轮已自行修复** |
| **NoOne（不是问题）** | **3** | 删除 `PlayHitFlash` 无影响面、A-6 相机漂移不成立、P2-04 缺 `.meta`（工具链自动生成） |

### 建议下一步

1. Engineer 先修 **P1-01 + P1-02**（各一处，合计约 5 行），这两条是结构性防御。
2. P2-01 ~ P2-03 可合并进后续批次，不阻塞本期验收。
3. 修完后在本地跑一次 EditMode 全量，把 43/43 的实跑结果回填本报告，
   再进入「待本地目视」那 5 条 R-x 的人工验收。
