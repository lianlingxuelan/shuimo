// -----------------------------------------------------------------------------
// BambooSceneContextVisualTests.cs -- 竹林可读性回归检查
// -----------------------------------------------------------------------------
// 这里特意覆盖「真模型」分支：模型文件只负责竹竿几何，叶子仍由场景系统统一
// 生成。否则换成 FBX 后常会只剩一根黑柱，视觉上甚至比 primitive 兜底更差。

using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Xianxia.Core;
using Xianxia.Unity.T2;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class BambooSceneContextVisualTests
    {
        [Test]
        public void ModelBamboo_StillBuildsReadableLeafCluster()
        {
            GameObject host = new GameObject("BambooVisualTest_Host");
            GameObject model = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Material trunk = new Material(Shader.Find("Sprites/Default"));
            Material leaf = new Material(Shader.Find("Sprites/Default"));

            try
            {
                model.name = "TestModel";
                BambooSceneContext context = host.AddComponent<BambooSceneContext>();
                context.forcePrimitiveBamboo = false;
                context.bambooModelPrefab = model;
                context.trunkMaterial = trunk;
                context.leafMaterial = leaf;
                context.trunkRadius = 2.0f;
                context.leavesMin = 3;
                context.leavesMax = 3;

                MethodInfo buildOne = typeof(BambooSceneContext).GetMethod(
                    "BuildOneBamboo", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(buildOne, "竹子生成入口被改名时，应同步更新这条视觉回归检查。");

                PCG32 rng = ZoneSeed.CreateRng("test_model_bamboo_leaves", false, 0);
                buildOne.Invoke(context, new object[]
                {
                    0, Vector2.zero, 20.0f, rng, host.transform
                });

                Transform bamboo = host.transform.Find("Bamboo_00");
                Assert.IsNotNull(bamboo, "真模型分支也应创建同一层级的竹子根节点。");
                Assert.AreEqual(3, CountDirectChildrenNamed(bamboo, "InkLeaf_"),
                    "真模型路径必须补齐统一的叶簇；否则画面只剩无辨识度的竹竿。");

                MeshRenderer modelRenderer = bamboo.Find("Trunk_Model").GetComponentInChildren<MeshRenderer>();
                Assert.AreSame(trunk, modelRenderer.sharedMaterial,
                    "真模型必须接入场景的水墨竹竿材质，不能保留未受控的黑色导入材质。");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(model);
                Object.DestroyImmediate(trunk);
                Object.DestroyImmediate(leaf);
            }
        }

        [Test]
        public void PrimitiveBamboo_UsesInkStrokeMeshesInsteadOfCylinderAndQuads()
        {
            GameObject host = new GameObject("BambooVisualTest_Primitive");
            Material trunk = new Material(Shader.Find("Sprites/Default"));
            Material leaf = new Material(Shader.Find("Sprites/Default"));

            try
            {
                BambooSceneContext context = host.AddComponent<BambooSceneContext>();
                context.forcePrimitiveBamboo = true;
                context.trunkMaterial = trunk;
                context.leafMaterial = leaf;
                context.trunkRadius = 2.0f;
                context.leavesMin = 3;
                context.leavesMax = 3;

                MethodInfo buildOne = typeof(BambooSceneContext).GetMethod(
                    "BuildOneBamboo", BindingFlags.Instance | BindingFlags.NonPublic);
                buildOne.Invoke(context, new object[]
                {
                    0, Vector2.zero, 80.0f,
                    ZoneSeed.CreateRng("test_ink_stroke_bamboo", false, 0), host.transform
                });

                Transform bamboo = host.transform.Find("Bamboo_00");
                MeshFilter trunkMesh = bamboo.Find("InkTrunk").GetComponent<MeshFilter>();
                Assert.Greater(trunkMesh.sharedMesh.vertexCount, 8,
                    "兜底竹竿应是带宽窄变化和竹节的水墨轮廓，不能再是 Unity Cylinder。");

                Transform leafTransform = bamboo.Find("InkLeaf_0");
                Assert.Greater(leafTransform.GetComponent<MeshFilter>().sharedMesh.vertexCount, 4,
                    "兜底竹叶应是尖叶轮廓，不能再是方形 Quad。");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(trunk);
                Object.DestroyImmediate(leaf);
            }
        }

        [Test]
        public void GroveLayout_ReservesAnOpenStageAroundTheHeroine()
        {
            FieldInfo clearRadiusField = typeof(BambooSceneContext).GetField("clearStageRadius");
            Assert.IsNotNull(clearRadiusField,
                "竹林需要一个可配置的中心留白半径，保障女主和挥砍动作始终可读。");

            GameObject host = new GameObject("BambooVisualTest_Layout");
            try
            {
                BambooSceneContext context = host.AddComponent<BambooSceneContext>();
                context.groveHalfExtent = 500.0f;
                context.bambooMinDist = 75.0f;
                clearRadiusField.SetValue(context, 160.0f);

                MethodInfo scatter = typeof(BambooSceneContext).GetMethod(
                    "ScatterPositions", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(scatter);
                scatter.Invoke(context, new object[]
                {
                    ZoneSeed.CreateRng("test_open_stage", false, 0), 20
                });

                FieldInfo planField = typeof(BambooSceneContext).GetField(
                    "_bambooPlan", BindingFlags.Instance | BindingFlags.NonPublic);
                var plan = (System.Collections.Generic.List<Vector2>)planField.GetValue(context);
                foreach (Vector2 position in plan)
                {
                    Assert.GreaterOrEqual(position.sqrMagnitude, 160.0f * 160.0f,
                        "竹竿不能进入女主中央表演区，否则角色与攻击动作会被遮挡。");
                }
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Atmosphere_DefaultSetup_DoesNotBuildRectangularMountainQuads()
        {
            GameObject host = new GameObject("AtmosphereVisualTest_Host");
            try
            {
                host.AddComponent<AtmosphereLayer>().Build();

                Assert.IsNull(host.transform.Find("AtmosphereRoot/InkMountain_0"),
                    "俯视竹林不应默认生成方形远山 Quad；它会被看成悬浮灰色矩形。");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Grove_DefaultSetup_DoesNotScatterRectangularRockPlanes()
        {
            GameObject host = new GameObject("BambooRockVisualTest_Host");
            try
            {
                BambooSceneContext context = host.AddComponent<BambooSceneContext>();

                Assert.AreEqual(0, context.rockCount,
                    "没有不规则石头素材时，默认不应把方形 Plane 当作石头散进竹林。");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Grove_InkPools_AreIrregularMeshShapesRatherThanQuads()
        {
            FieldInfo countField = typeof(BambooSceneContext).GetField("inkPoolCount");
            MethodInfo buildPools = typeof(BambooSceneContext).GetMethod(
                "BuildInkPools", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(countField,
                "水墨地表需要可配置的洇染块数量，不能只依赖整张纯色 Plane。");
            Assert.IsNotNull(buildPools,
                "水墨地表需要生成不规则洇染块的入口。");

            GameObject host = new GameObject("InkPoolVisualTest_Host");
            try
            {
                BambooSceneContext context = host.AddComponent<BambooSceneContext>();
                countField.SetValue(context, 3);
                buildPools.Invoke(context, null);

                Assert.AreEqual(3, CountDirectChildrenNamed(host.transform, "InkPool_"));
                MeshFilter mesh = host.transform.Find("InkPool_00").GetComponent<MeshFilter>();
                Assert.Greater(mesh.sharedMesh.vertexCount, 8,
                    "洇染块应是不规则多边形，不能退化成方形 Quad。");
                Color[] colors = mesh.sharedMesh.colors;
                Assert.Greater(colors.Length, 1,
                    "洇染块需要顶点颜色，才能从中心向边缘渐隐。");
                Assert.Greater(colors[0].a, colors[1].a,
                    "洇染块应从中心向边缘渐隐，不能保持一圈生硬的等深轮廓。");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        private static int CountDirectChildrenNamed(Transform root, string prefix)
        {
            int count = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                if (root.GetChild(i).name.StartsWith(prefix))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
