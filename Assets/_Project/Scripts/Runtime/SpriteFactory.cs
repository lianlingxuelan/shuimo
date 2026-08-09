// -----------------------------------------------------------------------------
// SpriteFactory.cs —— 零美术资源的运行时贴图工厂（asmdef: Xianxia.Unity.T2）
//
// 【为什么需要它：PRD P0-11「不引入任何美术二进制资源」】
// 切片里的每一个可见物——地块、玩家方块、敌人圆点、剑气月牙、HUD 血条——
// 全部是这里用 Texture2D 逐像素画出来的。工程里因此一张 png 都没有，
// clone 下来直接能跑，也不会有人问「美术资源在哪个网盘」。
//
// 【缓存与释放】
// Texture2D / Sprite 是**非托管资源**，GC 不会回收。世界每重建一次就 new 一批，
// 点二十次 Clean And Rebuild 就泄漏二十份。所以这里按 key 缓存，
// 并由 WorldBuilder 在每次重建前调 Clear() 显式销毁上一批。
//
// 【单一材质 = 单 DrawCall】
// 地形的四种地块共用**同一张**白色贴图，靠 TileBase.color 上色。同贴图同材质
// 才能被 Unity 合批成 1 个 DrawCall（PRD Q9 的 9600 格要求）。
// -----------------------------------------------------------------------------

// 注意：这里**刻意不写** `using System;`。本文件已有的 SafeDestroy(Object o) 用的是
// UnityEngine.Object，一旦引入 System 命名空间，简单名 Object 就会在 System.Object 与
// UnityEngine.Object 之间产生二义（CS0104）。异常类型改用全限定名 System.Exception 规避。
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>运行时生成的 Sprite / Texture 仓库。全静态，带缓存。</summary>
    public static class SpriteFactory
    {
        // key ⇒ 资源。两张表分开存，因为 Sprite 和 Texture 的销毁互不影响。
        private static readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>(16);
        private static readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>(16);

        // 已经警告过的 PNG 相对路径。缺一张图会在每帧的 Apply() 里反复命中失败分支，
        // 不去重的话 Console 会被同一行 Warning 刷爆，真正的报错反而被淹没。
        private static readonly HashSet<string> _pngWarned = new HashSet<string>();

        /// <summary>地块贴图边长（像素）。与 <see cref="WorldBuilder.TileUnit"/> 等值，PPU 取 1。</summary>
        public const int TilePixels = 32;

        /// <summary>圆形 / 月牙贴图的边长（像素）。取 64 兼顾锯齿与内存。</summary>
        public const int ShapePixels = 64;

        /// <summary>
        /// 销毁全部缓存资源。**必须**在世界重建前调用，否则每重建一次泄漏一批贴图。
        /// </summary>
        public static void Clear()
        {
            foreach (KeyValuePair<string, Sprite> kv in _sprites)
            {
                SafeDestroy(kv.Value);
            }
            foreach (KeyValuePair<string, Texture2D> kv in _textures)
            {
                SafeDestroy(kv.Value);
            }
            _sprites.Clear();
            _textures.Clear();
            // 缓存清空后，之前"缺图"的判断不再有效（用户可能刚把 StreamingAssets 补齐），
            // 所以警告去重表也要跟着重置，否则补好图之后反而再也看不到新的警告。
            _pngWarned.Clear();
        }

        /// <summary>
        /// 纯白正方形 Sprite，PPU=1 ⇒ 世界尺寸 = 像素数。
        /// 地形四种地块全部复用它，颜色交给 <c>Tile.color</c> —— 同贴图才能合批。
        /// </summary>
        public static Sprite WhiteTile()
        {
            Sprite cached;
            if (_sprites.TryGetValue("tile_white", out cached) && cached != null)
            {
                return cached;
            }

            Texture2D tex = NewTexture("tile_white", TilePixels, TilePixels);
            Color32[] px = new Color32[TilePixels * TilePixels];
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32(255, 255, 255, 255);
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);

            Sprite s = Sprite.Create(tex, new Rect(0.0f, 0.0f, TilePixels, TilePixels),
                                     new Vector2(0.5f, 0.5f), 1.0f, 0u, SpriteMeshType.FullRect);
            s.name = "sp_tile_white";
            s.hideFlags = HideFlags.DontSave;
            _sprites["tile_white"] = s;
            return s;
        }

        /// <summary>
        /// 纯色矩形 Sprite（PPU=1，尺寸 = 像素）。玩家占位方块、朝向指示条、HUD 底板都用它。
        /// </summary>
        public static Sprite SolidRect(string key, int w, int h, Color fill)
        {
            int width = w > 0 ? w : 1;
            int height = h > 0 ? h : 1;

            // 【BugFix】原实现 key 是 "rect_" + key，w/h/fill 全不参与 ——
            // SolidRect("player",40,40,蓝) 之后再 SolidRect("player",20,20,红)
            // 会**静默**命中前者，拿到 40×40 的蓝块。目前调用点恰好一一对应所以没爆，
            // 但这是定时炸弹。key 必须包含全部影响输出的参数（见设计 K3）。
            // 注意：用钳制后的 width/height 而非原始 w/h，保证 (0,0) 与 (1,1) 命中同一条目。
            string k = "rect|" + key + "|" + width + "x" + height + "|" + ColorUtility.ToHtmlStringRGBA(fill);
            Sprite cached;
            if (_sprites.TryGetValue(k, out cached) && cached != null)
            {
                return cached;
            }

            Texture2D tex = NewTexture(k, width, height);
            Color32 c = fill;
            Color32[] px = new Color32[width * height];
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = c;
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);

            Sprite s = Sprite.Create(tex, new Rect(0.0f, 0.0f, width, height),
                                     new Vector2(0.5f, 0.5f), 1.0f, 0u, SpriteMeshType.FullRect);
            s.name = "sp_" + k;
            s.hideFlags = HideFlags.DontSave;
            _sprites[k] = s;
            return s;
        }

        /// <summary>
        /// 实心圆 Sprite，可选描边（精英怪用描边区分）。
        /// 边缘按到圆心的距离做 1px 软过渡，纯硬边在 32px 尺度下锯齿刺眼。
        /// </summary>
        /// <param name="key">缓存 key。</param>
        /// <param name="fill">填充色。</param>
        /// <param name="outline">描边色。alpha 为 0 表示不描边。</param>
        /// <param name="outlinePx">描边宽度（贴图像素）。</param>
        public static Sprite Circle(string key, Color fill, Color outline, float outlinePx)
        {
            int n = ShapePixels;

            // 【BugFix】同 SolidRect：fill / outline / outlinePx 必须进 key，
            // 否则同名不同色的圆会互相覆盖（敌人按 Kind 上色，最容易踩）。
            // outlinePx 用"百分像素整数"入 key，避免浮点 ToString 受区域设置影响
            // （某些 locale 下小数点是逗号，会生成不稳定的 key）。
            string k = "circ|" + key + "|" + n
                       + "|" + ColorUtility.ToHtmlStringRGBA(fill)
                       + "|" + ColorUtility.ToHtmlStringRGBA(outline)
                       + "|" + Mathf.RoundToInt(outlinePx * 100.0f);
            Sprite cached;
            if (_sprites.TryGetValue(k, out cached) && cached != null)
            {
                return cached;
            }

            Texture2D tex = NewTexture(k, n, n);
            Color[] px = new Color[n * n];

            float c0 = (n - 1) * 0.5f;
            float rOuter = n * 0.5f - 1.0f;
            float rInner = rOuter - (outline.a > 0.0f ? outlinePx : 0.0f);

            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = x - c0;
                    float dy = y - c0;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    Color col;
                    if (d <= rInner)
                    {
                        col = fill;
                    }
                    else if (d <= rOuter)
                    {
                        col = outline.a > 0.0f ? outline : fill;
                    }
                    else
                    {
                        col = new Color(fill.r, fill.g, fill.b, 0.0f);
                    }

                    // 外缘 1px 软过渡，消除硬锯齿。
                    float edge = Mathf.Clamp01(rOuter + 0.5f - d);
                    col.a *= edge;
                    px[y * n + x] = col;
                }
            }

            tex.SetPixels(px);
            tex.Apply(false, false);

            Sprite s = Sprite.Create(tex, new Rect(0.0f, 0.0f, n, n),
                                     new Vector2(0.5f, 0.5f), 1.0f, 0u, SpriteMeshType.FullRect);
            s.name = "sp_" + k;
            s.hideFlags = HideFlags.DontSave;
            _sprites[k] = s;
            return s;
        }

        /// <summary>
        /// 弧形剑气「月牙」Sprite —— 这是 T04 特效的主体，也是本文件唯一有点意思的函数。
        ///
        /// 【为什么不画扇形】
        /// 扇形（把圆心到弧边整块涂满）看起来像手电筒光锥，是判定范围的可视化，
        /// 不是武侠的一剑。真正像剑气的是**月牙**：一条有厚度的圆弧，中间最粗、
        /// 两端收尖、外缘锐内缘钝。所以这里的取舍是：
        ///   · 只在 [rInner, rOuter] 这个半径环带内着色 —— 圆心附近留空，去掉光锥感
        ///   · 厚度沿角度按 sin 曲线收束 —— 两端自然收尖，形成刀锋轮廓
        ///   · alpha 沿半径向内衰减 —— 外缘亮、内缘淡，做出「劈开空气」的拖影
        ///
        /// 贴图坐标系：月牙开口朝 +X（右），与 <c>lastFacing</c> 的 0° 对齐，
        /// 使用时只需把 Transform 的 z 欧拉角设成 facing 角度即可。
        /// </summary>
        /// <param name="key">缓存 key。</param>
        /// <param name="tint">剑气颜色。</param>
        /// <param name="arcDeg">月牙张开的总角度（度）。</param>
        /// <param name="thicknessRatio">最厚处占半径的比例，0~1。</param>
        public static Sprite Crescent(string key, Color tint, float arcDeg, float thicknessRatio)
        {
            string k = "crescent_" + key;
            Sprite cached;
            if (_sprites.TryGetValue(k, out cached) && cached != null)
            {
                return cached;
            }

            int n = ShapePixels * 2;   // 月牙细节多，用 128px 免得刀锋糊成一团
            Texture2D tex = NewTexture(k, n, n);
            Color[] px = new Color[n * n];

            float c0 = (n - 1) * 0.5f;
            float rOuter = n * 0.5f - 1.0f;
            float halfArc = Mathf.Max(1.0f, arcDeg * 0.5f);
            float maxThick = Mathf.Clamp01(thicknessRatio) * rOuter;

            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    int idx = y * n + x;
                    float dx = x - c0;
                    float dy = y - c0;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    if (d < 1e-4f || d > rOuter)
                    {
                        px[idx] = new Color(tint.r, tint.g, tint.b, 0.0f);
                        continue;
                    }

                    // 与 +X 轴的夹角（度），范围 [-180, 180]。
                    float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                    float absAng = Mathf.Abs(ang);
                    if (absAng > halfArc)
                    {
                        px[idx] = new Color(tint.r, tint.g, tint.b, 0.0f);
                        continue;
                    }

                    // 角向收束：中间(t=1)最厚，两端(t=0)收成尖。
                    float t = Mathf.Cos(absAng / halfArc * Mathf.PI * 0.5f);
                    float thick = maxThick * t;
                    if (thick < 0.5f)
                    {
                        px[idx] = new Color(tint.r, tint.g, tint.b, 0.0f);
                        continue;
                    }

                    float rInner = rOuter - thick;
                    if (d < rInner)
                    {
                        px[idx] = new Color(tint.r, tint.g, tint.b, 0.0f);
                        continue;
                    }

                    // 径向渐变：贴外缘最实，往内渐隐 —— 这就是剑气的拖尾。
                    float radial = Mathf.Clamp01((d - rInner) / Mathf.Max(1.0f, thick));
                    float alpha = tint.a * Mathf.Pow(radial, 0.65f);

                    // 外缘 1px 抗锯齿。
                    alpha *= Mathf.Clamp01(rOuter + 0.5f - d);

                    px[idx] = new Color(tint.r, tint.g, tint.b, alpha);
                }
            }

            tex.SetPixels(px);
            tex.Apply(false, false);

            Sprite s = Sprite.Create(tex, new Rect(0.0f, 0.0f, n, n),
                                     new Vector2(0.5f, 0.5f), 1.0f, 0u, SpriteMeshType.FullRect);
            s.name = "sp_" + k;
            s.hideFlags = HideFlags.DontSave;
            _sprites[k] = s;
            return s;
        }

        /// <summary>
        /// uGUI Image 用的纯白小方块。
        ///
        /// 【所有 HUD 元素共用同一张，颜色走 Image.color】
        /// 每个元素各生成一张纯色贴图会把 HUD 拆成十几个 DrawCall，而且
        /// 改一次颜色就多泄漏一张贴图。共用白底 + 顶点着色是 uGUI 的标准做法。
        /// </summary>
        public static Sprite UiPixel()
        {
            return SolidRect("ui_white", 4, 4, Color.white);
        }

        // ---------------------------------------------------------------------
        // PNG 加载（StreamingAssets）
        //
        // 【为什么走 StreamingAssets 而不是 Resources / AssetDatabase】
        // 这批图必须以 PPU=1、自定义 pivot 使用，而"导入为 Unity 资产"会把
        // PPU / pivot / Read-Write / 压缩格式全部塞进 .meta —— 那是静态分析的黑洞，
        // 改错了 grep 查不出来。走 StreamingAssets 时 PNG 只是字节数据，
        // 全部参数由下面的 Sprite.Create 在代码里显式写死，100% 可审查。
        //
        // 【平台约束】File.ReadAllBytes 仅适用于 PC/macOS/Linux Standalone 与 Editor。
        // Android（图在 jar 内）/ WebGL（需 http）读不到，届时把 ReadBytes() 这
        // **一个私有方法**换成 UnityWebRequest 异步即可，其余代码不用动。
        // ---------------------------------------------------------------------

        /// <summary>
        /// 从 StreamingAssets 读一张 PNG 并转成 Sprite（PPU=1，自定义 pivot）。
        ///
        /// 【契约】失败时返回 null，**绝不抛异常**；同一 relPath 的失败只警告一次。
        /// 调用方（HeroineAnimator / WorldBuilder）据此走程序化 fallback。
        /// </summary>
        /// <param name="relPath">
        /// 相对 StreamingAssets 根的路径，**正斜杠、不含扩展名**。
        /// 例："characters/heroine/heroine_idle_1"（<c>.png</c> 由本方法内部拼接）。
        /// </param>
        /// <param name="w">调用方声明的期望宽度（像素）。与实际不符时告警。</param>
        /// <param name="h">调用方声明的期望高度（像素）。</param>
        /// <param name="pivot">归一化轴心。人形立绘锚脚底，取 (0.5, 0.28) 而非体心。</param>
        /// <param name="tint">乘算染色。<see cref="Color.white"/> 表示不染色（走快路径）。</param>
        /// <returns>加载成功的 Sprite；文件缺失 / 损坏 / IO 异常时为 null。</returns>
        public static Sprite TryLoadPng(string relPath, int w, int h, Vector2 pivot, Color tint)
        {
            if (string.IsNullOrEmpty(relPath))
            {
                return null;
            }

            int width = w > 0 ? w : 1;
            int height = h > 0 ? h : 1;
            string k = PngKey(relPath, width, height, pivot, tint);

            Sprite cached;
            if (_sprites.TryGetValue(k, out cached) && cached != null)
            {
                return cached;   // 命中缓存：每帧 Apply() 走的就是这条 O(1) 路径。
            }

            try
            {
                string abs = Path.Combine(Application.streamingAssetsPath, relPath + ".png");
                byte[] bytes = ReadBytes(abs);
                if (bytes == null || bytes.Length == 0)
                {
                    WarnOnce(relPath, "文件不存在或为空：" + abs);
                    return null;
                }

                // 不染色时 markNonReadable=true：纹理上传 GPU 后释放 CPU 副本，省一半内存。
                // 需要染色时必须 false，因为后面要 GetPixels32。
                bool plain = Mathf.Approximately(tint.r, 1.0f) && Mathf.Approximately(tint.g, 1.0f)
                             && Mathf.Approximately(tint.b, 1.0f) && Mathf.Approximately(tint.a, 1.0f);

                // ★ 必须经 NewTexture 建容器：它内部做 _textures[key] 登记，
                //   PNG 纹理因此自动进入 Clear() 的销毁循环，无需新增任何生命周期代码。
                //   LoadImage 会把这个 2×2 的占位容器自动 resize 成真实尺寸。
                Texture2D tex = NewTexture(k, 2, 2);
                if (!tex.LoadImage(bytes, plain))
                {
                    // 走到这里说明容器已经登记进 _textures 了。必须显式回收：
                    // 否则调用方每帧重试一次，就会用同 key 覆盖登记一次，
                    // 把上一张 orphan 掉（字典里没了、Clear() 也就销毁不到）。
                    DiscardTexture(k);
                    WarnOnce(relPath, "PNG 解码失败（文件可能已损坏）：" + abs);
                    return null;
                }

                // 清单校验：声明尺寸与资产实际尺寸不符时报出来。这是 39 帧清单
                // 唯一的自动化守卫 —— 美术换图改了尺寸而没同步 HeroineFrames 时会在这里响。
                if (tex.width != width || tex.height != height)
                {
                    WarnOnce(relPath, string.Format(
                        "尺寸与清单不符：声明 {0}×{1}，实际 {2}×{3}。已按实际尺寸出图，请同步 HeroineFrames 清单表。",
                        width, height, tex.width, tex.height));
                }

                if (!plain)
                {
                    Color32[] px = tex.GetPixels32();
                    for (int i = 0; i < px.Length; i++)
                    {
                        Color32 s0 = px[i];
                        px[i] = new Color32(
                            (byte)Mathf.RoundToInt(s0.r * tint.r),
                            (byte)Mathf.RoundToInt(s0.g * tint.g),
                            (byte)Mathf.RoundToInt(s0.b * tint.b),
                            (byte)Mathf.RoundToInt(s0.a * tint.a));
                    }
                    tex.SetPixels32(px);
                    tex.Apply(false, true);   // 染色完成后同样可以释放 CPU 副本
                }

                // 用**实际**尺寸裁 Rect 而非声明值：声明值偏大时 Sprite.Create 会
                // 因矩形越界而报错/返回 null，那就违反了"永不崩"的硬要求。
                // 不一致已在上面告警，这里以能跑为先。
                Sprite s = Sprite.Create(tex, new Rect(0.0f, 0.0f, tex.width, tex.height),
                                         pivot, 1.0f, 0u, SpriteMeshType.FullRect);
                if (s == null)
                {
                    DiscardTexture(k);
                    WarnOnce(relPath, "Sprite.Create 返回 null：" + abs);
                    return null;
                }

                s.name = "sp_" + k;
                s.hideFlags = HideFlags.DontSave;
                _sprites[k] = s;
                return s;
            }
            catch (System.Exception e)
            {
                // IO 异常、权限、路径过长、平台不支持同步读盘……一律吞掉转 null。
                // 能进到这里就一定没走到 _sprites[k] 赋值那一步，所以容器纹理（若已建）必是垃圾。
                DiscardTexture(k);
                WarnOnce(relPath, "读取异常：" + e.GetType().Name + " - " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 批量预热一组连续编号的帧：<c>prefix_1</c> … <c>prefix_&lt;count&gt;</c>。
        ///
        /// 【为什么预热而不是懒加载】避免玩家第一次挥剑 / 死亡时才去读盘，卡在最不该卡的瞬间。
        /// 39 帧合计约 136KB，一次性读完在毫秒量级，且发生在 BuildPlayer 这个本就重建世界的窗口内。
        /// </summary>
        /// <param name="dir">相对 StreamingAssets 的目录，正斜杠，例 "characters/heroine"。</param>
        /// <param name="prefix">文件名前缀，例 "heroine_idle"。</param>
        /// <param name="count">帧数。文件名编号**从 1 起**。</param>
        /// <returns>成功加载的帧数（0 表示整组都没读到）。</returns>
        public static int PreloadPngSequence(string dir, string prefix, int count,
                                             int w, int h, Vector2 pivot, Color tint)
        {
            int ok = 0;
            for (int i = 1; i <= count; i++)
            {
                string rel = dir + "/" + prefix + "_" + i;
                if (TryLoadPng(rel, w, h, pivot, tint) != null)
                {
                    ok++;
                }
            }
            return ok;
        }

        /// <summary>
        /// 仅探测 PNG 是否存在，不加载、不缓存、不告警。供快速分支判断用。
        /// </summary>
        /// <param name="relPath">同 <see cref="TryLoadPng"/>，不含扩展名。</param>
        public static bool PngAvailable(string relPath)
        {
            if (string.IsNullOrEmpty(relPath))
            {
                return false;
            }
            try
            {
                return File.Exists(Path.Combine(Application.streamingAssetsPath, relPath + ".png"));
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------------
        // 内部
        // ---------------------------------------------------------------------

        /// <summary>
        /// PNG 缓存 key：<c>png|&lt;relPath&gt;|&lt;w&gt;x&lt;h&gt;|&lt;tintRGBA&gt;|p&lt;x‰&gt;x&lt;y‰&gt;</c>。
        ///
        /// pivot 必须入 key：attack 帧是 96 宽画布、pivot 与 48 宽的 idle 不同，
        /// 若共用 key，先建的会覆盖后建的，角色会在挥剑瞬间整体横移半个身位 —— 极隐蔽。
        /// pivot 取千分比整数，避免浮点直接进字符串。
        /// </summary>
        private static string PngKey(string relPath, int w, int h, Vector2 pivot, Color tint)
        {
            return "png|" + relPath
                   + "|" + w + "x" + h
                   + "|" + ColorUtility.ToHtmlStringRGBA(tint)
                   + "|p" + Mathf.RoundToInt(pivot.x * 1000.0f) + "x" + Mathf.RoundToInt(pivot.y * 1000.0f);
        }

        /// <summary>
        /// 读磁盘字节。**换平台时只需要改这一个方法**（见本区块顶部的平台约束说明）。
        /// 文件不存在时返回 null 而不是抛 FileNotFoundException。
        /// </summary>
        private static byte[] ReadBytes(string absPath)
        {
            if (!File.Exists(absPath))
            {
                return null;
            }
            return File.ReadAllBytes(absPath);
        }

        /// <summary>
        /// 回收一个"建了但最终没用上"的容器纹理：先从 _textures 注销，再销毁。
        /// 只在 <see cref="TryLoadPng"/> 的失败分支调用。
        /// </summary>
        private static void DiscardTexture(string key)
        {
            Texture2D tex;
            if (!_textures.TryGetValue(key, out tex))
            {
                return;
            }
            _textures.Remove(key);
            SafeDestroy(tex);
        }

        /// <summary>同一 relPath 只警告一次，避免每帧刷屏淹没真正的报错。</summary>
        private static void WarnOnce(string relPath, string detail)
        {
            if (!_pngWarned.Add(relPath))
            {
                return;
            }
            Debug.LogWarning("[SpriteFactory] PNG 加载失败 '" + relPath + "'：" + detail
                             + "  → 已回退到程序化占位图形，游戏可继续运行。");
        }

        private static Texture2D NewTexture(string key, int w, int h)
        {
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.name = "tex_" + key;
            tex.filterMode = FilterMode.Bilinear;
            // Clamp 而非 Repeat：月牙贴图边缘若重复采样会在旋转时出现鬼影。
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.hideFlags = HideFlags.DontSave;
            _textures[key] = tex;
            return tex;
        }

        private static void SafeDestroy(Object o)
        {
            if (o == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                Object.Destroy(o);
            }
            else
            {
                Object.DestroyImmediate(o);
            }
        }
    }
}
