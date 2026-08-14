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

            // 4. Animator + 自动生成 Controller（5 状态）+ 默认 idle 动画
            AnimatorController ac = EnsureAnimatorController();
            EnsureIdleMotion(ac, skin.boneTransforms);
            Animator animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = ac;

            // 5. UnityBoneCharacterView（clip 默认已填 idle/walk/attack/hit/death）
            // 直接显式挂骨骼视图，不走 ResolveOn，避免回落到 SpriteCharacterView。
            UnityBoneCharacterView bv = root.GetComponent<UnityBoneCharacterView>()
                ?? root.gameObject.AddComponent<UnityBoneCharacterView>();
            bv.Bind(skin);
            // 立刻确保 MonoScript 引用正确落盘；某些 Unity 版本/程序集重载后
            // AddComponent 的脚本引用可能延迟，导致 Prefab 保存成 Missing Script。
            EnsureMonoScriptReference(bv);
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
                RepairMissingScript<UnityBoneCharacterView>(savedPrefab);
            }
            else
            {
                EnsureMonoScriptReference(viewOnPrefab);
            }

            // 清理场景临时对象（Prefab 已落盘）
            Object.DestroyImmediate(root);

            Report(
                "已生成 " + boneCount + " 根骨骼的角色 Prefab：\n" + PrefabPath +
                "\n\n并创建了 AnimatorController（idle/walk/attack/hit/death 5 状态）。\n" +
                "idle 已自动挂上默认呼吸动画 heroine_idle.anim，其余 4 个状态为空（可暂缺）。\n\n" +
                "下一步：\n" +
                "1) 把 Prefab 拖进场景，点 Play → 角色应原地轻微上下呼吸（骨骼已绑定）。\n" +
                "2) 打开 Animator 窗口，给 walk/attack/hit/death 各 State 的 Motion 槽\n" +
                "   拖入对应 .anim（暂缺不影响验证）。\n" +
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
        // 默认 idle 动画
        //
        // 给一个非 Unity 用户也能直接看到效果的兜底：若 idle 状态还没有 Motion，
        // 自动生成 heroine_idle.anim（hip 呼吸式上下浮动，循环），绑定到 idle 状态。
        // 幅度按 hip 实际 localPosition 的比例取值，自适应骨骼所在坐标系，
        // 无论骨骼是“像素尺度”还是“世界单位尺度”都保持视觉比例合理。
        // 已有 .anim / 已配 Motion 时跳过，绝不覆盖用户手录的动画。
        // =====================================================================

        private static void EnsureIdleMotion(AnimatorController ac, Transform[] boneTransforms)
        {
            AnimatorState idle = FindState(ac.layers[0].stateMachine, "idle");
            if (idle == null || idle.motion != null)
            {
                return;
            }

            const string clipPath = "Assets/_Project/Art/Characters/Heroine2D/heroine_idle.anim";
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
            {
                Transform hip = System.Array.Find(boneTransforms, t => t != null && t.name == "hip");
                float amplitude = 0.02f;
                if (hip != null)
                {
                    amplitude = Mathf.Max(0.01f, Mathf.Abs(hip.localPosition.y) * 0.02f);
                }

                clip = new AnimationClip();
                clip.name = "heroine_idle";
                clip.frameRate = 30f;

                AnimationCurve bob = new AnimationCurve();
                bob.AddKey(0.00f, 0f);
                bob.AddKey(0.50f, amplitude);
                bob.AddKey(1.00f, 0f);
                clip.SetCurve("hip", typeof(Transform), "m_LocalPosition.y", bob);

                // Mecanim 循环由 AnimationClipSettings.loopTime 控制（clip.SetLoop 不存在）。
                AssetDatabase.CreateAsset(clip, clipPath);
                AnimationClipSettings clipSettings = AnimationUtility.GetAnimationClipSettings(clip);
                clipSettings.loopTime = true;
                AnimationUtility.SetAnimationClipSettings(clip, clipSettings);
                AssetDatabase.SaveAssets();
            }

            idle.motion = clip;
            EditorUtility.SetDirty(idle);
        }

        // =====================================================================
        // Prefab 保存后修复：防止 MonoScript 引用丢失（m_Script:{fileID:0}）
        // =====================================================================

        /// <summary>
        /// 确保 MonoBehaviour 的 m_Script 引用指向正确的 MonoScript 资产。
        /// 在 AddComponent 后因程序集重载导致引用未落盘时，可通过 MonoScript.FromMonoBehaviour
        /// 重新绑定并写回。
        /// </summary>
        private static void EnsureMonoScriptReference<T>(T behaviour) where T : MonoBehaviour
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
            MonoScript ms = MonoScript.FromMonoBehaviour(behaviour);
            if (ms == null)
            {
                Debug.LogWarning("[HeroineBone] 无法获取 " + typeof(T).Name + " 的 MonoScript，跳过修复。");
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
        /// </summary>
        private static void RepairMissingScript<T>(GameObject root) where T : MonoBehaviour
        {
            if (root == null)
            {
                return;
            }
            MonoScript ms = null;
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
                // 首次需要时才去拿 T 的 MonoScript。
                if (ms == null)
                {
                    T dummy = root.GetComponent<T>();
                    if (dummy == null)
                    {
                        // 当前没有正常实例，创建一个临时组件用来取 MonoScript，然后立刻销毁。
                        dummy = root.AddComponent<T>();
                        ms = MonoScript.FromMonoBehaviour(dummy);
                        Object.DestroyImmediate(dummy, true);
                    }
                    else
                    {
                        ms = MonoScript.FromMonoBehaviour(dummy);
                    }
                }
                if (ms == null)
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
#endif
    }
}
