using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Xianxia.Unity.T2;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class HeroineVisualUpgradeTests
    {
        [Test]
        public void PreferredHeroineSprite_IsLoadedFromTheDedicatedResourcesPath()
        {
            Assert.AreEqual("Characters/heroine_ink_idle_v2_transparent",
                WorldBuilder.PreferredHeroineSpriteResourcePath,
                "场景应优先使用带透明通道的女主资源，不能再把带底色的旧单图硬编码进角色构建流程。");
        }

        [Test]
        public void AttackSprite_UsesTheDedicatedChromaKeyResourcesPath()
        {
            Assert.AreEqual("Characters/heroine_ink_attack_v2_chroma",
                WorldBuilder.PreferredHeroineAttackSpriteResourcePath,
                "挥剑图应走独立的抠像资源路径，由专用材质去除绿幕，避免再把带底色的图叠到角色上。");
        }

        [Test]
        public void WalkSprite_UsesTheDedicatedChromaKeyResourcesPath()
        {
            Assert.AreEqual("Characters/heroine_ink_walk_v2_chroma",
                WorldBuilder.PreferredHeroineWalkSpriteResourcePath,
                "行走图应与站立、挥剑图分离，移动时才能产生可见姿态变化。");
        }

        [Test]
        public void WalkAlternateSprite_UsesTheDedicatedChromaKeyResourcesPath()
        {
            Assert.AreEqual("Characters/heroine_ink_walk_v4_chroma",
                WorldBuilder.PreferredHeroineWalkAlternateSpriteResourcePath,
                "第二个跨步图应单独导入，行走循环才会有左右脚交替的变化。");
        }

        [Test]
        public void ConfigureSpriteOverrides_RecapturesTheRuntimeCharacterScale()
        {
            GameObject go = new GameObject("HeroineScaleProbe");
            go.AddComponent<SpriteRenderer>();
            UnityBoneCharacterView view = go.AddComponent<UnityBoneCharacterView>();

            go.transform.localScale = Vector3.one * 11.0f;
            view.ConfigureSpriteOverrides(null, null, null, null);
            view.PlayState(CharacterAnimState.Idle);

            MethodInfo onTick = typeof(UnityBoneCharacterView).GetMethod(
                "OnTick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onTick);
            onTick.Invoke(view, new object[] { 0.0f });

            Assert.AreEqual(Vector3.one * 11.0f, go.transform.localScale,
                "换上正式立绘后必须以场景设置的 11 倍缩放为动作基准，不能在第一帧退回 prefab 的旧缩放。");

            Object.DestroyImmediate(go);
        }

        [Test]
        public void AttackState_UsesDedicatedSpriteOnlyWhenItExists()
        {
            Assert.IsTrue(UnityBoneCharacterView.ShouldUseAttackSprite(CharacterAnimState.Attack, true));
            Assert.IsFalse(UnityBoneCharacterView.ShouldUseAttackSprite(CharacterAnimState.Attack, false));
            Assert.IsFalse(UnityBoneCharacterView.ShouldUseAttackSprite(CharacterAnimState.Walk, true));
        }

        [Test]
        public void RepeatedLoopState_DoesNotRestartAnimatorAtFrameZero()
        {
            Assert.IsFalse(UnityBoneCharacterView.ShouldRestartAnimator(
                CharacterAnimState.Idle, CharacterAnimState.Idle));
            Assert.IsFalse(UnityBoneCharacterView.ShouldRestartAnimator(
                CharacterAnimState.Walk, CharacterAnimState.Walk));
            Assert.IsTrue(UnityBoneCharacterView.ShouldRestartAnimator(
                CharacterAnimState.Idle, CharacterAnimState.Walk));
            Assert.IsTrue(UnityBoneCharacterView.ShouldRestartAnimator(
                CharacterAnimState.Attack, CharacterAnimState.Attack),
                "连续的一次性动作仍应允许从头播放，不能被循环状态的去重规则吞掉。");
        }

        [Test]
        public void DeathState_AppliesItsFallbackPoseWithoutWaitingForAnotherTick()
        {
            GameObject go = new GameObject("HeroineDeathPoseProbe");
            go.AddComponent<SpriteRenderer>();
            UnityBoneCharacterView view = go.AddComponent<UnityBoneCharacterView>();

            view.PlayState(CharacterAnimState.Death);

            Assert.Greater(Mathf.Abs(go.transform.localEulerAngles.z), 70.0f,
                "终局会立刻冻结表现时钟，死亡姿态必须在 PlayState 当次就可见。");
            Object.DestroyImmediate(go);
        }

        [Test]
        public void ActionSprites_AreImportedAsRuntimeSprites()
        {
            Assert.NotNull(Resources.Load<Sprite>(WorldBuilder.PreferredHeroineWalkSpriteResourcePath),
                "行走图必须以 Sprite 类型导入，否则运行时无法切换姿态。");
            Assert.NotNull(Resources.Load<Sprite>(WorldBuilder.PreferredHeroineWalkAlternateSpriteResourcePath),
                "第二个跨步图必须以 Sprite 类型导入，否则行走循环无法交替。");
            Assert.NotNull(Resources.Load<Sprite>(WorldBuilder.PreferredHeroineAttackSpriteResourcePath),
                "挥剑图必须以 Sprite 类型导入，否则运行时无法切换姿态。");
        }

        [Test]
        public void AttackSprite_PivotCompensatesForItsLargerBottomMargin()
        {
            Sprite attack = Resources.Load<Sprite>(WorldBuilder.PreferredHeroineAttackSpriteResourcePath);

            Assert.NotNull(attack);
            float normalizedPivotY = attack.pivot.y / attack.rect.height;
            Assert.That(normalizedPivotY, Is.InRange(0.54f, 0.55f),
                "挥剑图人物脚底比站立图高约 70px，必须上移 Pivot 才不会在攻击时整个人悬空。");
        }

        [Test]
        public void AlternateWalkSprite_PivotKeepsBothStepsOnTheSameGroundLine()
        {
            Sprite alternate = Resources.Load<Sprite>(WorldBuilder.PreferredHeroineWalkAlternateSpriteResourcePath);

            Assert.NotNull(alternate);
            float normalizedPivotY = alternate.pivot.y / alternate.rect.height;
            Assert.That(normalizedPivotY, Is.InRange(0.48f, 0.49f),
                "第二步图的脚底比第一步低约 24px，需要下移 Pivot，避免两帧交替时人物上下跳动。");
        }

        [Test]
        public void ChromaKeyMaterial_IsPackagedInResources()
        {
            Material material = Resources.Load<Material>("Characters/HeroineChromaKey");
            Assert.NotNull(material,
                "动作图依赖的抠像材质必须放进 Resources，避免发布构建剥离 Shader 后露出绿幕。");
            Assert.IsTrue(material.HasProperty("_DespillStrength"),
                "抠像材质还必须包含轮廓去绿参数，否则透明边缘仍会残留绿色光边。");
            Assert.Greater(material.GetFloat("_DespillStrength"), 0.0f,
                "轮廓去绿强度不能为 0。实际视觉结果仍需在 Play 模式放大检查。");
        }

        [Test]
        public void PreferredHeroineArt_HidesTheLegacyFacingMarker()
        {
            Assert.IsFalse(WorldBuilder.ShouldShowFacingMarker(true),
                "正式立绘已有武器和朝向，旧的荧光朝向条会穿过人物，必须隐藏。");
            Assert.IsTrue(WorldBuilder.ShouldShowFacingMarker(false),
                "蓝方块回退角色仍需要朝向条来表达攻击方向。");
        }

        [Test]
        public void PreferredHeroineArt_DoesNotAttachTheLegacyWorldWeapon()
        {
            Assert.IsFalse(WorldBuilder.ShouldAttachLegacyWorldWeapon(true),
                "白衣立绘已经自带剑，叠加旧 3D 武器会产生材质错误或双剑穿模。");
            Assert.IsTrue(WorldBuilder.ShouldAttachLegacyWorldWeapon(false),
                "没有正式立绘时，回退角色仍可挂旧世界武器。");
        }
    }
}
