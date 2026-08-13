# 人物资产契约（Unity 2D Animation 骨骼路线）

- 创建：2026-08-13
- 更新：2026-08-13（采用用户去背景图）
- 路线决策：人物 2.5D 骨骼采用 **Unity 2D Animation**（`com.unity.2d.animation`，已随 `com.unity.feature.2d` 间接安装，**零授权**），不引入 Spine 商业运行时。
- 目的：让外部美术 / AI 生成的人物资产「即插即用」接入 `CharacterView` 体系，并与全场 `FeedbackClock` 顿帧同步冻结。

---

## 1. 当前可用资产（已落盘工程目录）

路径：`Assets/_Project/Art/Characters/Heroine2D/`

| 文件名 | 来源 | 用途 |
|---|---|---|
| `heroine_base_open.png` | 用户去背景图 `四肢张开去背景.jpg` 转透明 | **首选绑骨母图**：四肢张开、正面、无遮挡，最适合 Skinning Editor 画骨骼 |
| `heroine_base_front.png` | 用户去背景图 `正常正视图去背景.jpg` 转透明 | 默认待机/行走 pose（手持剑，正面） |
| `heroine_base_side.png` | 用户去背景图 `正常侧视图去背景.jpg` 转透明 | 侧视图参考 / 攻击动画参考 |
| `heroine_xian.png` | 用户去背景图 `入仙去背景.jpg` 转透明 | 入仙形态（青绿仙气） |
| `heroine_mo.png` | 用户去背景图 `入魔去背景.jpg` 转透明 | 入魔形态（紫黑邪气） |
| `heroine_tri_front.png` | 用户去背景图 `三视图正面去背景.jpg` 转透明 | 三视图正面参考 |
| `heroine_tri_side.png` | 用户去背景图 `三视图侧面去背景.jpg` 转透明 | 三视图侧面参考 |
| `heroine_tri_back.png` | 用户去背景图 `三视图背面去背景.jpg` 转透明 | 三视图背面参考 |

> 所有图已从 JPG 黑底转成**透明底 PNG**，高度统一 1024 像素，可直接拖入 Unity 做 Sprite。

---

## 2. 形态玩法规划（正常 / 入仙 / 入魔）

当前实现策略：
- **一期（先跑通）**：只给 `heroine_base_open.png` 绑一套骨骼，做 `{idle, walk, attack, hit, death}` 五个动画。形态切换先不做。
- **二期**：把 `heroine_xian.png` / `heroine_mo.png` 同样各绑一套骨骼，做 3 个 SpriteSkin 预制体，由 `PlayerController` 或形态系统运行时切换（或更简单：用材质色调叠加 + 粒子特效表现仙/魔氛围）。

形态切换代码预留：可在 `UnityBoneCharacterView` 上加 `public SpriteSkin xianSkin / moSkin`，未来通过事件切换。

---

## 3. 骨骼层级规范

推荐在 `heroine_base_open.png` 上按此层级画骨：

```
root → hip → spine → chest → neck → head
              ├ shoulder_L → elbow_L → wrist_L
              ├ shoulder_R → elbow_R → wrist_R
              ├ thigh_L → shin_L → foot_L
              └ thigh_R → shin_R → foot_R
```

附加：
- `hair_back` / `hair_front`：头发加 2–3 段骨骼做飘动
- `sleeve_L` / `sleeve_R`：袖子加骨骼，挥剑时带动
- `skirt` / `robe`：长裙加 2 段骨骼，走路/攻击时摆动

---

## 4. 动画片段（与 `CharacterAnimState` 对齐）

必须提供：`idle` / `walk` / `attack` / `hit` / `death`
建议补充：`dodge` / `hurt`

片段用 Unity **Animator 状态机**管理，状态名即上面英文。
新增的 `UnityBoneCharacterView` 按映射播放，并通过 `FeedbackClock.Delta` 同步全场冻结。

---

## 5. 导入设置

- Texture Type = `Sprite (2D and UI)`
- Sprite Mode = `Single`
- Pixels Per Unit = `100`（所有 Sprite 一致）
- Filter Mode = `Bilinear`
- Compression = 项目默认（ASTC / ETC2）

---

## 6. 接入代码（已实现）

- 已新增 `UnityBoneCharacterView : CharacterView`（位于 `Assets/_Project/Scripts/Runtime/2.5D/CharacterView.cs`）。
- `CharacterView.ResolveOn` 已加分支：优先级 `Spine(SkeletonAnimation)` > `UnityBone(SpriteSkin)` > `Sprite`。探测到 `SpriteSkin` 即走骨骼视图；缺则回落 `SpriteCharacterView`。
- 推进闸门：用 `Animator.updateMode = Manual` + `Animator.Update(FeedbackClock.Delta)` 手动推进，与 Spine 分支同口径实现顿帧同步；受击对 `rootBone` 做基于 `dt` 的局部抖动；朝向翻转翻 `rootBone.localScale.x` 符号。
- 零破坏：`AttackController` / `PlayerController` / `CombatBridge` **未改**。

### 启用步骤（用户本地 Unity）
1. `Player Settings > Scripting Define Symbols` 追加 `HAS_2D_BONE_PACKAGE`。
2. 确认 `Xianxia.Unity.T2.asmdef` 已引用 `UnityEngine.U2D.Animation` 模块（已加）。
3. 把带 `SpriteSkin` + `Animator` 的角色预制体挂到 2.5D 角色根节点，`ResolveOn` 会自动接管。

详见配套文档：`docs/2d-bone-setup-guide.md`

---

## 7. 打击感增强（代码层已支持）

`UnityBoneCharacterView` 已内置：
- 受击抖动（`hitShakeDuration` / `hitShakeAmplitude`）
- 状态切换（`PlayState`）
- 顿帧同步（`FeedbackClock.Delta`）

额外打击感可在后续加：
- 受击红色 tint 闪（`SpriteRenderer.color` 插值）
- 攻击前摇蓄力缩放
- 命中白色闪光 + 剑气特效
- 镜头微震（`CameraController`）

---

## 8. 自测（Route A 扩展）

校验：① 预制体含 `SpriteSkin` + `Animator`；② 动画片段名集合 ⊇ `{idle, walk, attack, hit, death}`；
③ PlayMode 下状态切换与顿帧同步；④ 单角色缺骨骼组件时自动回落 2D Sprite 不报错。
