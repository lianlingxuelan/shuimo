using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class BambooBackdropFacingTests
    {
        [Test]
        public void BackdropSprite_UsesTheCompleteArtworkTexture()
        {
            Texture2D texture = new Texture2D(8, 5);

            Sprite sprite = BambooSceneContext.CreateBackdropSprite(texture);

            Assert.NotNull(sprite);
            Assert.AreEqual(new Rect(0.0f, 0.0f, 8.0f, 5.0f), sprite.rect,
                "2D 背景必须使用整张原画，不能只显示其中一块或退回空白。 ");

            Object.DestroyImmediate(sprite);
            Object.DestroyImmediate(texture);
        }

        [Test]
        public void IllustratedBackdropResource_IsPackagedAsTheWideSceneArtwork()
        {
            Texture2D texture = Resources.Load<Texture2D>(BambooSceneContext.GroveBackdropResourcePath);

            Assert.NotNull(texture,
                "水墨竹林底图必须位于 Resources/Environments，路径漂移会让运行时退回纯色地面。");
            Assert.GreaterOrEqual(texture.width, 1600,
                "底图宽度异常，可能误引用了缩略图或占位图。");
            float aspect = (float)texture.width / texture.height;
            Assert.That(aspect, Is.InRange(1.70f, 1.85f),
                "正式竹林底图应保持接近 16:9 的宽幅构图，避免镜头铺满时被严重拉伸。");
        }

        [Test]
        public void WorldRootLookup_UsesWorldBuilderCanonicalName()
        {
            GameObject root = new GameObject(WorldBuilder.RootName);

            Transform resolved = BambooSceneContext.FindWorldRootForBackdrop();

            Assert.AreSame(root.transform, resolved,
                "隐藏旧地图时必须查到 WorldBuilder 实际创建的根节点，否则纯色 Tilemap 会继续遮住底图。 ");

            Object.DestroyImmediate(root);
        }

        [Test]
        public void IllustratedBackdrop_SuppressesOnlyPrimitiveBambooVisuals()
        {
            Assert.IsFalse(BambooSceneContext.ShouldShowPrimitiveBamboo(true),
                "水墨底图已包含竹林时，不应再叠加粗糙的程序竹子视觉。");
            Assert.IsTrue(BambooSceneContext.ShouldShowPrimitiveBamboo(false),
                "没有水墨底图时，程序竹子仍应作为资源缺失时的视觉回退。");
        }

        [Test]
        public void IllustratedBackdrop_SkipsLegacyProceduralBambooEntirely()
        {
            Assert.IsFalse(BambooSceneContext.ShouldBuildLegacyProceduralBamboo(true),
                "完整水墨底图已有远景竹林，旧程序竹不应再生成可见几何或碰撞体。");
            Assert.IsTrue(BambooSceneContext.ShouldBuildLegacyProceduralBamboo(false),
                "缺少底图时仍保留旧程序竹，作为场景可玩的回退方案。");
        }

        [Test]
        public void EnvironmentArtwork_LoadsAsSprites_WhenUnityDoesNotExposeSpriteAsTheMainAsset()
        {
            Assert.IsNotNull(BambooSceneContext.LoadEnvironmentSprite("Environments/ink_bamboo_clump_v1"));
            Assert.IsNotNull(BambooSceneContext.LoadEnvironmentSprite("Environments/ink_harvest_bamboo_v1"));
            Assert.IsNotNull(BambooSceneContext.LoadEnvironmentSprite("Environments/ink_scholar_rock_wash_v1"));
        }

        [Test]
        public void T3DamagingAttack_AlsoDrivesTheCharacterAttackState()
        {
            Assert.IsTrue(BambooSceneContext.ShouldPlayCharacterAttackForT3Skill(true, true, true),
                "T3 普攻不会走旧 SwingCount，必须由技能施放事件直接驱动角色攻击姿态。");
            Assert.IsFalse(BambooSceneContext.ShouldPlayCharacterAttackForT3Skill(false, true, true));
            Assert.IsFalse(BambooSceneContext.ShouldPlayCharacterAttackForT3Skill(true, false, true));
            Assert.IsFalse(BambooSceneContext.ShouldPlayCharacterAttackForT3Skill(true, true, false));
        }

        [Test]
        public void T3AttackFacing_IsNormalizedAndFallsBackToRight()
        {
            Assert.AreEqual(Vector2.right, BambooSceneContext.ResolveAttackFacing(Vector2.zero),
                "没有瞄准向量时，T3 普攻、剑气和人物朝向必须使用同一个右向回退。");
            Vector2 diagonal = BambooSceneContext.ResolveAttackFacing(new Vector2(3.0f, 4.0f));
            Assert.That(diagonal.x, Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(diagonal.y, Is.EqualTo(0.8f).Within(1e-5f));
        }
    }
}
