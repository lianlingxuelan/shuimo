using UnityEngine;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>单株竹砍断时的轻量表现：几片竹叶飞散并显示材料获得。</summary>
    public sealed class BambooHarvestFeedback : MonoBehaviour
    {
        private const float Lifetime = 0.55f;
        private const int LeafCount = 6;

        private SpriteRenderer[] _leaves;
        private Vector3[] _velocity;
        private TextMesh _notice;
        private float _age;

        public static void Play(Vector3 position, string notice = "竹材 +1")
        {
            GameObject root = new GameObject("BambooHarvestFeedback");
            root.transform.position = new Vector3(position.x, position.y, -0.45f);
            root.AddComponent<BambooHarvestFeedback>().Build(notice);
        }

        private void Build(string notice)
        {
            Sprite leaf = SpriteFactory.Crescent("harvest_leaf", new Color(0.22f, 0.38f, 0.18f, 0.92f), 34.0f, 0.26f);
            _leaves = new SpriteRenderer[LeafCount];
            _velocity = new Vector3[LeafCount];
            for (int i = 0; i < LeafCount; i++)
            {
                GameObject part = new GameObject("Leaf");
                part.transform.SetParent(transform, false);
                part.transform.localRotation = Quaternion.Euler(0.0f, 0.0f, i * 57.0f);
                part.transform.localScale = Vector3.one * 0.22f;
                SpriteRenderer renderer = part.AddComponent<SpriteRenderer>();
                renderer.sprite = leaf;
                renderer.sortingOrder = VfxSlash.SortingOrder + 1;
                _leaves[i] = renderer;

                float angle = i * Mathf.PI * 2.0f / LeafCount + 0.23f;
                _velocity[i] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle) + 0.55f, 0.0f)
                    * (38.0f + (i % 3) * 9.0f);
            }

            GameObject text = new GameObject("BambooWoodNotice");
            text.transform.SetParent(transform, false);
            text.transform.localPosition = new Vector3(0.0f, 48.0f, 0.0f);
            _notice = text.AddComponent<TextMesh>();
            _notice.text = notice;
            _notice.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _notice.fontSize = 28;
            _notice.characterSize = 0.23f;
            _notice.anchor = TextAnchor.MiddleCenter;
            _notice.alignment = TextAlignment.Center;
            _notice.color = new Color(0.18f, 0.33f, 0.16f, 1.0f);
            _notice.GetComponent<MeshRenderer>().sortingOrder = VfxSlash.SortingOrder + 2;
        }

        private void Update()
        {
            float dt = FeedbackClock.Delta;
            if (dt <= 0.0f)
            {
                return;
            }
            _age += dt;
            float t = Mathf.Clamp01(_age / Lifetime);
            for (int i = 0; i < _leaves.Length; i++)
            {
                SpriteRenderer leaf = _leaves[i];
                if (leaf == null)
                {
                    continue;
                }
                leaf.transform.localPosition += _velocity[i] * dt;
                leaf.transform.Rotate(0.0f, 0.0f, 210.0f * dt);
                Color color = leaf.color;
                color.a = (1.0f - t) * 0.92f;
                leaf.color = color;
            }
            if (_notice != null)
            {
                _notice.transform.localPosition += Vector3.up * (24.0f * dt);
                Color color = _notice.color;
                color.a = 1.0f - t;
                _notice.color = color;
            }
            if (_age >= Lifetime)
            {
                Destroy(gameObject);
            }
        }
    }
}
