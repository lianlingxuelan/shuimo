# automation-1786170865072「仙侠RPG-Unity-自动推进」执行记忆

每 6 小时按 SOP 推进一个 pending 任务。**只记高层结论与踩过的坑，不放完整交付内容**（完整记录在 `docs/changelog.md` 与 `xianxia-rpg/.../memory/YYYY-MM-DD.md`）。

## 环境约束（每轮都适用，别再踩）

- 唯一工程根：`F:/AI-project/ancientGame/shuimofeng/shuimofeng`。**严禁碰 `F:/AI-project/xianxia-rpg/`**（Godot/Web 原型仓，前任 QA 跑错仓库误报过"代码不存在"）。
- 工程有**两套脚本根**：`Assets/Scripts/`（纯逻辑内核）与 `Assets/_Project/Scripts/Runtime/`（Unity 表现层）。搜不到先换根再下结论。
- 自检脚本在 `Assets/Scripts/Systems/Combat/Tests/`（**不在** `tools/`）：`t1_selfcheck.py` / `t3_selfcheck.py`，用 managed python 直接跑。
- 本环境**无 Unity、无 dotnet**，永远不能编译/跑 Unity 测试。交付回报必须写明"待用户本地验证"。
- **本会话无 TeamCreate / SendMessage 工具**，团队协作退化为直接派 Agent（`name` 与 `subagent_type` 同名）+ 主理人亲自兜底质量关卡。
- 派工铁律：**绝不轻信 agent 回报的 completed，必须自己 grep/ls 上盘核实**。子 agent 常撞 max turns 丢结论。

## 轮次记录

### 2026-08-08 20:30 · 第 1 轮

- **背景**：TaskList 为空（跨会话不持久），从 `docs/changelog.md` + 外部 memory 恢复上下文。发现美术阶段 D 后台任务**正在写盘**（`Assets/images/` 时间戳实时变动）→ 主动避开美术，改推代码侧被暂缓的 P0-5 / P0-6。本轮结束时阶段 D 已自行完成（39 帧）。
- **交付**：P0-5（主菜单/ESC 暂停/退出）+ P0-6（操作引导）+ PlayMode 场景名 BugFix。新增 3 个 HUD + 1 个测试文件（27 用例），改 6 个文件。详见 changelog 阶段 14。
- **关键架构决策**：引入**统一暂停闸门** `IsGameplayBlocked = IsRunOver || _menuPaused`，`Scheduler.Paused` 收敛为全仓唯一写入点。后续任何新增暂停来源都必须走 `SetMenuPaused()`，**不要再直接写 Scheduler.Paused**。
- **主菜单不建场景**：靠 `MainMenuHud.SkipOnNextLoad`（static 跨 LoadScene）决定重开是否跳过主菜单。改动重开逻辑时注意这个旗标是**一次性**的（Start 读完即复位）。
- **QA 抓到的真 bug（有价值）**：
  1. 引导文案写「右键→技能三」，但工程**根本没有 Skill3**，右键实际绑 Skill1（与 K 同动作）。→ 写任何 UI 文案前先查 `Input/InputBindingProfile.cs` 实际绑定。
  2. `GetComponentInChildren<T>()` **默认跳过未激活对象**；本工程 HUD 的 `Build()` 末尾都会 `SetActive(false)`，所以测试里取组件**必须带 `includeInactive: true`**。这是本工程 HUD 测试的通用坑。
- **主理人自查发现**：新 UI 元素与 `HudSkillBar`（`BottomMargin=34` + `CellSize=76`，占 y∈[34,110]）位置冲突。**加底部 UI 前先算这个区间**。
- **护栏**：t3 9/9、t1 64/64（倍率 2.5294x 指纹未动）；T3 类型宇宙 66→70 个 .cs 全部可解析（机器校验"没臆造 API"，但**不等于编译通过**）。
- **遗留给用户**：本地 Unity 验证清单（主菜单/ESC/引导/R 重开/Test Runner 跑 EditMode + 5 条 PlayMode）。

### 2026-08-08 23:1x · 第 1.5 轮（用户在场，非本自动化触发）

- 美术阶段 E 落地（女主 39 帧接进 Unity），记录见 changelog 阶段 15。**上一轮忘了回写本文件**，导致第 2 轮开局误判进度 —— 教训：每轮结束必须回写本文件，否则下一轮要靠 changelog 反推。

### 2026-08-09 03:0x · 第 2 轮

- **背景**：TaskList 仍为空（跨会话不持久，已成常态，别再指望它）。从 changelog + 本文件恢复上下文，`ls`/`grep` 核实 P0 六项与美术阶段 E 均已落盘。用户重点清单里 P0-5/P0-6/PlayMode 场景名**都是上轮已交付项**。
- **选题**：P1-6 玩家成长曲线（纯逻辑，本环境可自证）。**主动回避美术**：敌人精灵批量要烧 ImageGen 积分，无人值守时段不擅自消耗，留给用户在场拍板。这条以后照办。
- **交付**：`Progression.cs` + 2 测试 + 6 处接线 + 4 份文档。详见 changelog 阶段 16。
- **★ 最重要的架构裁定（Q-1）**：`DifficultyBridge.PlayerHpMax` 语义改为「**平衡基准血量**，永久钉死 260」。原先 `CombatBridge.ApplyPlayerDamageModel()` 把玩家**实时** HpMax 回写进这个 d_eff 靶子分子，导致升级后怪同步变强、**玩家白升级** + d_eff 从 4.0 漂移。全仓赋值点现只剩 2 处。**后续任何人想改回实时回写都是错的**，注释已推翻重写并留警示。
- **踩坑清单（复用价值高）**：
  1. `Combatant.Level` 是**敌人区域等级**（`DifficultyBridge.cs:328/407` 写），玩家等级不可复用。
  2. 玩家**两条互不引用的 raw 源**：`AttackController.cs:35 AttackRaw`（普攻，`WorldBuilder.cs:564` 无条件挂载）与 `SkillConfig.cs:190 BASIC_RAW`（技能表）。只改一条 = 升级后普攻毫无变化。
  3. `t3_selfcheck.py` 的 `T3_PURE`(L76-89) + `EXPECTED_COUNT`(L120) 是**写死清单**，新增纯逻辑文件必须登记否则护栏红。
  4. `Combatant.Hp`/`HpMax` 透传到 `WCore.cs:65/68` **裸字段、无钳制**（架构文档曾误称有钳制，已订正）。
  5. 浮点边界测试按 **ULP** 挑值：`34.999999f` 在 float32 下会被舍入成精确 `35.0f`。
  6. **假绿高发区**：两侧传同一来源的等值断言恒真 —— QA 这轮就抓到一条。
- **护栏**：t3 9/9、t1 64/64、倍率 2.5294x 未动（主理人亲自复跑 2 次）。
- **遗留给用户**：本地 Unity 编译 + `ProgressionTests` + PlayMode（重点验「升级后普攻伤害变大」，那是坑 2 的靶子）+ 补跑存量 88 条 NUnit + 把 `GameplaySceneName="SampleScene"` 占位改成真实场景名。

### 2026-08-09 03:0x · 第 2.5 轮（自动化触发，用户不在场）

- **背景**：用户在场反馈 Test Runner 192 通过 / 7 失败（P2_01~03 + P4_01~02 + MENU09 + PI11），授权"随便改不请求"。本环境无 Unity，走 BugFix 路径复修测试侧 + 清占位符。
- **交付**：3 测试文件复修，**零生产代码改动**。`P0_2_P0_4_PlayModeTests.cs`（+UnityTearDown 复位 `SkipOnNextLoad` + `WaitForKernelSteps`/`WaitForRunOver` 轮询锚 `scheduler.TotalSteps`）、`P1_6_ProgressionIntegrationTests.cs`（+[TearDown] 同步复位 + `FindBridge` 三级守卫 + PI11 诊断消息）、`P0_5_MenuHudTests.cs`（MENU09 改探针语义，主理人亲改防假绿）。`TODO(用户)` 三文件全清。
- **根因**：MENU09 = 测试隔离污染（static `SkipOnNextLoad` 未复位，非生产 bug）；P2/P4 = QA 抓 2 真 bug（帧等待 ≠ 内核步进 / `WaitForEnemies` 无法武装 scheduler）；PI11 = 待本地 Console 定位。
- **护栏**：t3 9/9、t1 64/64、倍率 2.5294x 未动（主理人复跑）。
- **重要修正**：SampleScene **已是真实战斗场景**（GameObject 1602164645 同时挂 CombatBridge + CombatController，zoneId=zone_youhuang，playerHpMax=260）。`GameplaySceneName="SampleScene"` 本就正确，**不用再改**；本轮只清掉了误导性的 `TODO(用户)` 注释。第 2 轮「遗留」里那条"改场景名占位"已结案。
- **遗留给用户**：本地 Unity 重跑 Test Runner；PI11 仍红时把完整 Console 报错发回精修根因。

### 2026-08-09 10:0x · 第 3 轮（自动化触发，用户不在场）

- **选题**：用户重点清单里 P0-5/P0-6/PlayMode 场景名/按 R 重开均前几轮已结案；美术样品需 ImageGen 积分（无人值守不擅自烧）。P1-2 受击反馈是"打得爽不爽"分水岭，且**纯 Unity 表现层、不碰数值**，风险最低 → 选它。
- **交付**：标准 SOP 全栈落地——7 新增（FeedbackClock / HitFeedbackConfig / HitFeedbackDirector / CameraShake / DamagePopupLayer / PlayerHitFlash / P1_2_HitFeedbackTests[**43 用例**]）+ 9 修改（CombatEventsUnity / CombatView / CombatEventsT3Unity / CameraFollow / CombatBridge / WorldBuilder / VfxSlash / VfxSkill / HeroineAnimator）。4 份文档（PRD/架构/类图/时序图/QA 报告）。详见 changelog 阶段 18。
- **★ 最高裁定（A-1）**：顿帧**禁用 Time.timeScale**——会连确定性内核一起冻、破坏 2.5294x 指纹。改三层时钟分离：新增 `FeedbackClock`（`static bool Frozen` + `Delta => Frozen?0:Time.deltaTime`），顿帧只钉渲染位置、内核照吃真实时间；所有表现层计时改吃 `FeedbackClock.Delta`。
- **QA 真缺陷（Engineer 修）**：P1-01（`HitFeedbackDirector.OnHitFeedback/OnEnemyDied` 禁用态仍改 `FeedbackClock.Frozen` → `:336`/`:397` 加 `isActiveAndEnabled` 守卫）；P1-02（`HitFeedbackConfig` 的 static 无复位钩子 → 加 `ResetStatics()` + `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`）。QA 故意留 1 条指认 P1-01 的用例，工程师修源码后**未篡改测试文件**。
- **护栏（主理人亲自复跑，独立确认）**：t3 9/9 PASS（83 .cs → 类型 146/命名空间 17，全可解析）；t1 64/64 PASS，倍率 **2.5294x 未动**。4 地雷 grep 复核全过（唯一暂停写入点 :1425、飘字引用 HudSkillBar 常量 :776、接线 +=/-= 对称 :521/522 接 :549/550 拆、`_stopRemain -= Time.deltaTime` :620 真实时间无死锁）。
- **★ 重要订正（t3 护栏机制）**：第 2 轮踩坑清单第 3 条"T3_PURE+EXPECTED_COUNT 是写死清单、新增纯逻辑文件必须登记否则护栏红"**有误**。实测 `T3_ALL=T3_PURE+T3_UNITY+T3_BRIDGE` 是写死 33 文件**自洽清单**（`len(T3_ALL)==EXPECTED_COUNT` 恒真，与新增无关）；真正纳入新文件的是 **L700 `rglob("*.cs")` 全树可解析性检查**。故新增文件**无需登记**；若强行登记务必同步 +1 EXPECTED_COUNT，否则 A1 红。第 2 轮给 Progression.cs 登记的改动属多余（无害）。
- **遗留给用户**：本地 Unity 编译 + Test Runner 跑 `P1_2_HitFeedbackTests`（43 条，P1-01 修复后应全绿）+ 目视验顿帧/屏震/分阵营闪白/飘字；`HitFeedbackConfig` 强度系数（如 `HeavyRatioEnemy=0.45f`）为起调值待实测普攻伤害后校准；`FeedbackIntensity` 默认 1.0 可调。

### 2026-08-09 22:0x · 第 4 轮（用户问询触发，非纯自动化）

- **触发**：用户实测 Test Runner **193/6**（MENU09 已绿，P2_01~03 + P4_01~02 仍红）；PI11 Console 报错明确为 EditMode 下调 `SceneManager.LoadScene` 抛 `InvalidOperationException`（须 `EditorSceneManager.OpenScene`）；用户授权「先把操作屏补上，最起码能看到伤害」。
- **★ 关键事实澄清（避免重复造轮子）**：飘字/受击反馈系统（`DamagePopupLayer`/`HitFeedbackDirector`/`HitFeedbackConfig`/`PlayerHitFlash` + `CombatBridge.SetupHitFeedback`）**已在第 3 轮 P1-2 标准 SOP 全栈落地**（详见 changelog 阶段 18 + `docs/unity-p1-2-hitfeedback-*`/`docs/p1-2-qa-report.md`）。本轮**不是重新实现**，而是确认可验收 + 根治 PI11。子 agent 派工时若一头扎进"从零写飘字"会浪费一轮——先 Grep 既有实现。
- **工作流**：⚡ 快速模式（团队 `software-combat-hud-damage-numbers-f1a6`）。首派工程师**网络 502 失败**（代理/防火墙截断 HTTPS），但崩溃前已将 PI11 修成完整 `Application.isPlaying` 分支；二次重派工程师复核确认飘字系统 + PI11 均就绪，纯验证 **IS_PASS: YES（0 文件改动）**。
- **PI11 根治（本轮唯一实质新增）**：`P1_6_ProgressionIntegrationTests.cs` 的 `LoadGameplayScene` 按 `Application.isPlaying` 分支——PlayMode→`SceneManager.LoadScene`(L459)；EditMode→`#if UNITY_EDITOR`→`EditorSceneManager.OpenScene`(L482) + 手动驱动 `Awake→ApplyPlayerDamageModel→SetupT3→SetupProgression` 最小生命周期链；`#else`→`LoadScene`(L499)。原 `260` 三处断言 + `Player.HpMax==494` + `Level==10` 全保留。
- **QA 严过关 NoOne（通过）**：t3 **9/9**、t1 **64/64**（围攻倍率 **2.5294x** 未动）；跨文件符号静态断链独立核实 **126 处运行时 + 132 处测试零断链**，程序集引用 T2→Unity 单向合法；逻辑抽查（环形池恒返回合法槽位、单一分档、对称接线、守卫顺序）全过。新增 `tools/qa_p12_linkcheck.py` / `tools/qa_p12_balance.py` 补 t3 D2/B 覆盖不到的 T2 树盲区。
- **★ 本轮踩坑（重要，下轮别再犯）**：写 changelog 时差点插入**第二个「阶段 18」**——因为第 3 轮 memory 记「详见 changelog 阶段 18」让我误以为该章节还没写，实际它早已在 changelog（L630 `## 阶段 18`）。**写入 changelog/文档前务必先 Grep 现有阶段编号与章节**，避免重复阶段号、重复叙述。已修正为 `### 阶段 19`（聚焦 PI11 根治 + 既有系统复验，引用阶段 18）。
- **遗留**：① 存量 `P0_2_P0_4_PlayModeTests.cs` L73/L377 仍是裸 `SceneManager.LoadScene` 无 `isPlaying` 守卫，与 PI11 bug 同类——建议独立跟进，不塞本批次；② 无 Unity 环境，真机行为（SendMessage Awake 拉起 EventsUnity、视觉手感）只能本地验证。
- **护栏（主理人复跑）**：t3 **9/9**、t1 **64/64**，倍率 **2.5294x** 未动。

### 2026-08-09 深夜 · 第 5 轮（用户问询触发，非纯自动化）

- **触发**：用户实测 Test Runner **222 passed / 20 failed**。PI11 已绿（前轮 EditMode LoadScene 修复生效）；但 `P1_2_HitFeedbackTests` 大量 `Popup_*` 红，核心 `Popup_SameTargetWithinWindow_MergesIntoOneSlot` 报 `Expected:1 But:0`。
- **工作流**：🔧 BugFix 快捷路径（团队 `software-bugfix-hitfeedback-merge`）。
- **根因**：`DamagePopupLayer.TryPlace` 投影不可用（`Camera.main==null`/`_canvasRect==null`/`TryProject` 失败）时静默 `return false` → 整条 `Push` 丢弃 → 两次命中都没建槽 → 计数恒 0。仅 EditMode 测试暴露，真实战斗投影走通不触发。
- **修复（1 文件、最小改动）**：`TryPlace`(L710-719) 改为投影失败→降级画布中心落点 + `return true`。"命中必有回应"优先于精确位置；真实手感零变化。
- **★ QA 关键澄清**：任务书把 P1-01（`Frozen_StaysReleased_WhenDisabledDirectorStillReceivesEvent`）列为"预期红"，但磁盘 `HitFeedbackDirector.cs:336/397` 的 `isActiveAndEnabled` 守卫**第 3 轮已落地**，该用例现在**应转绿**，类型 B 空集。→ `P1_2_HitFeedbackTests.cs:9-12` 文件头"有 1 条预期失败"注释**已过期**（R4，建议下轮顺手清）。
- **QA 严过关 · 路由 NoOne（通过）**：t3 **9/9**、t1 **64/64 @ 2.5294x**；静态断链零（含 CS0165 专项论证）；**逻辑对拍模型 `tools/qa_popup_fallback_model.py` 34/34 PASS**（证明投影不可用⇒降级⇒断言满足，且真机路径逐位一致）。失败分类：类型 A（依赖 Push，约10条）预计转绿、类型 C（只 Build/早退）本绿。
- **遗留（非阻断）**：① R3 真机进场首帧若 `Camera.main` 未装配，新行为在画布正中弹未挂靠敌人的飘字（UX 取舍，本地留意）；② R4 过期文件头注释；③ 存量 `P0_2_P0_4_PlayModeTests.cs` L73/L377 仍裸 `SceneManager.LoadScene` 无 isPlaying 守卫（PI11 同类），仍建议独立跟进。
- **诚实边界**：无 Unity/dotnet，未编译未跑 NUnit。

### 2026-08-09 傍晚 · 第 6 轮（用户在场授权"1、2、3 全做"，非纯自动化）

- **触发**：用户授权把 1=清 P0_2_P0_4 测试红、2=美术特效、3=像素动画"全部搞了"，自主排优先级不反复请求。中途 QA 抓 4 缺陷的 Round 2 工程师撞 **429 频率限制**（reset 23:39），但**修复已在断连前写盘**——教训：429 只杀回执不杀已落盘改动，先 ls/grep 实盘再决定重派，别盲目重派。
- **交付（详见 changelog 阶段 21）**：
  ① P0_2_P0_4 5 条 PlayMode 红根治：双根因（asmdef 锁 EditMode 首句 LoadScene 必红 + P0-6 引导冻结调度器）+ **asmdef 迁移**到 `Tests/PlayMode/`（新 `Xianxia.Unity.T2.PlayModeTests.asmdef`）→ EditMode Ignore → PlayMode 真执行。
  ② 美术特效框架 Round 2：4 缺陷（CS1513 缺 `}` / CS0266 int-float 契约 / 冻结不对称）全部修复，QA Round 2 路由 **NoOne 放行**，护栏双绿。
  ③ 像素动画：**代码层无解、资源主因**——walk 帧站桩换皮（一拐一拐）、walk_down 第4帧下沉（站起来趴下）、attack 只画剑缺身体（攻击隐身）、dodge 帧漂移。交付精确美术补图清单（主理人逐张视觉核验坐实）。
- **★ 本轮踩坑（复用价值高）**：① 建 TeamCreate 后**任务列表作用域被重置**（旧全局任务不可见，需在团队作用域重建跟踪任务）；② 工程师"IS_PASS: NO"不一定是坏结果——排查型任务里"代码无解 + 精确资源清单"是正确交付，主理人要会分诊；③ 像素帧问题**必须逐张看 PNG 才能定性**（我 Read 了 walk_side_1/_6、walk_down_4、attack_1 四张，视觉证据直接坐实工程师判断）。
- **护栏（主理人复跑）**：t3 **9/9**、t1 **64/64 @ 2.5294x** 未动。
- **遗留给用户**：本地 Unity 编译 + Test Runner（P0_2_P0_4 PlayMode 真绿）；粒子 prefab 挂载目视；动画补图（P1：attack 6 + walk_side/up 各 6；P2：walk_down 6；P3：dodge 4）。

### 2026-08-09 19:4x · 第 7 轮（用户在场讨论后转入，非纯自动化）

- **触发**：用户讨论《斩妖行》→ 2.5D 方向（3D 场景 + Spine 骨骼角色 + 八方向 + 重特效打击感），授权评估可行性。
- **背景发现**：TaskList 仅含上轮遗留的"评估 2.5D"任务（已自动 completed）；P0 六项 + P1-6 + P1-2 **前 6 轮已全部交付**，用户"重点关注"清单（P0-5/P0-6/PlayMode 场景名/R 重开）均为已交付项——差距是用户感知滞后于实际进度，非真缺口。
- **交付**：
  ① 文档修复：`docs/unity-t3-prd.md` 闪避资源描述 3 处修复（灵力→体力，20→25），附录 B 不一致记录同步结案。
  ② 2.5D 可行性评估：`docs/unity-2.5d-feasibility.md`（完整报告：架构复用度分析/切换成本/三步路线图/5 项待拍板决策）。
- **核心结论**：内核 100% 可复用（纯 C# 33 文件一字不改），表现层需重写（世界/相机/动画/特效）。推荐 2D 先收尾→3 轮技术验证→正式开发，**不推荐现在切**。
- **★ 本轮踩坑**：用户认为 P0-5/P0-6 还没做（feature-closure-plan 仍标"无"），但 6 轮 memory 详细记录了交付。feature-closure-plan.md 是 v1.0（08-08 首次落盘后未更新三态标注），内存已偏移。后续每完成大模块需回写 closure plan 的三态表，否则用户/下轮 automation 会基于过期数据判断。
- **护栏（主理人复跑）**：本轮零代码改动，t3 9/9、t1 64/64 @ 2.5294x 未动。
- **遗留给用户**：① 本地 Unity 验证收尾；② 2.5D 路线 5 项决策。

### 2026-08-10 08:20 · 第 8 轮

- **选题**：三态表复核后确认 **P1-3 音效**是 P1 中唯一一行代码都没写的项（`grep AudioSource` 全仓零命中，两个 `PlaySfx` 委托定义了却零订阅，事件一直在空发）。P0 六项/P1-2/P1-6 已交付；美术需 ImageGen 积分（无人值守不擅自烧）。
- **工作流**：标准 SOP 全跑通（PM→架构→工程→QA），工程师**分两批派工**（批1 = DSP 3028 行，批2 = 装配+接线+测试 5047 行）规避子 agent 撞轮次上限。
- **交付**：6 新增 + 1 修改 + 1 测试 = 8075 行；4 份文档（PRD 573 / 架构 1129 / QA 报告 / 2 张 mermaid）。12 音效 + 1 环境衬底**全部程序化合成，零二进制资源**。详见 changelog 阶段 23。
- **★ 关键裁定（后续别推翻）**：
  - **音频吃 `Time.deltaTime`，永不吃 `FeedbackClock.Delta`**。顿帧期 `FeedbackClock.Delta ≡ 0` → 节流窗口不推进 → 连击第二击被误判「同 key 重复」而静音丢弃。顿帧恰恰每次命中都发生，等于连击必哑。这条与「顿帧禁用 Time.timeScale」是**两个不同层面**，别混。
  - 合成用固定种子私有 `System.Random`（`AudioConfig.MakeSynthRandom(key)`），**绝不碰内核 `SkillRng`/`PCG32`**，否则污染 2.5294x 指纹。
  - 技能 key 一律引 `SkillConfig.SKILL_*` 常量，**禁止写字面量**。
- **★ 本轮踩坑 1**：主理人简报把技能 id 写成短名 `basic` / `circle_burst`，实际是 `skill_basic_slash` / `skill_circle_burst` / `skill_blood_lotus` / `dodge_roll`。PM 抓出、grep 复核确认。**照简报实现的话 4 个技能里 3 个音效会静默哑掉且不报错**。教训：给下游的简报里任何字符串常量都必须先 grep 核对，别凭记忆写。
- **★ 本轮踩坑 2**：工程师批1 派工返回 `499 canceled`。按既有教训先 `ls` 核实——3 个文件已落盘（40KB/38KB/65KB）。**网络错误只杀回执不杀落盘改动，永远先核实再决定是否重派**。本轮据此省掉一次重复派工。
- **★ 本轮踩坑 3（护栏方法论）**：给 `audio_syntax_check.py` 加了跨文件双向 key 差集后，顺手写变异测试验证护栏本身有效——**首版 4 个变异里 2 个"漏检"，查下来是探针用了不存在的 key 名导致 `replace` 退化成空操作（假阴性），不是护栏漏**。已加 assert 前置自检。结论沉淀：**任何静态护栏落地后必须做变异测试，否则无法区分"没问题"和"没测到"**。
- **护栏加固**：`audio_syntax_check.py` 升级为解析三份**源文件**做双向差集（不依赖副本，测得出内核侧字面量漂移）；新增 `audio_guard_mutation_test.py`，4/4 变异全捕获。改动过护栏脚本后务必复跑变异测试。
- **P2 补丁（主理人直接补）**：`AudioClipFactory.cs:85` 加 `RuntimeInitializeOnLoadMethod` 复位钩子；`AudioDirector.cs:358` 加维护约束注释（将来加 UI 音效须把守卫1 下移并加 `Bus != Ui` 例外，否则暂停菜单点击音被吞）。
- **护栏（主理人复跑）**：t1 **64/64**、t3 **9/9**，**2.5294x 未漂移**；`git status --short Assets/Scripts/` 为空，内核零改动。
- **遗留给用户**：① 首次 Unity 打开后**务必提交自动生成的 7 个 `.meta`**（否则换机 GUID 错乱）；② 跑 `P1_3_AudioTests` 53 条；③ 戴耳机实听。

## 下一轮候选（按优先级）

1. **用户本地验证反馈优先**：若报编译错/行为异常 → BugFix。这已是连续第 3 轮的首选项，用户一直没回验证结果。
2. Task #5：补 P1-3 暂停收敛（A-17/A-18）+ 节流窗口测试。**建议等用户确认现有 53 条真绿再动**，避免把编译错误叠进 1487 行测试文件。
3. 敌人/NPC 精灵批量（需 ImageGen 积分，等用户在场）。
4. P1 遗留：P1-1 手感调参（必须人手试，本环境做不了）、P1-5 双区域 + 安全区、F-1 多波次永久锁 Won。
5. 若用户拍板 2.5D 决策 → 进入技术验证阶段。

**⚠️ 库存积压警告**：P0 全交付 + P1 已交付 4/7，但**全部标注"待用户本地验证"且从未被验证过**。继续无脑堆代码的边际收益在快速下降——下一轮若仍无用户反馈，优先做「不需要 Unity 就能自证」的事（护栏、文档、纯逻辑），而不是再加一个待验证的 Unity 表现层模块。

### 2026-08-10 22:4x · 第 9 轮（用户对话触发，非纯自动化）

- **触发**：用户问"启动2.5后还能回2.0吗""不知道怎么做""喜欢斩妖行打击感"。用架构隔离图+分支图说明：内核100%复用、回退是零成本 `git checkout main`；打击感=反馈系统（顿帧/屏震/闪白/飘字/音效/特效），与2D/2.5D无关，2.5D 的 3D 粒子反而增强打击层次，是加分项。
- **用户拍板**（AskUserQuestion q-0）：选「竹林2.5D技术验证」路径。
- **交付**：
  - 创建并推送 `feature/2.5d` 分支（main 保留 2D 可玩版不动）。
  - `docs/2.5d-tech-verify-plan.md`：3 验证子项（竹林3D场景/Spine角色/砍竹子特效）+ 验收标准 + 资源清单 + 代码骨架设计 + 里程碑 + 风险回退。
  - `Assets/_Project/Scripts/Runtime/2.5D/IsometricCameraRig.cs`：等距俯视相机骨架（纯 Transform，低风险，待真机验证）。
- **下一步（轮次B）**：竹林3D场景+砍竹子特效（验证1.1+1.3），复用战斗内核与 FeedbackClock 顿帧（A-1 裁定）；Spine 接入留轮次C。
- **回退保障**：不顺眼即 `git checkout main`，本分支零影响主线。
- **护栏**：本轮零内核改动（仅新增骨架文件，未动 `Assets/Scripts/`）；t1/t3 未跑但内核零改动，倍率 2.5294x 不受影响。
- **遗留给用户**：① 本地 Unity 切到 `feature/2.5d` 分支验相机/场景；② 2D 主线（main）的**复活黑屏 Bug 仍在**，待用户本地反馈走 BugFix（主线优先级未降）。

### 2026-08-10 22:4x · 第 10 轮（自动化触发，用户不在场）

- **触发**：自动化第 10 轮。开局先 `git log --all` + `grep` 核实，发现**重大分支分叉**（前 9 轮 memory 只按线性历史记，漏看了 `main` 上的两次提交）：
  - `main` 已含 `98ee5ed`（P1-3 音效，归位 main）+ `b7c3ad7`（黑屏**基础**修复：OnSceneLoaded 无条件 BuildAll）。`feature/2.5d` 分支点早于这两次提交，故本分支工作树看不到。
  - 因此第 8 轮「P1-3 已交付」是**真**的（在 main），只是 feature/2.5d 工作树 grep 不到 → 不是 agent 撒谎，是分支分叉。前几轮"缺阶段23"恐慌解除。
- **本轮回应的两件事（均在 feature/2.5d 工作树，未提交）**：
  - **(A) 黑屏完整修复**：`Bootstrap.cs` 在 main 基础修复之上补 `ResetStatics()`@SubsystemRegistration（复位 `_firstSceneLoaded`/`_registered`）+ 幂等 `sceneLoaded -=/+` + 只读访问器 + `P0_4_BootstrapResetTests.cs`（562行 BR01–BR09）+ `.meta`（GUID cb7993bf… 唯一）。**main 的 b7c3ad7 缺这两项 → 关 Domain Reload 第 2 次 PlayMode 仍黑屏**，本版才是完整根治。
  - **(B) P2-1 BOSS 接线（A′方案）**：内核 `Encounter.cs`+175（BossPending）/`RunPhase.cs`+13（读 PendingAwareEnemyCount，纯查询重载零改）/`RunPhaseTests.cs`+219（RP15–RP19）；Unity `CombatController`+30/`CombatBridge`+509（ArmBossPending→MarkBossPending@1591、TickBossFlow→ClearBossPending@1819）/`EnemySpawner`+62/`Hud`+38/`WorldBuilder`+109；新增 `BossFlowConfig`(203)/`FxAutoDespawn`(62)/`HudBossBar`(341) + 3 文档。
- **并发冲突核实（工程师预警）**：并发 agent 改 `WorldBuilder.cs`（Bootstrap.IsWorldLive 依赖 HasGeneratedWorld/Grid）→ grep 实证两者仍在（@217/@90），误报无冲突。
- **护栏（主理人亲自复跑）**：`t1` **64/64 @ 2.5294x**（未漂）、`t3` **9/9**（89 .cs 全可解析）。内核零数值改动。
- **★ 本轮新踩坑（分支分叉）**：memory 不能只按线性历史记。每轮除 `git status`/`ls`/`grep` 外，必须 `git log --all` + `git branch -a` 确认是否有别的分支/提交抢先落地了同主题改动（本次 main 的 P1-3 与黑屏修复就是例子）。否则会误判"agent 没落盘"或"阶段号缺失"。
- **★ 本轮文档决策**：feature/2.5d 的 changelog 止于阶段 22，main 已用阶段 23（P1-3）。本回合记为**阶段 24**（skip 23，避免合并冲突），并在阶段 24 内写明 Bootstrap.cs 合并回 main 应以 feature/2.5d 版为准（含 ResetStatics+幂等订阅+测试访问器，main 版缺这些）。
- **护栏方法论印证**：无 Unity/dotnet 环境下，t1（倍率 2.5294x）+ t3（类型宇宙全解析）双绿 = 跨文件引用与数值口径未漂的实证，是"敢信并发 agent 落盘"的唯一硬凭据。
- **未提交 / 未合并**：全部改动停在 feature/2.5d 工作树，自动化不代用户提交或合并。

### 2026-08-11 08:0x · 第 11 轮（自动化触发，用户不在场）

- **选题**：TaskList 仍空。遵守上轮"库存积压警告"——**刻意不新增待验证的 Unity 表现层模块**，改为收束两个能在无 Unity 环境下自证的风险：① 分支分叉扩大 ② 内核新状态机 BossPending 无任何护栏在看。
- **工作流**：📋 部分工作流（架构评审 + QA 护栏），高见远 / 严过关**双线并行**派工，主理人亲自复核。C# 生产代码**零改动**。
- **交付**：`docs/branch-merge-plan.md`(783) + `bosspending_selfcheck.py`(1022, 53/53) + `bosspending_guard_mutation_test.py`(565, 26/26 捕获 18/18) + `docs/p2-1-bosspending-qa-report.md`(349) + changelog 阶段 26。详见 changelog 阶段 26。
- **★ 关键发现 1（合并策略的决定性事实）**：`git merge-tree --write-tree main feature/2.5d` **退出码 0** —— 已提交部分零冲突，**全部冲突 100% 来自未提交工作树**（约 5416 行无 git 备份）。故合并第一步必须是落盘 commit，这是全场唯一单点风险。
- **★ 关键发现 2（CRLF 预演陷阱）**：`core.autocrlf=true`，blob 纯 LF / 工作树纯 CRLF。拿 `git show` 导出的文件直接与工作树文件三方合并 → **整文件冲突假象**（架构师首次预演即踩）。手工比对前必须归一化；真实 `git merge` 不受影响。
- **★ 关键发现 3（变异测试再次证明自己）**：M-B3（失败优先→胜利优先）首轮**未被捕获**。根因：原用例自带 BOSS 债，`PendingAware = 0+1 = 1` 令判胜分支 `count <= 0` 本就不成立，**债自己把判胜分支挡死**，对调 ④⑤ 照样 Lost。→ **判别性用例必须无债**，已拆 BP-B2a/B2b。这是测试缺陷非生产缺陷，QA 自修未碰 C#。
- **★ 关键发现 4（.py 护栏不入库）**：`.gitignore:44` 排除 `Assets/Scripts/**/Tests/*.py`，`git ls-files "*.py"` 返回**空** → t1/t3/bosspending 四个脚本 **clone 即丢**。护栏是无 Unity 环境下唯一硬凭据，丢了就无法复现任何"双绿"结论。口径还不统一（main 的 `Assets/_Project/audio_syntax_check.py` 反而入库）。**未擅自改，待用户拍板白名单**。
- **changelog 阶段号雷（第10轮埋、本轮排除）**：第10轮只看到 main 占了阶段 23，**漏看 feature 自己也有一个阶段 23**（P2-1 接线 08-09 夜）→ 合并后两个「阶段 23」且 git 报 `rc=0`，是**唯一"工具报绿、结果是错的"**处。裁定：阶段号按**落盘时序**递增，终态 22→23(P2-1)→24(P1-3，合并时 main 侧 23→24)→25(第10轮)→26(本轮)。feature 侧已改完，第10轮误导提示已划删除线。
- **主理人裁定 3 条**：D-1 R-4 超时判胜**接受现状**（软锁远比误判胜利严重），登记待办要诊断痕迹；D-2 **不**让内核护栏引用 `_Project/`（2.5D 重写表现层时会让三套护栏集体变红，失去信号价值），I-3 归 `P2_1_BossWiringTests`；D-3 不变量统一按 **4 条**（简报漏了 I-4，`Encounter.cs:188`），D9 已由钉 3 条收紧为 4 条。
- **简报错误自查**：我给 QA 的简报把不变量写成 3 条（实为 4 条）、`Clear()` 行号写成约 L486-495（实为 ClearBossPending@492 / RunState.Reset@497）、⑥ 写成只有 `RemoveDead()`（实为 `FlushPendingAdds()`+`RemoveDead()`）。**"简报字符串必先 grep"这条铁律我自己又踩了一次**——下轮给下游的不变量/行号也要逐条 grep，不能只 grep 字符串常量。
- **护栏（主理人亲自复跑）**：`t1` **64/64 @ 2.5294x 未漂**、`t3` **9/9**（90 .cs / 类型 155）、`bosspending` **53/53**、变异 **26/26**；`Encounter.cs`/`RunPhase.cs` SHA-256 跑前跑后逐字节一致。
- **未提交 / 未合并**：全部停在 feature/2.5d 工作树，自动化不代用户提交或合并。

## 下一轮候选（按优先级）

1. **合并落地（已从"建议"升级为"最高优先"）**：`docs/branch-merge-plan.md` 已把路铺平（§8 逐条可复制命令 + 每步回退）。**第一步落盘 commit 必须尽快做**——5416 行无 git 备份是全场唯一单点风险，一次误操作即全灭。若下轮用户仍不在场，可考虑仅执行"落盘 commit"这一步（纯保护性动作、不合并不推送、`git reset --soft HEAD~1` 即可撤销），但需在汇报中显著告知。
2. **用户本地验证反馈** → BugFix（已连续 4 轮为首选项，用户始终未回）。
3. 用户拍板 `.py` 护栏 gitignore 白名单（影响护栏可持续性）。
4. 待办 D-1：R-4 超时判胜的诊断痕迹（需 Unity 侧改动，等验证通道打通再做）。
5. 敌人/NPC 精灵批量（需 ImageGen 积分，等用户在场）。
6. 2.5D 轮次B：竹林 3D 场景 + 砍竹子特效（Unity 表现层，**在验证通道打通前不建议做**）。

**⚠️ 库存积压警告（第 3 轮延续，但本轮已开始正确应对）**：P0 全交付 + P1 交付 4/7 + P2-1 已接线，全部"待用户本地验证"且从未被验证过。第 11 轮已改为只做自证型工作（护栏/文档/裁定），这个方向应**继续保持**，直到用户给出第一份本地验证反馈。判断标准很简单：**如果一项工作的产出无法在本环境被证明是对的，就不要在这一轮做它。**

### 2026-08-11 14:3x · 第 12 轮（自动化触发，用户不在场）

- **选题**：直接执行第 11 轮「下一轮候选」最高优先项——消除全场唯一单点风险：feature/2.5d 上**未提交、无 git 备份**的第 10/11 轮产出（约 5416 行）。`TaskList` 仍空。用户「重点关注」清单（P0-5/P0-6/PlayMode 场景名/R 重开）经 grep 核实均为前 11 轮已交付项，**未重复实现**。
- **工作流**：⚡ 快速模式（主理人直接执行 + 护栏自证，非代码改动，不派子 agent）。
- **交付**：本地 commit `4803840`「P2-1 BOSS 接线 + 黑屏完整修复（第10/11轮产出）落盘：保护性本地 commit」——**30 文件 / +6476 / -10**，工作树已干净。**不合并不推送**（遵循第 11 轮裁定，`git reset --soft HEAD~1` 可无损回退）。
- **护栏（提交前主理人亲自复跑）**：`t1` **64/64 @ 2.5294x 未漂**、`t3` **9/9**、`bosspending_selfcheck` **53/53**；`Encounter.cs`/`RunPhase.cs` SHA-256 提交前后逐字节一致。
- **★ 本轮踩坑（偏差处理）**：任务书 step 4② 要求回写 `F:/AI-project/xianxia-rpg/2026-07-30-00-16-54/.workbuddy/memory/YYYY-MM-DD.md`，但该路径是**严禁触碰**的 Godot/Web 原型仓（硬约束）。已改在 shuimofeng 工程内落记忆（changelog 阶段 27 + 本文件 + 项目日报），**未触碰 xianxia-rpg**。下轮若再遇此模板指令，照此处理。
- **★ CRLF 提示**：`git add -A` 时 30 个文件报「LF 将被 CRLF 替换」warning——是 `core.autocrlf=true` 的正常归一化（计划 §1.1 已记录），非错误，提交后工作树仍为 CRLF、blob 为 LF。
- **遗留给用户**：① 按需 `git push origin feature/2.5d` 完成远端最终备份；② 仍按 `docs/branch-merge-plan.md` §8 推进合并（阶段 2–5，含 CombatBridge 唯一冲突人工裁决、Bootstrap 禁 -X、changelog 阶段 23→24 移位）；③ 本地 Unity 验证 B 栏 14 项闸门；④ 拍板 `.py` 护栏 gitignore 白名单；⑤ 待办 D-1。

## 下一轮候选（按优先级）

1. **合并推进（落盘已完成，下一步解锁）**：保护 commit `4803840` 已就位，单点风险归零。下一步按 `docs/branch-merge-plan.md` §8 阶段 2 在 feature 分支 `git merge main`——预期仅 `CombatBridge.cs` 1 处冲突，按 §6.1「保留双方」机械解；`Bootstrap.cs` 自动合并且**禁用 `-X ours/theirs`**；`changelog.md` 需手工把 main 侧「阶段 23 · P1-3」改 24 并移位（§6.3）。**此步涉及 merge，建议用户在场或显式授权后再做**（自动化不代 merge/不代 push）。
2. **用户本地验证反馈**（已连续 5 轮首选项，用户从未回）→ BugFix：把 `docs/branch-merge-plan.md` §9 B 栏 14 项闸门跑一遍，红则回报。
3. **`.py` 护栏 gitignore 白名单拍板**：`t1`/`t3`/`bosspending` 四个脚本被 `.gitignore:44` 排除，clone 即丢。建议白名单 `!Assets/Scripts/**/Tests/*_selfcheck.py` + `!*_mutation_test.py`，待用户拍板。
4. 待办 D-1：R-4 超时判胜诊断痕迹（需 Unity 侧改动，等验证通道打通）。
5. 敌人/NPC 精灵批量（需 ImageGen 积分，等用户在场）。
6. 2.5D 轮次B：竹林 3D 场景 + 砍竹子特效（Unity 表现层，验证通道打通前不建议）。

**⚠️ 库存积压警告（延续，本轮已正确应对）**：P0 全交付 + P1 交付 4/7 + P2-1 已接线，全部"待用户本地验证"且从未被验证。本轮把最高优先的自证型风险（未提交产出）消除后，**继续遵循「产出无法在本环境自证就不做」原则**——下轮若用户仍不在场，优先推进可自证项（合并、护栏白名单、文档），而非新增待验证 Unity 模块。
