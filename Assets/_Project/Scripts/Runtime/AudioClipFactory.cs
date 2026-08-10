// -----------------------------------------------------------------------------
// AudioClipFactory.cs —— key → AudioClip 的合成缓存（asmdef: Xianxia.Unity.T2）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译、未经实听**。
// 引用 UnityEngine（AudioClip.Create / SetData 必须在主线程调用），不参与 Python 对拍。
//
// 【它是 SpriteFactory 的音频孪生兄弟，连坑都一样】
// SpriteFactory.cs 的文件头写着一句话，把 Texture2D 换成 AudioClip 一个字都不用改：
//
//   > Texture2D / Sprite 是**非托管资源**，GC 不会回收。世界每重建一次就 new 一批，
//   > 点二十次 Clean And Rebuild 就泄漏二十份。
//
// AudioClip 由 AudioClip.Create() 凭空造出来，它不是任何 GameObject 的子物体、
// 不在任何场景层级里 —— 场景卸载**不会**碰它。所以本类必须提供 Clear()，
// 并且必须有人在世界重建 / 拆线时调它（AudioDirector.ClearAll → CombatBridge.TeardownAudio）。
// 这是架构 §2.10 与验收项 A-19 的全部内容。
//
// 【三张表，各管一件事，缺一不可】
//   _clips   key → AudioClip     缓存本体
//   _failed  合成失败的 key       黑名单。失败是**永久**的（配方漏实现 / 参数非法
//                                 都不会自己好起来），不拉黑就会每次命中都重试一次
//                                 完整的 DSP 合成 —— 那是"没有声音"之外再加一个掉帧。
//   _warned  已经警告过的 key     去重。抄 SpriteFactory._pngWarned（:37）的范式：
//                                 缺一个音效会在每次命中反复走失败分支，不去重的话
//                                 Console 被同一行 Warning 刷爆，真正的报错反而被淹没。
//
// 【为什么 Clear() 要连 _warned 一起清】
// 同 SpriteFactory.cs:60-62 的裁定：缓存清空后，之前"这个 key 坏了"的判断不再有效
// （有人可能刚补上了配方分支），警告去重表跟着重置，否则修好之后反而再也看不到新警告。
//
// 【静音是安全降级，不是错误】
// Get() 返回 null 是一条**合法**路径：调用方（AudioDirector.Play）拿到 null 就把这个
// key 标记为 Broken 并静默跳过，游戏照常跑。本类不抛异常给调用方 —— Play() 跑在内核
// OnHit 的调用栈上，一个音频异常把整场战斗掀了是绝对不可接受的（R-07）。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 运行时程序化合成的 <see cref="AudioClip"/> 仓库。全静态，带缓存与失败黑名单。
    ///
    /// 【为什么是 static class 而不是 MonoBehaviour】
    /// 与 <see cref="SpriteFactory"/> 同一条理由：它没有任何字段需要 Update，
    /// 给纯缓存套一个 MonoBehaviour 只会引入"实例还没 Awake 就被读"的时序风险。
    /// 代价是它的生命周期比场景长，所以 <see cref="Clear"/> 必须被显式调用 ——
    /// 这一点在文件头和 <see cref="Clear"/> 的注释里各写了一遍。
    /// </summary>
    public static class AudioClipFactory
    {
        // =====================================================================
        // 状态
        // =====================================================================

        /// <summary>key ⇒ 已合成的 clip。容量给 16（表里 13 条，留一点余量避免扩容）。</summary>
        private static readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>(16);

        /// <summary>
        /// 合成失败的 key 黑名单。**永久**，直到 <see cref="Clear"/>。
        ///
        /// 失败的原因只有三类：key 不在表里、配方抛异常、Unity 侧 Create/SetData 失败。
        /// 三类都不会在同一次运行里自己恢复，所以重试没有任何收益，只有代价。
        /// </summary>
        private static readonly HashSet<string> _failed = new HashSet<string>();

        /// <summary>已经警告过的 key（去重）。范式抄 <c>SpriteFactory._pngWarned</c>。</summary>
        private static readonly HashSet<string> _warned = new HashSet<string>();

        /// <summary>
        /// 关闭 Domain Reload 时的兜底复位（QA 报告 P2-1）。
        ///
        /// 【为什么需要，即使已有 Clear()】
        /// 正常退出 Play 时 <c>CombatBridge.OnDestroy → TeardownAudio → AudioDirector.ClearAll
        /// → AudioClipFactory.Clear()</c> 会清空三个集合。但若 <c>OnDestroy</c> 在更早的语句
        /// 抛了异常，这条链就断了；而关闭 Domain Reload 后 static 不会自动清空，
        /// 第二次 Play 时 <c>_clips</c> 会持有已被 Unity 销毁的 <see cref="AudioClip"/>。
        /// 那种情况由 <see cref="Get"/> 里的 Unity <c>==</c> 重载判空兜住（最坏是重合成一次，
        /// 不会 NRE），但"进 Play 就是干净状态"这个不变量本身值得用 3 行换。
        ///
        /// 与 <see cref="AudioConfig.ResetStatics"/> 取齐防御等级。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Clear();
        }

        // =====================================================================
        // 只读查询（给测试与日志用，不参与播放路径）
        // =====================================================================

        /// <summary>当前缓存里的 clip 数量。</summary>
        public static int CachedCount
        {
            get { return _clips.Count; }
        }

        /// <summary>当前黑名单里的 key 数量。装配期日志会打它，非 0 就说明有配方没跑通。</summary>
        public static int FailedCount
        {
            get { return _failed.Count; }
        }

        /// <summary>某个 key 是否已进入失败黑名单。</summary>
        /// <param name="key">音效 key。</param>
        /// <returns>在黑名单里返回 true。</returns>
        public static bool IsFailed(string key)
        {
            return !string.IsNullOrEmpty(key) && _failed.Contains(key);
        }

        // =====================================================================
        // 主入口
        // =====================================================================

        /// <summary>
        /// 取一个 key 对应的 <see cref="AudioClip"/>。命中缓存直接返回；
        /// 未命中则**同步**合成一次并入缓存；失败返回 null 并只警告一次。
        ///
        /// 【为什么缓存命中还要判 clip != null】
        /// Unity 的 Object 重载了 == ：被 Destroy 掉的对象在 C# 层引用还在，
        /// 但 == null 为真。字典里留着一个"已销毁"的条目是完全可能的
        /// （有人在别处 Destroy 了它，或者 Clear 的时序出了岔）。抄 SpriteFactory:72
        /// 的写法：命中但已销毁 ⇒ 当作未命中，重新合成并覆盖该条目。
        ///
        /// 【为什么返回 null 而不抛异常】
        /// 见文件头。null 是一条合法的降级路径，调用方据此静默跳过。
        /// </summary>
        /// <param name="key">音效 key，必须与 <see cref="AudioConfig.Table"/> 里的字符串逐字符一致。</param>
        /// <returns>可用的 clip；未注册 / 合成失败时返回 null。</returns>
        public static AudioClip Get(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            AudioClip cached;
            if (_clips.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            // 黑名单在缓存之后判：万一有人先 Clear 再 Get，黑名单已经空了，
            // 这一判就是零成本；而正常路径下它挡住了对已知坏 key 的重复合成。
            if (_failed.Contains(key))
            {
                return null;
            }

            SfxSpec spec;
            if (!AudioConfig.TryGetSpec(key, out spec))
            {
                // 未注册的 key。这是"内核传出了一个表里没有的字符串"，
                // 属于 R-07 明确要求静默降级的情形：警告一次，拉黑，继续跑。
                _failed.Add(key);
                WarnOnce(key, "该 key 不在 AudioConfig.Table 里（13 条注册项）");
                return null;
            }

            AudioClip clip = Synthesize(key, spec);
            if (clip == null)
            {
                // Synthesize 内部已经警告过并说明了原因，这里只负责拉黑。
                _failed.Add(key);
                return null;
            }

            _clips[key] = clip;
            return clip;
        }

        /// <summary>
        /// 批量预合成。返回**本次调用后**成功可用的 clip 条数。
        ///
        /// 【为什么返回 int 而不是 void】
        /// 装配期需要一行"13 个音效已就绪"的日志。数字对不上（比如只有 12）是
        /// 唯一能在没有实听条件下发现"某张配方悄悄挂了"的信号 —— 而"安静地没有
        /// 声音"正是 PRD §1.3 ① 点名的最难排查的一类 bug。
        ///
        /// 【逃生阀：AudioConfig.PrewarmAmbience】
        /// 环境衬底一个人占 67% 的样本量（265k / 393k）。这个 bool 置 false 时
        /// 本方法**跳过**它，其余 12 个 P0 音效照常预合成。注意这不是"改成懒合成"——
        /// 懒合成只是把 265k 样本的开销从"看着主菜单"挪到"刚进战斗"，那是更糟的位置。
        /// 关掉就是整条环境衬底链路都不启用（AmbienceLayer 侧同样判这个 bool）。
        ///
        /// 【为什么不并行 / 不分帧】
        /// AudioClip.Create 与 SetData 必须在主线程调用，分帧只能把总耗时摊开、
        /// 不能减少，代价却是引入"这个 clip 还没准备好"的中间态 —— 于是每条播放
        /// 路径都要多一个分支。预合成的全部价值就是消灭这个分支（架构 §2.2）。
        /// </summary>
        /// <param name="keys">要预合成的 key 列表，通常传 <see cref="AudioConfig.AllKeys"/>。</param>
        /// <returns>成功就绪的 clip 条数。</returns>
        public static int Prewarm(string[] keys)
        {
            if (keys == null)
            {
                return 0;
            }

            int ok = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (!AudioConfig.PrewarmAmbience && key == AudioConfig.AmbienceKey)
                {
                    // 逃生阀生效：跳过，且**不**拉黑 —— 拉黑会让"把 bool 改回 true"
                    // 在同一次运行里失效，那是一个会让人怀疑自己改错了地方的坑。
                    continue;
                }

                if (Get(key) != null)
                {
                    ok++;
                }
            }
            return ok;
        }

        /// <summary>
        /// 销毁全部缓存的 clip 并清空三张表。**必须**在拆线 / 世界重建前调用。
        ///
        /// 【★ 调用方必须先摘引用，本方法不负责】
        /// 架构 §2.10 的三步顺序是：
        ///   ① AudioDirector.StopAllVoices()   —— 每路 AudioSource.Stop()
        ///   ② 遍历 voice：Source.clip = null   —— 摘掉引用
        ///   ③ AudioClipFactory.Clear()         —— Object.Destroy(clip)
        /// 本方法是第 ③ 步。前两步在 AudioDirector 里，顺序不可交换 ——
        /// 在 AudioSource 仍持有并播放某个 clip 时 Destroy 它，Unity 的行为是未定义的，
        /// 轻则 Console 报错，重则播出垃圾采样（一段刺耳的噪声）。
        /// "在 BOSS 死亡音（1.15 s）播到一半时按 R 重开"是一条玩家一定会走的路径。
        /// </summary>
        public static void Clear()
        {
            foreach (KeyValuePair<string, AudioClip> kv in _clips)
            {
                SafeDestroy(kv.Value);
            }
            _clips.Clear();
            _failed.Clear();
            // 缓存清空后，之前"这个 key 坏了"的结论不再有效（配方可能刚被补上），
            // 警告去重表要跟着重置，否则修好之后反而再也看不到新的警告。
            // 与 SpriteFactory.cs:60-62 是同一条裁定。
            _warned.Clear();
        }

        // =====================================================================
        // 内部
        // =====================================================================

        /// <summary>
        /// 真正做一次合成：渲染 PCM → 建 AudioClip → 灌数据。任何一步失败都返回 null。
        ///
        /// 【为什么整段 try/catch，而不是让异常往上抛】
        /// SfxRecipes.Render 的 default 分支是**故意**抛 NotSupportedException 的
        /// （见 SfxRecipes.cs:86-91："宁可在预合成期吵一声，也不要在运行期安静"）。
        /// 那一声必须由这里接住：预合成跑在 CombatBridge.Start() 里，抛上去会中断
        /// 整个装配流程，13 个音效里坏 1 个就让游戏起不来 —— 那比没声音严重得多。
        ///
        /// 【为什么用 AudioConfig.MakeSynthRandom(key) 而不是 AudioDirector 的 _rng】
        /// 这是**合成**期的随机流，必须是固定种子的（同一个 key 每次启动音色逐位相同）。
        /// AudioDirector._rng 的种子是 Environment.TickCount，用它合成会导致
        /// "每次启动音色略有不同"，摧毁固定种子存在的全部理由（架构 §2.7 末的纪律）。
        /// </summary>
        /// <param name="key">音效 key。</param>
        /// <param name="spec">已查到的规格。</param>
        /// <returns>合成好的 clip；失败返回 null（已警告）。</returns>
        private static AudioClip Synthesize(string key, SfxSpec spec)
        {
            float[] data = null;
            try
            {
                data = SfxRecipes.Render(spec, AudioConfig.MakeSynthRandom(key));
            }
            catch (System.Exception e)
            {
                WarnOnce(key, "配方渲染抛异常：" + e.Message);
                return null;
            }

            if (data == null || data.Length <= 0)
            {
                WarnOnce(key, "配方返回了空缓冲（样本数 0），检查 DurationSec / SampleRate");
                return null;
            }

            AudioClip clip = null;
            try
            {
                // 单声道、非流式。第 5 个参数 stream=false 表示数据一次性驻留内存 ——
                // 本期全部 clip 加起来约 1.5 MB，流式解码的复杂度完全没有必要。
                clip = AudioClip.Create("sfx_" + key, data.Length, 1, spec.SampleRate, false);
                if (clip == null)
                {
                    WarnOnce(key, "AudioClip.Create 返回 null（样本数 " + data.Length +
                                  "，采样率 " + spec.SampleRate + "）");
                    return null;
                }

                if (!clip.SetData(data, 0))
                {
                    // SetData 返回 false 而不抛异常，是最容易被忽略的一条失败路径：
                    // 不判它的话，得到的是一个长度正确、内容全 0 的 clip ——
                    // 表现为"这个音效播了，但是没有声音"，比直接报错难查得多。
                    WarnOnce(key, "AudioClip.SetData 返回 false，clip 内容可能全为静音");
                    SafeDestroy(clip);
                    return null;
                }

                // 与 SpriteFactory.NewTexture 一致：程序化生成的资源不进资产库，
                // 也不希望它在 Editor 里被误保存进场景。
                clip.hideFlags = HideFlags.DontSave;
                return clip;
            }
            catch (System.Exception e)
            {
                WarnOnce(key, "AudioClip 创建/灌数据抛异常：" + e.Message);
                SafeDestroy(clip);
                return null;
            }
        }

        /// <summary>
        /// 同一个 key 只警告一次，避免每次命中都刷屏淹没真正的报错。
        /// 范式与 <c>SpriteFactory.WarnOnce</c>（:535）逐字对应。
        /// </summary>
        /// <param name="key">出问题的 key。</param>
        /// <param name="detail">具体原因，会原样拼进日志。</param>
        private static void WarnOnce(string key, string detail)
        {
            if (!_warned.Add(key))
            {
                return;
            }
            Debug.LogWarning(AudioConfig.LogPrefix + " 音效 '" + key + "' 不可用：" + detail
                             + "  → 该音效已静音降级，游戏可继续运行。");
        }

        /// <summary>
        /// 播放模式用 Destroy，编辑器模式用 DestroyImmediate。
        /// 逐字抄 <c>SpriteFactory.SafeDestroy</c>（:557）—— EditMode 测试里
        /// Destroy 不会立即生效，泄漏会累积到整个测试会话结束。
        /// </summary>
        /// <param name="o">待销毁对象，允许为 null。</param>
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
