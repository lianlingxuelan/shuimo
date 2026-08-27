using System.Collections.Generic;
using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>Layered, collider-free water-ink presentation for one navigation landmark.</summary>
    public sealed class NavigationLandmarkView : MonoBehaviour
    {
        public const string RootName = "NavigationLandmarks_Root";

        private static readonly Color Ink = new Color(0.14f, 0.16f, 0.14f, 0.88f);
        private static readonly Color InkLight = new Color(0.28f, 0.30f, 0.25f, 0.72f);
        private static readonly Color Bamboo = new Color(0.30f, 0.39f, 0.27f, 0.88f);
        private static readonly Color Paper = new Color(0.64f, 0.58f, 0.43f, 0.90f);
        private static readonly Color Warm = new Color(0.68f, 0.43f, 0.19f, 0.82f);
        private static readonly Color Shadow = new Color(0.10f, 0.12f, 0.10f, 0.24f);
        private static readonly Color Transparent = new Color(0f, 0f, 0f, 0f);
        private static readonly Dictionary<Transform, Transform> OwnedRoots =
            new Dictionary<Transform, Transform>();
        private static readonly Dictionary<Transform, Transform> OwnersByRoot =
            new Dictionary<Transform, Transform>();

        private readonly List<PrimitiveBinding> primitiveBindings = new List<PrimitiveBinding>();

        private readonly struct PrimitiveBinding
        {
            public SpriteRenderer Renderer { get; }
            public string CircleKey { get; }

            public PrimitiveBinding(SpriteRenderer renderer, string circleKey)
            {
                Renderer = renderer;
                CircleKey = circleKey;
            }

            public bool IsCircle => !string.IsNullOrEmpty(CircleKey);
        }

        public NavigationLandmarkKind Kind { get; private set; }

        public static Transform BuildRoot(
            Transform owner,
            FirstChapterLayout layout,
            uint seed,
            Sprite bambooSprite,
            Sprite rockSprite)
        {
            if (owner == null || layout == null)
            {
                return null;
            }

            Transform existing;
            if (OwnedRoots.TryGetValue(owner, out existing) && existing != null)
            {
                DestroyOwnedRoot(existing);
            }
            else
            {
                OwnedRoots.Remove(owner);
            }

            Transform root = new GameObject(RootName).transform;
            root.SetParent(null, false);
            root.position = new Vector3(0f, 0f, 0f);
            root.localRotation = Quaternion.Euler(0f, 0f, 0f);
            root.localScale = Vector3.one;
            OwnedRoots[owner] = root;
            OwnersByRoot[root] = owner;

            NavigationLandmarkPlacement[] placements = NavigationLandmarkPlan.Create(layout, seed);
            for (int i = 0; i < placements.Length; i++)
            {
                Build(root, placements[i], i, bambooSprite, rockSprite);
            }

            return root;
        }

        public static void DestroyOwnedRoot(Transform root)
        {
            if (root == null)
            {
                return;
            }

            Transform owner;
            if (!OwnersByRoot.TryGetValue(root, out owner))
            {
                return;
            }

            OwnersByRoot.Remove(root);
            Transform current;
            if (OwnedRoots.TryGetValue(owner, out current) && current == root)
            {
                OwnedRoots.Remove(owner);
            }

            root.gameObject.SetActive(false);
            root.name = RootName + "_Retired";
            root.SetParent(null, true);

            if (Application.isPlaying)
            {
                Object.Destroy(root.gameObject);
            }
            else
            {
                Object.DestroyImmediate(root.gameObject);
            }
        }

        private void OnEnable()
        {
            SpriteFactory.Cleared += HandleSpriteFactoryCleared;
        }

        private void OnDisable()
        {
            SpriteFactory.Cleared -= HandleSpriteFactoryCleared;
        }

        private void HandleSpriteFactoryCleared()
        {
            for (int i = 0; i < primitiveBindings.Count; i++)
            {
                PrimitiveBinding binding = primitiveBindings[i];
                if (binding.Renderer == null)
                {
                    continue;
                }

                binding.Renderer.sprite = binding.IsCircle
                    ? SpriteFactory.Circle(binding.CircleKey, Color.white, Transparent, 0f)
                    : SpriteFactory.UiPixel();
            }
        }

        private static NavigationLandmarkView Build(
            Transform parent,
            NavigationLandmarkPlacement placement,
            int index,
            Sprite bambooSprite,
            Sprite rockSprite)
        {
            GameObject host = new GameObject(string.Format(
                "NavigationLandmark_{0:D2}_{1}",
                index + 1,
                placement.Kind));
            host.transform.SetParent(parent, false);
            host.transform.position = new Vector3(placement.Position.x, placement.Position.y, 0f);
            host.transform.localScale = Vector3.one * placement.Scale;

            NavigationLandmarkView view = host.AddComponent<NavigationLandmarkView>();
            view.Kind = placement.Kind;
            view.BuildLayers(placement.SortingOrder, bambooSprite, rockSprite);
            return view;
        }

        private void BuildLayers(int order, Sprite bambooSprite, Sprite rockSprite)
        {
            switch (Kind)
            {
                case NavigationLandmarkKind.BambooClump:
                    BuildBamboo(order, bambooSprite);
                    break;
                case NavigationLandmarkKind.ScholarRock:
                    BuildRock(order, rockSprite);
                    break;
                case NavigationLandmarkKind.Sign:
                    BuildSign(order);
                    break;
                case NavigationLandmarkKind.Lantern:
                    BuildLantern(order);
                    break;
                case NavigationLandmarkKind.Basket:
                    BuildBasket(order);
                    break;
                case NavigationLandmarkKind.BuildingSilhouette:
                    BuildBuilding(order);
                    break;
            }
        }

        private void BuildBamboo(int order, Sprite sprite)
        {
            if (sprite != null)
            {
                MakeLayer("Painted_Bamboo", sprite, Bamboo, new Vector2(0f, 42f), new Vector2(8f, 8f), 0f, order);
            }
            else
            {
                MakeRect("Bamboo_Trunk_A", Ink, new Vector2(-13f, 45f), new Vector2(7f, 92f), -3f, order);
                MakeRect("Bamboo_Trunk_B", Bamboo, new Vector2(11f, 38f), new Vector2(6f, 80f), 4f, order + 1);
                MakeCircle("Bamboo_Leaves_A", Bamboo, new Vector2(-22f, 80f), new Vector2(48f, 24f), -18f, order + 2);
                MakeCircle("Bamboo_Leaves_B", InkLight, new Vector2(22f, 68f), new Vector2(42f, 22f), 19f, order + 2);
            }
            MakeCircle("Bamboo_Shadow", Shadow, new Vector2(0f, 1f), new Vector2(92f, 22f), 0f, order - 1);
        }

        private void BuildRock(int order, Sprite sprite)
        {
            if (sprite != null)
            {
                MakeLayer("Painted_Scholar_Rock", sprite, InkLight, new Vector2(0f, 34f), new Vector2(7f, 7f), 0f, order);
            }
            else
            {
                MakeCircle("Scholar_Rock_Silhouette", Ink, new Vector2(0f, 32f), new Vector2(92f, 72f), -7f, order);
                MakeCircle("Scholar_Rock_Wash", InkLight, new Vector2(-8f, 40f), new Vector2(54f, 42f), 12f, order + 1);
            }
            MakeCircle("Scholar_Rock_Shadow", Shadow, new Vector2(0f, 1f), new Vector2(104f, 24f), 0f, order - 1);
        }

        private void BuildSign(int order)
        {
            MakeRect("Sign_Post", Ink, new Vector2(0f, 37f), new Vector2(8f, 76f), 0f, order);
            MakeRect("Sign_Board", Paper, new Vector2(0f, 72f), new Vector2(64f, 30f), -2f, order + 1);
            MakeRect("Sign_Ink_Stroke", Ink, new Vector2(0f, 72f), new Vector2(42f, 5f), -2f, order + 2);
            MakeRect("Sign_Cap", InkLight, new Vector2(0f, 91f), new Vector2(74f, 7f), -2f, order + 2);
        }

        private void BuildLantern(int order)
        {
            MakeRect("Lantern_Post", Ink, new Vector2(0f, 41f), new Vector2(7f, 84f), 0f, order);
            MakeRect("Lantern_Arm", Ink, new Vector2(13f, 80f), new Vector2(32f, 6f), 0f, order + 1);
            MakeCircle("Lantern_Glow", Warm, new Vector2(25f, 61f), new Vector2(34f, 38f), 0f, order + 1);
            MakeRect("Lantern_Frame", Ink, new Vector2(25f, 61f), new Vector2(5f, 40f), 0f, order + 2);
            MakeRect("Lantern_Caps", InkLight, new Vector2(25f, 82f), new Vector2(36f, 6f), 0f, order + 2);
        }

        private void BuildBasket(int order)
        {
            MakeRect("Basket_Body", Paper, new Vector2(0f, 17f), new Vector2(62f, 31f), 0f, order);
            MakeRect("Basket_Rim", Ink, new Vector2(0f, 34f), new Vector2(68f, 7f), 0f, order + 2);
            MakeRect("Basket_Weave_A", InkLight, new Vector2(-18f, 17f), new Vector2(5f, 28f), -6f, order + 1);
            MakeRect("Basket_Weave_B", InkLight, new Vector2(18f, 17f), new Vector2(5f, 28f), 6f, order + 1);
            MakeRect("Basket_Handle_L", Ink, new Vector2(-23f, 49f), new Vector2(5f, 31f), -12f, order + 1);
            MakeRect("Basket_Handle_R", Ink, new Vector2(23f, 49f), new Vector2(5f, 31f), 12f, order + 1);
            MakeRect("Basket_Handle_Top", Ink, new Vector2(0f, 63f), new Vector2(48f, 5f), 0f, order + 1);
        }

        private void BuildBuilding(int order)
        {
            MakeRect("Building_Body", InkLight, new Vector2(0f, 47f), new Vector2(142f, 92f), 0f, order);
            MakeRect("Building_Door", Ink, new Vector2(0f, 30f), new Vector2(31f, 58f), 0f, order + 1);
            MakeRect("Building_Roof_L", Ink, new Vector2(-42f, 102f), new Vector2(96f, 16f), 9f, order + 2);
            MakeRect("Building_Roof_R", Ink, new Vector2(42f, 102f), new Vector2(96f, 16f), -9f, order + 2);
            MakeRect("Building_Eaves", Paper, new Vector2(0f, 91f), new Vector2(176f, 7f), 0f, order + 3);
            MakeRect("Building_Window_L", Paper, new Vector2(-43f, 50f), new Vector2(23f, 28f), 0f, order + 1);
            MakeRect("Building_Window_R", Paper, new Vector2(43f, 50f), new Vector2(23f, 28f), 0f, order + 1);
        }

        private SpriteRenderer MakeRect(
            string name,
            Color color,
            Vector2 localPosition,
            Vector2 size,
            float angle,
            int order)
        {
            SpriteRenderer renderer = MakeSizedLayer(
                name,
                SpriteFactory.UiPixel(),
                color,
                localPosition,
                size,
                angle,
                order);
            primitiveBindings.Add(new PrimitiveBinding(renderer, null));
            return renderer;
        }

        private SpriteRenderer MakeCircle(
            string name,
            Color color,
            Vector2 localPosition,
            Vector2 size,
            float angle,
            int order)
        {
            string key = "navigation_landmark_" + name;
            Sprite sprite = SpriteFactory.Circle(
                key,
                Color.white,
                Transparent,
                0f);
            SpriteRenderer renderer = MakeSizedLayer(
                name,
                sprite,
                color,
                localPosition,
                size,
                angle,
                order);
            primitiveBindings.Add(new PrimitiveBinding(renderer, key));
            return renderer;
        }

        private SpriteRenderer MakeSizedLayer(
            string name,
            Sprite sprite,
            Color color,
            Vector2 localPosition,
            Vector2 worldSize,
            float angle,
            int order)
        {
            Vector3 spriteSize = sprite != null ? sprite.bounds.size : Vector3.one;
            float width = spriteSize.x > 0f ? spriteSize.x : 1f;
            float height = spriteSize.y > 0f ? spriteSize.y : 1f;
            return MakeLayer(
                name,
                sprite,
                color,
                localPosition,
                new Vector2(worldSize.x / width, worldSize.y / height),
                angle,
                order);
        }

        private SpriteRenderer MakeLayer(
            string name,
            Sprite sprite,
            Color color,
            Vector2 localPosition,
            Vector2 size,
            float angle,
            int order)
        {
            GameObject layer = new GameObject(name);
            layer.transform.SetParent(transform, false);
            layer.transform.localPosition = new Vector3(localPosition.x, localPosition.y, 0f);
            layer.transform.localScale = new Vector3(size.x, size.y, 1f);
            layer.transform.localRotation = Quaternion.Euler(0f, 0f, angle);

            SpriteRenderer renderer = layer.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.color = color;
            renderer.sortingOrder = order;
            return renderer;
        }
    }
}
