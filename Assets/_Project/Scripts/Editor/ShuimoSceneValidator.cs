#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.Rendering;
using System.Collections.Generic;
using System.Linq;

namespace Shuimo.EditorTools
{
    /// <summary>
    /// 一键场景体检 + 可选 InkGround 地面生成。
    /// 把所有容易踩的新手坑编码成一次点击的报告：
    ///   1) 误激活 HDRP/URP 导致全项目紫屏
    ///   2) 漏做地面 / 地面尺寸不够覆盖活动区
    ///   3) 地面 y 没对齐到 0（玩家/竹林根部）
    ///   4) 玩家(HeroineBone)缺失或 y 异常
    ///   5) 闪避执行序（PlayerController -100 先于 DodgeController -90）
    ///   6) 相机钳制(CameraFollow)在线
    /// 与 docs/ground-world-design-notes.md 的硬数据保持一致。
    /// </summary>
    public static class ShuimoSceneValidator
    {
        // 活动区硬尺寸（WorldBuilder: 120x80 格 * TileUnit 32 = 3840x2560）
        private const float WORLD_WIDTH = 3840f;
        private const float WORLD_HEIGHT = 2560f;
        private const float Y_TOLERANCE = 0.5f;
        private const string INK_GROUND_SHADER = "Xianxia/Ink/InkGround";
        private const string INK_GROUND_RICH_SHADER = "Xianxia/Ink/InkGroundRich";

        [MenuItem("Shuimo/Validate Scene", false, 2000)]
        public static void Validate()
        {
            var lines = new List<string>();
            int errors = 0, warns = 0;

            // 1) 渲染管线
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                lines.Add($"[错误] 当前激活了非 Built-in 渲染管线：{GraphicsSettings.currentRenderPipeline.GetType().Name}。请勿激活 HDRP/URP，否则全项目材质紫屏。");
                errors++;
            }
            else
            {
                lines.Add("[通过] 渲染管线 = Built-in（正确）。");
            }

            var scene = SceneManager.GetActiveScene();
            var all = new List<GameObject>();
            foreach (var root in scene.GetRootGameObjects())
                Collect(root, all);

            // 2) 地面
            var ground = all.FirstOrDefault(IsGround);
            if (ground == null)
            {
                lines.Add("[警告] 未发现地面（InkGround 材质或名为 Ground 的对象）。玩家会悬空/走到虚空。可用 Shuimo/Scene/Create Ink Ground 生成。");
                warns++;
            }
            else
            {
                float y = ground.transform.position.y;
                if (Mathf.Abs(y) > Y_TOLERANCE)
                {
                    lines.Add($"[警告] 地面 y={y:0.##}，建议=0 与玩家/竹林根部对齐。");
                    warns++;
                }
                else
                {
                    lines.Add($"[通过] 地面存在且 y≈0（{ground.name}）。");
                }

                var mf = ground.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    var s = mf.sharedMesh.bounds.size;
                    var sc = ground.transform.lossyScale;
                    float w = s.x * sc.x;
                    float h = s.z * sc.z;
                    if (w < WORLD_WIDTH * 0.9f || h < WORLD_HEIGHT * 0.9f)
                    {
                        lines.Add($"[警告] 地面尺寸 {w:0}x{h:0} 小于活动区 {WORLD_WIDTH}x{WORLD_HEIGHT}，边缘会露馅。建议铺满。");
                        warns++;
                    }
                    else
                    {
                        lines.Add($"[通过] 地面覆盖活动区（{w:0}x{h:0}）。");
                    }
                }

                // 朝向检查：Plane 默认水平(+Y)。若被误转成竖直则报警。
                var up = ground.transform.TransformDirection(Vector3.up);
                if (up.y < 0.5f)
                {
                    lines.Add("[警告] 地面法线不是朝上(+Y)，可能被误旋转过（常见坑：把 Plane 转了 -90° 立成墙）。请把 Rotation 复位到 (0,0,0)。");
                    warns++;
                }
            }

            // 3) 玩家
            var player = all.FirstOrDefault(g =>
                g.GetComponent<Xianxia.Unity.T2.PlayerController>() != null ||
                g.name.ToLower().Contains("heroine"));
            if (player == null)
            {
                lines.Add("[错误] 未发现玩家（HeroineBone / PlayerController）。");
                errors++;
            }
            else
            {
                lines.Add($"[信息] 玩家 = {player.name}，y={player.transform.position.y:0.##}。");
            }

            // 4) 闪避执行序
            ReportExecOrder(lines, typeof(Xianxia.Unity.T2.PlayerController));
            ReportExecOrder(lines, typeof(Xianxia.Unity.T2.DodgeController));

            var dodge = Object.FindObjectOfType<Xianxia.Unity.T2.DodgeController>();
            lines.Add(dodge != null
                ? "[通过] DodgeController 存在（闪避逻辑在线）。"
                : "[警告] 未发现 DodgeController（闪避逻辑缺失）。");

            // 5) 相机钳制
            var cam = Object.FindObjectOfType<Xianxia.Unity.T2.CameraFollow>();
            lines.Add(cam != null
                ? "[通过] CameraFollow 存在（活动区钳制在线）。"
                : "[信息] 未发现 CameraFollow（相机钳制）。");

            string summary = "=== Shuimo 场景体检 ===\n" + string.Join("\n", lines) +
                             $"\n--- 错误 {errors} / 警告 {warns} ---";
            Debug.Log(summary);
            EditorUtility.DisplayDialog("Shuimo 场景体检",
                $"错误 {errors} / 警告 {warns}\n详见 Console。", "OK");
        }

        [MenuItem("Shuimo/Scene/Create Ink Ground", false, 2100)]
        public static void CreateInkGround()
        {
            // 注意：Plane 默认已是水平(XZ 平面, 法线 +Y)，**不要**旋转 -90。
            var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
            go.name = "InkGround";
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            // 默认 Plane 为 10x10，缩放到覆盖活动区
            go.transform.localScale = new Vector3(WORLD_WIDTH / 10f, 1f, WORLD_HEIGHT / 10f);

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = EnsureInkGroundMaterial();

            if (go.GetComponent<MeshCollider>() == null)
                go.AddComponent<MeshCollider>();

            Undo.RegisterCreatedObjectUndo(go, "Create Ink Ground");
            Debug.Log("[Shuimo] 已生成 InkGround 地面：覆盖活动区 3840x2560，y=0，InkGround 材质，水平朝向(+Y)。");
            EditorUtility.DisplayDialog("Shuimo",
                "已生成 InkGround 地面（覆盖 3840x2560，y=0，水平朝向）。\n如不想用可删掉该对象，手动按设计笔记做也行。", "OK");
        }

        [MenuItem("Shuimo/Scene/Create Ink Ground (Rich)", false, 2101)]
        public static void CreateInkGroundRich()
        {
            // 增强水墨地表：宣纸底 + 淡墨晕染 + 毛笔笔触 + 细纸纹。
            // 注意：Plane 默认已是水平(XZ 平面, 法线 +Y)，**不要**旋转 -90。
            var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
            go.name = "InkGroundRich";
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = new Vector3(WORLD_WIDTH / 10f, 1f, WORLD_HEIGHT / 10f);

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = EnsureRichMaterial();

            if (go.GetComponent<MeshCollider>() == null)
                go.AddComponent<MeshCollider>();

            Undo.RegisterCreatedObjectUndo(go, "Create Ink Ground (Rich)");
            Debug.Log("[Shuimo] 已生成 InkGroundRich 地面（增强水墨地表，覆盖 3840x2560，y=0，水平朝向）。");
            EditorUtility.DisplayDialog("Shuimo",
                "已生成 InkGroundRich 地面（增强水墨笔触/晕染）。\n如不想用可删掉该对象，改回纯色 InkGround 也行。", "OK");
        }

        private static Material EnsureRichMaterial()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m != null && m.shader != null && m.shader.name.Contains("InkGroundRich"))
                    return m;
            }

            var shader = Shader.Find(INK_GROUND_RICH_SHADER);
            if (shader == null)
            {
                Debug.LogWarning($"[Shuimo] 找不到 {INK_GROUND_RICH_SHADER} shader，回退到 Standard。");
                return new Material(Shader.Find("Standard"));
            }

            var mat = new Material(shader) { name = "MAT_InkGroundRich" };
            const string dir = "Assets/_Project/Materials";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets/_Project", "Materials");
            AssetDatabase.CreateAsset(mat, dir + "/MAT_InkGroundRich.mat");
            AssetDatabase.SaveAssets();
            return mat;
        }

        private static Material EnsureInkGroundMaterial()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m != null && m.shader != null && m.shader.name.Contains("InkGround"))
                    return m;
            }

            var shader = Shader.Find(INK_GROUND_SHADER);
            if (shader == null)
            {
                Debug.LogWarning($"[Shuimo] 找不到 {INK_GROUND_SHADER} shader，回退到 Standard。");
                return new Material(Shader.Find("Standard"));
            }

            var mat = new Material(shader) { name = "MAT_InkGround" };
            const string dir = "Assets/_Project/Materials";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets/_Project", "Materials");
            AssetDatabase.CreateAsset(mat, dir + "/MAT_InkGround.mat");
            AssetDatabase.SaveAssets();
            return mat;
        }

        private static void ReportExecOrder(List<string> lines, System.Type t)
        {
            var attr = (DefaultExecutionOrder)System.Attribute.GetCustomAttribute(t, typeof(DefaultExecutionOrder));
            if (attr == null)
            {
                lines.Add($"[警告] {t.Name} 未标注 DefaultExecutionOrder。");
                return;
            }
            lines.Add($"[信息] {t.Name} 执行序 = {attr.order}（越小越先跑；PlayerController 应 -100 先于 DodgeController -90）。");
        }

        private static bool IsGround(GameObject g)
        {
            if (g.name.ToLower().Contains("ground") || g.name.ToLower().Contains("inkground"))
                return true;
            var mr = g.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != null && mr.sharedMaterial.shader != null &&
                mr.sharedMaterial.shader.name.Contains("InkGround"))
                return true;
            return false;
        }

        private static void Collect(GameObject g, List<GameObject> all)
        {
            all.Add(g);
            foreach (Transform c in g.transform)
                Collect(c.gameObject, all);
        }
    }
}
#endif
