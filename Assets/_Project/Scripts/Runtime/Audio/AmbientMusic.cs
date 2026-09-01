// Assets/_Project/Scripts/Runtime/Audio/AmbientMusic.cs
// feature/2.5d —— 场景传统 BGM 自动播放（仙侠氛围）
//
// 【资源来源】用户导入的 Xiaoyi_Traditional_Music_Pack（岁华流年 / 月明悠悠 / 暖雪融春），
// 已拷贝到 Assets/_Project/Resources/Audio/BGM/ 以便运行时 Resources.LoadAll 加载，
// 无需手工拖引用、不改动任何 .unity 场景文件。
//
// 【自举方式】[RuntimeInitializeOnLoadMethod] 在场景加载后自动建一个常驻宿主对象，
// 顺序循环播放 BGM。DontDestroyOnLoad 保证重开场景不重复、不断曲。
//
// 【红线】纯表现层；不依赖战斗内核，不写任何时钟/状态。

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 场景传统 BGM 自动播放器。通过自举宿主对象运行，无需改场景。
    /// </summary>
    public sealed class AmbientMusic : MonoBehaviour
    {
        private AudioSource _src;
        private AudioClip[] _tracks;
        private int _idx;

        private void Awake()
        {
            _src = gameObject.AddComponent<AudioSource>();
            _src.loop = false;
            _src.playOnAwake = false;
            _src.volume = 0.55f;

            _tracks = Resources.LoadAll<AudioClip>("Audio/BGM");
            _idx = 0;

            if (_tracks == null || _tracks.Length == 0)
            {
                Debug.LogWarning("[AmbientMusic] Resources/Audio/BGM 下未找到任何音频，跳过 BGM。");
            }
        }

        private void Update()
        {
            if (_tracks == null || _tracks.Length == 0)
            {
                return;
            }

            // 当前曲播完 → 切下一首；到尾回环。简单顺序播放，无交叉淡入。
            if (!_src.isPlaying)
            {
                if (_idx >= _tracks.Length)
                {
                    _idx = 0;
                }
                AudioClip clip = _tracks[_idx];
                if (clip != null)
                {
                    _src.clip = clip;
                    _src.Play();
                }
                _idx++;
            }
        }
    }

    /// <summary>
    /// 自举：场景加载后自动创建 BGM 宿主。只在尚无宿主时建一个，避免重复。
    /// </summary>
    internal static class AmbientMusicBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (GameObject.Find("AmbientMusicHost") != null)
            {
                return;
            }
            GameObject go = new GameObject("AmbientMusicHost");
            go.AddComponent<AmbientMusic>();
            Object.DontDestroyOnLoad(go);
        }
    }
}
