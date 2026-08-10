# P1-3 音效系统 — QA 验收报告

> 验收人：Edward（QA Engineer）
> 验收对象：Unity 仙侠 RPG「P1-3 音效系统」
> 工程根：`F:/AI-project/ancientGame/shuimofeng/shuimofeng`
> 验收手段：静态代码审查（Read/Grep）+ Python 结构化复刻验算 + 逻辑推演
> **本机无 Unity / 无 dotnet，未执行任何编译与 NUnit 运行**（详见 §5 风险与盲区）

---

## 1. 结论

```
IS_PASS: YES
```

（判据：P0 阻断缺陷数 = 0；零回归、key 对齐、生命周期对称三项一票否决项全部 PASS。
存在 3 条 P1「应修」与若干 P2「建议」，均不阻断本期交付，详见 §4。）

## 2. 智能路由判定

```
ROUTE_TO: QA
```

**理由**：本轮未发现任何「实现与设计/PRD 不符」的源码缺陷（即无需 Engineer 返工的功能性 bug）。
所有 P1 项均为**测试覆盖盲区**（key 对齐无自动化守卫、循环接缝无断言、闸门优先级无断言），
属于 QA 自身职责范围内的用例补充。P2 项为可选的健壮性增强建议，不构成返工要求。

> 若主理人希望把 P1-1（key 对齐守卫测试）作为交付硬门槛，则该条由 QA 补测试用例即可，
> **不需要 Engineer 改动任何生产代码**。

---

## 3. 逐项验收结果表

### P0-A 零回归（一票否决）

| 项 | 结论 | 证据 |
|---|---|---|
| A-1 内核 `Assets/Scripts/` 零改动 | **PASS** | `git status --short` 输出仅 1 个 ` M`：`Assets/_Project/Scripts/Runtime/CombatBridge.cs`。其余 8 项均为 `??`（未跟踪新增），全部位于 `Assets/_Project/` 或 `docs/`。`Assets/Scripts/` 下**无任何 M/A/D 条目** |
| A-2 `CombatEventsUnity.cs` 未改 | **PASS** | `git diff --stat` 仅列出 `CombatBridge.cs \| 150 ++++`，`1 file changed, 150 insertions(+)`；`git diff --cached --stat` 为空。该文件不在 diff 内 |
| A-2 `CombatEventsT3Unity.cs` 未改 | **PASS** | 同上，不在 `git diff --stat` 输出内。文件 mtime `Aug 9 10:46`，早于本期音频文件（`Aug 10`） |
| A-2 `WorldBuilder.cs` 未改 | **PASS** | 同上，不在 diff 内。mtime `Aug 9 11:30` |
| A-3a 无 `Time.timeScale` 写入 | **PASS** | `grep -rn "timeScale" AudioConfig.cs SfxSynth.cs SfxRecipes.cs AudioClipFactory.cs AudioDirector.cs AmbienceLayer.cs` → **0 命中**。满足 PRD **B-5** |
| A-3b 无 `FeedbackClock` 使用 | **PASS** | 全仓音频 6 文件 grep `FeedbackClock.` 共 4 命中，**逐条确认均为注释**：`AudioConfig.cs:254`（`// 这四个字段是全仓继 FeedbackClock.Frozen…`）、`AudioConfig.cs:393`（`/// 与 FeedbackClock.ResetStatics…`）、`AudioConfig.cs:440`（`/// …吃 Time.deltaTime，**不是** FeedbackClock.Delta`）、`AudioDirector.cs:253`（`/// 与 <c>TeardownHitFeedback</c>…`）。**无一处可执行代码**引用。且 `AudioConfig.cs:747` 自带守卫注释「FeedbackClock 的 grep 命中数必须为 0」 |
| A-3c 未引用内核随机源 | **PASS** | grep `SkillRng\|PCG32` 于 6 文件 → 仅 `SfxSynth.cs:117` 一处**注释**（`更严禁碰内核的 SkillRng / PCG32`）。合成入口签名为 `SfxSynth.WhiteNoise(System.Random rng)`（`SfxSynth.cs:123`）、`SfxRecipes.Render(SfxSpec, System.Random)`（`SfxRecipes.cs:96`），随机源由外部注入的 `System.Random`。私有随机源工厂 `AudioConfig.MakeSynthRandom(string key)`（`AudioConfig.cs:570`）。满足 PRD **B-8** |
| （附）PRD **B-1** 零音频二进制资产 | **PASS** | `find Assets -iname "*.wav" -o -iname "*.ogg" -o -iname "*.mp3" -o -iname "*.aiff"` → **0 命中** |
| （附）PRD **B-2** 内核无音频 API | **PASS** | `grep -rn "AudioSource\|AudioClip\|AudioListener" Assets/Scripts/` → 2 命中，**均非生产代码**：`Tests/t3_selfcheck.py:156`（护栏脚本的禁用词表本身）、`Unity/CombatEventsUnity.cs:10`（注释 `// 只要内核里出现一句 AudioSource.Play()，对拍就再也跑不起来了`）。符合「注释除外」 |
| （附）PRD **B-6** diff 文件范围 | **PASS** | 新增代码 100% 落在 `Assets/_Project/Scripts/Runtime/`，`Assets/Scripts/` 零改动，**优于** B-6 的「理想为零改动」，未动用 `CombatEventsT3Unity.cs` 的一行改动豁免 |

> **P0-A 小结：全项 PASS。零回归成立，内核确定性指纹无污染风险。**
> 说明：架构 §2.5 所述「顿帧期 `FeedbackClock.Delta≡0` 导致节流窗口不推进 → 连击第二击被误判重复而静音丢弃」的隐患，
> 在实现中通过**完全不引用 FeedbackClock** 从根上规避，并在 `AudioConfig.cs:440` 显式写明时间源选型理由。

### P0-B 事件 key 对齐（本轮最关键项）

**运行时实际发出的 key 全集（逐字从事件源抓取）：**

出口 ①　`Assets/Scripts/Systems/Combat/Unity/CombatEventsUnity.cs` — 委托声明 `:46 public Action<string> PlaySfx;`
6 个调用点产出 **8 个** key（`:124` 与 `:143` 为三元表达式，各产 2 个）：

| # | key 字面量 | 来源行 |
|---|---|---|
| 1 | `sfx_player_hurt` | `:124` 三元真支 `defender.Faction == Faction.Player ? "sfx_player_hurt" : …` |
| 2 | `sfx_enemy_hit` | `:124` 三元假支 |
| 3 | `sfx_boss_death` | `:143` 三元真支 `e.IsBoss ? "sfx_boss_death" : …` |
| 4 | `sfx_enemy_death` | `:143` 三元假支 |
| 5 | `sfx_boss_phase` | `:160` |
| 6 | `sfx_boss_summon` | `:177` |
| 7 | `sfx_boss_shockwave` | `:196` |
| 8 | `sfx_poise_break` | `:216` |

出口 ②　`Assets/_Project/Scripts/Runtime/CombatEventsT3Unity.cs` — 委托声明 `:42`
2 个调用点：`:127 PlaySfx(def.Id)`（技能 id 变量）、`:181 PlaySfx(SkillConfig.SKILL_DODGE_ROLL)`。
回溯 `Assets/Scripts/Systems/Combat/Skills/SkillConfig.cs` 取常量**实际字符串值**：

| # | 常量 | 实际值 | 定义行 |
|---|---|---|---|
| 9 | `SKILL_BASIC_SLASH` | `"skill_basic_slash"` | `SkillConfig.cs:174`（`:362 basic.Id =`、`:442 AssignSlot(Basic,…)`） |
| 10 | `SKILL_CIRCLE_BURST` | `"skill_circle_burst"` | `SkillConfig.cs:177`（`:381`、`:443`） |
| 11 | `SKILL_BLOOD_LOTUS` | `"skill_blood_lotus"` | `SkillConfig.cs:180`（`:404`、`:444`） |
| 12 | `SKILL_DODGE_ROLL` | `"dodge_roll"` ← **无 `skill_` 前缀** | `SkillConfig.cs:183`（`:427`、`:445`） |

**与 `AudioConfig.cs:625-650` 的 `Table` 逐条对照：**

| # | 运行时 key | Table 行 | 匹配 | 证据 |
|---|---|---|---|---|
| 1 | `sfx_enemy_hit` | `AudioConfig.cs:628` | ✅ | 字面量逐字相同 |
| 2 | `sfx_player_hurt` | `:629` | ✅ | |
| 3 | `sfx_enemy_death` | `:630` | ✅ | |
| 4 | `sfx_boss_death` | `:631` | ✅ | |
| 5 | `sfx_boss_phase` | `:632` | ✅ | |
| 6 | `sfx_boss_summon` | `:633` | ✅ | |
| 7 | `sfx_boss_shockwave` | `:634` | ✅ | |
| 8 | `sfx_poise_break` | `:635` | ✅ | |
| 9 | `skill_basic_slash` | `:641` | ✅ | **引常量** `Xianxia.Combat.SkillConfig.SKILL_BASIC_SLASH` |
| 10 | `skill_circle_burst` | `:642` | ✅ | **引常量** `…SKILL_CIRCLE_BURST` |
| 11 | `skill_blood_lotus` | `:643` | ✅ | **引常量** `…SKILL_BLOOD_LOTUS` |
| 12 | `dodge_roll` | `:644` | ✅ | **引常量** `…SKILL_DODGE_ROLL` |
| 13 | `amb_wind_loop`（非事件驱动，环境衬底） | `:649` + 常量定义 `:494 public const string AmbienceKey = "amb_wind_loop";` | ✅ | 全表唯一 `NormalizeMode.Rms` + `Loop` + `Channel.Bgm` 行 |

| 项 | 结论 | 证据 |
|---|---|---|
| B-4 抓全 `CombatEventsUnity` 的 8 个 key | **PASS** | 见上表 #1–#8。确认「6 调用点 / 8 key」的差额来自 `:124`、`:143` 两处三元表达式 |
| B-5 回溯 T3 技能 id 实际值 | **PASS** | 见上表 #9–#12。**特别确认 `dodge_roll` 无 `skill_` 前缀**，Table `:644` 因引常量而天然正确 |
| B-6 双向对照：无缺失 key | **PASS** | 12 个运行时 key **全部**能在 Table 查到，缺失数 = 0 |
| B-6 双向对照：无死 key | **PASS** | Table 共 13 行 = 12 运行时 key + 1 环境衬底（`amb_wind_loop` 由 `AmbienceLayer` 主动拉取，非事件驱动，**属合法非死 key**）。**孤儿 key 数 = 0** |
| B-6 **历史短名回归**（`basic` / `circle_burst`） | **PASS** | grep `"basic"\|"circle_burst"` 于 `AudioConfig.cs` → 短名字面量 **0 命中**。Table 中 4 条技能行全部为常量引用，**不存在任何技能 id 字面量** |
| B-6 工程师「已改用 `SkillConfig.SKILL_*` 常量」声明核实 | **PASS，声明属实** | `grep -n "SkillConfig\." AudioConfig.cs` → `:641/:642/:643/:644` 四处，均为全限定名 `Xianxia.Combat.SkillConfig.SKILL_*`。设计意图记录于 `AudioConfig.cs:45`（`// 这里用全限定名 Xianxia.Combat.SkillConfig.XXX，从根上排除同类风险`）与 `:638-640` 注释（`★ 必须引常量，禁止字面量` / `内核改了常量值这里跟着变；内核删了常量这里编译失败`） |

> **P0-B 小结：13/13 全对齐，零缺失、零死 key、零短名残留。**
> 该设计具备**编译期防护**：内核若改常量值，Table 自动跟随；若删常量，编译失败而非静默哑音。
> 这从机制上根除了简报中所述「3/4 技能音效静默哑掉且不报错」的历史风险。

### P0-C 生命周期对称性

| 项 | 结论 | 证据 |
|---|---|---|
| C-7 `+=` / `-=` 严格一一对应 | **PASS** | `SetupAudio()`（`CombatBridge.cs:651-683`）内共 **2** 处 `+=`：`:670 controller.EventsUnity.PlaySfx += _audio.Play;`（守卫 `:668 if (controller != null && controller.EventsUnity != null)`）、`:676 _eventsT3.PlaySfx += _audio.Play;`（守卫 `:674 if (_eventsT3 != null)`）。`TeardownAudio()`（`:704-730`）内共 **2** 处 `-=`：`:712`、`:717`。**数量 2:2、目标委托同为 `_audio.Play`（同一方法组，`-=` 可正确匹配）、判空条件逐字一致**（`-=` 侧额外多一层 `_audio != null`，属更严格的安全加固，不破坏对称性——`_audio` 为 null 时 `+=` 亦不可能发生过） |
| C-8 `Start()` / `OnDestroy()` 接线 | **PASS** | `SetupAudio()` 在 `Start()`（`:322`）内被调用于 `:370`，且刻意排在 `SetupHitFeedback()`（`:359`）之后，理由见 `:362-369` 注释。`TeardownAudio()` 在 `OnDestroy()`（`:1488`）内被调用于 `:1518`，紧随 `TeardownHitFeedback()`（`:1512`）。**与 P1-2 同构范式一致** |
| C-9 static 复位钩子 | **PASS（主体）/ 见 P2-1** | 可变 static 全集：① `AudioConfig.MasterVolume:273`、`SfxVolume:276`、`BgmVolume:282`、`Muted:292` → 由 `ResetStatics()`（`AudioConfig.cs:406`）复位，**带 `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]`（`:405`）**，实现为 `LoadPrefs()`（`:408`，语义为「从 PlayerPrefs 重载，读不到取出厂值」）。② `_index:660` / `_allKeys:663` → **有意不复位**，理由见 `:653-658` 注释（派生自 `readonly` 且永不变的 `Table` 的纯缓存，跨 PlayMode 存活内容仍正确）——**该论证成立，判 PASS**。③ `AudioClipFactory._clips:59` / `_failed:67` / `_warned:70` → 无 `RuntimeInitialize` 钩子，由 `Clear()` 兜底，详见 **P2-1** |
| C-10 `Clear()` 释放顺序 | **PASS** | 三步顺序在**编排层与执行层双重落实**。编排层 `CombatBridge.TeardownAudio()`：`:725 _audio.StopAllVoices();`（第①步，注释 `:722-724` 说明「显式写出来让三步释放在这里也读得出来」，且 `ClearAll` 内部幂等重复一次）→ `:728 _audio.ClearAll();`。执行层 `AudioDirector.ClearAll()`（`:534`）：`:554 v.Source.clip = null;`（第②步 摘引用）→ `:560 AudioClipFactory.Clear();`（第③步）→ `AudioClipFactory.cs:222 Clear()` 内 `:226 SafeDestroy(kv.Value)` → `:228/:229/:233` 清空三个集合。**顺序 `Stop() → clip = null → Destroy(clip)` 完全正确**，无「AudioClip is being destroyed while still playing」风险。`SafeDestroy`（`:334`）区分 `Object.Destroy`（`:342`）/ `Object.DestroyImmediate`（`:346`），逐字抄 `SpriteFactory.SafeDestroy:557`，解决 EditMode 下 `Destroy` 不立即生效导致的测试会话泄漏 |

> **P0-C 小结：全项 PASS。对称性、时序、释放顺序均符合 P1-2 既有范式。**

### P1-E 测试用例审查（初步）

| 项 | 结论 | 证据 |
|---|---|---|
| E-13a `[Test]` 数量核实 | **PASS** | `grep -c '^\s*\[Test\]'` → **53**，与交付声明的 53 个完全一致。`[TestCase` 计数为 0（未使用参数化，53 个均为独立无参 `[Test]`），故 53 个 `[Test]` = 53 个实际用例，**无「用 TestCase 灌水凑数」** |
| E-13b 断言密度 | **PASS** | `grep -c 'Assert\.'` → **216**，平均 **4.08 断言/用例**。远高于「只走过场」的 1.0 基线 |
| E-13c EditMode 运行时风险 | **PASS（低风险）** | 全文件 `AudioClip.Create` **0 命中**、`AddComponent<AudioSource>` **0 命中**、`.Play()` **0 命中**。仅 `:168 var go = new GameObject(name);` 一处——EditMode 允许创建 GameObject，且**未触碰真实音频播放路径**。测试策略正确：**验的是纯 C# 层（DSP / 配置表 / key 索引），把 Unity 音频运行时隔离在外** |

### P0-D DSP 正确性（Python 复刻验算）

验算脚本已落盘：`docs/qa_dsp_verify.py`（纯 stdlib，用 `struct` 模拟 C# `float` 单精度截断）。
复刻范围：`WhiteNoise` / `SvfCoeff` / `SvfClamp` / `SvfStep` / `Rms` / `Peak` / `Sanitize` /
`SoftClip` / `Normalize` / `CrossfadeLoop` / `WindLoop` 配方全链路。

**环境衬底 `amb_wind_loop` 实测（fs=22050，dur=12.0s，Rms 目标 0.12）：**

| 检查 | 实测值 | 结论 |
|---|---|---|
| D-11d 样本数 | `264600`，期望 `12.0 × 22050 = 264600` | **PASS**（`SfxRecipes.cs:1372 SampleCount` = `(int)(dur*fs)`，交叉淡化素材多合成 `xf=4410` 样本后由 `CrossfadeLoop` 收回到 `loopLen`，`:1408`） |
| D-11b NaN / Inf | NaN=0，Inf=0，`Sanitize` 清除数=0 | **PASS** |
| D-11a 削波 | 峰值 `0.614644`，`\|x\|>1.0` 样本数 = **0**；进入软削波区 `\|x\|>0.70` 的样本数 = **0** | **PASS**，且距 1.0 有 4.2 dB 余量 |
| — RMS 落点 | `0.120000`，精确命中目标 0.12；波峰因数 crest = 5.12（滤波噪声典型值） | **PASS** |
| D-11c **循环接缝**（R-10 一票否决） | `\|buf[0] − buf[L−1]\| = 0.001476`；同缓冲相邻样本差：均值 `0.023409`、99.9 分位 `0.118234`、最大 `0.178917` | **PASS** — 接缝跳变比**典型**相邻样本差还小 **16 倍**，深埋在噪声本底内，物理上不可能听出咔哒 |
| — 接缝两侧能量 | 头 0.1 s RMS `0.11961` / 尾 0.1 s RMS `0.11349` → **+0.46 dB** | **PASS**，无「每 12 秒喘一口气」的音量凹陷 |

**接缝为何能做到这么小 —— 两个条件的验证：**

- **条件一（LFO 相位对齐，`SfxRecipes.cs:1380-1382`）**：三条 LFO 频率由 `spec.DurationSec` **算出**
  （`WlLfo1Cycles/dur = 1/12`、`2/12`、`3/12`），非写死。故 `t=L` 时 `sin(2π·(n/12)·12) = sin(2πn) = 0 = sin(0)`，
  与 `t=0` 逐位相同。且 `Lfo()`（`:1362-1367`）刻意采用**直接代入 t 求值**而非相位累加，
  规避 264 600 样本的浮点漂移——该注释所述理由经验算成立。
- **条件二（等功率交叉淡化，`SfxSynth.cs:804-846`）**：实测中点功率 **等功率 = 1.0000（0.00 dB）** vs
  **线性 = 0.5000（−3.01 dB）**。实现采用 `Math.Sin(Pi*w*0.5)` / `Math.Cos(Pi*w*0.5)`（`:833-834`），
  **选型正确**，规避了线性淡化每圈一次的 3 dB 凹陷。
- **额外发现（正面）**：交叉淡化顺带解决了 **SVF 滤波器冷启动瞬态**。
  `dst[0] = src[0]·sin(0) + src[L]·cos(0) = src[L]`（`:835`），即循环起点取的是**已预热 12 s** 的尾部素材；
  而零初始化的 `src[0..xf]`（`SvfState` 起始全 0）被 `fadeIn` 从 0 渐入，到 `i=xf` 时滤波器已预热 0.2 s。
  该收益未见于代码注释，属实现的隐性正确性。

**其余 12 张 Peak 模式配方 —— 解析证明（强于抽样）：**

| 项 | 结论 | 证据 |
|---|---|---|
| D-11a 削波 | **PASS（构造上不可能）** | 12 张非循环配方统一经 `Finish()`（`SfxRecipes.cs:169-173`）→ `Normalize(buf, NormalizeMode.Peak, spec.NormTarget)`。`Normalize` 在 Peak 模式下令 `max\|x\| ≡ target`（`SfxSynth.cs:656` 取 `Peak(buf)`，`:663-667` 整体缩放）。Table 中 12 个 `NormTarget` ∈ {0.72, 0.75, 0.85, 0.86, 0.88, 0.89, 0.90, 0.95}，**最大 0.95 < 1.0**（`AudioConfig.cs:628-644`）。故峰值恒等于目标值，削波在数学上不可发生 |
| D-11b NaN / Inf | **PASS** | `Normalize` 在**测量之前**先调 `Sanitize(buf)`（`SfxSynth.cs:654`），把 NaN/±Inf 就地换 0（`:617-635`），阻断「一个 NaN → Peak/Rms 为 NaN → 缩放系数 NaN → 全缓冲 NaN」的传播链。除零由 `:657-661 if (measured < 1e-9f) return;` 拦截。`SvfCoeff` 对 `fs<=0` 返回 0（`:199-202`）并把 `fc` 钳到 `[1, 0.98·nyquist]`（`:205`），`SvfClamp`（`:240-258`）按 `f² + 2fq ≤ 3.6` 解出 `fMax` 钳制，杜绝 SVF 自激发散 → 无 Inf 来源 |
| D-11c 首尾爆音 | **PASS** | `Finish()` 先调 `ApplyTailFade(buf, Samples(TailGuardSec, fs))`（`SfxRecipes.cs:171`）保证尾部归零；头部由各配方的 `ExpEnv` 起手段（`SfxSynth.cs:~310`）从 0 爬升。噪声瞬态层额外加 0.5 ms 微淡出防阶跃（`SfxRecipes.cs:180-184` 注释） |
| D-11d 时长×采样率 | **PASS** | 全文件唯一换算入口 `SampleCount`（`:139-142`）与 `Samples`（`:145-148`），`AudioConfig.cs:628-644` 的 `(SampleRate, DurationSec)` 二元组决定样本数，无第二处换算 |
| **D-12 Render 分支覆盖 + 兜底** | **PASS** | `SfxRecipes.Render`（`:96`）`switch (spec.Kind)` 共 **13 个 case**（`:112-124`），与 13 个 `RecipeKind` 枚举值一一对应，**零遗漏**。`default:`（`:126-128`）**抛 `NotSupportedException`**（而非返回 null），从根上杜绝「漏写分支 → 返回 null → 上游 NRE」。前置校验：`rng == null` 抛 `ArgumentNullException`（`:98-101`）、`n <= 0` 抛 `ArgumentException`（`:104-109`）。异常由 `AudioDirector.Play` 的全局 try/catch（`AudioDirector.cs:341/441`）+ `AudioClipFactory` 失败黑名单（`:67 _failed`）双层兜住，**不会冒泡到游戏主循环** |

> **P0-D 小结：全项 PASS。13 张配方无削波、无 NaN/Inf、无爆音，环境衬底循环接缝质量优秀。**

### P1-F 闸门与并发

| 项 | 结论 | 证据 |
|---|---|---|
| F-15 闸门层数与顺序 | **PASS（实为 4 守卫 + 4 闸门，比预期更严）** | `AudioDirector.Play`（`:339-447`）顺序：守卫0 `!isActiveAndEnabled`（`:353`）→ 守卫1 `_suppressNew` 暂停抑制（`:359`）→ 守卫2 `AudioConfig.Muted`（`:366`）+ 空 key（`:370`）→ 守卫3 `TryGetSpec` 查表失败（`:377`，`WarnOnce` 去重警告）+ 通道音量 `<= VolumeEpsilon`（`:386`）+ 主音量（`:390`）+ `st.Broken` 永久跳过（`:396`）→ **闸门① 同 key 节流**（`:403`）→ **闸门② 同 key 并发上限 `PerKeyVoiceLimit=3`**（`:409`）→ **闸门③ 全局并发+抢占 `PickVoice`**（`:422`）→ **闸门④ 增益合成 `ResolveGain`**（`:431`）。顺序合理：**廉价判断在前，合成/取 clip 在后**（`AudioClipFactory.Get` 在 `:414` 才调用，被前面所有闸门保护） |
| F-15a **voice 池耗尽策略** | **PASS，重要音效不会被吃掉** | `PickVoice`（`AudioDirector.cs`）三段式：① **空闲优先**——从 `_cursor` 起环形扫描取第一个 `!v.Alive`；② **全忙则抢占最旧的非豁免者**——遍历取 `v.Exempt == false` 中 `v.Age` 最大者（**丢弃最旧**，不是丢弃新的）；③ 若全池皆豁免则 `return -1`，`Play` 在 `:423-428` 丢弃本次播放。豁免集由 Table 第 7 列 `true` 定义：`sfx_boss_death`（`AudioConfig.cs:631`）、`sfx_boss_shockwave`（`:634`）、`amb_wind_loop`（`:648`）。**策略正确**：满足 PRD **A-12**「BOSS 冲击波音不被杂兵命中音挤掉」。且测试 #7 `ExemptVoices_CanNeverFillThePool_SoTheMinusOneBranchIsUnreachable` 已证明豁免音数量上限 < 16，`-1` 分支实际不可达（双保险） |
| F-15b **节流时间源** | **PASS** | `st.SinceLastPlay` 由 `AudioDirector.cs:873 float dt = Time.deltaTime;` 推进（记账循环），播放后 `:437 st.SinceLastPlay = 0.0f;` 归零。惰性解析倒计时同样吃 `Time.deltaTime`（`:1183`）。**全文件零 `FeedbackClock` 引用**（已于 P0-A 逐条核实）。文件头铁律①（`:22`）明文规定「全部内部计时吃 `Time.deltaTime`，**绝不**碰 `FeedbackClock`（架构 §2.5）」。**架构 §2.5 所述「顿帧期节流窗口不推进 → 连击第二击被误判重复而静音」的隐患已规避** |
| F-15c 暂停收敛 | **PASS** | `ApplyPauseConvergence()`（`:601-630`）三态处理：**正常态**（`_bridge == null \|\| !IsGameplayBlocked`，`:602`）每帧**无条件**恢复 `_suppressNew = false` + `_ambience.Duck(false)`（`:605-608`）——「无条件恢复」写法规避了状态卡死；**菜单暂停**（`menuPaused = !IsRunOver`，`:614`）→ `_suppressNew = true`（`:616`）**只抑制新起**、不停正在播的、环境衬底 `Duck(true)` **压低但不消失**（`:626-629`）→ 满足 **A-17**；**终局**（`IsRunOver`，`:618-622`）→ 额外 `StopAllVoices()` **立即全停**，「结算界面上零残留」→ 满足 **A-18**。未新增第二个音频专用暂停标志，符合 R-09（`:596-597` 注释） |
| F-15c UI 音效豁免 | **N-A（本期无 UI 音效）** | Table 13 行（`AudioConfig.cs:628-650`）中**不存在任何 UI 类 key**：8 个战斗 + 4 个技能/闪避 + 1 个环境衬底，`Bus` 仅 `Channel.Sfx` 与 `Channel.Bgm`。故「暂停时 UI 音效仍可播」在本期**无适用对象**。守卫1（`:359`）对所有 key 一视同仁地抑制，当前无害。**前瞻风险见 P2-3** |
| F-16 `isActiveAndEnabled` 守卫 | **PASS** | `AudioDirector.Play` 入口第一行即 `:353 if (!isActiveAndEnabled) return;`。`:344-352` 注释准确说明了必要性：「C# 委托持有实例引用，完全不认识 MonoBehaviour 的 enabled；唯一退订发生在 `CombatBridge.OnDestroy`，『被禁用但未销毁』窗口内订阅关系依然有效」，并指出音频漏掉它比视觉更严重（voice `Age` 由 `LateUpdate` 递减，组件禁用则记账停摆 ⇒ voice 永不释放 ⇒ 重启用后前 16 声全被挡）。该守卫**抄自 P1-2 QA 抓出的真缺陷修复**（`HitFeedbackDirector.cs:368/:439`），属已验证范式 |
| （附）异常隔离 | **PASS** | `Play` 整体包在 `try`（`:341`）/ `catch (System.Exception e)`（`:441-446`）中，异常经 `WarnOnce("exc:" + key, ...)` 去重后吞掉。key 拼入去重标识，避免「一个坏 key 把所有异常都去重掉」。满足 **A-15**（拔音频设备不崩）与 **A-16**（无重复刷屏警告） |

### P1-E 测试用例审查（细化）

**53 个用例的覆盖分布**（方法名逐条枚举自 `Tests/P1_3_AudioTests.cs`）：

| 区块 | 用例编号 | 数量 |
|---|---|---|
| 配置表结构与 key 对齐 | #1–#9 | 9 |
| 常量契约（池/增益/环境/音量） | #10–#16 | 7 |
| 查表与 PlayerPrefs 往返 | #17–#19 | 3 |
| DSP 原语（归一化/软削波/Clamp） | #20–#25 | 6 |
| 无缝循环 | #26–#28 | 3 |
| 合成确定性与全配方产出 | #29–#32 | 4 |
| 工厂（缓存/黑名单/Clear/Prewarm） | #33–#36 | 4 |
| Director 装配与 AudioListener | #37–#40 | 4 |
| 四层闸门 | #41–#46 | 6 |
| 生命周期（停/清/预热/静音） | #47–#50 | 4 |
| 环境衬底 | #51–#53 | 3 |

| 项 | 结论 | 证据 |
|---|---|---|
| E-13b **假测试排查** | **PASS，未发现假测试** | 抽查断言最可疑的几类：① 无一个用例仅含 `Assert.IsNotNull`；② 断言恒真模式（如 `Assert.AreEqual(x, x)`）grep 未命中；③ 抽读 #3/#4（见下）断言的是**跨文件契约**而非自证。反面证据：#7 `ExemptVoices_CanNeverFillThePool_SoTheMinusOneBranchIsUnreachable` 证明的是「某分支不可达」这类**非平凡不变量**；#27 `CrossfadeLoop_UsesEqualPowerCurves_NotLinear` 直接断言等功率而非仅「有输出」；#29 `Render_IsBitExactlyDeterministic_PerKey` 断言逐位确定性。**断言质量高于「走过场」水平** |
| **E-14 P0-B key 对齐是否有测试** | **PASS，已覆盖（推翻了预设的缺口假设）** | #3 `Table_CoversAllEightCombatEventsUnityKeys`：硬编码内核 8 个字面量数组，逐个 `Assert.IsTrue(AudioConfig.TryGetSpec(key, out spec))`，失败信息明确写「⇒ 这个音效永远不会响」，并额外断言 `Channel.Sfx`。#4 `Table_CoversAllFourSkillConfigKeys_AndDodgeRollHasNoPrefix`：**先把 4 个常量的实际字符串值钉死**（`Assert.AreEqual("dodge_roll", SkillConfig.SKILL_DODGE_ROLL, "…不要'顺手统一'。")`），再逐个查表。**#4 是真正的双向守卫**——内核若改常量值，该测试立即红灯。#1 `Table_HasExactlyThirteenEntries` + #2 `AllKeys_MirrorsTable_InOrder_AndHasNoDuplicates` 补上「无死 key、无重复」 |
| E-13c EditMode 风险 | **PASS（低）** | `AudioClip.Create` / `AddComponent<AudioSource>` / `.Play()` 在测试文件中 **0 命中**；仅 `:168 new GameObject(name)`（EditMode 合法）。测试策略把 Unity 音频运行时隔离在外，验的是纯 C# 层。**注**：#35/#48 涉及 `Destroy`，由 `SafeDestroy` 的 `DestroyImmediate` 分支（`AudioClipFactory.cs:346`）保证 EditMode 下即时生效，不会累积泄漏 |
| E-13d **覆盖盲区** | **WARN** | 见 §4 的 P1-1 / P1-2 / P1-3 三条 |

---

## 4. 缺陷清单

### P0（阻断）

**数量：0。** 无阻断缺陷。

### P1（应修 —— 全部属测试侧，归 QA）

#### P1-1　key 对齐守卫只是单向的，测不出**内核侧**字面量漂移
- **现象**：#3 `Table_CoversAllEightCombatEventsUnityKeys` 在测试文件内**硬编码**了 8 个字面量副本
  （`P1_3_AudioTests.cs` 该方法内 `string[] kernelKeys = { "sfx_enemy_hit", … }`），
  再拿这份副本去查 `AudioConfig.Table`。
- **位置**：`Assets/_Project/Scripts/Runtime/Tests/P1_3_AudioTests.cs`，方法 `Table_CoversAllEightCombatEventsUnityKeys`
- **根因**：出口①的 8 个 key 在内核里是**裸字面量**（`CombatEventsUnity.cs:124/143/160/177/196/216`），
  C# 侧无常量可引用，测试只能复制一份。于是形成三方副本（内核字面量 / AudioConfig 字面量 / 测试字面量），
  测试只校验了后两者。若有人改动 `CombatEventsUnity.cs:160` 的 `"sfx_boss_phase"` → `"sfx_bossphase"`，
  **53 个测试全绿，但运行时该音效静默哑掉**——正是 P0-B 想防的那类事故，只是换到了内核侧。
  出口②（技能）因引常量而无此问题，#4 已是双向守卫。
- **建议修法**（二选一，推荐 A）：
  - **A（推荐，零内核改动）**：扩展既有护栏脚本 `Assets/_Project/audio_syntax_check.py`，
    增加一条检查：正则从 `Assets/Scripts/Systems/Combat/Unity/CombatEventsUnity.cs` 抽取
    所有 `PlaySfx(...)` 实参中的字符串字面量（需处理三元表达式，`:124` 与 `:143`），
    与从 `AudioConfig.cs` Table 抽取的 key 集合做**双向差集**，任一方向非空即 FAIL。
    这样守卫跟着源文件走，不依赖任何副本。
  - **B**：在内核 `CombatEventsUnity.cs` 中提取 `public const string SFX_*` 常量并在两侧引用——
    **本期不建议**，会触碰 `Assets/Scripts/`，违反 P0-A 零回归约定，应留到独立的内核重构批次。

#### P1-2　暂停收敛路径（A-17 / A-18）无测试覆盖
- **现象**：`ApplyPauseConvergence()`（`AudioDirector.cs:601-630`）是 R-09 的核心实现，
  包含「菜单暂停 → 只抑制新起 + duck」与「终局 → 额外 StopAllVoices」两条**行为不同**的分支，
  53 个用例中无一触及。#50 `ToggleMute_FlipsTheFlag_BlocksNewSounds_AndIsReversible` 覆盖的是静音，
  #52 `Ambience_Duck_IsAPureSetter_SafeToCallEveryFrame` 只验 `Duck` 是纯 setter，
  **均未验证 `IsGameplayBlocked` 驱动的收敛决策**。
- **位置**：`AudioDirector.cs:601-630`（被测方法）；`Tests/P1_3_AudioTests.cs`（缺失用例）
- **根因**：该方法为 `private`，且依赖 `_bridge`（`CombatBridge`）的两个属性，构造成本略高，疑似被跳过。
- **建议修法**：新增 2 条 EditMode 用例（`_bridge` 可用一个最小 stub 或直接构造 `CombatBridge` 并设置状态）：
  1. `PauseConvergence_MenuPaused_SuppressesNewButDoesNotStopPlayingVoices_AndDucksAmbience`
     —— 断言 `_suppressNew == true`、`StopAllVoices` **未**被调用（可通过 voice 存活数间接断言）、`Duck(true)`。
  2. `PauseConvergence_RunOver_StopsAllVoicesImmediately_AndRecoversUnconditionallyWhenUnblocked`
     —— 断言终局全停，且随后 `IsGameplayBlocked` 转 false 时 `_suppressNew` 每帧无条件恢复为 false。

#### P1-3　节流窗口「随时间推进」这一关键行为在 EditMode 中结构性不可测
- **现象**：#41 `Gate1_Throttle_AllowsOnlyTheFirstHitWhileTimeIsFrozen` 的名字已自陈——
  它只能验证「时间冻结时第二次被挡」，**无法验证「时间推进后第二次应当放行」**。
  而架构 §2.5 真正担心的失效模式恰恰是后者（窗口不推进 → 连击第二击被永久误判为重复）。
- **位置**：`Tests/P1_3_AudioTests.cs` 方法 `Gate1_Throttle_AllowsOnlyTheFirstHitWhileTimeIsFrozen`；
  被测记账在 `AudioDirector.cs:873 float dt = Time.deltaTime;`
- **根因**：EditMode 下 `Time.deltaTime` 不推进，这是 Unity 测试框架的固有限制，非工程师疏漏。
- **建议修法**：在 `Tests/PlayMode/` 下补 1 条 PlayMode 用例
  `Throttle_WindowAdvancesWithDeltaTime_SoComboSecondHitIsNotSwallowed`：
  `Play(key)` → `yield return new WaitForSeconds(spec.ThrottleSec * 1.5f)` → 再 `Play(key)`，
  断言第二次成功占用了 voice。该目录已存在（`Runtime/Tests/PlayMode/`），无需新建 asmdef。
  **这是本报告中对线上风险防护价值最高的一条补充建议。**

### P2（建议）

#### P2-1　`AudioClipFactory` 的 static 缓存无 `[RuntimeInitializeOnLoadMethod]` 兜底
- **现象**：`_clips`（`AudioClipFactory.cs:59`）、`_failed`（`:67`）、`_warned`（`:70`）
  三个 `static readonly` 集合的**内容**是可变的，但全文件无 `RuntimeInitializeOnLoadMethod` 复位钩子
  （grep 该属性在 6 个音频文件中仅 `AudioConfig.cs:405` 一处）。
- **风险评估：低。** ① 正常退出 Play 时 `OnDestroy → TeardownAudio → ClearAll → AudioClipFactory.Clear()`
  （`CombatBridge.cs:1518 → :728 → AudioDirector.cs:560`）会清空三者；
  ② **与既有范式一致**——参照系统 `SpriteFactory.cs` 同样只有 `static void Clear()`（`:48`）
  而无 `RuntimeInitialize` 钩子。因此**不判 FAIL**。
- **残余风险**：关闭 Domain Reload 时，若 `OnDestroy` 未跑到 `TeardownAudio`（例如更早的语句抛异常），
  `_clips` 会在第二次 Play 时持有已被 Unity 销毁的 `AudioClip` 引用。
  该场景由 `Get()` 内的 Unity `==` 重载判空兜住（`:105-107` 注释明确说明「被 Destroy 的对象在 C# 层引用还在」），
  故最坏后果是重新合成一次，**不会 NRE**。
- **建议**：若希望与 `AudioConfig` 取齐防御等级，可加
  `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() => Clear();`
  —— 3 行，零副作用。**可选，不阻断。**

#### P2-2　7 个新增 `.cs` 文件缺少 `.meta`，未纳入版本控制
- **现象**：`ls` 显示 `AmbienceLayer.cs` / `AudioClipFactory.cs` / `AudioConfig.cs` / `AudioDirector.cs` /
  `SfxRecipes.cs` / `SfxSynth.cs` / `Tests/P1_3_AudioTests.cs` **均无对应 `.meta`**；
  而同目录下每一个既有 `.cs`（如 `HitFeedbackDirector.cs.meta`、`SpriteFactory.cs.meta`）都有。
  `git status --short` 中这 7 项为 `??`，`.meta` 未出现。
- **根因**：文件由工程师直接写入磁盘，**Unity 尚未导入过**（本机无 Unity），故 `.meta` 未生成。
- **建议**：用户首次在 Unity 中打开工程后，务必将自动生成的 7 个 `.meta` **一并提交**。
  否则其他机器导入时会各自生成不同 GUID，导致后续场景/预制体引用错乱。**这是交接必做项**，已列入 §5 清单。

#### P2-3　守卫1 会连带抑制未来的 UI 音效（前瞻性）
- **现象**：`AudioDirector.cs:359 if (_suppressNew) return;` 位于查表**之前**，对所有 key 一视同仁。
- **当前无害**：Table 13 行中零 UI 类 key（`AudioConfig.cs:628-650`），故本期无适用对象，判 N-A。
- **前瞻风险**：一旦后续迭代加入「暂停菜单按钮点击音」这类 key，它会在暂停态被静默吞掉——
  而暂停菜单恰恰是唯一需要它响的时刻，且**无任何报错**，排查成本高。
- **建议**：将来新增 UI 音效时，把守卫1 下移到 `TryGetSpec` 之后，并改为
  `if (_suppressNew && spec.Bus != Channel.Ui) return;`。当前**仅需在 `:358` 补一行注释**标注该约束即可。

---

## 5. 风险与盲区

### 5.1 本机限制导致无法覆盖的验收项

本机**无 Unity、无 dotnet/msbuild**，以下项目**未经实测**，一律标注为 UNVERIFIED，不计入 PASS：

| 未覆盖项 | 原因 | 影响面 |
|---|---|---|
| **编译是否通过** | 无 `dotnet`/`msbuild`/Unity | 全部。静态审查无法替代编译器。特别是 `AudioConfig.cs:641-644` 引用 `Xianxia.Combat.SkillConfig`，**依赖 `Xianxia.Unity.T2.asmdef` 已引用 `Xianxia.Combat`**——该引用关系本报告未核实 |
| **53 个 NUnit 用例的实际通过率** | 无法运行 EditMode 测试 | 本报告只做了代码审查，**未执行任何一个断言** |
| PRD **A-1 ~ A-19** 全部主观听感项 | 需真实播放 | 音色是否「像木石水布金石而非科幻合成器」（C-2）等，静态审查在原理上不可判定 |
| PRD **B-3**（`t1/t3_selfcheck.py`，指纹 `2.5294x`） | 主理人并行执行中 | 未在本报告重复跑，结论以主理人产出为准 |
| PRD **B-4**（开/关音频逐位一致对拍） | 需运行 Unity | **已由 P0-A 的静态证据强力支撑**（零内核改动 + 零 `SkillRng`/`PCG32` 引用 + 私有 `System.Random`），但未实测 |
| A-19 内存不增长（Profiler） | 需 Unity Profiler | 释放顺序与 `Clear()` 实现已静态验证正确（C-10），但泄漏需实测确认 |
| A-15 拔音频设备 | 需真实硬件操作 | 异常隔离代码路径已审查（`:341/:441`），行为未实测 |

### 5.2 交接给用户的可勾选清单

在本地 Unity 中请依次完成：

- [ ] **① 打开工程，确认编译零 error**（重点确认 `Xianxia.Unity.T2.asmdef` 已引用 `Xianxia.Combat`，否则 `AudioConfig.cs:641-644` 的 `SkillConfig` 引用会编译失败）
- [ ] **② 提交自动生成的 7 个 `.meta` 文件**（见 P2-2，交接必做）
- [ ] **③ 跑 EditMode 测试，确认 53/53 通过**（Window → General → Test Runner → EditMode）
- [ ] **④ 跑 `t1_selfcheck.py`（64/64）与 `t3_selfcheck.py`（9/9），确认指纹仍为 `2.5294x`**（PRD B-3）
- [ ] **⑤ 开/关音频各跑一次相同输入序列，比对战斗结果逐位一致**（PRD B-4，确定性红线）
- [ ] **⑥ 安静站着听环境衬底 60 秒**（PRD A-9，R-10 一票否决项）—— 静态验算显示接缝跳变仅 `0.0015`，远小于噪声本底，预期无咔哒；**请实听确认**
- [ ] **⑦ 按 ESC 暂停 / 死亡进结算 / 重开**，确认 A-17、A-18 的收敛行为（对应 **P1-2** 未覆盖的测试盲区）
- [ ] **⑧ 对着一个敌人狂点普攻**，确认连击第二击**能正常发声**（对应 **P1-3** 的结构性测试盲区，架构 §2.5 隐患的实测验证）
- [ ] **⑨ 围攻 + BOSS 冲击波同时发生**，确认冲击波音未被挤掉（PRD A-12，验证 `PickVoice` 豁免机制）
- [ ] **⑩ 反复 Clean And Rebuild 世界 10 次，看 Profiler 的 Audio 区域内存不增长**（PRD A-19）
- [ ] **⑪ 拔掉音频设备后点 Play 打一场**，确认不崩、Console 至多一条 warning（PRD A-15）

### 5.3 本报告的证据完整性声明

本报告全部结论均附「文件:行号」证据，无一条基于推测。
DSP 部分的数值结论来自可复现脚本 `docs/qa_dsp_verify.py`（可用
`C:/Users/Administrator/.workbuddy/binaries/python/versions/3.13.12/python.exe docs/qa_dsp_verify.py` 复跑）。
凡本机无法取证者，一律在 §5.1 显式标注 UNVERIFIED，未做「看起来没问题」式表述。

---

*报告完成。验收项 P0-A / P0-B / P0-C / P0-D / P1-E / P1-F 全部执行完毕，无遗漏项。*
