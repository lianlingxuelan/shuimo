// 场景氛围层：在 SampleScene 运行时程序化注入水墨天空 + 远山剪影。
// 解决「白底 + 悬浮柱子、不像场景」的问题。
//
// 设计约定（与项目红线一致）：
//  - 零侵入：本组件通过 [RuntimeInitializeOnLoadMethod] 自挂载，不修改任何已有
//    脚本 / 场景文件 / 角色与竹子资产。若你在场景里手动挂一个本组件，则自挂载会跳过。
//  - 全程序化：天空/远山都是几何体 + 自写 Unlit shader，不依赖任何外部美术资源，
//    也不引入 M_MoonFlowersSky 之类的第三方包（避免外部引用风险）。
//  - 不动竹子：本层独立于 BambooSceneContext，竹子方向争议由主理人另定。
//
// 使用：本地 Shuimo/Scene/Force Recompile 后进入 PlayMode 即可看到效果。
// 若想调参，可在场景里手动挂一个 AtmosphereLayer 组件，在 Inspector 改下方字段。
using UnityEngine;
using Xianxia.Unity.T2;

[DisallowMultipleComponent]
public class AtmosphereLayer : MonoBehaviour
{
    [Header("天空")]
    public Color skyTop = new Color(0.78f, 0.82f, 0.86f, 1f);
    public Color skyBottom = new Color(0.93f, 0.92f, 0.88f, 1f);
    public float skyRadius = 3000f;

    [Header("远山")]
    public Color mountainInk = new Color(0.32f, 0.36f, 0.40f, 1f);
    public int mountainLayers = 2;
    public float mountainBaseZ = -1600f;   // 第一层远山纵深
    public float mountainLayerGap = 700f;   // 层间纵深
    public float mountainHeight = 700f;     // 山高
    public float mountainWidth = 6000f;     // 山宽（覆盖视野）

    [Header("雾")]
    [Tooltip("雾现在由 BambooSceneContext 统一管理；此处保留仅作向后兼容。")]
    public bool enableFog = true;
    [Tooltip("已废弃：BambooSceneContext 使用 Linear 雾(start/end)。")]
    public float fogDensity = 0.0016f;
    [Tooltip("已废弃：BambooSceneContext 使用 fogColor。")]
    public Color fogColor = new Color(0.84f, 0.87f, 0.89f, 1f);

    private static AtmosphereLayer s_instance;
    private bool _built;
    private Transform _sky;

    void Awake()
    {
        if (s_instance == null) s_instance = this;
        if (_built) return;
        Build();
    }

    void OnDestroy()
    {
        if (s_instance == this) s_instance = null;
    }

    void LateUpdate()
    {
        // 天空球跟随相机，始终包住视野（球半径足够大，相机不会穿出）。
        if (_sky != null && Camera.main != null)
        {
            _sky.position = Camera.main.transform.position;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Auto()
    {
        if (!Application.isPlaying) return;
        if (s_instance != null) return;
        // 仅在我们的游戏场景（SampleScene，内有 BambooSceneContext）注入。
        if (Object.FindObjectOfType<BambooSceneContext>() == null) return;
        var go = new GameObject("AtmosphereLayer");
        s_instance = go.AddComponent<AtmosphereLayer>();
        Debug.Log("[2.5D][AtmosphereLayer] 已自动注入场景氛围层（水墨天空 + 远山）。雾由 BambooSceneContext 接管。");
    }

#if UNITY_EDITOR
    // 编辑器预览：不进 PlayMode 也能看水墨天空 + 远山 + 雾。
    // 用法：先「Shuimo/2.5D/预览竹林(编辑器)」放置竹林，再点本菜单。
    // 退出前点「清除水墨氛围预览」避免序列化进场景。
    [UnityEditor.MenuItem("Shuimo/2.5D/预览水墨氛围(编辑器)")]
    static void EditorPreviewMenu()
    {
        if (Application.isPlaying) return;
        if (Object.FindObjectOfType<BambooSceneContext>() == null)
        {
            UnityEditor.EditorUtility.DisplayDialog("水墨氛围", "请先「Shuimo/2.5D/预览竹林(编辑器)」放置竹林，再预览氛围。", "OK");
            return;
        }
        if (Object.FindObjectOfType<AtmosphereLayer>() != null) return;
        var go = new GameObject("AtmosphereLayer");
        go.AddComponent<AtmosphereLayer>().Build();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log("[2.5D][AtmosphereLayer] 编辑器预览已生成。退出前请「清除水墨氛围预览」。");
    }

    [UnityEditor.MenuItem("Shuimo/2.5D/清除水墨氛围预览")]
    static void EditorClearMenu()
    {
        var a = Object.FindObjectOfType<AtmosphereLayer>();
        if (a != null) Object.DestroyImmediate(a.gameObject);
        // 雾由 BambooSceneContext 管理，通过「清除竹林预览」恢复。
        Debug.Log("[2.5D][AtmosphereLayer] 编辑器预览已清除（天空/远山）。雾请通过「清除竹林预览」恢复。");
    }
#endif

    public void Build()
    {
        if (_built) return;
        _built = true;

        var root = new GameObject("AtmosphereRoot");
        root.transform.SetParent(transform, false);

        // 1) 渐变天空大球（内壁，跟随相机）
        var sky = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sky.name = "InkSky";
        sky.transform.SetParent(root.transform, false);
        sky.transform.localScale = Vector3.one * (skyRadius * 2f);
        var skyCol = sky.GetComponent<Collider>();
        if (skyCol != null) Object.Destroy(skyCol);
        var skyRend = sky.GetComponent<MeshRenderer>();
        var skyMat = new Material(Shader.Find("Xianxia/Ink/InkSky"));
        if (skyMat != null)
        {
            skyMat.SetColor("_TopColor", skyTop);
            skyMat.SetColor("_BottomColor", skyBottom);
            skyRend.sharedMaterial = skyMat;
        }
        skyRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        skyRend.receiveShadows = false;
        if (Camera.main != null) sky.transform.position = Camera.main.transform.position;
        _sky = sky.transform;

        // 2) 远山剪影（多层，固定在竹林子区外围远处）
        var mtnMat = new Material(Shader.Find("Xianxia/Ink/InkMountain"));
        if (mtnMat != null) mtnMat.SetColor("_InkColor", mountainInk);

        for (int i = 0; i < mountainLayers; i++)
        {
            var m = GameObject.CreatePrimitive(PrimitiveType.Quad);
            m.name = string.Format("InkMountain_{0}", i);
            m.transform.SetParent(root.transform, false);
            float z = mountainBaseZ - i * mountainLayerGap;
            float w = mountainWidth * (1f + i * 0.25f);
            float h = mountainHeight * (1f + i * 0.2f);
            m.transform.localScale = new Vector3(w, h, 1f);
            // 远山在山脚处立地：y = h/2；奇偶层左右错开一点避免完全重叠
            m.transform.localPosition = new Vector3((i % 2 == 0 ? -1f : 1f) * 200f, h * 0.5f, z);
            // Quad 默认法线 +Z，放在 -Z 远处正对相机即可，无需旋转。
            var mCol = m.GetComponent<Collider>();
            if (mCol != null) Object.Destroy(mCol);
            var mr = m.GetComponent<MeshRenderer>();
            if (mtnMat != null) mr.sharedMaterial = mtnMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // 3) 雾：由 BambooSceneContext.ApplyFog() 统一管理，避免两处设置互相覆盖。
        //    AtmosphereLayer 只负责天空 + 远山；若 BambooSceneContext 不存在，本层也不会被注入。
    }
}
