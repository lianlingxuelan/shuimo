# 本地验证 Checklist · 水墨风仙侠 RPG（shuimofeng）

> **为什么需要这份清单**：本开发环境无 Unity / 无 dotnet，所有 Unity 侧行为（编译、Test Runner、PlayMode 手感）必须由你在本地验证。代码侧 P0 六项 + P1-2 + P1-6 已全部交付，但**未经真机验证前不能视为完成**。
>
> **前置**：`git clone git@github.com:lianlingxuelan/shuimo.git`（或拉最新），用 **Unity 2022.3.62** 打开。

---

## 一、编译
- [ ] Console 无红色编译错误
- [ ] 若有编译错 → 复制完整报错发回，走 BugFix 快捷路径

## 二、Test Runner · EditMode
- [ ] `Window → General → Test Runner`，切 **EditMode** 标签 → Run All
- [ ] 记录 passed / failed 数量
- [ ] 预期：PI11 已绿（第4轮 EditMode LoadScene 修复）；P1_2 合并降级修复后应大部分转绿
- [ ] **已知遗留红（非阻断）**：`P2_01~03` + `P4_01~02` 共 6 条（历史已知，待独立跟进，不影响主玩法）

## 三、Test Runner · PlayMode
- [ ] 切 **PlayMode** 标签 → Run All
- [ ] 重点验：`P0_2_P0_4_PlayModeTests`（第6轮 asmdef 迁移到 `Tests/PlayMode/` 后应真绿）、`P1_2_HitFeedbackTests`（43 条）
- [ ] ⚠️ 存量隐患：`P0_2_P0_4_PlayModeTests.cs` L73 / L377 仍裸 `SceneManager.LoadScene` 无 `isPlaying` 守卫（与 PI11 同类），若仍红可独立跟进

## 四、手动 PlayMode · 六键全链路
- [ ] **WASD**：八方向移动（含斜向）
- [ ] **左键**：普攻（伤害数字飘出 + 顿帧 + 屏震）
- [ ] **K / L**：技能 1 / 技能 2
- [ ] **Shift**：闪避（i-frame 0.20s）
- [ ] **ESC**：暂停 / 继续 / 返回主菜单
- [ ] **R**：重开（确认跳过主菜单、世界重建、角色可见）
- [ ] **主菜单**：开始游戏 / 退出
- [ ] **操作引导**：开局静态说明 + HUD 常驻按键小字

## 五、视觉验证
- [ ] 受击反馈：顿帧 / 屏震 / 分阵营闪白 / 伤害飘字（P1-2）
- [ ] 粒子 prefab 挂载目视（美术阶段特效框架）
- [ ] 女主 39 帧精灵动画（walk / attack / dodge）
- [ ] **已知动画帧问题（需补图）**：
  - `attack` 6 帧：只画剑缺身体（攻击隐身）
  - `walk_side` / `walk_up`：站桩换皮（一拐一拐）
  - `walk_down` 第 4 帧：下沉（站起来趴下）
  - `dodge` 4 帧：帧漂移

## 六、验证后决策点
| 你的判断 | 下一步 |
|---|---|
| 2D 版本跑通，想直接搞 2.5D | 启动技术验证：竹林 3D 场景 + Spine 角色 + 砍竹子特效（2-3 轮出结论） |
| 2D 版本先收尾 | 本地验证收尾 + 回写 feature-closure-plan 三态 |
| 编译 / 行为报错 | 发回报错 → BugFix 路径 |
| 想先看 2.5D 五项决策 | 拍板 Spine vs DragonBones / URP vs Built-in / 相机视角 / 水墨 Shader / 是否保留 2D 版 → 进入技术验证阶段 |

---

**一句话**：验证通过前，齐活林不盲目推新功能（P0 已全交付，再写是库存积压）。你验证完告诉我结果，我据此决定走 BugFix 还是启动 2.5D 技术验证。
