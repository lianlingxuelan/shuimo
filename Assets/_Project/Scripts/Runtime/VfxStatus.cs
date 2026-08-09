// -----------------------------------------------------------------------------
// VfxStatus.cs —— 状态染色特效：给中毒/灼烧/减速的角色罩一层对应颜色的光晕
//
// 【核心难题：身上同时挂 4 个状态时，该显示什么颜色？】
// 把 4 种颜色混合平均，结果一定是一坨浑浊的灰褐色，玩家什么也读不出来。
// 本文件的解法是"优先级 + 单色显示"：只显示优先级最高的那个状态的颜色，
// 其余状态只通过"让光晕更浓"来体现存在感（见 Recompute）。
// 这样玩家一眼就能读出"最要紧的那个状态是什么"，同时也能感知"身上状态很多"。
// 信息设计上，突出重点永远优于平铺全部。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using Xianxia.Combat;

namespace Xianxia.Unity.T2
{
    /// <summary>挂在角色上的状态染色组件。按需自动创建光晕子物体。</summary>
    [DisallowMultipleComponent]
    public sealed class VfxStatus : MonoBehaviour
    {
        /// <summary>光晕的渲染层偏移，保证盖在角色本体之上。</summary>
        public const int SortingOrder = 12;

        /// <summary>单个状态的基础不透明度。</summary>
        public const float BaseAlpha = 0.18f;

        /// <summary>每多一层/多一个状态追加的不透明度。</summary>
        public const float StackAlpha = 0.06f;

        /// <summary>
        /// 不透明度上限。
        /// 【为什么必须封顶】没有上限的话，叠满状态时光晕会浓到把角色完全糊住，
        /// 玩家看不见自己的动作和位置 —— 特效反而妨碍了游戏。
        /// 凡是"可累加的视觉强度"都必须设上限，这是特效设计的铁律。
        /// </summary>
        public const float MaxAlpha = 0.46f;

        /// <summary>颜色过渡速度。见 LateUpdate 的插值。</summary>
        public const float BlendSpeed = 12.0f;

        // 各状态的代表色。用 static readonly 而非 const 是因为 Color 是结构体，不能声明为 const。
        public static readonly Color PoisonTint = new Color(0.36f, 0.86f, 0.42f, 1.0f);    // 毒=绿
        public static readonly Color BurnTint = new Color(0.98f, 0.44f, 0.24f, 1.0f);      // 灼=橙红
        public static readonly Color SlowTint = new Color(0.42f, 0.66f, 0.98f, 1.0f);      // 缓=冰蓝
        public static readonly Color BreakDefTint = new Color(0.72f, 0.44f, 0.94f, 1.0f);  // 破防=紫
        public static readonly Color GenericTint = new Color(0.86f, 0.86f, 0.86f, 1.0f);   // 未知=灰白

        /// <summary>一条正在生效的状态记录（仅特效层需要的最小信息）。</summary>
        private struct Entry
        {
            public string Id;
            public int Stacks;
            public Color Tint;
            public int Priority;
        }

        private readonly List<Entry> _entries = new List<Entry>(4);
        private SpriteRenderer _overlay;
        private Color _targetColor = new Color(1.0f, 1.0f, 1.0f, 0.0f);
        private float _radius = 16.0f;
        private Transform _anchor;

        public int ActiveCount
        {
            get { return _entries.Count; }
        }

        public Transform Anchor
        {
            get { return _anchor; }
            set
            {
                _anchor = value;
                if (_overlay != null && _anchor != null)
                {
                    _overlay.transform.SetParent(_anchor, false);
                    _overlay.transform.localPosition = Vector3.zero;
                }
            }
        }

        public static Color TintFor(string statusId)
        {
            if (string.IsNullOrEmpty(statusId))
            {
                return GenericTint;
            }
            if (statusId == StatusConfig.SE_POISON)
            {
                return PoisonTint;
            }
            if (statusId == StatusConfig.SE_BURN)
            {
                return BurnTint;
            }
            if (statusId == StatusConfig.SE_SLOW)
            {
                return SlowTint;
            }
            if (statusId == StatusConfig.SE_BREAK_DEF)
            {
                return BreakDefTint;
            }
            return GenericTint;
        }

        /// <summary>
        /// 按状态定义取颜色：先查内置配色，查不到再用定义里自带的 RGB。
        ///
        /// 【这是"约定优于配置"的一个实例】
        /// 四个核心状态的颜色写死在代码里，保证美术观感统一、不会被策划误配成难看的色；
        /// 而策划新加的自定义状态，可以在数据表里自带颜色，无需改代码。
        /// 常用的走内置、特殊的可扩展，两头都照顾到。
        ///
        /// 注意这里用"是否等于 GenericTint"来判断"有没有命中内置配色"，
        /// 属于用返回值兼做标志位，略微含蓄。代价是：如果哪天真有个状态的自定义色
        /// 恰好等于 GenericTint，它会被误判成"没命中"。当前配色下不会发生，
        /// 但这是本方法一个需要留意的隐含前提。
        /// </summary>
        public static Color TintFor(StatusEffectDef def)
        {
            if (def == null)
            {
                return GenericTint;
            }
            Color byId = TintFor(def.Id);
            if (byId != GenericTint)
            {
                return byId;
            }
            return new Color(def.TintR, def.TintG, def.TintB, 1.0f);
        }

        /// <summary>
        /// 状态的显示优先级，数字越大越优先占据光晕颜色。
        ///
        /// 【这个排序不是随便定的，它反映"哪个信息对玩家最要紧"】
        ///   破防(4) 最高 —— 直接关系到"现在打他伤害翻倍"，是行动决策依据；
        ///   灼烧(3)、中毒(2) 次之 —— 掉血速度不同，灼烧更急；
        ///   减速(1) 最低 —— 影响最温和；
        ///   未知(0) 兜底。
        /// 排序依据是"看到它玩家会不会改变操作"，而不是"哪个特效好看"。
        /// </summary>
        public static int PriorityFor(string statusId)
        {
            if (statusId == StatusConfig.SE_BREAK_DEF)
            {
                return 4;
            }
            if (statusId == StatusConfig.SE_BURN)
            {
                return 3;
            }
            if (statusId == StatusConfig.SE_POISON)
            {
                return 2;
            }
            if (statusId == StatusConfig.SE_SLOW)
            {
                return 1;
            }
            return 0;
        }

        /// <summary>
        /// 取得（没有就自动挂上）某个物体的状态特效组件。
        ///
        /// 【这个"有则取、无则加"的静态工厂方法很值得学】
        /// 调用方只需写 VfxStatus.For(enemy).Apply(...)，完全不用关心
        /// 这个敌人身上到底挂没挂过特效组件、是不是第一次中毒。
        /// 把"确保存在"的复杂度收敛在一处，调用点就能保持干净，也不会因为
        /// 忘了初始化而空引用。Unity 里给动态生成的物体加组件时非常常用。
        /// </summary>
        public static VfxStatus For(GameObject host)
        {
            if (host == null)
            {
                return null;
            }
            VfxStatus vfx = host.GetComponent<VfxStatus>();
            if (vfx == null)
            {
                vfx = host.AddComponent<VfxStatus>();
            }
            return vfx;
        }

        public void SetRadius(float radius)
        {
            if (radius > 0.0f)
            {
                _radius = radius;
            }
            ApplyScale();
        }

        public void Apply(string statusId, int stacks, Color tint)
        {
            if (string.IsNullOrEmpty(statusId))
            {
                return;
            }
            int n = stacks > 0 ? stacks : 1;
            // 先找有没有同 id 的旧记录：同一个状态刷新层数时应当"更新"而不是"再加一条"，
            // 否则列表里会堆积出一堆重复的中毒条目，浓度计算全错。
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Id == statusId)
                {
                    // 【这三行是 struct 存在 List 里时的必要写法，不是啰嗦】
                    // Entry 是结构体（值类型）。_entries[i] 返回的是一份**拷贝**，
                    // 直接写 _entries[i].Stacks = n 编译器会直接报错，
                    // 因为改的是临时拷贝、改了也没用。
                    // 所以必须：取出拷贝 → 改拷贝 → 把拷贝写回原位置。
                    // 这是值类型与引用类型行为差异最容易绊倒新手的地方之一。
                    Entry existing = _entries[i];
                    existing.Stacks = n;
                    existing.Tint = tint;
                    _entries[i] = existing;
                    Recompute();
                    return;
                }
            }

            Entry entry;
            entry.Id = statusId;
            entry.Stacks = n;
            entry.Tint = tint;
            entry.Priority = PriorityFor(statusId);
            _entries.Add(entry);
            Recompute();
        }

        public void Apply(ActiveStatus status)
        {
            if (status == null || status.Def == null)
            {
                return;
            }
            Apply(status.Def.Id, status.Stacks, TintFor(status.Def));
        }

        public void Remove(string statusId)
        {
            if (string.IsNullOrEmpty(statusId))
            {
                return;
            }
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Id == statusId)
                {
                    _entries.RemoveAt(i);
                    Recompute();
                    return;
                }
            }
        }

        public void Clear()
        {
            _entries.Clear();
            Recompute();
        }

        /// <summary>
        /// 重算目标颜色：选出优先级最高的状态定色，用状态总量定浓度。
        /// 注意本方法只算出"目标值"，不直接改画面 —— 真正的过渡在 LateUpdate。
        /// </summary>
        private void Recompute()
        {
            if (_entries.Count == 0)
            {
                // 【只把 alpha 归零，刻意保留 RGB 不变】
                // 如果连 RGB 一起重置成白色，LateUpdate 的插值会让光晕在消失过程中
                // 先从绿色渐变成白色再淡出 —— 玩家会看到一次莫名其妙的"变白"闪烁。
                // 保留原色只降透明度，才是干净的淡出。
                _targetColor = new Color(_targetColor.r, _targetColor.g, _targetColor.b, 0.0f);
                return;
            }

            // 线性扫描找优先级最高者。条目最多四五个，不值得为此排序或建堆。
            int best = 0;
            for (int i = 1; i < _entries.Count; i++)
            {
                if (_entries[i].Priority > _entries[best].Priority)
                {
                    best = i;
                }
            }

            // 浓度公式：基础值 + 主状态的额外层数 + 其它状态的数量
            //   BaseAlpha                          —— 有一个状态就有的底噪
            //   StackAlpha * (top.Stacks - 1)      —— 主状态每多一层更浓（减 1 是因为第 1 层已计入基础值）
            //   StackAlpha * (_entries.Count - 1)  —— 每多一种别的状态也更浓（同理减 1）
            // 这就是前面说的"其余状态不抢颜色、但贡献浓度"的具体实现：
            // 玩家看颜色知道最危险的是什么，看浓度知道自己身上有多脏。
            Entry top = _entries[best];
            float alpha = BaseAlpha + StackAlpha * (top.Stacks - 1);
            if (_entries.Count > 1)
            {
                alpha += StackAlpha * (_entries.Count - 1);
            }
            if (alpha > MaxAlpha)
            {
                alpha = MaxAlpha;   // 封顶，理由见 MaxAlpha 注释
            }
            _targetColor = new Color(top.Tint.r, top.Tint.g, top.Tint.b, alpha);
        }

        /// <summary>
        /// 每帧把当前颜色朝目标颜色平滑靠拢。
        ///
        /// 【为什么用 LateUpdate 而不是 Update】
        /// LateUpdate 在所有 Update 之后执行。角色的移动、状态的增删都在 Update 阶段完成，
        /// 特效在 LateUpdate 里贴上去，才能保证光晕跟的是角色**这一帧最终**的位置，
        /// 不会出现光晕滞后于角色的"拖影"。凡是"跟随/贴附"类逻辑都应放 LateUpdate。
        /// </summary>
        private void LateUpdate()
        {
            // 【惰性创建：没有状态时连光晕物体都不建】
            // 场上几十个敌人，绝大多数身上没有任何状态。若开局给每个都建一个光晕
            // SpriteRenderer，就是几十个白白参与渲染剔除的对象。
            // 等真的需要显示时（alpha > 0）才创建，是很实在的优化。
            if (_overlay == null)
            {
                if (_targetColor.a <= 0.001f)
                {
                    return;
                }
                EnsureOverlay();
            }

            // 【为什么要插值过渡，而不是直接 _overlay.color = _targetColor】
            // 状态的增删是瞬间事件。直接赋值会让光晕"啪"地跳变，非常廉价刺眼；
            // 尤其是状态反复刷新时会疯狂闪烁。用 Lerp 拉出 0.1 秒左右的过渡，
            // 观感立刻高级很多。这是特效里性价比最高的一招。
            //
            // 乘 Time.deltaTime 是为了让过渡速度与帧率无关：
            // 不乘的话，144 帧的机器过渡速度会是 60 帧机器的 2.4 倍。
            // Clamp01 则防止极端卡顿（deltaTime 很大）时插值系数超过 1 导致过冲。
            Color current = _overlay.color;
            float k = Mathf.Clamp01(Time.deltaTime * BlendSpeed);
            Color next = Color.Lerp(current, _targetColor, k);
            _overlay.color = next;

            // 完全透明时干脆关掉渲染器：透明物体依然会走渲染管线（且透明混合开销不小），
            // 关掉能实打实省一笔。阈值取 0.004 而不是 0，是因为 Lerp 是渐近的，
            // 数学上永远除不尽、alpha 会无限接近 0 却不等于 0。
            bool visible = next.a > 0.004f;
            if (_overlay.enabled != visible)
            {
                _overlay.enabled = visible;
            }
        }

        private void EnsureOverlay()
        {
            if (_overlay != null)
            {
                return;
            }

            SpriteRenderer body = GetComponentInChildren<SpriteRenderer>();
            if (body != null && body.sprite != null)
            {
                _radius = Mathf.Max(4.0f, body.bounds.extents.magnitude * 0.72f);
            }

            GameObject go = new GameObject("StatusTint");
            go.transform.SetParent(_anchor != null ? _anchor : transform, false);
            go.transform.localPosition = Vector3.zero;

            _overlay = go.AddComponent<SpriteRenderer>();
            _overlay.sprite = SpriteFactory.Circle(
                "status_tint", Color.white, new Color(0.0f, 0.0f, 0.0f, 0.0f), 0.0f);
            _overlay.sortingOrder = body != null ? body.sortingOrder + SortingOrder : SortingOrder;
            _overlay.color = new Color(_targetColor.r, _targetColor.g, _targetColor.b, 0.0f);

            ApplyScale();
        }

        private void ApplyScale()
        {
            if (_overlay == null)
            {
                return;
            }
            float scale = _radius * 2.0f / SpriteFactory.ShapePixels;
            _overlay.transform.localScale = new Vector3(scale, scale, 1.0f);
        }
    }
}
