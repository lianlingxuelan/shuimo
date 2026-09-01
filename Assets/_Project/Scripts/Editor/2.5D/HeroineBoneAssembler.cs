// =============================================================================
// HeroineBoneAssembler.cs
//
// 竹林 2.5D · 女主绑骨一键装配（Editor Only）
//
// 【职责】把已经写好骨骼数据（SpriteBone + 权重）的 heroine_base_open.png
//   组装成场景里真正可见、可驱动的 2D 骨骼角色：
//     1. 读 Sprite 骨骼数据（Sprite.GetBones()）
//     2. 调 Unity 原生 SpriteSkinUtility.CreateBoneHierarchy（与 Inspector
//        “Create Bones” 按钮同一代码路径），自动建骨骼 Transform 层级，
//        并回填 rootBone / boneTransforms（internal setter 由包内代码调用）
//     3. 挂 SpriteRenderer + SpriteSkin + Animator
//     4. 生成 AnimatorController（idle/walk/attack/hit/death 5 状态），
//        并自动生成默认 heroine_idle.anim（hip 呼吸浮动，循环）绑到 idle
//     5. 挂 UnityBoneCharacterView（clip 字段默认已填）
//     6. 存 Prefab → Assets/_Project/Prefabs/HeroineBone.prefab
//
// 【用法】
//   方式 A（自动）：打开 Unity 工程即自动补建缺失的 Prefab，无需操作。
//   方式 B（手动）：菜单 Shuimo/2.5D/一键生成女主绑骨Prefab。
//
// 【红线】纯 Editor 工具，不碰运行时组件/后端/数据库/场景现有对象；
//   幂等：Prefab 已存在 / 状态已配 Motion 时自动跳过，绝不覆盖手录动画。
// =============================================================================

using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Xianxia.Unity.T2;

#if HAS_2D_BONE_PACKAGE
using UnityEngine.U2D.Animation;
#endif

namespace Shuimo.EditorTools
{
    // SpriteSkin 类型在 UnityEngine.U2D.Animation 命名空间下（2D Animation 包运行时），
    // 与 CharacterView.cs 同口径：整体包在 #if HAS_2D_BONE_PACKAGE 内，符号未定义时不编译，
    // 避免对包的硬依赖报错。
    public static class HeroineBoneAssembler
    {
        private const string HeroineSpritePath =
            "Assets/_Project/Art/Characters/Heroine2D/heroine_base_open.png";
        private const string PrefabDir = "Assets/_Project/Prefabs";
        private const string PrefabPath = PrefabDir + "/HeroineBone.prefab";
        private const string AnimatorPath =
            "Assets/_Project/Art/Characters/Heroine2D/HeroineBoneAnimator.controller";
        private const string UnityBoneCharacterViewScriptPath =
            "Assets/_Project/Scripts/Runtime/2.5D/UnityBoneCharacterView.cs";
        private static readonly string[] RequiredStates = { "idle", "walk", "attack", "hit", "death" };

        [MenuItem("Shuimo/2.5D/一键生成女主绑骨Prefab", false, 220)]
        public static void Assemble()
        {
#if HAS_2D_BONE_PACKAGE
            TryAssemble(false);
#else
            EditorUtility.DisplayDialog(
                "HeroineBone",
                "未定义 HAS_2D_BONE_PACKAGE。\n\n" +
                "请打开 Edit → Project Settings → Player → Scripting Define Symbols，\n" +
                "添加 HAS_2D_BONE_PACKAGE 后点 Apply 等编译完成，再重试本菜单。",
                "OK");
#endif
        }

        /// <summary>
        /// 安全升级已经存在的 HeroineBone：只为没有 Motion 的状态补基础动画，
        /// 不重建也不删除 Prefab，用户手录的 Motion 保持原样。
        /// </summary>
        [MenuItem("Shuimo/2.5D/补全女主默认骨骼动作", false, 221)]
        public static void CompleteExistingMotions()
        {
#if HAS_2D_BONE_PACKAGE
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("HeroineBone", "未找到 HeroineBone.prefab，请先执行“一键生成女主绑骨Prefab”。", "OK");
                return;
            }

            SpriteSkin skin = prefab.GetComponent<SpriteSkin>();
            Animator animator = prefab.GetComponent<Animator>();
            AnimatorController controller = animator != null
                ? animator.runtimeAnimatorController as AnimatorController
                : null;
            if (skin == null || skin.boneTransforms == null || skin.boneTransforms.Length == 0 || controller == null)
            {
                EditorUtility.DisplayDialog(
                    "HeroineBone",
                    "现有预制体缺少可用 SpriteSkin 骨骼或标准 AnimatorController，无法安全补全动作。",
                    "OK");
                return;
            }

            EnsureDefaultMotions(controller, skin.boneTransforms);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log("[HeroineBone] 已补全现有 HeroineBone 的空状态动作，不覆盖已有 Motion。");
            EditorUtility.DisplayDialog("HeroineBone", "已补全 idle / walk / attack / hit / death 的空状态动作。", "OK");
#else
            EditorUtility.DisplayDialog("HeroineBone", "未定义 HAS_2D_BONE_PACKAGE，无法处理骨骼动作。", "OK");
#endif
        }

        /// <summary>
        /// 在 Skinning Editor 已生成网格和权重后，安全启用现有 prefab 的 SpriteSkin。
        /// 不重建 prefab，也不会覆盖 Animator 或用户已录制的动作。
        /// </summary>
        [MenuItem("Shuimo/2.5D/启用已蒙皮女主的真实骨骼", false, 222)]
        public static void EnableExistingSpriteSkin()
        {
#if HAS_2D_BONE_PACKAGE
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            SpriteSkin skin = prefab != null ? prefab.GetComponent<SpriteSkin>() : null;
            SpriteRenderer renderer = prefab != null ? prefab.GetComponent<SpriteRenderer>() : null;
            if (skin == null || renderer == null || renderer.sprite == null
                || skin.rootBone == null || skin.boneTransforms == null || skin.boneTransforms.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "HeroineBone",
                    "现有预制体缺少 SpriteSkin、源图或骨骼层级，无法安全启用真实蒙皮。",
                    "OK");
                return;
            }

            skin.enabled = true;
            EditorUtility.SetDirty(skin);
            PrefabUtility.SavePrefabAsset(prefab);
            AssetDatabase.SaveAssets();
            Debug.Log("[HeroineBone] 已启用 SpriteSkin；运行时将保留源图并由 Animator 驱动骨骼。");
            EditorUtility.DisplayDialog(
                "HeroineBone",
                "已启用 SpriteSkin。请进入 Play 模式确认角色可见，并检查 idle / walk / attack 动作。",
                "OK");
#else
            EditorUtility.DisplayDialog("HeroineBone", "未定义 HAS_2D_BONE_PACKAGE，无法启用真实骨骼。", "OK");
#endif
        }

        /// <summary>
        /// 打开工程 / 脚本重新编译后，若 HeroineBone.prefab 尚不存在则自动补建（静默、幂等）。
        /// 让不熟悉 Unity 的操作者“打开工程即可直接使用”，无需手动点菜单。
        /// 仅在 HAS_2D_BONE_PACKAGE 定义时执行建骨；失败路径只打日志、不弹窗。
        /// </summary>
        [InitializeOnLoadMethod]
        private static void AutoAssembleIfMissing()
        {
            if (Application.isBatchMode)
            {
                return;
            }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
            {
                return;
            }

            // delayCall：等本轮资产导入/刷新完成后再建，避免工程首次打开时资产未就绪。
            EditorApplication.delayCall += () =>
            {
#if HAS_2D_BONE_PACKAGE
                if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
                {
                    return;
                }
                TryAssemble(true);
#endif
            };
        }

#if HAS_2D_BONE_PACKAGE
        private static bool TryAssemble(bool silent)
        {
            // 统一反馈：静默（自动建）走 Console 日志，手动（菜单）走对话框。
            void Report(string message, bool isError)
            {
                if (silent)
                {
                    if (isError)
                    {
                        Debug.LogWarning("[HeroineBone] " + message);
                    }
                    else
                    {
                        Debug.Log("[HeroineBone] " + message);
                    }
                }
                else
                {
                    EditorUtility.DisplayDialog("HeroineBone", message, "OK");
                }
            }

            // 0. 读骨骼数据
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(HeroineSpritePath);
            if (sprite == null)
            {
                Report("找不到：" + HeroineSpritePath, true);
                return false;
            }

            // 1. 场景临时对象（根）
            GameObject root = new GameObject("HeroineBone_Test");
            root.transform.position = Vector3.zero;

            // 2. SpriteRenderer（先挂，SpriteSkin.OnEnable 需要取到它）
            SpriteRenderer sr = root.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 10;

            // 3. SpriteSkin + Unity 原生建骨（反射可能抛异常，捕获后走失败路径）
            SpriteSkin skin = root.AddComponent<SpriteSkin>();
            skin.alwaysUpdate = true;
            try
            {
                CreateBoneHierarchy(skin);
            }
            catch (System.Exception ex)
            {
                Object.DestroyImmediate(root);
                Report("骨骼层级创建失败：\n" + ex.Message, true);
                return false;
            }

            if (skin.rootBone == null || skin.boneTransforms == null || skin.boneTransforms.Length == 0)
            {
                Object.DestroyImmediate(root);
                Report("骨骼层级创建失败，请查看 Console 日志。", true);
                return false;
            }

            // 4. Animator + 自动生成 Controller（5 状态）+ 默认骨骼动画。
            //    不覆盖用户已经手录的 Motion；空状态才补上可运行的基础动作。
            AnimatorController ac = EnsureAnimatorController();
            EnsureDefaultMotions(ac, skin.boneTransforms);
            Animator animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = ac;

            // 5. UnityBoneCharacterView（clip 默认已填 idle/walk/attack/hit/death）
            // 直接显式挂骨骼视图，不走 ResolveOn，避免回落到 SpriteCharacterView。
            UnityBoneCharacterView bv = root.GetComponent<UnityBoneCharacterView>()
                ?? root.gameObject.AddComponent<UnityBoneCharacterView>();
            bv.Bind(skin);
            // 立刻确保 MonoScript 引用正确落盘；某些 Unity 版本/程序集重载后
            // AddComponent 的脚本引用可能延迟，导致 Prefab 保存成 Missing Script。
            EnsureMonoScriptReference(bv, UnityBoneCharacterViewScriptPath);
            Debug.Log("[HeroineBone] 已挂 UnityBoneCharacterView（骨骼驱动视图）。");

            // 6. 存 Prefab
            if (!Directory.Exists(PrefabDir))
            {
                Directory.CreateDirectory(PrefabDir);
                AssetDatabase.Refresh();
            }
            int boneCount = skin.boneTransforms.Length;

            // 6.1 旧 Prefab 若已存在且组件损坏（如 m_Script:{fileID:0}），
            //     SaveAsPrefabAsset 的“覆盖”行为可能保留损坏条目。先删掉旧的，
            //     让本次保存从零创建，避免历史损坏状态污染。
            if (AssetDatabase.LoadAssetAtPath<Object>(PrefabPath) != null)
            {
                AssetDatabase.DeleteAsset(PrefabPath);
                AssetDatabase.Refresh();
            }

            GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            if (savedPrefab == null)
            {
                Object.DestroyImmediate(root);
                Report("Prefab 保存失败（SaveAsPrefabAsset 返回 null）。请检查 Console 是否有其他报错。", true);
                return false;
            }

            // 6.2 验证并修复 UnityBoneCharacterView 的脚本引用。
            //     个别 Unity 版本/程序集重载场景下，AddComponent 后 MonoScript
            //     引用可能没写进 Prefab，表现为 m_Script:{fileID:0}（Missing Script）。
            var viewOnPrefab = savedPrefab.GetComponent<UnityBoneCharacterView>();
            if (viewOnPrefab == null)
            {
                // 组件存在但脚本引用损坏时 GetComponent<T> 会返回 null，
                // 尝试按损坏组件修复：找到所有 MonoBehaviour，把脚本为空的重新绑定。
                RepairMissingScript<UnityBoneCharacterView>(savedPrefab, UnityBoneCharacterViewScriptPath);
            }
            else
            {
                EnsureMonoScriptReference(viewOnPrefab, UnityBoneCharacterViewScriptPath);
            }

            // 6.3 最终兜底：直接扫描 Prefab YAML，把仍损坏的 m_Script 用已知 GUID 修复。
            //     这是对抗 Unity 在条件编译符号/程序集重载后脚本引用丢失的最后手段。
            RepairMissingScriptInPrefabYaml(PrefabPath);

            // 清理场景临时对象（Prefab 已落盘）
            Object.DestroyImmediate(root);

            Report(
                "已生成 " + boneCount + " 根骨骼的角色 Prefab：\n" + PrefabPath +
                "\n\n并创建了 AnimatorController（idle/walk/attack/hit/death 5 状态）。\n" +
                "idle/walk/attack/hit/death 均已自动挂上基础骨骼动作；已有手录动画不会被覆盖。\n\n" +
                "下一步：\n" +
                "1) 把 Prefab 拖进场景，点 Play → 角色应原地轻微上下呼吸（骨骼已绑定）。\n" +
                "2) 打开 Animator 窗口，可把对应 State 的基础动作替换为精修 .anim。\n" +
                "3) 之后可按 docs/2d-bone-setup-guide.md 第 10 步接入 WorldBuilder。",
                false);
            return true;
        }

        // =====================================================================
        // 原生建骨：反射调用 UnityEngine.U2D.Animation.SpriteSkinUtility
        // 的 internal 扩展方法 CreateBoneHierarchy(this SpriteSkin)。
        // 这是 Inspector 上 “Create Bones” 按钮走的同一代码路径，
        // 会按 SpriteBone 数据建 Transform 层级并回填 rootBone/boneTransforms。
        // =====================================================================

        private static void CreateBoneHierarchy(SpriteSkin skin)
        {
            var utilityType = typeof(SpriteSkin).Assembly.GetType("UnityEngine.U2D.Animation.SpriteSkinUtility");
            if (utilityType == null)
            {
                throw new System.InvalidOperationException("[HeroineBone] 未找到 SpriteSkinUtility 类型，请升级 2D Animation 包。");
            }

            var method = utilityType.GetMethod(
                "CreateBoneHierarchy",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(SpriteSkin) },
                null);
            if (method == null)
            {
                throw new System.InvalidOperationException("[HeroineBone] 未找到 SpriteSkinUtility.CreateBoneHierarchy 方法，请升级 2D Animation 包。");
            }

            method.Invoke(null, new object[] { skin });
        }

        // =====================================================================
        // Animator Controller
        // =====================================================================

        private static AnimatorController EnsureAnimatorController()
        {
            AnimatorController ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(AnimatorPath);
            if (ac == null)
            {
                ac = AnimatorController.CreateAnimatorControllerAtPath(AnimatorPath);
            }

            var sm = ac.layers[0].stateMachine;
            AnimatorState defaultState = null;
            foreach (string name in RequiredStates)
            {
                AnimatorState existing = FindState(sm, name);
                if (existing == null)
                {
                    AnimatorState state = sm.AddState(name);
                    if (name == "idle")
                    {
                        defaultState = state;
                    }
                }
                else if (name == "idle")
                {
                    defaultState = existing;
                }
            }
            if (defaultState != null)
            {
                sm.defaultState = defaultState;
            }

            EditorUtility.SetDirty(ac);
            AssetDatabase.SaveAssets();
            return ac;
        }

        private static AnimatorState FindState(AnimatorStateMachine sm, string name)
        {
            if (sm == null)
            {
                return null;
            }
            foreach (ChildAnimatorState child in sm.states)
            {
                if (child.state != null && child.state.name == name)
                {
                    return child.state;
                }
            }
            return null;
        }

        // =====================================================================
        // 默认骨骼动作
        //
        // 新建的骨骼 Controller 过去只有 idle Motion，导致状态名虽然齐全，实际
        // walk / attack / hit / death 却永远没有任何骨骼变化。这里为五个空状态
        // 生成可替换的基础动画：一旦 SpriteSkin 权重完成，Animator 就能立刻驱动
        // 身体、四肢与裙摆；已有手录 Motion 一律不碰。
        // =====================================================================

        private static void EnsureDefaultMotions(AnimatorController ac, Transform[] boneTransforms)
        {
            if (ac == null || boneTransforms == null)
            {
                return;
            }

            Transform hip = FindBone(boneTransforms, "hip");
            float amplitude = hip != null
                ? Mathf.Max(0.01f, Mathf.Abs(hip.localPosition.y) * 0.02f)
                : 0.02f;

            EnsureDefaultMotion(ac, "idle", "heroine_idle", true, boneTransforms, amplitude);
            EnsureDefaultMotion(ac, "walk", "heroine_walk", true, boneTransforms, amplitude);
            EnsureDefaultMotion(ac, "attack", "heroine_attack", false, boneTransforms, amplitude);
            EnsureDefaultMotion(ac, "hit", "heroine_hit", false, boneTransforms, amplitude);
            EnsureDefaultMotion(ac, "death", "heroine_death", false, boneTransforms, amplitude);
            AssetDatabase.SaveAssets();
        }

        private static void EnsureDefaultMotion(
            AnimatorController controller,
            string stateName,
            string clipName,
            bool loop,
            Transform[] boneTransforms,
            float amplitude)
        {
            AnimatorState state = FindState(controller.layers[0].stateMachine, stateName);
            if (state == null || state.motion != null)
            {
                return;
            }

            string clipPath = "Assets/_Project/Art/Characters/Heroine2D/" + clipName + ".anim";
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
            {
                clip = new AnimationClip();
                clip.name = clipName;
                clip.frameRate = 30f;
                PopulateDefaultMotion(clip, stateName, boneTransforms, amplitude);
                AssetDatabase.CreateAsset(clip, clipPath);
            }

            AnimationClipSettings clipSettings = AnimationUtility.GetAnimationClipSettings(clip);
            clipSettings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, clipSettings);
            state.motion = clip;
            EditorUtility.SetDirty(state);
        }

        private static void PopulateDefaultMotion(
            AnimationClip clip,
            string stateName,
            Transform[] bones,
            float amplitude)
        {
            float hipY = LocalPositionY(bones, "hip");
            float hipX = LocalPositionX(bones, "hip");
            float hipZ = LocalEulerZ(bones, "hip");
            float chestZ = LocalEulerZ(bones, "chest");
            float leftThighZ = LocalEulerZ(bones, "thigh_L");
            float rightThighZ = LocalEulerZ(bones, "thigh_R");
            float leftArmZ = LocalEulerZ(bones, "shoulder_L");
            float rightArmZ = LocalEulerZ(bones, "shoulder_R");
            float skirtLeftZ = LocalEulerZ(bones, "skirt_L");
            float skirtRightZ = LocalEulerZ(bones, "skirt_R");

            switch (stateName)
            {
                case "idle":
                    SetCurve(clip, "hip", "m_LocalPosition.y", 0f, hipY, 0.5f, hipY + amplitude, 1f, hipY);
                    SetCurve(clip, "hair_back_L", "localEulerAnglesRaw.z", 0f, LocalEulerZ(bones, "hair_back_L"), 0.5f, LocalEulerZ(bones, "hair_back_L") + 3f, 1f, LocalEulerZ(bones, "hair_back_L"));
                    SetCurve(clip, "hair_back_R", "localEulerAnglesRaw.z", 0f, LocalEulerZ(bones, "hair_back_R"), 0.5f, LocalEulerZ(bones, "hair_back_R") - 3f, 1f, LocalEulerZ(bones, "hair_back_R"));
                    break;
                case "walk":
                    SetCurve(clip, "hip", "m_LocalPosition.y", 0f, hipY, 0.25f, hipY + amplitude * 2.4f, 0.5f, hipY, 0.75f, hipY + amplitude * 2.4f, 1f, hipY);
                    SetCurve(clip, "thigh_L", "localEulerAnglesRaw.z", 0f, leftThighZ - 18f, 0.5f, leftThighZ + 18f, 1f, leftThighZ - 18f);
                    SetCurve(clip, "thigh_R", "localEulerAnglesRaw.z", 0f, rightThighZ + 18f, 0.5f, rightThighZ - 18f, 1f, rightThighZ + 18f);
                    SetCurve(clip, "shoulder_L", "localEulerAnglesRaw.z", 0f, leftArmZ + 12f, 0.5f, leftArmZ - 12f, 1f, leftArmZ + 12f);
                    SetCurve(clip, "shoulder_R", "localEulerAnglesRaw.z", 0f, rightArmZ - 12f, 0.5f, rightArmZ + 12f, 1f, rightArmZ - 12f);
                    break;
                case "attack":
                    SetCurve(clip, "hip", "localEulerAnglesRaw.z", 0f, hipZ, 0.14f, hipZ - 9f, 0.32f, hipZ + 11f, 0.52f, hipZ);
                    SetCurve(clip, "chest", "localEulerAnglesRaw.z", 0f, chestZ, 0.14f, chestZ - 14f, 0.32f, chestZ + 25f, 0.52f, chestZ);
                    SetCurve(clip, "shoulder_R", "localEulerAnglesRaw.z", 0f, rightArmZ, 0.14f, rightArmZ - 26f, 0.32f, rightArmZ + 55f, 0.52f, rightArmZ);
                    SetCurve(clip, "shoulder_L", "localEulerAnglesRaw.z", 0f, leftArmZ, 0.14f, leftArmZ + 12f, 0.32f, leftArmZ - 20f, 0.52f, leftArmZ);
                    SetCurve(clip, "skirt_L", "localEulerAnglesRaw.z", 0f, skirtLeftZ, 0.32f, skirtLeftZ - 10f, 0.52f, skirtLeftZ);
                    SetCurve(clip, "skirt_R", "localEulerAnglesRaw.z", 0f, skirtRightZ, 0.32f, skirtRightZ + 10f, 0.52f, skirtRightZ);
                    break;
                case "hit":
                    SetCurve(clip, "hip", "m_LocalPosition.x", 0f, hipX, 0.08f, hipX - amplitude * 2.5f, 0.22f, hipX);
                    SetCurve(clip, "chest", "localEulerAnglesRaw.z", 0f, chestZ, 0.08f, chestZ - 13f, 0.22f, chestZ);
                    break;
                case "death":
                    SetCurve(clip, "hip", "localEulerAnglesRaw.z", 0f, hipZ, 0.42f, hipZ - 76f, 0.7f, hipZ - 82f);
                    SetCurve(clip, "chest", "localEulerAnglesRaw.z", 0f, chestZ, 0.42f, chestZ + 18f, 0.7f, chestZ + 22f);
                    break;
            }
        }

        private static Transform FindBone(Transform[] bones, string boneName)
        {
            return System.Array.Find(bones, bone => bone != null && bone.name == boneName);
        }

        private static float LocalPositionY(Transform[] bones, string boneName)
        {
            Transform bone = FindBone(bones, boneName);
            return bone != null ? bone.localPosition.y : 0.0f;
        }

        private static float LocalPositionX(Transform[] bones, string boneName)
        {
            Transform bone = FindBone(bones, boneName);
            return bone != null ? bone.localPosition.x : 0.0f;
        }

        private static float LocalEulerZ(Transform[] bones, string boneName)
        {
            Transform bone = FindBone(bones, boneName);
            if (bone == null)
            {
                return 0.0f;
            }
            float angle = bone.localEulerAngles.z;
            return angle > 180.0f ? angle - 360.0f : angle;
        }

        private static void SetCurve(AnimationClip clip, string path, string propertyName, params float[] keys)
        {
            AnimationCurve curve = new AnimationCurve();
            for (int i = 0; i + 1 < keys.Length; i += 2)
            {
                curve.AddKey(keys[i], keys[i + 1]);
            }
            clip.SetCurve(path, typeof(Transform), propertyName, curve);
        }

        // =====================================================================
        // Prefab 保存后修复：防止 MonoScript 引用丢失（m_Script:{fileID:0}）
        // =====================================================================

        /// <summary>
        /// 确保 MonoBehaviour 的 m_Script 引用指向正确的 MonoScript 资产。
        /// 在 AddComponent 后因程序集重载导致引用未落盘时，优先用 MonoScript.FromMonoBehaviour
        /// （按运行时类型解析，单类文件时恒正确），失败再按 .cs 资产路径加载兜底。
        /// </summary>
        private static void EnsureMonoScriptReference<T>(T behaviour, string scriptAssetPath)
            where T : MonoBehaviour
        {
            if (behaviour == null)
            {
                return;
            }
            SerializedObject so = new SerializedObject(behaviour);
            SerializedProperty scriptProp = so.FindProperty("m_Script");
            if (scriptProp == null)
            {
                return;
            }
            if (scriptProp.objectReferenceValue != null)
            {
                return;
            }
            // 优先按运行时类型取 MonoScript（单类文件时恒正确，不依赖文件名/主类）。
            MonoScript ms = MonoScript.FromMonoBehaviour(behaviour);
            if (ms == null)
            {
                // 兜底：组件类型不可解析时按 .cs 资产路径加载（拆分后该文件为单类）。
                ms = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptAssetPath);
            }
            if (ms == null)
            {
                Debug.LogWarning(
                    "[HeroineBone] 无法加载 MonoScript：" + scriptAssetPath +
                    "，跳过修复 " + typeof(T).Name + "。");
                return;
            }
            scriptProp.objectReferenceValue = ms;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(behaviour);
            AssetDatabase.SaveAssets();
            Debug.Log("[HeroineBone] 已修复 " + typeof(T).Name + " 的 MonoScript 引用。");
        }

        /// <summary>
        /// 当 GetComponent&lt;T&gt; 因脚本引用损坏返回 null 时，遍历 root 上所有
        /// MonoBehaviour，把 m_Script 为空的条目重新绑定到 T 对应的 MonoScript。
        /// 直接按 .cs 资产路径加载：拆分后该文件为单类，path 即唯一标识 MonoScript，
        /// 拿到的 fileID 必为 11500000（指向本类）。Unity 2022.3 没有 MonoScript.FromType，
        /// 故此处固定走路径方案，跨版本稳定。
        /// </summary>
        private static void RepairMissingScript<T>(GameObject root, string scriptAssetPath)
            where T : MonoBehaviour
        {
            if (root == null)
            {
                return;
            }
            // 按 .cs 资产路径加载 MonoScript；单类文件下 path 即唯一标识，fileID=11500000 必指向 T。
            MonoScript ms = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptAssetPath);
            if (ms == null)
            {
                Debug.LogWarning(
                    "[HeroineBone] 无法加载 MonoScript：" + scriptAssetPath +
                    "，跳过修复损坏的 " + typeof(T).Name + "。");
                return;
            }
            MonoBehaviour[] all = root.GetComponents<MonoBehaviour>();
            foreach (MonoBehaviour mb in all)
            {
                if (mb == null)
                {
                    continue;
                }
                SerializedObject so = new SerializedObject(mb);
                SerializedProperty scriptProp = so.FindProperty("m_Script");
                if (scriptProp == null)
                {
                    continue;
                }
                if (scriptProp.objectReferenceValue != null)
                {
                    continue;
                }
                scriptProp.objectReferenceValue = ms;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(mb);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[HeroineBone] 已修复损坏的 " + typeof(T).Name + " 脚本引用。");
        }

        /// <summary>
        /// 最终兜底：直接扫描 Prefab YAML。若 UnityBoneCharacterView 特征字段（clipIdle）
        /// 所在的 MonoBehaviour 块里 m_Script 仍为 {fileID: 0}，则强行写为
        /// UnityBoneCharacterView.cs 的已知 GUID。这是对抗 Unity 在条件编译/程序集重载后
        /// 脚本引用丢失的最后手段。
        /// </summary>
        private static void RepairMissingScriptInPrefabYaml(string prefabPath)
        {
            if (!File.Exists(prefabPath))
            {
                return;
            }

            string text = File.ReadAllText(prefabPath);
            // 匹配：m_Script: {fileID: 0} 且同一块内包含 hitShakeDuration/clipIdle 字段。
            // 捕获组 $1 是 hitShakeDuration 之后到 clipIdle 之前的所有内容（含 clipIdle 前字段）。
            const string pattern =
                @"m_Script: \{fileID: 0\}" +
                @"(\s+m_Name:\s*\n\s+m_EditorClassIdentifier:\s*\n" +
                @"\s+hitShakeDuration: [\d.]+\n" +
                @"\s+hitShakeAmplitude: [\d.]+\n" +
                @"\s+clipIdle:)";
            const string targetGuid = "0bb5ffdf01ff4b48866c1e99478ede64";
            string replacement = "m_Script: {fileID: 11500000, guid: " + targetGuid + ", type: 3}$1";

            string result = System.Text.RegularExpressions.Regex.Replace(
                text, pattern, replacement, System.Text.RegularExpressions.RegexOptions.Singleline);

            if (result == text)
            {
                return;
            }

            File.WriteAllText(prefabPath, result);
            AssetDatabase.Refresh();
            AssetDatabase.SaveAssets();
            Debug.Log("[HeroineBone] 已通过 YAML 兜底修复损坏的 UnityBoneCharacterView 脚本引用。");
        }
#endif
    }
}
