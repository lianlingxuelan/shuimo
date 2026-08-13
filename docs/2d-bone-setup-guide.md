# Unity 2D Animation 绑骨操作指引

- 针对：`shuimofeng` 工程 feature/2.5d 分支
- 目标：把 `Assets/_Project/Art/Characters/Heroine2D/heroine_base_open.png` 绑成 2D 骨骼角色，接入 `UnityBoneCharacterView`
- 环境：Unity 2022.3.62f3c1

---

## 0. 前置检查

1. 打开 Unity 工程后，等编译完成。
2. `Edit → Project Settings → Player → Other Settings → Scripting Define Symbols`
   - 加一项：`HAS_2D_BONE_PACKAGE`
   - 点 Apply，等编译。
3. Console 应无 `SpriteSkin` / `Animator` 相关编译错误。

---

## 1. 导入 Sprite

1. 打开文件夹 `Assets/_Project/Art/Characters/Heroine2D/`。
2. 选中 `heroine_base_open.png`。
3. Inspector 设置：
   - Texture Type = `Sprite (2D and UI)`
   - Sprite Mode = `Single`
   - Pixels Per Unit = `100`
   - Filter Mode = `Bilinear`
   - 点 **Apply**。

---

## 2. 打开 Skinning Editor

1. 保持选中 `heroine_base_open.png`。
2. Inspector 点 **Sprite Editor**。
3. Sprite Editor 窗口顶部下拉，从 `Sprite Editor` 切换为 **Skinning Editor**。

---

## 3. 画骨骼（推荐层级）

左侧工具栏点 **Create Bone**。

按这个顺序在角色身上点出骨骼链：

```
hip → spine → chest → neck → head
chest → shoulder_L → elbow_L → wrist_L
chest → shoulder_R → elbow_R → wrist_R
hip → thigh_L → shin_L → foot_L
hip → thigh_R → shin_R → foot_R
```

附加：
- 头顶向后画 2 段 `hair_back`
- 额头/鬓角画 2 段 `hair_front`
- 袖子各加 1 段 `sleeve_L` / `sleeve_R`
- 裙摆加 2 段 `skirt_L` / `skirt_R`

左侧 **Skeleton** 面板里把骨骼重命名为上面英文。

---

## 4. 蒙皮（Auto Weights）

1. 左侧点 **Auto Weights**（或 Generate Weights）。
2. 点 **Generate**，等进度条走完。
3. 拖动 hip / 手臂 / 腿，看身体是否自然跟着变形。
4. 如果某块变形太丑，切到 **Weight Brush** 手动刷权重。
5. 点顶部 **Apply** 保存。

---

## 5. 场景里创建测试角色

1. Hierarchy 右键 → `2D Object → Sprite`。
2. 命名 `HeroineBone_Test`。
3. Sprite 字段拖入 `heroine_base_open.png`。
4. 选中它，菜单 `Component → Sprite Skin`。
   - Unity 会自动识别你刚才绑的骨骼数据。
5. 再 `Component → Animator`。
6. 再 `Component → Unity Bone Character View`（在 `Xianxia.Unity.T2` 命名空间下）。

---

## 6. 创建 Animator Controller

1. Project 窗口 `Assets/_Project/Art/Characters/Heroine2D/` 右键 → `Create → Animator Controller`。
2. 命名 `HeroineBoneAnimator`。
3. 拖到 `HeroineBone_Test` 的 Animator 组件 `Controller` 槽里。

---

## 7. 录制 5 个动画片段

选中 `HeroineBone_Test`，菜单 `Window → Animation → Animation`。

### 7.1 idle
1. Animation 窗口点 **Create**，保存为 `heroine_idle.anim`。
2. 时间轴 0:00，给 hip 的 Position.y 打关键帧。
3. 时间轴 0:30（半秒处），hip 上移 2–3 像素，做呼吸感。
4. 停止录制。

### 7.2 walk
1. 再点 Create，保存 `heroine_walk.anim`。
2. 做腿部前后摆动 + hip 轻微上下（走路节奏）。

### 7.3 attack
1. Create `heroine_attack.anim`。
2. 关键帧：
   - 0:00 起手（剑后拉）
   - 0:10 挥出（上半身旋转 + 右臂前伸）
   - 0:30 收势

### 7.4 hit
1. Create `heroine_hit.anim`。
2. 关键帧：身体后仰 + 红色 tint（可选）。

### 7.5 death
1. Create `heroine_death.anim`。
2. 身体倒下 + alpha 淡出。

在 Animator 窗口里，这些 State 会自动出现。把 `heroine_idle` 设为默认（橙色）。

---

## 8. 配置 UnityBoneCharacterView

选中 `HeroineBone_Test`，Inspector 里 `Unity Bone Character View` 组件：

| 字段 | 填什么 |
|---|---|
| Clip Idle | `idle` |
| Clip Walk | `walk` |
| Clip Attack | `attack` |
| Clip Hit | `hit` |
| Clip Death | `death` |
| Hit Shake Duration | `0.22` |
| Hit Shake Amplitude | `0.18` |

---

## 9. 运行验证

> **一键自检（推荐先做）**：菜单 `Shuimo/2.5D/运行 骨骼绑定自检`（由 `BoneSetupSelfTest.cs` 提供）。
> 它会自动检查 6 项：编译符号、Sprite 导入、预制体存在、SpriteSkin 骨骼、Animator 5 状态、clip 字段填写，
> 并输出 PASS/WARN/FAIL 报告到 `docs/bone-setup-selftest-report.md`。**全 PASS 才说明绑骨无误**，再往下 PlayMode。

手动核对（自检 PASS 后可跳过）：

1. 点 **Play**。
2. 角色应播放 idle。
3. 用现有 `PlayerController` 移动，应切换 walk。
4. 按攻击键，应切换 attack。
5. 受击时应抖动。
6. Console 无红字。

---

## 10. 接入现有玩家系统

当前 `WorldBuilder.BuildPlayer` 是运行时创建 2D 帧动画角色。要做骨骼版玩家，有两种方式：

**方式 A（推荐）：替换玩家预制体**
1. 把 `HeroineBone_Test` 拖到 Project 窗口做成 Prefab：`Assets/_Project/Prefabs/HeroineBone.prefab`。
2. 在 `WorldBuilder.BuildPlayer` 里 `Instantiate(heroineBonePrefab)` 代替原来的 `new GameObject` + `HeroineAnimator`。
3. 确保 prefab 根节点有 `SpriteSkin` + `Animator` + `UnityBoneCharacterView`。

**方式 B（兼容）：运行时动态给现有对象加 SpriteSkin**
- 较复杂，不推荐；因为 SpriteSkin 需要编辑期绑定骨骼数据。

---

## 11. 形态切换（二期）

等基础骨骼跑通后再做：
1. 给 `heroine_xian.png` 和 `heroine_mo.png` 也各绑一套骨骼。
2. 做成两个独立 prefab：`HeroineBone_Xian`、`HeroineBone_Mo`。
3. 运行时按形态切换 Instantiate 的 prefab 即可。

或者更简单：保留一个骨骼角色，用材质颜色叠加 + 粒子特效区分仙/魔。
