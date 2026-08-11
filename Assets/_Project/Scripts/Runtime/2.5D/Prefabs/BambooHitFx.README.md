# BambooHitFx —— 砍竹粒子（墨迹 / 竹叶飞溅）

> 分支 `feature/2.5d` · 轮次 B · 配套 `../BambooVfx.cs`
> 设计依据：`docs/bamboo-2_5d-roundB-design.md` §3.3

---

## 1. 为什么这里没有直接手写 .prefab（以及怎么一键生成）

目标资产是 `BambooHitFx.prefab`（本目录）。**当前交付环境没有 Unity / dotnet**，
无法生成并校验合法的 `.prefab`：`ParticleSystem` 的 YAML 序列化包含几十个模块子
结构（`InitialModule` / `ShapeModule` / `EmissionModule` / `SizeBySpeedModule` / ...）
以及版本化字段（`serializedVersion`），手写出来无法编译校验，一旦格式不合法
Unity 会直接报导入错误、甚至污染 `Library/`。

**所以不手写 YAML，改让 Unity 自己序列化。** 交付两件东西：

1. `../BambooVfx.cs` 里的 `public static GameObject CreateFxTemplate(name, radius, depthAxis, leafMaterial)`
   —— 特效层级的唯一构造实现；
2. `Assets/_Project/Scripts/Editor/BambooHitFxPrefabBaker.cs`
   —— Editor 烘焙器，调上面这个方法搭好层级后
   `PrefabUtility.SaveAsPrefabAsset` 存盘。

> **一键生成：菜单 `Shuimo / 2.5D / 烘焙 BambooHitFx.prefab`**
> （想同时启用 Resources 兜底就选 `…（含 Resources 副本）`；
> 想推倒重来选 `清除 BambooHitFx 烘焙产物`。）

这样做的额外好处：**运行时兜底和烘焙出的 prefab 共用同一份构造代码**，
参数天然一致，不会出现「代码里一套、prefab 里另一套」的漂移。

对应实现见 `../BambooVfx.cs`：

| 方法 | 作用 |
|---|---|
| `CreateFxTemplate(name, r, axis, mat)` | **public static**，构造整棵特效层级（烘焙器与运行时共用） |
| `BuildRuntimeFx(pos, rot)` | 运行时兜底：调 `CreateFxTemplate` 后摆位，根节点名 `BambooHitFx_Runtime` |
| `BuildInkSplash(parent, r)` | 第一路：墨点飞溅（小、快、锥形喷出） |
| `BuildLeafFall(parent, r, axis, mat)` | 第二路：竹叶飘落（大、慢、带重力与自转） |
| `ConfigureRenderer(go, ps, mat)` | 复用竹叶水墨材质，缺失时退 `Sprites/Default` |

烘焙器额外做三件运行时不需要的事（见 `PrepareForBake`）：

- 存盘前 `Stop + Clear`，不把「播到一半的中间态」烘进 prefab；
- `main.playOnAwake = true`，让 prefab 单独拖进场景也能自播；
- 把运行时 `new Material(...)`（`HideFlags.DontSave`，存不进 prefab）
  换成持久化材质资产 `MAT_BambooHitFx.mat`，避免存出来是空材质 → 粉红。

---

## 2. 运行时的三级取用优先级

`BambooVfx.SpawnFx()` 按以下顺序取粒子，**代码无需改动即可平滑切换到正式 prefab**：

1. **Inspector 引用** —— `BambooVfx.inkLeafPrefab`，或由
   `BambooSceneContext.inkLeafPrefab` 在 `Configure()` 时统一下发。
2. **Resources 兜底** —— `Resources.Load<GameObject>("2.5D/BambooHitFx")`，
   即把 prefab 放一份到 `Assets/_Project/Resources/2.5D/BambooHitFx.prefab`。
   命中一次后会缓存到 `inkLeafPrefab`，不会每次都走 `Resources.Load`。
3. **运行时构造** —— 上面两条都没有时，调 `BuildRuntimeFx()`。
   可用 `allowRuntimeFxFallback = false` 关掉。

---

## 3. 生成正式 prefab 的操作步骤（用户在本地 Unity 内执行）

### 3.1 推荐：一键烘焙（不用进 PlayMode）

1. 等 Unity 编译完，菜单 **`Shuimo / 2.5D / 烘焙 BambooHitFx.prefab（含 Resources 副本）`**。
2. 控制台会打出两条落盘路径，Project 窗口自动 ping 到新资产：
   - `Assets/_Project/Scripts/Runtime/2.5D/Prefabs/BambooHitFx.prefab`
   - `Assets/_Project/Resources/2.5D/BambooHitFx.prefab`
   - 附带材质 `Prefabs/MAT_BambooHitFx.mat`
3. 把 prefab 拖到场景里 `BambooSceneContext` 的 **`inkLeafPrefab`** 字段
   （烘了 Resources 副本的话这步可以省，`SpawnFx` 会自己 `Resources.Load` 到）。
4. 之后运行时构造分支不再被调用（prefab 分支优先级最高）。

### 3.2 备选：从 PlayMode 里手动拖

1. 进入 PlayMode，砍一根竹子，Hierarchy 里会出现 `BambooHitFx_Runtime`。
2. 选中它拖进本目录，命名为 `BambooHitFx.prefab`。
3. **注意**：这条路存出来的 prefab，`playOnAwake` 是 `false`、材质是运行时
   临时材质。`SpawnFx` 里已经补了一刀 `ps.Play()` 兜底所以仍会出粒子，
   但材质需要你手动重指一个持久化材质，否则会是粉红。
   —— 这也是推荐走 3.1 的原因。

> 建议顺手把粒子贴图换成水墨笔触图（`ParticleSystemRenderer.sharedMaterial`），
> 目前占位复用的是竹叶材质 / `Sprites/Default`，是纯色方块。

---

## 4. 顿帧同步（红线相关）

`ParticleSystem` 内部按 `Time.deltaTime` 推进，**不认 `FeedbackClock`**。
因此 `BambooVfx.SyncFxPause(frozen)` 在 `FeedbackClock.Delta == 0` 时
显式调 `ps.Pause(true)`、恢复时调 `ps.Play(true)`，让粒子与竹子的晃动/倾倒
在同一帧一起停、一起走。

严格遵守：

- 只调 `ParticleSystem` 自己的 API；
- **不写** `Time.timeScale`；
- **不写** `FeedbackClock.Frozen`（唯一写入者仍是 `HitFeedbackDirector`）；
- 按已拍板的 **D6**，竹子只冻自身动画，**不扩 `HitFeedbackDirector`**。

---

## 5. 两路粒子的占位参数（便于在 Inspector 内继续调）

| 参数 | InkSplash（墨点） | LeafFall（竹叶） |
|---|---|---|
| duration | 0.5 | 1.2 |
| startLifetime | 0.25 – 0.55 | 0.8 – 1.5 |
| startSpeed | `r`×6 – `r`×16 | `r`×1.5 – `r`×5 |
| startSize | `r`×0.18 – `r`×0.55 | `r`×0.8 – `r`×1.8 |
| burst | 18 – 26 | 5 – 9 |
| shape | Cone 32°，radius `r`×0.4 | Sphere，radius `r`×1.2 |
| 颜色 | 深墨 `#0F1712` → `#293823` | 竹青 `#26381F` → `#47572F` |
| 额外 | sizeOverLifetime 衰减 | forceOverLifetime 沿 −深度轴下落 + 自转 |

其中 `r` = `BambooSceneContext.trunkRadius`（默认 14，世界单位 = px，PPU=1）。
