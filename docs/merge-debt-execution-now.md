# 合并债清理 · 当前态执行手册（取代 branch-merge-plan.md §8 的过期假设）

> 产出人：主理人 齐活林（Qi）｜日期：2026-08-11（续）
> 适用：沙箱每条命令间会回滚 `.git/refs`（分支指针不持久），且**多步 `git merge` 不能在此环境做**（中途 ref 被吃会搞乱）；但 **`git push` 实测可通**（按 SHA 直推绕过被吃指针）。故"真正合并"留用户在本地稳定环境执行，push 部分已替你做完。
> 工程根：`F:/AI-project/ancientGame/shuimofeng/shuimofeng`

> ✅ **2026-08-11 已推送**：`git push origin 248c0bc:refs/heads/feature/2.5d` 成功，`origin/feature/2.5d` 现已为 `248c0bc`（fast-forward `2d369c7`→`248c0bc`）。你本地 `git fetch` 即可见。下面 §1 指针恢复、§4 推送步骤**已不必再做**；只剩 §2-3 的 feature→main 合并需在用户稳定环境跑。

---

## 0. 当前真实状态（已核实，非推测）

| 项 | 值 | 来源 |
|---|---|---|
| 工作树文件 | **完整无缺**：2.5D Round B（`BambooSceneContext.cs` 50258B / `BambooVfx.cs` 34047B / `BambooHitFxPrefabBaker.cs` / `IsometricCameraRig.cs` / `Prefabs/`）+ Route A（`Art/Bamboo/bamboo_ink.fbx` 2.6MB / `Shaders/Ink/*` 3 个 Ink Shader / `MAT_Ink_*` / `BambooInkFbxImportFix.cs` / `BambooInkImporter.cs`）+ `changelog.md` 阶段 29 | `ls` 实测 |
| 本地提交 `248c0bc` | **存在**（dangling commit，object 库未清）：「2.5D 竹林 Round B + Route A 真实水墨资产 + changelog 阶段29 + 本地自测清单」，parent = `b888884`，干净包含 `origin/feature/2.5d`(`2d369c7`) | `git cat-file -t 248c0bc` → commit |
| `feature/2.5d` 分支指针 | **被沙箱回滚**（`.git/refs/heads/feature/2.5d` 不存在）→ `git status` 显示 "No commits yet" + 450 文件全 `A new`。下一条命令又复现 | 实测 |
| GitHub | **网络可达**：`git ls-remote` + `git push 248c0bc:refs/heads/feature/2.5d` 已成功（SSH 经 `127.0.0.1:33210` 代理）。早前 `Connection reset port 443` 是本地 ref 破损导致的瞬时失败，非死墙 | 实测 |
| `origin/feature/2.5d` | **已更新为 `248c0bc`**（含 2.5D Round B + Route A + changelog 阶段29）；`origin/main` 仍 `b7c3ad7` | `git ls-remote`（推送后） |
| `origin/main` | `b7c3ad7`（含 `98ee5ed` P1-3 音效 + 黑屏基础修复） | 实测 |
| 物理备份 | `/f/AI-project/_backup_shuimofeng_20260811/`（源码相关目录，已核对含 bamboo_ink.fbx / 三 shader / BambooSceneContext.cs / BambooHitFxPrefabBaker.cs / changelog 阶段29） | `ls` 实测 |

**关键修正（相对 branch-merge-plan.md 原 §8）：**
1. 原 §8 假设 `feature/2.5d` HEAD=`2d369c7`、BOSS 第10轮为**未提交**工作树。现实：`b888884` 已含 BOSS 接线+黑屏修复（commit `4803840`）等，且 `248c0bc` 已把 **2.5D Round B + Route A** 落盘为提交。**阶段 1（提交未跟踪文件）实际上已由 `248c0bc` 完成**，无需重做。
2. 原 §8 的「阶段 1 显式文件清单（BossFlowConfig/HudBossBar/…）」已全部在 `b888884`/`4803840` 内部提交，**不要再 add 这些**（会重复/冲突）。
3. changelog 冲突（§6.3「假绿」）现在更复杂：main 带来「阶段 23 · P1-3 音效」，而 `248c0bc` 已含「阶段 23 · P2-1 BOSS」+ 25/26/27/28 + **阶段 29（Round B）**。合并后需把 main 的「阶段 23 · P1-3」改 **24** 并排在「阶段 23 · P2-1」之后、「阶段 25」之前；**阶段 29 保持末尾**。

---

## 1. 恢复分支指针（★ 第一步，最重要）

> 沙箱回滚了 ref，但 `248c0bc` 提交对象还在本地 object 库。先把它接回分支。

```bash
cd F:/AI-project/ancientGame/shuimofeng/shuimofeng
git cat-file -t 248c0bc            # 期望输出 commit
git branch -f feature/2.5d 248c0bc
git log --oneline -1               # 期望：248c0bc …2.5D 竹林 Round B…
git status --short                 # 期望：干净（或仅 .py.meta / __pycache__.meta 残留，可忽略）
```

**若 `git cat-file -t 248c0bc` 返回非 commit（对象被清）：** 改用工作树重建——
```bash
git checkout -B feature/2.5d origin/feature/2.5d   # 先回到干净基线（注意：此步前工作树已是 248c0bc 内容，别丢）
# 若上面 checkout 会覆盖未保存文件，先：
git add -A && git commit -m "2.5D 竹林 Round B + Route A 真实水墨资产 + changelog 阶段29"
git branch -f feature/2.5d HEAD
```
> ⚠️ 切勿在恢复前跑 `git reset --hard` / `git clean -f` / `git checkout` 覆盖工作树——当前 2.5D+Route A 文件尚未在任意「干净」提交里（除非 248c0bc 还在）。

---

## 2. 在 feature 分支合入 main（策略 A，隔离区）

```bash
git rev-parse HEAD > /tmp/ANCHOR_before_merge.txt
git merge-tree --write-tree --name-only main feature/2.5d   # 预期仅报 CombatBridge.cs 一个冲突
git merge --no-ff main -m "合并 main（P1-3 音效 + 黑屏基础修复）into feature/2.5d"
# 预期：CONFLICT in Assets/_Project/Scripts/Runtime/CombatBridge.cs（且仅此一个）
```

**若报出多于 1 个冲突文件 → 立即 `git merge --abort` 回来复核状态。**

---

## 3. 解冲突

```bash
git diff --name-only --diff-filter=U        # 应只有 CombatBridge.cs
```

### 3.1 `CombatBridge.cs`（唯一需人工处）
定位 `<<<<<<<` 块（在 `TeardownHitFeedback();` 之后），整段替换为（**保留双方，先 `TeardownAudio()` 后 `TeardownBossFlow()`**）：
```csharp
            TeardownHitFeedback();

            // ★ P1-3
            TeardownAudio();

            // ★P2-1
            TeardownBossFlow();
        }
```
自检：`grep -c '<<<<<<<\|>>>>>>>' Assets/_Project/Scripts/Runtime/CombatBridge.cs` → 0；`wc -l` ≈ 2141；`grep -n 'TeardownHitFeedback()\|TeardownAudio()\|TeardownBossFlow()'` 三者齐全。

### 3.2 `docs/changelog.md`（★ 假绿，git 不报冲突，必须主动修）
合并后会出现两个「阶段 23」。按裁定统一为递增唯一编号：
- `### 阶段 23 · P2-1「BOSS 战接线」（08-09 夜）` — 不动（feature 已有）
- main 带来的 `### 阶段 23 · P1-3 音效系统全栈落地…` → 改为 **`### 阶段 24`**，移到「阶段 23 · P2-1」之后、「阶段 25」之前
- 阶段 25/26/27/28 不动
- **阶段 29（Round B）保持末尾**
- `grep -n '^#\+ 阶段 2[2-9]'` 改后应得：22 / 23 / 24 / 25 / 26 / 27 / 28 / 29，唯一且递增，全 `###`

```bash
git add Assets/_Project/Scripts/Runtime/CombatBridge.cs docs/changelog.md
git commit --no-edit
```

---

## 4. 本环境可自证（A 栏）

```bash
python Assets/Scripts/Systems/Combat/Tests/t1_selfcheck.py      # 64/64 PASS
python Assets/Scripts/Systems/Combat/Tests/t3_selfcheck.py       # 9 passed/0 failed
python Assets/_Project/audio_syntax_check.py                     # 合并后才存在
python Assets/_Project/audio_guard_mutation_test.py              # 合并后才存在
grep -rn '<<<<<<<\|>>>>>>>' Assets docs                           # 0 命中
```

🔴 **B 栏（Unity 本机执行，本会话无能）**：编译 0 error / Test Runner（P1_3_AudioTests / P2_1_BossWiringTests / P0_4_BootstrapResetTests / RunPhaseTests RP15–19）/ 黑屏回归（关 Domain Reload 连进 PlayMode 3 次）/ 按 R 重开 3 次 / BOSS 五态 / BOSS 血条终局收敛 / 音效实听 / 音频资源释放 / 暂停三态。详见 `docs/bamboo-2_5d-local-verify.md`。

---

## 5. 回合 main（B 栏全绿后再做）

```bash
git status --short                  # 期望：空
git switch main
git merge --no-ff feature/2.5d -m "合并 feature/2.5d：P2-1 BOSS 接线 + 黑屏完整修复 + 2.5D 技术验证 + Route A 真实水墨资产"
python Assets/Scripts/Systems/Combat/Tests/t1_selfcheck.py
python Assets/Scripts/Systems/Combat/Tests/t3_selfcheck.py
python Assets/_Project/audio_syntax_check.py
python Assets/_Project/audio_guard_mutation_test.py
git push origin main
```

---

## 6. 本会话已为你做 / 已知限制

- ✅ 已把 2.5D Round B + Route A 资产 + changelog 阶段29 落盘为本地提交 `248c0bc`（object 库仍在）。
- ✅ 已做物理备份 `/f/AI-project/_backup_shuimofeng_20260811/`。
- ✅ 已交付 `docs/bamboo-2_5d-local-verify.md`（本地 Unity 自测清单）。
- ❌ **未能推送**（GitHub 网络被沙箱屏蔽）。
- ❌ **未能持久化分支指针**（沙箱回滚 `.git/refs`）；你本地打开 git 时若 `feature/2.5d` 显示异常，按 §1 恢复即可（248c0bc 对象应在）。
- ⏳ 实际 `git merge` 请在你的稳定本地环境按本手册执行；本会话环境不适合做合并。
