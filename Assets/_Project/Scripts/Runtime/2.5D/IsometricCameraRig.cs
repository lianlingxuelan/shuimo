using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 等距俯视相机控制器（2.5D 技术验证骨架，feature/2.5d 分支）。
    /// 固定 45° 俯视角、正交投影，平滑跟随玩家。纯表现层，不含任何战斗逻辑。
    /// 本文件不影响 main 分支；本环境无 Unity，未编译，待真机验证。
    /// </summary>
    public class IsometricCameraRig : MonoBehaviour
    {
        [Header("跟随目标")]
        [Tooltip("要跟随的玩家 Transform；运行时由 BambooSceneContext 注入")]
        public Transform target;

        [Header("相机参数")]
        [Tooltip("相机相对目标的偏移（等距俯视）")]
        public Vector3 offset = new Vector3(-8f, 14f, -8f);
        [Tooltip("跟随平滑系数（越大越跟手）")]
        public float smoothSpeed = 6f;
        [Tooltip("正交视野尺寸")]
        public float orthographicSize = 9f;

        private Camera _cam;

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            if (_cam == null) _cam = Camera.main;
            ApplyIsometricProjection();
        }

        private void ApplyIsometricProjection()
        {
            if (_cam == null) return;
            _cam.orthographic = true;
            _cam.orthographicSize = orthographicSize;
            // 等距：固定 45° 俯视，不参与战斗确定性
            transform.rotation = Quaternion.Euler(45f, 45f, 0f);
        }

        private void LateUpdate()
        {
            if (target == null) return;
            Vector3 desired = target.position + offset;
            transform.position = Vector3.Lerp(transform.position, desired, smoothSpeed * Time.deltaTime);
        }

        /// <summary>
        /// 由场景上下文在玩家生成后调用，注入跟随目标。
        /// </summary>
        public void BindTarget(Transform playerTransform)
        {
            target = playerTransform;
        }
    }
}
