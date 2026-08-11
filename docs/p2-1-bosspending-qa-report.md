# P2-1 BossPending 内核状态机 —— QA 护栏报告

| 项 | 值 |
|---|---|
| 工程根 | `F:/AI-project/ancientGame/shuimofeng/shuimofeng` |
| 被测对象 | `Assets/Scripts/Systems/Combat/Encounter.cs`（BossPending 切片）<br>`Assets/Scripts/Systems/Combat/RunPhase.cs`（RunStateMachine.Evaluate） |
| 执行环境 | 无 Unity、无 dotnet；纯 Python 3.13.12（managed python） |
| 生产代码改动 | **零**。两份 .cs 的 SHA-256 与 mtime 跑前跑后逐字节一致（见 §5） |
| 新增文件 | 2 个 .py + 本报告 |

---

## 1. 为什么需要这套护栏（盲区论证）

第 10 轮首次在内核里引入了新状态机 `BossPending`，用来根治「清完最后一只杂兵当场判胜、
BOSS 战直接蒸发」的零跨越竞态。但现有两道护栏都看不见它：

| 已有护栏 | 规模 | 看守什么 | 为什么看不见 BossPending |
|---|---|---|---|
| `t1_selfcheck.py` | 2199 行 | 数值对拍，围攻/单挑倍率 **2.5294x** 生命线 | 眼里只有伤害公式，不认识「阶段」 |
| `t3_selfcheck.py` | 765 行 | 类型宇宙可解析性，证明没臆造 API | 眼里只有符号表，不认识「语义」 |

`BossPending` 的正确性**全在语义上**：判定口径用哪个计数、销债与裁判归位谁先谁后、
计时器是归零还是暂停。数值护栏与类型护栏一条都覆盖不到 —— 这就是盲区。

C# 侧虽有 `RunPhaseTests.cs` 的 RP15–RP19（L457/497/530/572/616），但**本环境跑不了 NUnit**，
因此需要一套纯 Python 的独立复证。

---

## 2. 交付物

| 文件 | 行数 | 作用 |
|---|---|---|
| `Assets/Scripts/Systems/Combat/Tests/bosspending_selfcheck.py` | 1022 | 主护栏：行为对拍 + 源码静态锚点，**53 checks** |
| `Assets/Scripts/Systems/Combat/Tests/bosspending_guard_mutation_test.py` | 565 | 变异测试：给护栏本身做体检，**18 条变异 + 1 条登记盲区** |
| `docs/p2-1-bosspending-qa-report.md` | 本文件 | 覆盖矩阵、运行输出、诚实边界、待裁决项 |

输出风格与 `t3_selfcheck.py` 对齐（`check()` 打 `[PASS]/[FAIL]`、`section()` 分节、
结尾统计并 `exit(1) on any FAIL`），方便日后统一批跑。

---

## 3. 覆盖矩阵（用例 ↔ 不变量 ↔ 源码锚点）

### 3.1 行为对拍（A/B/C 组，Python 参考模型）

| 用例 | 断言内容 | 不变量 | 对应源码锚点 |
|---|---|---|---|
| BP-A1 | 深度 4 全操作序列穷举（4096 条） | I-1, I-1', I-2, D-1~3 | 全切片 |
| BP-A2 | 随机长序列 4000 条（seed=20250211，长度 8–40） | 同上 | 全切片 |
| BP-A3 | 置债状态穷举 16 组输入 ×120 步，**绝不出 Won** | **I-2** | `RunPhase.cs` L220 ⑤ |
| BP-B1a | 零跨越 3→0 一步收尸不早判 | I-2 | `Encounter.cs` L236 / `RunPhase.cs` L260 |
| BP-B1b | 销债后**下一步**正常判胜（证明不是"永远判不了"） | I-2 反向 | `Encounter.cs` L267 |
| BP-B2a | **无债**同归于尽 → Lost（判别性用例，钉住失败优先） | 失败优先 | `RunPhase.cs` L213 ④ 先于 L220 ⑤ |
| BP-B2b | **有债**同归于尽 → Lost（债不挡判负） | I-2 边界 | `RunPhase.cs` L213 |
| BP-B3 | 幂等闸门：Won/Lost 落定后 1000 局随机操作不回退 | 终局不可逆 | `RunPhase.cs` L190 ① |
| BP-B4a/b | `MarkBossPending` 幂等：不重置已累计计时、债不累加成 2 | I-1' | `Encounter.cs` L249-252 |
| BP-B5 | `ClearBossPending` 返回值 true=真销债 / false=本无债 | — | `Encounter.cs` L275 / L280 |
| BP-B6a/b | Total 有敌人时也累加；Idle 有敌人时恒 0 | 计时语义 | `Encounter.cs` L673 / L679-686 |
| BP-B7a–d | **敌人中途冒出又消失** → Idle 被打断**归零重计**（非暂停） | 计时语义 | `Encounter.cs` L683-686 else 分支 |
| BP-B8 | 未置债时 Tick 完全空转，两计时器恒 0 | D-1 | `Encounter.cs` L667-670 |
| BP-B9a–d | `Clear()` 后 I-4 三字段归零、口径与裁判自洽、第二局不带上局债 | **I-4** | `Encounter.cs` L492 → L497 |
| BP-B10 | 无玩家时不判定**也不布防** | 装配顺序安全 | `RunPhase.cs` L198 ② |
| BP-C1 | **光有债、零真敌人也会布防**（推论已核实成立） | 布防语义 | `RunPhase.cs` L207 ③ 收 PendingAware |
| BP-C2 | 债布防后销债 → 立刻判胜（零真敌人的一局也算赢） | 设计观察 | 见 §6 待裁决第 1 条 |
| BP-C3 | 对照组：无债无敌 → 永不布防、永不判胜 | 空场景不算已清场 | `RunPhase.cs` L220 |
| BP-C4 | 生产序（先首波后置债）布防由真敌人点亮 | 与生产接线一致 | `CombatBridge.cs` L325 → L332 |

**导出不变量**（由源码语义推出，一并纳入穷举守卫）：

- **D-1**：`!BossPending ⟹ 两个计时器恒为 0`（mark/clear 夯零 + tick 早返回三条路径合成）
- **D-2**：`Idle <= Total` 恒成立
- **D-3**：两个计时器非负

### 3.2 源码静态锚点（D 组，解析 .cs 源文本）

| 用例 | 钉住的实现细节 | 源码位置 |
|---|---|---|
| BP-D1 | `PendingAwareEnemyCount` 表达式**恰为** `AliveEnemyCount + (_bossPending ? 1 : 0)` | `Encounter.cs` L236-239 |
| BP-D2a/b/c | `Evaluate(Encounter)` 第三参**必须**是 `enc.PendingAwareEnemyCount`，且真代码里**不得出现** `enc.AliveEnemyCount` | `RunPhase.cs` L260 |
| BP-D3 | `Clear()` 内 `ClearBossPending()`（L492）**先于** `RunState.Reset()`（L497） | `Encounter.cs` L492/L497 |
| BP-D4a–d | `StepFixed` 内 `TickBossPending(dt)`（L643）夹在 `RemoveDead()`（L636）**之后**、`RunState.Evaluate(this)`（L649）**之前**，且只调一次 | `Encounter.cs` L636/643/649 |
| BP-D5a–e | Tick：未置债早返回；Total **无条件**累加且位于 Idle 分支之前；**else 分支是归零不是跳过** | `Encounter.cs` L665-687 |
| BP-D6 | `MarkBossPending` 幂等早返回 `if (_bossPending) { return; }` | `Encounter.cs` L249-252 |
| BP-D7a/b/c | `ClearBossPending` 无债分支 `return false`、真销分支 `return true`、无债分支也夯零两计时器 | `Encounter.cs` L267-281 |
| BP-D8a–d | `Evaluate(bool,bool,int)` 五步判定顺序 ①<②<③<④<⑤；④判负严格早于⑤判胜；布防单向（不得有 `_armed = false`） | `RunPhase.cs` L190/198/207/213/220 |
| BP-D9 | 三条不变量契约注释仍在位（防契约与实现一起漂走） | `Encounter.cs` L184-188 |
| BP-D10 | **全树扫描**：生产代码中无 `Evaluate(..., AliveEnemyCount)` 调用（78 个 .cs，测试目录豁免） | 全 Assets 树 |
| BP-D11 | 两份内核源码零 `UnityEngine` 引用（asmdef `noEngineReferences` 红线） | 两文件 |

> **解析纪律**：所有 D 组检查一律跑在**剥掉注释与字符串字面量**之后的真代码上。
> 原因是硬性的：`RunPhase.cs` L250-259 的注释整段在讨论「这里读的是 PendingAwareEnemyCount，
> 不是 AliveEnemyCount」，`Encounter.cs` L173/L186/L485 的注释里也写满了
> `ClearBossPending()` / `RunState.Reset()`。直接 grep 会被这些**讲解文字**污染 ——
> 顺序检查会读到注释里的行号，口径检查会在注释里"发现"不存在的 Bug。
> 方法体一律用**花括号配平**提取，不用正则 `.*?`（后者只能抓到第一个 `}`，
> 是"顺序检查看起来通过了、其实只扫了半个方法"的经典假阴性）。

---

## 4. 实际运行输出（关键行）

### 4.1 主护栏 `bosspending_selfcheck.py` → **53 passed / 0 failed**

```
  [PASS] BP-A1 穷举深度4 全序列不变量                       4096 条序列，违规 0 次
  [PASS] BP-A2 随机长序列不变量(seed=20250211)            4000 条序列，违规 0 次
  [PASS] BP-A3 I-2 置债穷举 16 组×120步 无 Won           反例 0 个
  [PASS] BP-B1a 零跨越 3→0 不早判                       phase=Playing pendingAware=1
  [PASS] BP-B1b 销债后下一步正常判胜                        clear()=True phase=Won
  [PASS] BP-B2a 失败优先 无债同归于尽 → Lost               phase=Lost（胜利优先的实现在此会给出 Won）
  [PASS] BP-B2b 有债同归于尽 → Lost(债不挡判负)             phase=Lost（不得为 Won 或 Playing）
  [PASS] BP-B3 幂等闸门 终局不可逆(1000 局)                 违规 0 次
  [PASS] BP-B4a Mark 幂等 不重置已累计计时                  重复3次后 total=1.0000(前 1.0000) idle=1.0000(前 1.0000)
  [PASS] BP-B7b Idle 见敌立刻归零(非暂停)                  打断后 idle=0.000000（期望 0，暂停语义会是 1.0000）
  [PASS] BP-B7c Idle 打断后从 0 重计                    第三段 idle=0.5000（期望≈0.5；若为≈1.5 则 else 退化成了跳过）
  [PASS] BP-B9c Clear 后 口径与裁判自洽                   pendingAware=0 phase=Playing armed=False
  [PASS] BP-B9d 第二局不带上局债 可正常判胜                    开局=Playing 清场后=Won
  [PASS] BP-C1 光有债即布防(armed 被债点亮)                 armed=True phase=Playing pendingAware=1
  [PASS] BP-C2 债布防后销债 → 立刻判胜(零真敌人的一局也算赢)          phase=Won ——【设计观察，见报告『待主理人裁决』第 1 条】
  [PASS] BP-D1 PendingAware 表达式 = Alive + (债?1:0)  实得: { get { return AliveEnemyCount + (_bossPending ? 1 : 0); } }
  [PASS] BP-D2b Evaluate(Encounter) 不得出现 enc.AliveEnemyCount  出现=False
  [PASS] BP-D3 Clear(): ClearBossPending 先于 RunState.Reset    行号 销债 L492 < 归位 L497
  [PASS] BP-D4b TickBossPending 在 RemoveDead 之后       L636 < L643
  [PASS] BP-D4c TickBossPending 在 RunState.Evaluate 之前 L643 < L649
  [PASS] BP-D5e Idle 的 else 分支是归零而非跳过             命中 else { _bossPendingIdle = 0.0f; }
  [PASS] BP-D8b 判定顺序 ①<②<③<④<⑤                    行号: L190 < L198 < L207 < L213 < L220
  [PASS] BP-D8c 失败优先于胜利(④ 在 ⑤ 之前)                判负 L213 < 判胜 L220 —— 同归于尽必须判负
  [PASS] BP-D10 生产代码无 Evaluate(..., AliveEnemyCount)  扫描 78 个 .cs，违规 0 处
  [PASS] BP-D11 两份内核源码零 UnityEngine 引用            去注释后未出现 UnityEngine

==============================================================================
  P2-1 BossPending 护栏: 53 passed / 0 failed
==============================================================================
  结果: PASS
```

### 4.2 变异测试 `bosspending_guard_mutation_test.py` → **捕获率 18/18**

```
  [PASS] MUT-000 沙盒基线全绿                            52 passed / 0 failed exit=0

  [PASS] M-S1  PendingAwareEnemyCount 丢掉债项            exit=1 报红 1 条
  [PASS] M-S2  Evaluate(Encounter) 手滑改回 enc.AliveEnemyCount  exit=1 报红 4 条
  [PASS] M-S3  Clear() 两行对调：先让裁判归位、后销债             exit=1 报红 1 条
  [PASS] M-S4  TickBossPending 的 else 由『归零』退化成『跳过』   exit=1 报红 1 条
  [PASS] M-S5  StepFixed 里 TickBossPending 挪到判定之后      exit=1 报红 1 条
  [PASS] M-S6  Evaluate 判定顺序对调：胜利优先于失败              exit=1 报红 2 条
  [PASS] M-S7  MarkBossPending 去掉幂等早返回                exit=1 报红 1 条
  [PASS] M-S8  ClearBossPending 无债分支返回值翻转             exit=1 报红 2 条
  [PASS] M-S9  Evaluate 内混入 _armed = false               exit=1 报红 1 条
  [PASS] M-S10 删掉 I-1 不变量契约注释                       exit=1 报红 1 条
  [PASS] M-S11 内核引入 UnityEngine（asmdef 红线破防）        exit=1 报红 1 条
  [PASS] M-B1  参考模型 pending_aware 丢掉债项               exit=1 报红 7 条
  [PASS] M-B2  模型里 Idle 的 else 退化成『跳过』              exit=1 报红 2 条
  [PASS] M-B3  模型判定顺序对调：胜利优先于失败                   exit=1 报红 1 条
  [PASS] M-B4  模型里 MarkBossPending 失去幂等               exit=1 报红 1 条
  [PASS] M-B5  模型去掉幂等闸门                             exit=1 报红 1 条
  [PASS] M-B6  模型 evaluate_enc 传 alive_enemy_count      exit=1 报红 6 条
  [PASS] M-B7  模型销债时不归零计时器                         exit=1 报红 3 条
  [PASS] M-B8  模型 clear() 两行对调（登记盲区）               预期未捕获，实际 exit=0 报红=无

  [PASS] MUT-900 原文件未被改动 Encounter.cs               sha256=7fdfdd87cdac… size=60810 mtime=一致
  [PASS] MUT-900 原文件未被改动 RunPhase.cs                sha256=b802e00d919b… size=15475 mtime=一致
  [PASS] MUT-901 源码变异捕获率                           11/11
  [PASS] MUT-902 模型变异捕获率                           7/7
  [PASS] MUT-903 盲区用例行为符合登记预期                     1 条已登记盲区（由静态锚点兜底）: M-B8

  结果: PASS —— 每一条注入的错误都被护栏当场抓住。
```

### 4.3 变异测试首轮抓到了主护栏自己的一个真盲点（过程留痕）

第一次跑变异测试时 **M-B3 未被捕获**（`exit=0`，护栏全绿）。排查结论：

> 最初的 `BP-B2` 用例是「玩家死 + 敌人清零 + **有债**」，它验的其实是
> 「I-2 不妨碍判负」，**验不了失败优先**。因为有债时
> `PendingAwareEnemyCount = 0 + 1 = 1`，⑤ 的 `count <= 0` 本就不成立 ——
> **债自己把判胜分支挡死了**。于是把 ④⑤ 顺序对调（改成胜利优先），
> 结果照样是 Lost，用例依旧全绿。

判别性用例必须**无债**：只有在 Won 分支真正可达的前提下，「谁排在前面」才会体现在结果上。
据此把该用例拆成 `BP-B2a`（无债，判别性）/ `BP-B2b`（有债，验 I-2 不挡判负），
重跑后 M-B3 被正确捕获。

这是一次**测试代码的缺陷，不是生产代码的缺陷**，已由 QA 自行修复，未触碰任何 C# 文件。
它也正是变异测试这条铁律的价值所在：区分「没问题」与「没测到」。

### 4.4 前置自检（防第 8 轮的假阴性坑）

第 8 轮踩过的坑：变异探针用了源码里不存在的字符串，`str.replace()` 静默返回原文，
「注入了错误、护栏没报红」被误读成漏检，实为空操作假阴性。本轮的
`apply_mutation()` / `swap_two()` / `prepend_line()` 三个注入原语强制四步自检：

1. 断言目标串在源文本中**确实存在**且出现次数符合预期；
2. 执行替换；
3. 断言替换后内容与原文**真的不同**；
4. 断言变异特征已生效（新 token 出现 / 旧 token 消失）；
   外加落盘后再读回比对一次。

任意一步不满足即 `MutationError` 中止并记 FAIL —— 宁可报错，也绝不让空操作伪装成通过。

此外 `MUT-000 沙盒基线全绿` 是必跑前提：基线若是红的，后续每一条「变异被捕获」都可能
只是沙盒坏了，而不是护栏起了作用。

### 4.5 回归护栏（证明没动到既有资产）

```
$ python Assets/Scripts/Systems/Combat/Tests/t1_selfcheck.py
  汇总: 64 / 64 通过
  频率表: 1源=1.700  2源=3.350  3源=4.300  4源=4.300  8源=4.300
  围攻/单挑倍率: 2.5294x（基线 2.53x）
  端到端(T1-14): 围攻 4.300 次/s  单挑 1.700 次/s  倍率 2.5294x
  结果: PASS                                            [exit=0]

$ python Assets/Scripts/Systems/Combat/Tests/t3_selfcheck.py
  T3-T05 静态护栏: 9 passed / 0 failed
  结果: PASS                                            [exit=0]
```

**t1 64/64，围攻倍率 2.5294x 未漂移；t3 9/9。**
`t3_selfcheck.py` 的 `EXPECTED_COUNT = 33` 未改动 —— 新增的是 .py 文件，
不属于 T3 表面 .cs 自洽清单；全树 rglob 可解析性检查只扫 `*.cs`，故不受影响。

---

## 5. 生产代码零改动证明

| 文件 | SHA-256(前16) | 大小 | mtime |
|---|---|---|---|
| `Encounter.cs` | `7fdfdd87cdac1b05` | 60810 | `1786376414.2769105` |
| `RunPhase.cs` | `b802e00d919b0155` | 15475 | `1786376434.0056083` |

跑前记录、跑完复核，**三项全部逐字节一致**。所有变异均在
`tempfile.TemporaryDirectory()` 建出的 `tmp/Assets/...` 镜像目录内进行，
护栏靠 `BOSSPENDING_ASSETS` 环境变量改道读副本，物理上不存在写回原文件的路径。

---

## 6. 诚实边界 —— 本护栏证明不了什么

> **本护栏证明的是「Python 参考模型自洽」+「C# 源文本锚点与该模型一致」，
> 不等于 C# 编译通过，不等于 NUnit（RP15–RP19）通过。**

逐条展开：

1. **全程没有执行任何一行 C# 代码。** 本环境无 Unity、无 dotnet。D 组做的是**文本解析**，
   不做类型推导、不解析重载、不做控制流分析。一个能骗过所有正则、但 C# 编译器会拒绝的
   写法，本护栏抓不到。
2. **A/B/C 组测的是 Python 参考模型，不是 C# 实现。** 模型与实现之间的桥梁**只有** D 组的
   源文本锚点。锚点覆盖到的语义（口径、顺序、归零 vs 跳过、返回值、判定优先级）是可信的；
   锚点之外的实现细节，模型说了不算。
3. **`AliveEnemyCount` 本身被抽象成了一个整数。** 真实实现是遍历 `Combatants` 累加
   `IsEnemy && IsAlive`（`Encounter.cs` L501-515）。收尸时机、`FlushPendingAdds` 与
   `RemoveDead` 的交互不在本护栏射程内 —— 那是 `CombatKernelTests` 的责任田。
4. **浮点精度不对齐。** 模型用 Python double 累加，C# 侧是 float32。60 步累加到 1.0 秒
   这个量级两者差异远小于本护栏 0.02s 的容差，但**不要**拿本护栏的计时数值去反推
   C# 的逐位结果。
5. **变异测试是抽样，不是穷举。** 18 条变异证明护栏在这 18 个方向上睁着眼；
   清单之外的方向仍可能漏检。
6. **已登记盲区 M-B8**：`Clear()` 内两行顺序在单线程模型下**不可观测**（对调后最终状态
   完全相同），行为组抓不到，目前**仅由 D3 静态锚点守住**。已在变异测试里显式登记为
   `blindspot` 并断言「确实抓不到」，把它钉在明面上而非假装不存在。

---

## 7. 待主理人裁决

**未发现 C# 生产代码的功能性 Bug。** 以下三条是「设计观察 / 契约边界」，
按硬约束一律不自行改动，交裁决。

### 7.1 【设计观察】零真敌人的一局也会判胜 —— 布防可被「债」单独点亮

**事实链**（已由 BP-C1 / BP-C2 钉住）：

1. `Evaluate(Encounter)` 第三参传的是 `PendingAwareEnemyCount`（`RunPhase.cs` L260）；
2. ③ 布防条件 `aliveEnemyCount > 0`（L207）收的就是这同一个数；
3. 故 `AliveEnemyCount == 0 且 BossPending == true` 时，`PendingAware == 1 > 0` →
   **`_armed` 被债单独点亮，场上一只真敌人都没有**；
4. 此后一旦销债，下一固定步 `armed && count <= 0` 成立 → **Won**。

**可达性**：当前生产接线下这条路径**存在但被上层顺序挡住**。
`CombatBridge.Start` 里 `ArmBossPending()` 排在 `SpawnFirstWave()` **之后**
（`CombatBridge.cs` L325 → L332），所以首波必有真敌人先点亮 `_armed`。
源码注释（L328-330）也明确写了这个顺序要求，理由是"语义上说不清"。

**真正会撞上的场景是 R-4 硬超时兜底**（`CombatBridge.cs` L1730-1744）：
BOSS 滞留 12 秒仍未生成 → 强制 `ClearBossPending()` → 玩家在空地图上等了 12 秒之后
**直接弹出胜利结算**，而这一局根本没打到 BOSS。

**请裁决**：这是"宁可这局没 BOSS 也绝不软锁"取舍下的**既定后果**，还是需要产品侧
另作表现（例如硬超时时不判 Won 而是给一个中性收场）？纯属产品口径问题，
内核逻辑本身自洽，QA 不作判断。

### 7.2 【契约边界】I-3 的保证责任完全在 Unity 上层，内核无法自证

I-3（`ClearBossPending()` 必须发生在 `Encounter.Add(boss)` **之后**）是一条
**跨层时序契约**。内核侧没有任何机制能强制它 —— `ClearBossPending()` 是公开方法，
谁都能在任意时刻调。当前正确性依赖 `CombatBridge.TrySpawnBossNow()` 里
「先 `Add` 后 `ClearBossPending`」的写法（`CombatBridge.cs` L1817-1819）。

本护栏对 I-3 的覆盖因此**只到行为模型层**（BP-B1b 验了"销债后才允许判胜"这个后果），
**没有**静态锚点去钉 `CombatBridge.cs` 里那两行的先后 —— 因为那属于 Unity 表现层，
不在本次任务划定的内核范围内。

**请裁决**：是否需要我追加一条静态锚点，把 `TrySpawnBossNow()` 内
`Add(...)` 先于 `ClearBossPending()` 的行序也钉住？（工作量约 20 行，
放进现有 D 组即可；但会让内核护栏伸手到 `_Project/Scripts/Runtime/`，
需要您确认这个边界越界是否可接受。）

### 7.3 【文档订正建议】源码写死的不变量是 **4 条**，不是 3 条

`Encounter.cs` L183-188 的契约块实际列了 **I-1 / I-2 / I-3 / I-4** 四条，
其中 **I-4「`Clear()` ⟹ 三个字段全部归零」** 在本次任务简报中未被提及
（简报只列了三条）。I-4 已由 BP-B9b 与 C# 侧 RP18 双重覆盖，无需改代码，
仅建议后续文档统一按 4 条表述。

---

## 8. 与任务简报不符之处（以源码为准的订正）

任务简报明确说明「我给的是线索不是圣旨，以源码为准」。逐条复核结果如下：

| # | 简报说法 | 源码实况 | 判定 |
|---|---|---|---|
| 1 | `Clear()` 约 L486-495 | 方法体 L460-498；`ClearBossPending()` 在 **L492**，`RunState.Reset()` 在 **L497** | **行号订正**（顺序结论正确） |
| 2 | 源码里写死"三条不变量" | 实为 **四条**，多一条 I-4「Clear() ⟹ 三字段归零」（L188） | **订正**，见 §7.3 |
| 3 | 「`MarkBossPending` 重复调用幂等（不重置已累计的计时器）—— 请读原文确认」 | 原文 L249-252 `if (_bossPending) { return; }` —— **已置位时直接返回，两个计时器一个字节都不碰**。简报的写法正确 | ✅ 简报正确 |
| 4 | 「`_armed` 由 `PendingAwareEnemyCount > 0` 点亮意味着光有债也会布防 —— 请核实推论是否成立」 | **推论成立**，已由 BP-C1 钉住；后果见 §7.1 | ✅ 简报推论正确 |
| 5 | `ClearBossPending()` 返回值语义"自己读原文确认" | `false` = 本来就没欠债；`true` = 真的清掉了一笔。且**未置位分支也会夯零两个计时器**（L271-274，注释说明是为了让"没欠债"状态无论怎么走到都长得一样） | 已确认，由 BP-D7a/b/c 钉住 |
| 6 | ⑥ 是 `RemoveDead()` | ⑥ 实为两句：`FlushPendingAdds()`（L635）+ `RemoveDead()`（L636） | 细节补充，不影响夹心结论 |
| 7 | 字段 L192/196/200、属性 L206/216/226、`PendingAwareEnemyCount` L236、`MarkBossPending` L247、`ClearBossPending` L267、`TickBossPending` L665、`StepFixed` 内 L643、`Evaluate` L187/L239/L260、RP15–RP19 L457/497/530/572/616 | **逐条核对，全部准确** | ✅ |
| 8 | `t3_selfcheck.py` 的 `EXPECTED_COUNT = 33` 与新增 .py 无关、不要改 | 确认：清单只收 `.cs`，D 组全树 rglob 亦只扫 `*.cs`。**未改动** | ✅ |

---

## 9. 结论

| 项 | 结果 |
|---|---|
| 主护栏 `bosspending_selfcheck.py` | **53 / 53 PASS**（exit 0） |
| 变异测试捕获率 | **18 / 18**（源码 11/11 + 模型 7/7），另 1 条登记盲区行为符合预期 |
| t1 回归 | **64 / 64 PASS**，围攻倍率 **2.5294x** 未漂移 |
| t3 回归 | **9 / 9 PASS** |
| C# 生产代码改动 | **零**（SHA-256 / size / mtime 三项跑前跑后一致） |
| 发现的 C# 功能性 Bug | **无** |
| 待裁决项 | 3 条（1 设计观察 + 1 契约边界 + 1 文档订正） |
| QA 自查修复 | 1 条（BP-B2 判别性不足，由变异测试 M-B3 暴露，已拆分修复） |
