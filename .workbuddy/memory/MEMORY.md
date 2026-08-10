# 项目长期记忆 · 水墨风仙侠 RPG（shuimofeng）

> 本文件是跨对话/跨任务的**总纲**。任何新对话先读此文件即可接上全局状态。
> 详细轮次记录见 `.workbuddy/automations/automation-1786170865072/memory.md`；逐交付细节见 `docs/changelog.md`。

## 一、身份与角色
- 项目：Unity 仙侠 RPG，`F:/AI-project/ancientGame/shuimofeng/shuimofeng`
- 主理人：齐活林（SoftwareCompany Expert 提供的 PM/架构/工程/QA 协作工作流）
- 用户：lianlingxuelan（GitHub 同名，仓库 `git@github.com:lianlingxuelan/shuimo.git`）

## 二、核心架构（务必牢记）
- **三层架构**：纯 C# 战斗内核（`Assets/Scripts/`，零 UnityEngine 依赖）→ Unity 薄壳层（`Assets/_Project/Scripts/Runtime/`）→ 表现层（场景/角色/特效/相机）
- **围攻倍率指纹 2.5294x**：所有战斗改动必须跑 `t1_selfcheck.py` 确认倍率未漂移
- **最高裁定 A-1**：顿帧**禁用 `Time.timeScale`**（会冻确定性内核）。改用 `FeedbackClock`（static `Frozen` + `Delta= Frozen?0:Time.deltaTime`），内核照吃真实时间
- **裁定 Q-1**：`DifficultyBridge.PlayerHpMax` 语义=「平衡基准血量」，**永久钉死 260**。禁止改回实时回写（否则玩家白升级 + d_eff 漂移）
- **统一暂停闸门**：`IsGameplayBlocked = IsRunOver || _menuPaused`，唯一写入点 `SetMenuPaused()`，禁止直接写 `Scheduler.Paused`
- **裁定 A-2（音频）**：音频层吃 `Time.deltaTime`，**永不吃 `FeedbackClock.Delta`**。顿帧期 `Delta≡0` 会让节流窗口不推进 → 连击第二击被误判「同 key 重复」而静音丢弃。这条与 A-1 是**两个层面**，别混
- **裁定 A-3（音频随机源）**：音效合成用私有固定种子 `System.Random`（`AudioConfig.MakeSynthRandom(key)`），**绝不碰内核 `SkillRng`/`PCG32`**，否则污染 2.5294x 指纹
- **裁定 A-4（key 常量化）**：音效表里技能 key 一律引 `SkillConfig.SKILL_*` 常量，**禁止写字面量**（历史上短名 `circle_burst` 写错过，会让 3/4 技能音效静默哑掉且不报错）

## 三、当前进度（截至 2026-08-10 第8轮）
- ✅ **P0 六项全部代码交付**：P0-2 死亡/结算、P0-3 胜负判定、P0-4 重开、P0-5 主菜单/ESC暂停/退出、P0-6 操作引导、PlayMode 场景名
- ✅ **P1-2 受击反馈**：顿帧/屏震/分阵营闪白/伤害飘字（含 PI11 EditMode LoadScene 修复）
- ✅ **P1-6 玩家成长曲线**：`Progression.cs`（纯逻辑，88+ NUnit）
- ✅ **P1-3 音效系统**（第8轮）：6 新增 + `CombatBridge` 接线 + 53 测试 = 8075 行。12 音效 + 1 环境衬底**全部程序化合成，零二进制资源**。内核零改动，只订阅其 `PlaySfx` 事件。旋律 BGM 本期不做（留扩展位）
- ✅ **美术阶段 E**：女主 39 帧水墨精灵已接进 Unity
- ✅ **竹林 2.5D 技术验证已启动**（2026-08-10）：`feature/2.5d` 分支（main 保留 2D 可玩版不动），计划见 `docs/2.5d-tech-verify-plan.md`；等距相机骨架 `IsometricCameraRig.cs` 已落盘
- 📋 **2.5D 可行性评估**：`docs/unity-2.5d-feasibility.md` 已出
- ⚠️ **唯一瓶颈**：本环境无 Unity/dotnet，所有 Unity 侧行为**待用户本地验证**

## 四、硬约束（每轮适用，别踩）
- **唯一工程根**：`F:/AI-project/ancientGame/shuimofeng/shuimofeng`
- **严禁碰** `F:/AI-project/xianxia-rpg/`（Godot/Web 原型仓，历史误报"代码不存在"源于此）
- **两套脚本根**：`Assets/Scripts/`（内核）vs `Assets/_Project/Scripts/Runtime/`（表现层），搜不到先换根
- **自检脚本位置**：`Assets/Scripts/Systems/Combat/Tests/`（t1/t3_selfcheck.py），用 managed python 跑
- **无 Unity 环境**：永远不能编译/跑 Unity 测试，交付必写"待用户本地验证"
- **派工铁律**：绝不轻信 agent 的 completed，必须自己 grep/ls 上盘核实
- **回执 ≠ 落盘**：agent 报 `499 canceled` / 撞轮次上限时，**先 `ls` 核实文件再决定是否重派**。网络错误只杀回执，不杀已落盘的改动
- **简报字符串必先 grep**：给下游 agent 的简报里任何常量字符串（技能 id、事件 key、路径）都必须先 grep 核对，凭记忆写会传导成静默 bug
- **护栏必做变异测试**：静态护栏落地后要注入已知错误验证它真会报，否则分不清「没问题」和「没测到」。音频侧见 `Assets/_Project/audio_guard_mutation_test.py`

## 五、用户关键决策与偏好
- **2.5D 方向已拍板走技术验证**（2026-08-10）：用户选「竹林2.5D技术验证」路径，在 `feature/2.5d` 分支探路，不切换主线；验证通过再正式切
- **方向细节**（与《斩妖行》差异）：八方向自由移动 + 等距俯视 + 3D 场景(竹林真3D几何) + Spine 骨骼角色(Billboard) + 重特效打击感
- 用户明确喜欢《斩妖行》打击感 → 打击感=反馈系统(顿帧/屏震/闪白/飘字/音效/特效)，与2D/2.5D无关；2.5D 的 3D 粒子反而增强打击层次，是加分项
- 美术方向倾向 Spine 骨骼动画降成本
- 仓库已瘦身：Python 护栏脚本(.gitignore 排除不推送)、tools/ 质量脚本、AI 出图中间产物已移除
- 用户常滞后于实际进度（feature-closure-plan 三态需主动回写）

## 六、待用户本地验证
1. Unity 2022.3.62 打开编译
2. Test Runner 跑 EditMode + PlayMode（重点 P0_2_P0_4 PlayMode 真绿、P1_2_HitFeedbackTests 43条）
3. PlayMode 走 WASD+左键+K+L+Shift，验六键全链路
4. 粒子 prefab 挂载目视、动画补图（attack/walk/dodge 帧问题）
5. **音效（第8轮新增）**：跑 `P1_3_AudioTests` 53 条；戴耳机实听 12 音效 + 环境衬底（无爆音/削波、暂停收敛、重开无残留、M 键静音）
6. **⚠️ 首次 Unity 打开后务必提交自动生成的 7 个音频 `.meta`**，否则换机导入 GUID 错乱

## 七、下一轮候选（优先级）
1. 用户本地验证反馈（编译错/行为异常）→ BugFix
2. Task #5：补 P1-3 暂停收敛（A-17/A-18）+ 节流窗口测试（建议等现有 53 条确认真绿再动）
3. 敌人/NPC 精灵批量（需 ImageGen 积分，等用户在场）
4. **竹林 2.5D 技术验证（feature/2.5d 分支）已启动**：轮次B=竹林3D场景+砍竹子特效(验证1.1+1.3)；轮次C=Spine角色接入(验证1.2)

**⚠️ 库存积压警告**：P0 全交付 + P1 已交付 4/7（P1-2/P1-3/P1-6 + 部分 P1-4），但**全部"待用户本地验证"且从未被验证过**。继续堆代码的边际收益在下降——若仍无用户反馈，优先做「不需要 Unity 就能自证」的事（护栏、文档、纯逻辑），而非再加一个待验证的 Unity 表现层模块。
