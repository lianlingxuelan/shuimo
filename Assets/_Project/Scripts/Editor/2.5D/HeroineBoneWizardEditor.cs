using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;
using UnityEngine.U2D;

namespace Shuimo.EditorTools
{
    public static class HeroineBoneWizardEditor
    {
        private const string HeroineSpritePath = "Assets/_Project/Art/Characters/Heroine2D/heroine_base_open.png";

        [MenuItem("Shuimo/2.5D/生成女主默认骨骼", false, 210)]
        public static void GenerateDefaultBones()
        {
            var textureImporter = AssetImporter.GetAtPath(HeroineSpritePath) as TextureImporter;
            if (textureImporter == null)
            {
                Debug.LogError($"[HeroineBoneWizard] 找不到文件: {HeroineSpritePath}");
                return;
            }

            var factory = new SpriteDataProviderFactories();
            factory.Init();
            var dataProvider = factory.GetSpriteEditorDataProviderFromObject(textureImporter);
            dataProvider.InitSpriteEditorDataProvider();

            var spriteRects = dataProvider.GetSpriteRects();
            if (spriteRects == null || spriteRects.Length == 0)
            {
                Debug.LogError("[HeroineBoneWizard] 没有 SpriteRect，请先确认 Texture Type = Sprite (2D and UI) 且 Sprite Mode = Single。");
                return;
            }

            var spriteRect = spriteRects[0];
            var guid = spriteRect.spriteID;

            var boneDataProvider = dataProvider.GetDataProvider<ISpriteBoneDataProvider>();
            var existing = boneDataProvider.GetBones(guid);
            if (existing != null && existing.Count > 0)
            {
                if (!EditorUtility.DisplayDialog("骨骼已存在",
                    $"当前 Sprite 已经有 {existing.Count} 根骨骼。继续会覆盖为默认骨架。", "覆盖", "取消"))
                {
                    return;
                }
            }

            var size = new Vector2(spriteRect.rect.width, spriteRect.rect.height);
            var bones = BuildSkeleton(size);

            boneDataProvider.SetBones(guid, bones);
            dataProvider.Apply();

            textureImporter.SaveAndReimport();
            EditorUtility.DisplayDialog("完成",
                $"已为 heroine_base_open 生成 {bones.Count} 根默认骨骼。\n\n" +
                "下一步：打开 Sprite Editor → Skinning Editor，按 Auto Weights 生成权重，然后微调骨骼位置。",
                "OK");
        }

        private static List<SpriteBone> BuildSkeleton(Vector2 size)
        {
            // 原点 = sprite pivot（当前为 Center），单位 = 像素，y 向上
            var half = size * 0.5f;

            // 定义：name, parentName, start（绝对像素坐标），end（绝对像素坐标）
            var defs = new List<BoneDef>
            {
                // 躯干
                new BoneDef("hip", null, new Vector2(0, -60), new Vector2(0, 40)),
                new BoneDef("spine", "hip", new Vector2(0, 40), new Vector2(0, 130)),
                new BoneDef("chest", "spine", new Vector2(0, 130), new Vector2(0, 210)),
                new BoneDef("neck", "chest", new Vector2(0, 210), new Vector2(0, 250)),
                new BoneDef("head", "neck", new Vector2(0, 250), new Vector2(0, 340)),
                new BoneDef("head_top", "head", new Vector2(0, 340), new Vector2(0, 400)),

                // 手臂（张开姿态）
                new BoneDef("shoulder_L", "chest", new Vector2(-130, 170), new Vector2(-210, 160)),
                new BoneDef("elbow_L", "shoulder_L", new Vector2(-210, 160), new Vector2(-280, 150)),
                new BoneDef("wrist_L", "elbow_L", new Vector2(-280, 150), new Vector2(-330, 145)),

                new BoneDef("shoulder_R", "chest", new Vector2(130, 170), new Vector2(210, 160)),
                new BoneDef("elbow_R", "shoulder_R", new Vector2(210, 160), new Vector2(280, 150)),
                new BoneDef("wrist_R", "elbow_R", new Vector2(280, 150), new Vector2(330, 145)),

                // 腿
                new BoneDef("thigh_L", "hip", new Vector2(-55, -60), new Vector2(-60, -280)),
                new BoneDef("shin_L", "thigh_L", new Vector2(-60, -280), new Vector2(-65, -470)),
                new BoneDef("foot_L", "shin_L", new Vector2(-65, -470), new Vector2(-65, -500)),

                new BoneDef("thigh_R", "hip", new Vector2(55, -60), new Vector2(60, -280)),
                new BoneDef("shin_R", "thigh_R", new Vector2(60, -280), new Vector2(65, -470)),
                new BoneDef("foot_R", "shin_R", new Vector2(65, -470), new Vector2(65, -500)),

                // 头发/裙摆装饰
                new BoneDef("hair_back_L", "head", new Vector2(-50, 280), new Vector2(-90, 220)),
                new BoneDef("hair_back_R", "head", new Vector2(50, 280), new Vector2(90, 220)),
                new BoneDef("hair_front_L", "head", new Vector2(-35, 290), new Vector2(-60, 250)),
                new BoneDef("hair_front_R", "head", new Vector2(35, 290), new Vector2(60, 250)),

                new BoneDef("skirt_L", "hip", new Vector2(-40, -30), new Vector2(-130, -120)),
                new BoneDef("skirt_R", "hip", new Vector2(40, -30), new Vector2(130, -120)),
            };

            // 名字 -> 索引
            var nameToIndex = new Dictionary<string, int>();
            for (int i = 0; i < defs.Count; i++)
                nameToIndex[defs[i].Name] = i;

            // 先按 parent 排在前面（当前 defs 已经满足），保险起见做拓扑排序
            var ordered = TopologicalSort(defs, nameToIndex);
            nameToIndex.Clear();
            for (int i = 0; i < ordered.Count; i++)
                nameToIndex[ordered[i].Name] = i;

            var spriteBones = new List<SpriteBone>(ordered.Count);

            // 计算每个 bone 的绝对旋转（从 start 指向 end）
            var absRotations = new Quaternion[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                var d = ordered[i];
                var dir = (d.End - d.Start);
                dir.z = 0f;
                absRotations[i] = Quaternion.FromToRotation(Vector3.right, dir.normalized);
            }

            for (int i = 0; i < ordered.Count; i++)
            {
                var d = ordered[i];
                int parentId = -1;
                if (!string.IsNullOrEmpty(d.ParentName) && nameToIndex.TryGetValue(d.ParentName, out var pi))
                    parentId = pi;

                Vector3 localPos;
                Quaternion localRot;
                if (parentId < 0)
                {
                    localPos = new Vector3(d.Start.x, d.Start.y, 0f);
                    localRot = absRotations[i];
                }
                else
                {
                    var parentDef = ordered[parentId];
                    var parentAbsRot = absRotations[parentId];
                    var parentStart = new Vector3(parentDef.Start.x, parentDef.Start.y, 0f);
                    var childStart = new Vector3(d.Start.x, d.Start.y, 0f);
                    localPos = Quaternion.Inverse(parentAbsRot) * (childStart - parentStart);
                    localRot = Quaternion.Inverse(parentAbsRot) * absRotations[i];
                }

                float length = Vector2.Distance(d.Start, d.End);

                spriteBones.Add(new SpriteBone
                {
                    name = d.Name,
                    guid = System.Guid.NewGuid().ToString("N"),
                    position = localPos,
                    rotation = localRot,
                    length = length,
                    parentId = parentId,
                    color = new Color32(255, 255, 255, 255)
                });
            }

            return spriteBones;
        }

        private static List<BoneDef> TopologicalSort(List<BoneDef> defs, Dictionary<string, int> originalIndex)
        {
            var result = new List<BoneDef>(defs.Count);
            var visited = new HashSet<string>();
            foreach (var d in defs)
                Visit(d, defs, originalIndex, visited, result, new HashSet<string>());
            return result;
        }

        private static void Visit(BoneDef d, List<BoneDef> defs, Dictionary<string, int> indexMap,
            HashSet<string> visited, List<BoneDef> result, HashSet<string> visiting)
        {
            if (visited.Contains(d.Name)) return;
            if (visiting.Contains(d.Name))
                throw new InvalidOperationException($"骨骼循环依赖: {d.Name}");
            visiting.Add(d.Name);
            if (!string.IsNullOrEmpty(d.ParentName) && indexMap.TryGetValue(d.ParentName, out var pi))
                Visit(defs[pi], defs, indexMap, visited, result, visiting);
            visiting.Remove(d.Name);
            visited.Add(d.Name);
            result.Add(d);
        }

        private class BoneDef
        {
            public string Name;
            public string ParentName;
            public Vector2 Start;
            public Vector2 End;

            public BoneDef(string name, string parentName, Vector2 start, Vector2 end)
            {
                Name = name;
                ParentName = parentName;
                Start = start;
                End = end;
            }
        }
    }
}
