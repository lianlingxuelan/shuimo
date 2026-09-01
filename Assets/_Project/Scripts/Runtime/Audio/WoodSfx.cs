// Assets/_Project/Scripts/Runtime/Audio/WoodSfx.cs
// feature/2.5d —— 程序化木制品音效（砍竹 / 断竹）
//
// 【为什么是代码合成】
// 用户导入的 Unity 商店包全部是 BGM / 循环乐（Free Fantasy Music Pack、
// Music Loops Mini Set、Xiaoyi_Traditional_Music_Pack），没有任何「音效(SFX)」。
// 而砍竹需要的是「脆裂 + 闷响」的一次性音效，不是背景乐。
// 故本类用 PCM 采样在运行时合成竹裂声，零外部资源依赖，直接补上「音效没听到」的缺口。
//
// 【红线】纯表现层、无战斗内核依赖；只在 BambooVfx 受击/断裂时由表现层调用。

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 程序化合成的木制品受击音效（砍竹 / 断竹）。懒加载并缓存，全场景共享同一段 clip。
    /// </summary>
    public static class WoodSfx
    {
        private static AudioClip _chop;
        private static AudioClip _tick;

        /// <summary>竹断裂的主音效：脆裂噪声爆发 + 低频闷响。</summary>
        public static AudioClip Chop
        {
            get { if (_chop == null) _chop = BuildChopClip(); return _chop; }
        }

        /// <summary>每次挥砍命中的轻响（比断裂弱很多），给连续砍击节奏感。</summary>
        public static AudioClip Tick
        {
            get { if (_tick == null) _tick = BuildTickClip(); return _tick; }
        }

        private static AudioClip BuildChopClip()
        {
            int sr = 22050;
            float dur = 0.34f;
            int n = Mathf.Max(1, (int)(sr * dur));
            float[] data = new float[n];
            float crackDecay = 22.0f;   // 噪声爆发衰减速度（越大越「脆」）
            float thunkFreq = 150.0f;   // 低频闷响频率
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float env = Mathf.Exp(-crackDecay * t);
                float noise = UnityEngine.Random.value * 2.0f - 1.0f;
                float crack = noise * env;
                float thunk = Mathf.Sin(2.0f * Mathf.PI * thunkFreq * t) * Mathf.Exp(-9.0f * t) * 0.6f;
                data[i] = (crack * 0.85f + thunk) * 0.5f;
            }
            AudioClip clip = AudioClip.Create("WoodChop", n, 1, sr, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip BuildTickClip()
        {
            int sr = 22050;
            float dur = 0.12f;
            int n = Mathf.Max(1, (int)(sr * dur));
            float[] data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float env = Mathf.Exp(-60.0f * t);
                float noise = UnityEngine.Random.value * 2.0f - 1.0f;
                data[i] = noise * env * 0.5f;
            }
            AudioClip clip = AudioClip.Create("WoodTick", n, 1, sr, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
