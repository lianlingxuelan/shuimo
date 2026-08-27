using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 单张立绘尚未替换为分层骨骼前使用的临时姿态。
    /// 保持为独立、可测试的数据模型，后续接入 Blender 骨骼时可整体替换。
    /// </summary>
    public struct CutoutPose
    {
        public readonly float TiltDeg;
        public readonly float WidthScale;
        public readonly float HeightScale;

        public CutoutPose(float tiltDeg, float widthScale, float heightScale)
        {
            TiltDeg = tiltDeg;
            WidthScale = widthScale;
            HeightScale = heightScale;
        }
    }

    public static class FallbackCutoutPose
    {
        private static readonly CutoutPose Neutral = new CutoutPose(0.0f, 1.0f, 1.0f);

        public static CutoutPose Evaluate(CharacterAnimState state, float phase)
        {
            if (state != CharacterAnimState.Attack)
            {
                return Neutral;
            }

            float t = Mathf.Clamp01(phase);
            if (t < 0.24f)
            {
                return Lerp(Neutral, new CutoutPose(20.0f, 0.93f, 1.07f), t / 0.24f);
            }

            if (t < 0.68f)
            {
                return Lerp(
                    new CutoutPose(20.0f, 0.93f, 1.07f),
                    new CutoutPose(-48.0f, 1.14f, 0.90f),
                    (t - 0.24f) / 0.44f);
            }

            return Lerp(
                new CutoutPose(-48.0f, 1.14f, 0.90f),
                Neutral,
                (t - 0.68f) / 0.32f);
        }

        private static CutoutPose Lerp(CutoutPose from, CutoutPose to, float t)
        {
            return new CutoutPose(
                Mathf.Lerp(from.TiltDeg, to.TiltDeg, t),
                Mathf.Lerp(from.WidthScale, to.WidthScale, t),
                Mathf.Lerp(from.HeightScale, to.HeightScale, t));
        }
    }
}
