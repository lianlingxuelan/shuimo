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

## 下一轮候选（按优先级）

1. **用户本地验证反馈优先**：若报编译错/行为异常 → BugFix。
2. 敌人/NPC 精灵批量（需 ImageGen 积分，等用户在场）。
3. 回写 `feature-closure-plan.md` 三态标注（P0-5/P0-6/P1-6/P1-2 标记"已交付"）。
4. P1 遗留：F-1 多波次永久锁 Won、`unity-t3-prd.md §4.4`（本轮已修复）。
5. 若用户拍板 2.5D 决策 → 进入技术验证阶段。
