// -----------------------------------------------------------------------------
// InventoryDemo.cs —— 背包 + 技能图鉴的运行时演示装载器
//
// 【为什么用 [RuntimeInitializeOnLoadMethod] 自挂载】
// 跟 AtmosphereLayer 同款零侵入套路：不修改任何场景文件、不往 Hierarchy 里手动拖东西，
// 进入 PlayMode 后自动创建一套 Canvas + 网格 + 图鉴。你按 'I' 键可开关显示。
//
// 【内容全是占位 / 示例】下面塞的「竹材 / 嫩笋 / 朱果」只是为了让格子非空、看得见效果，
// 真正的物品清单由你用 ItemDefinition（ScriptableObject）在编辑器里定义。
// 这个 Demo 只是「大概的骨架」，证明整条链路（模型 → 定义 → UI）跑得通。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using Xianxia.Combat;
using Xianxia.Inventory;

namespace Xianxia.Unity.T2
{
    /// <summary>背包 + 技能图鉴的运行时演示装载器（自挂载）。</summary>
    [DefaultExecutionOrder(300)]
    public sealed class InventoryDemo : MonoBehaviour
    {
        private static InventoryDemo s_instance;

        private GameObject _canvasGo;
        private InventoryModel _model;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Auto()
        {
            if (!Application.isPlaying)
            {
                return;
            }
            if (s_instance != null)
            {
                return;
            }
            if (GameObject.Find("InventoryDemoRoot") != null)
            {
                return;
            }
            GameObject go = new GameObject("InventoryDemoRoot");
            s_instance = go.AddComponent<InventoryDemo>();
        }

        private void Awake()
        {
            Build();
        }

        private void OnDestroy()
        {
            if (s_instance == this)
            {
                s_instance = null;
            }
        }

        private void Update()
        {
            // 按 I 开关背包 + 图鉴显示（纯演示快捷键，不与战斗输入冲突）。
            if (Input.GetKeyDown(KeyCode.I) && _canvasGo != null)
            {
                _canvasGo.SetActive(!_canvasGo.activeSelf);
            }
        }

        private void Build()
        {
            _canvasGo = new GameObject("InventoryCanvas");
            _canvasGo.transform.SetParent(transform, false);
            Canvas canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50; // 低于 Hud(100)，不挡血条 / 调试面板
            CanvasScaler scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = Hud.ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            _canvasGo.AddComponent<GraphicRaycaster>();

            BuildInventory(canvas.transform);
            BuildCodex(canvas.transform);
        }

        private void BuildInventory(Transform canvas)
        {
            // 1) 数据模型（纯逻辑）
            const int capacity = 20;
            const int columns = 4;
            _model = new InventoryModel(capacity);

            // 2) 物品定义查表（运行时造占位示例；正式物品请建 ItemDefinition.asset）
            Dictionary<string, ItemDefinition> defs = new Dictionary<string, ItemDefinition>();
            RegisterDef(defs, "bamboo_wood", "竹材", ItemType.Material, 99,
                        new Color(0.55f, 0.72f, 0.45f, 1.0f), "砍断竹子所得，建造与炼制的基础材料。");
            RegisterDef(defs, "bamboo_shoot", "嫩笋", ItemType.Material, 99,
                        new Color(0.80f, 0.86f, 0.55f, 1.0f), "小概率额外掉落，可食用或炼丹。");
            RegisterDef(defs, "vermilion_fruit", "朱果", ItemType.Consumable, 10,
                        new Color(0.92f, 0.40f, 0.34f, 1.0f), "示例消耗品（占位），食用回复少量气血。");

            // 3) 注册最大堆叠并塞入示例数量
            foreach (KeyValuePair<string, ItemDefinition> kv in defs)
            {
                _model.RegisterMaxStack(kv.Key, kv.Value.MaxStack);
            }
            _model.Add("bamboo_wood", 30);
            _model.Add("bamboo_shoot", 12);
            _model.Add("vermilion_fruit", 3);

            // 4) UI
            new InventoryGridView(canvas, _model, defs, columns);
        }

        private static void RegisterDef(Dictionary<string, ItemDefinition> defs, string id,
                                        string name, ItemType type, int max, Color tint, string desc)
        {
            ItemDefinition d = ScriptableObject.CreateInstance<ItemDefinition>();
            d.ItemId = id;
            d.DisplayName = name;
            d.Type = type;
            d.MaxStack = max;
            d.Tint = tint;
            d.Description = desc;
            defs[id] = d;
        }

        private void BuildCodex(Transform canvas)
        {
            SkillTable table = SkillConfig.BuildDefaultTable();
            new SkillCodexPanel(canvas, table);
        }
    }
}
