# Ink Minimap and Navigation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a permanent water-ink minimap, reliable world-position feedback, boundary feedback, and recognizable first-chapter road landmarks.

**Architecture:** Pure rules handle world-to-map projection, marker visibility, boundary detection, and landmark placement. Thin Unity adapters render a dedicated overlay Canvas, register scene entities explicitly, and consume the player's existing clamped movement without changing combat or camera behavior.

**Tech Stack:** Unity 2022.3 LTS, C# runtime scripts, UnityEngine.UI legacy Text/Image, NUnit EditMode tests, existing `WorldBuilder`, `PlayerController`, `EnemyNpcSpawner`, and `FirstChapterLayout`.

**Spec:** `docs/superpowers/specs/2026-08-27-ink-minimap-and-navigation-design.md`

## Global Constraints

- Preserve the existing illustrated water-ink backdrop and current player movement speed.
- Do not modify combat-kernel scheduling, `Time.timeScale`, or camera-follow behavior.
- The minimap must use a dedicated Screen Space Overlay Canvas above ordinary HUD and below modal menus.
- Do not search the whole scene every frame; entities register and unregister markers explicitly.
- Bamboo and ordinary rocks are scenery only and never appear as minimap markers.
- Invalid world dimensions must degrade to a safe “地图绘制中” state without NaN values or exceptions.
- Keep the central road, spawn area, encounter area, dialogue area, and shop entrance visually clear.
- New behavior is implemented test-first and committed in focused increments.

---

### Task 1: World-to-minimap projection and marker visibility rules

**Files:**
- Create: `Assets/_Project/Scripts/Runtime/Navigation/MinimapProjection.cs`
- Create: `Assets/_Project/Scripts/Runtime/Navigation/MinimapVisibilityRules.cs`
- Test: `Assets/_Project/Scripts/Runtime/Tests/MinimapProjectionTests.cs`
- Test: `Assets/_Project/Scripts/Runtime/Tests/MinimapVisibilityRulesTests.cs`

**Interfaces:**
- Consumes: world position, world size, icon padding, player position, marker position, marker kind, and reveal radius.
- Produces: `Vector2 MinimapProjection.Project(Vector2 worldPosition, Vector2 worldSize, float iconPadding01)` and `bool MinimapVisibilityRules.ShouldShow(MinimapMarkerKind kind, Vector2 player, Vector2 marker, float revealRadius)`.

- [ ] **Step 1: Write the failing projection tests**

```csharp
[Test]
public void Project_MapsWorldCornersAndCenterIntoNormalizedMapSpace()
{
    Assert.AreEqual(new Vector2(0.05f, 0.05f), MinimapProjection.Project(Vector2.zero, new Vector2(1000f, 800f), 0.05f));
    Assert.AreEqual(new Vector2(0.5f, 0.5f), MinimapProjection.Project(new Vector2(500f, 400f), new Vector2(1000f, 800f), 0.05f));
    Assert.AreEqual(new Vector2(0.95f, 0.95f), MinimapProjection.Project(new Vector2(1000f, 800f), new Vector2(1000f, 800f), 0.05f));
}

[Test]
public void Project_InvalidWorldSizeReturnsMapCenterWithoutNaN()
{
    Vector2 result = MinimapProjection.Project(new Vector2(10f, 20f), Vector2.zero, 0.05f);
    Assert.AreEqual(new Vector2(0.5f, 0.5f), result);
    Assert.IsFalse(float.IsNaN(result.x) || float.IsNaN(result.y));
}
```

- [ ] **Step 2: Run the projection fixture and verify RED**

Run in Unity Test Runner: `EditMode > Xianxia.Unity.T2.Tests.MinimapProjectionTests`.

Expected: compile failure because `MinimapProjection` does not exist.

- [ ] **Step 3: Implement minimal projection rules**

```csharp
public static Vector2 Project(Vector2 worldPosition, Vector2 worldSize, float iconPadding01)
{
    if (worldSize.x <= 0f || worldSize.y <= 0f) return new Vector2(0.5f, 0.5f);
    float pad = Mathf.Clamp(iconPadding01, 0f, 0.49f);
    float x = Mathf.Lerp(pad, 1f - pad, Mathf.Clamp01(worldPosition.x / worldSize.x));
    float y = Mathf.Lerp(pad, 1f - pad, Mathf.Clamp01(worldPosition.y / worldSize.y));
    return new Vector2(x, y);
}
```

- [ ] **Step 4: Write visibility tests and verify RED**

```csharp
[TestCase(MinimapMarkerKind.Player, 9999f, true)]
[TestCase(MinimapMarkerKind.ChapterEnemy, 9999f, true)]
[TestCase(MinimapMarkerKind.QuestTarget, 9999f, true)]
[TestCase(MinimapMarkerKind.Enemy, 120f, true)]
[TestCase(MinimapMarkerKind.Enemy, 121f, false)]
public void ShouldShow_RespectsPermanentAndLocalMarkers(MinimapMarkerKind kind, float distance, bool expected)
{
    bool actual = MinimapVisibilityRules.ShouldShow(kind, Vector2.zero, Vector2.right * distance, 120f);
    Assert.AreEqual(expected, actual);
}
```

Expected: compile failure because marker kinds and visibility rules do not exist.

- [ ] **Step 5: Implement minimal visibility rules and run both fixtures GREEN**

Permanent kinds return true. Ordinary enemies return true only when squared distance is within `revealRadius * revealRadius`. NPC, shop, and building markers return true.

- [ ] **Step 6: Commit**

```powershell
git add -- Assets/_Project/Scripts/Runtime/Navigation/MinimapProjection.cs Assets/_Project/Scripts/Runtime/Navigation/MinimapVisibilityRules.cs Assets/_Project/Scripts/Runtime/Tests/MinimapProjectionTests.cs Assets/_Project/Scripts/Runtime/Tests/MinimapVisibilityRulesTests.cs
git commit -m "feat: add minimap projection rules"
```

### Task 2: Explicit marker registration lifecycle

**Files:**
- Create: `Assets/_Project/Scripts/Runtime/Navigation/MinimapMarker.cs`
- Create: `Assets/_Project/Scripts/Runtime/Navigation/MinimapMarkerRegistry.cs`
- Test: `Assets/_Project/Scripts/Runtime/Tests/MinimapMarkerRegistryTests.cs`
- Modify: `Assets/_Project/Scripts/Runtime/2.5D/EnemyNpcSpawner.cs:428`

**Interfaces:**
- Consumes: `MinimapMarkerKind`, stable marker id, display name, `Transform`, and permanent-visibility flag.
- Produces: `MinimapMarkerRegistry.Markers`, `Changed` event, `Register(MinimapMarker)`, `Unregister(MinimapMarker)`, and `MinimapMarker.Configure(...)`.

- [ ] **Step 1: Write registry lifecycle tests**

```csharp
[Test]
public void Registry_RegisterThenUnregisterMaintainsUniqueLiveMarkers()
{
    GameObject go = new GameObject("marker");
    MinimapMarker marker = go.AddComponent<MinimapMarker>();
    marker.Configure(MinimapMarkerKind.Npc, "guide", "竹市引路人", true);

    MinimapMarkerRegistry.Register(marker);
    MinimapMarkerRegistry.Register(marker);
    Assert.AreEqual(1, MinimapMarkerRegistry.Markers.Count);

    MinimapMarkerRegistry.Unregister(marker);
    Assert.AreEqual(0, MinimapMarkerRegistry.Markers.Count);
    Object.DestroyImmediate(go);
}
```

- [ ] **Step 2: Run fixture and verify RED**

Run: `EditMode > Xianxia.Unity.T2.Tests.MinimapMarkerRegistryTests`.

Expected: compile failure because marker lifecycle types do not exist.

- [ ] **Step 3: Implement registry and component lifecycle**

`MinimapMarker.OnEnable` registers, `OnDisable` unregisters, and `OnDestroy` unregisters defensively. Registry uses a `List<MinimapMarker>` with duplicate protection, removes null entries on read, emits `Changed` only when membership changes, and clears static state through `RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)`.

- [ ] **Step 4: Add markers at entity creation sites**

In `EnemyNpcSpawner.SpawnEnemy`, configure ordinary enemies as `Enemy` and the scripted first-chapter enemy as `ChapterEnemy`. In `SpawnNpc`, configure `Npc` with the existing `InteractableMarker.displayName`. Do not add scene scans.

- [ ] **Step 5: Run registry tests GREEN and perform static compile check**

Run the fixture and confirm no duplicate markers after disable/enable. Then run `git diff --check` on the four touched files.

- [ ] **Step 6: Commit**

```powershell
git add -- Assets/_Project/Scripts/Runtime/Navigation/MinimapMarker.cs Assets/_Project/Scripts/Runtime/Navigation/MinimapMarkerRegistry.cs Assets/_Project/Scripts/Runtime/Tests/MinimapMarkerRegistryTests.cs Assets/_Project/Scripts/Runtime/2.5D/EnemyNpcSpawner.cs
git commit -m "feat: register minimap scene markers"
```

### Task 3: Boundary detection and player feedback signal

**Files:**
- Create: `Assets/_Project/Scripts/Runtime/Navigation/BoundaryFeedbackRules.cs`
- Create: `Assets/_Project/Scripts/Runtime/Navigation/WorldBoundaryFeedback.cs`
- Test: `Assets/_Project/Scripts/Runtime/Tests/BoundaryFeedbackRulesTests.cs`
- Modify: `Assets/_Project/Scripts/Runtime/PlayerController.cs:304`

**Interfaces:**
- Consumes: attempted position, clamped position, input direction, current unscaled time, and last feedback time.
- Produces: `BoundaryFeedbackRules.WasBlocked(...)`, `BoundaryFeedbackRules.CanNotify(...)`, `PlayerController.BoundaryBlockedThisFrame`, `PlayerController.BoundaryDirection`, and `WorldBoundaryFeedback.Triggered`.

- [ ] **Step 1: Write failing boundary tests**

```csharp
[Test]
public void WasBlocked_RequiresMovementInputAndAClampedAxis()
{
    Assert.IsTrue(BoundaryFeedbackRules.WasBlocked(new Vector2(110f, 50f), new Vector2(100f, 50f), Vector2.right));
    Assert.IsFalse(BoundaryFeedbackRules.WasBlocked(new Vector2(100f, 50f), new Vector2(100f, 50f), Vector2.right));
    Assert.IsFalse(BoundaryFeedbackRules.WasBlocked(new Vector2(110f, 50f), new Vector2(100f, 50f), Vector2.zero));
}

[Test]
public void CanNotify_ThrottlesContinuousBoundaryPressure()
{
    Assert.IsTrue(BoundaryFeedbackRules.CanNotify(5f, 3f, 1.5f));
    Assert.IsFalse(BoundaryFeedbackRules.CanNotify(4f, 3f, 1.5f));
}
```

- [ ] **Step 2: Run fixture and verify RED**

Expected: compile failure because `BoundaryFeedbackRules` does not exist.

- [ ] **Step 3: Implement pure rules and run GREEN**

Use squared-distance epsilon `0.0001f` for attempted-versus-clamped difference. `CanNotify` returns true when `now - lastNotification >= cooldown`.

- [ ] **Step 4: Expose read-only boundary state from PlayerController**

Within `ApplyDisplacement`, preserve the attempted `Vector2`, perform the existing clamp unchanged, then set:

```csharp
BoundaryBlockedThisFrame = BoundaryFeedbackRules.WasBlocked(attempted, new Vector2(pos.x, pos.y), velocity);
BoundaryDirection = BoundaryBlockedThisFrame ? velocity.normalized : Vector2.zero;
```

Reset both values at the beginning of each `Update` before gameplay-blocked early return.

- [ ] **Step 5: Implement WorldBoundaryFeedback adapter**

Bind one `PlayerController`, emit `Triggered(Vector2 direction)` only on a blocked frame allowed by the 1.5-second cooldown, and expose `LastDirection` for HUD rendering tests.

- [ ] **Step 6: Run tests, enter Play Mode, and verify no movement regression**

Verify free movement in the middle of the world, correct clamping at all four edges, no repeated console spam, and no change to dodge displacement.

- [ ] **Step 7: Commit**

```powershell
git add -- Assets/_Project/Scripts/Runtime/Navigation/BoundaryFeedbackRules.cs Assets/_Project/Scripts/Runtime/Navigation/WorldBoundaryFeedback.cs Assets/_Project/Scripts/Runtime/Tests/BoundaryFeedbackRulesTests.cs Assets/_Project/Scripts/Runtime/PlayerController.cs
git commit -m "feat: expose world boundary feedback"
```

### Task 4: Water-ink minimap HUD

**Files:**
- Create: `Assets/_Project/Scripts/Runtime/Navigation/InkMinimapHud.cs`
- Create: `Assets/_Project/Scripts/Runtime/Navigation/InkMinimapPalette.cs`
- Test: `Assets/_Project/Scripts/Runtime/Tests/InkMinimapHudTests.cs`
- Modify: `Assets/_Project/Scripts/Runtime/Hud.cs:153`

**Interfaces:**
- Consumes: `MinimapMarkerRegistry.Markers`, `MinimapProjection.Project`, `MinimapVisibilityRules.ShouldShow`, `WorldBuilder.WorldWidth`, `WorldBuilder.WorldHeight`, player facing, and boundary-trigger events.
- Produces: one 310×220 reference-resolution map panel, marker images keyed by stable id, player arrow rotation, target label, and directional boundary wash.

- [ ] **Step 1: Write failing HUD structure tests**

```csharp
[Test]
public void Build_CreatesOneDedicatedCanvasAndPlayerMarker()
{
    GameObject host = new GameObject("hud-host");
    InkMinimapHud hud = host.AddComponent<InkMinimapHud>();
    hud.Build();

    Assert.AreEqual(1, host.GetComponentsInChildren<Canvas>(true).Length);
    Assert.IsNotNull(host.transform.Find("InkMinimapCanvas/Scroll/PlayerArrow"));
    Assert.IsNotNull(host.transform.Find("InkMinimapCanvas/Scroll/BoundaryWash"));
    Object.DestroyImmediate(host);
}
```

- [ ] **Step 2: Run fixture and verify RED**

Expected: compile failure because `InkMinimapHud` does not exist.

- [ ] **Step 3: Implement the visual token palette**

Define exact colors: paper `#D9CEAAE8`, deep ink `#25312BEF`, road ink `#756E5D99`, player jade `#2A8A78FF`, cinnabar `#A73A32FF`, NPC gold `#B99849FF`. Keep them in `InkMinimapPalette` so tests and rendering share one source.

- [ ] **Step 4: Build the static scroll once**

Create a Screen Space Overlay Canvas at sorting order 160, `CanvasScaler` matching `Hud.ReferenceResolution`, a top-right anchored 310×220 paper panel, two offset ink border lines, a clipped 278×174 map area, road strokes, target label, player arrow, and four inactive boundary-wash images. Reuse `Hud.NewRect`, `Hud.NewImageRect`, and `Hud.NewText`.

- [ ] **Step 5: Implement marker pooling and refresh**

Maintain `Dictionary<string, Image>` and a reusable image stack. Rebuild membership only when registry `Changed` fires. Update player every frame and non-player markers at 10 Hz or after a 2-world-unit movement. Apply marker visibility rules before activating images.

- [ ] **Step 6: Bind boundary wash and text feedback**

On boundary trigger, activate the matching edge wash for 0.55 seconds and show “前方无路” for 0.9 seconds. Fade with unscaled time so pause/menu states do not strand the effect.

- [ ] **Step 7: Run tests GREEN and check three resolutions**

Use Game view resolutions 1280×720, 1920×1080, and 1200×900. Confirm the minimap avoids top-left health bars, bottom navigation, right-side diagnostics, and modal adventure pages.

- [ ] **Step 8: Commit**

```powershell
git add -- Assets/_Project/Scripts/Runtime/Navigation/InkMinimapHud.cs Assets/_Project/Scripts/Runtime/Navigation/InkMinimapPalette.cs Assets/_Project/Scripts/Runtime/Tests/InkMinimapHudTests.cs Assets/_Project/Scripts/Runtime/Hud.cs
git commit -m "feat: add water ink minimap hud"
```

### Task 5: Deterministic road landmarks and richer encounter spacing

**Files:**
- Create: `Assets/_Project/Scripts/Runtime/Story/NavigationLandmarkPlan.cs`
- Create: `Assets/_Project/Scripts/Runtime/Story/NavigationLandmarkView.cs`
- Test: `Assets/_Project/Scripts/Runtime/Tests/NavigationLandmarkPlanTests.cs`
- Modify: `Assets/_Project/Scripts/Runtime/2.5D/BambooSceneContext.cs:564`
- Modify: `Assets/_Project/Scripts/Runtime/Story/FirstChapterLayout.cs`

**Interfaces:**
- Consumes: `FirstChapterLayout`, deterministic seed, existing environment sprites, and story-node reserve rules.
- Produces: `NavigationLandmarkPlacement[] NavigationLandmarkPlan.Create(FirstChapterLayout layout, uint seed)` and runtime landmark views for bamboo clumps, scholar rocks, signs, lanterns, baskets, and building silhouettes.

- [ ] **Step 1: Write failing landmark placement tests**

```csharp
[Test]
public void Create_AddsAtLeastFiveKindsWithoutBlockingStoryNodes()
{
    FirstChapterLayout layout = FirstChapterLayout.Build(Vector2.zero);
    NavigationLandmarkPlacement[] items = NavigationLandmarkPlan.Create(layout, 20260827u);

    Assert.GreaterOrEqual(items.Select(x => x.Kind).Distinct().Count(), 5);
    foreach (NavigationLandmarkPlacement item in items)
    {
        Assert.IsFalse(layout.IsNearReservedStoryNode(item.Position));
        Assert.IsFalse(item.BlocksRoadCenter);
    }
}
```

- [ ] **Step 2: Run fixture and verify RED**

Expected: compile failure because landmark types do not exist.

- [ ] **Step 3: Implement deterministic landmark rules**

Provide 12–18 placements distributed on both sides of the road. Include all six kinds: bamboo clump, scholar rock, sign, lantern, basket, and building silhouette. Use the supplied seed only for bounded jitter and scale variation; never use `UnityEngine.Random`.

- [ ] **Step 4: Implement layered runtime views**

Use existing environment sprites for bamboo and rock. Build sign, lantern, basket, and building silhouettes from cached `SpriteFactory` sprites with muted ink colors, roof/body layering, and no colliders. Register NPC/shop/building markers but not decorative scenery.

- [ ] **Step 5: Attach landmark construction to BambooSceneContext**

Call one `BuildNavigationLandmarks()` after existing foreground bamboo and before harvest bamboo. Parent all landmark views under `_groveRoot` so `Unload()` removes only owned scenery.

- [ ] **Step 6: Run tests GREEN and visually inspect the road**

Verify at least five reference-object types are visible while walking, central travel remains open, the encounter area is readable, and landmarks do not follow the player incorrectly. If `_groveRoot` follows the player in this project configuration, parent navigation landmarks to the static world root instead and retain the same plan coordinates.

- [ ] **Step 7: Commit**

```powershell
git add -- Assets/_Project/Scripts/Runtime/Story/NavigationLandmarkPlan.cs Assets/_Project/Scripts/Runtime/Story/NavigationLandmarkView.cs Assets/_Project/Scripts/Runtime/Tests/NavigationLandmarkPlanTests.cs Assets/_Project/Scripts/Runtime/2.5D/BambooSceneContext.cs Assets/_Project/Scripts/Runtime/Story/FirstChapterLayout.cs
git commit -m "feat: add first chapter navigation landmarks"
```

### Task 6: Assemble minimap, markers, and first-chapter encounter flow

**Files:**
- Modify: `Assets/_Project/Scripts/Runtime/WorldBuilder.cs:163`
- Create: `Assets/_Project/Scripts/Runtime/Story/FirstChapterRuntime.cs`
- Modify: `Assets/_Project/Scripts/Runtime/2.5D/EnemyNpcSpawner.cs`
- Test: `Assets/_Project/Scripts/Runtime/Tests/NavigationAssemblyTests.cs`
- Test: `Assets/_Project/Scripts/PlayModeTests/NavigationPlayModeTests.cs`

**Interfaces:**
- Consumes: all components from Tasks 1–5 and the approved first-chapter layout/state rules.
- Produces: a complete runtime chain from player movement to minimap position, boundary feedback, enemy removal, NPC/shop markers, and three-stage encounter spacing.

- [ ] **Step 1: Write failing assembly test**

```csharp
[Test]
public void BuildScene_AttachesOneNavigationStackToGeneratedWorld()
{
    WorldBuilder.BuildScene();
    Assert.AreEqual(1, Object.FindObjectsOfType<InkMinimapHud>().Length);
    Assert.AreEqual(1, Object.FindObjectsOfType<WorldBoundaryFeedback>().Length);
    Assert.IsNotNull(Object.FindObjectOfType<PlayerController>().GetComponent<MinimapMarker>());
}
```

- [ ] **Step 2: Run assembly test and verify RED**

Expected: missing navigation stack or missing player marker.

- [ ] **Step 3: Add one idempotent assembly method**

Add `BuildNavigation(Transform worldRoot, Transform player)` to `WorldBuilder`. It gets-or-adds player marker, boundary feedback, and HUD exactly once, then binds references. Call it after `BuildHud`.

- [ ] **Step 4: Connect first-chapter markers and encounter groups**

Create `FirstChapterRuntime` as the MonoBehaviour adapter around the existing pure `FirstChapterRules`. It builds one teaching enemy at `RoadEncounter`, two separated patrol enemies farther along the road, and a safe-radius exclusion around `Shop`. It advances the existing chapter state from proximity and enemy-death events, then registers guide NPC, shop, building, and quest-target markers when each entity becomes available.

- [ ] **Step 5: Write and run PlayMode behavior test**

The test moves the player by a known world distance, waits one frame, and asserts player-arrow normalized movement. It then destroys one marked enemy and asserts its icon is removed after registry refresh. Finally it attempts movement beyond one world edge and asserts boundary feedback fires once inside the cooldown window.

- [ ] **Step 6: Run full relevant test suites**

Run EditMode fixtures: `MinimapProjectionTests`, `MinimapVisibilityRulesTests`, `MinimapMarkerRegistryTests`, `BoundaryFeedbackRulesTests`, `InkMinimapHudTests`, `NavigationLandmarkPlanTests`, and `NavigationAssemblyTests`. Run `NavigationPlayModeTests`. Confirm zero failures and no new console errors.

- [ ] **Step 7: Perform final visual acceptance**

Enter Play Mode and capture one screenshot each for: spawn with full minimap, movement halfway up the road, boundary feedback, teaching enemy marker, NPC/shop markers, and an open adventure panel proving modal layering. Confirm all seven Unity acceptance criteria in the spec.

- [ ] **Step 8: Commit and push a safe review branch**

```powershell
git add -- Assets/_Project/Scripts/Runtime/WorldBuilder.cs Assets/_Project/Scripts/Runtime/Story/FirstChapterRuntime.cs Assets/_Project/Scripts/Runtime/2.5D/EnemyNpcSpawner.cs Assets/_Project/Scripts/Runtime/Tests/NavigationAssemblyTests.cs Assets/_Project/Scripts/PlayModeTests/NavigationPlayModeTests.cs
git commit -m "feat: assemble first chapter navigation loop"
git push origin HEAD:refs/heads/codex/ink-minimap-navigation-20260827
```
