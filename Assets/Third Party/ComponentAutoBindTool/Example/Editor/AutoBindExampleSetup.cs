using System;
using System.IO;
using System.Linq;
using ComponentAutoBindTool.Editor;
using ComponentAutoBindTool.ViewCore;
using ComponentAutoBindTool.Core;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Example.Editor
{
    public static class AutoBindExampleSetup
    {
        public const string ExampleRoot = "Assets/Third Party/ComponentAutoBindTool/Example";
        public const string ScenePath = ExampleRoot + "/Scenes/Example.unity";
        public const string PrefabPath = ExampleRoot + "/Prefabs/AutoBindTest.prefab";
        public static readonly string[] Catalog = { "AutoBindTest", "Cover", "Multi", "Delayed", "HUD", "Top", "System", "Widgets" };
        private static TMP_FontAsset m_Font;
        private static readonly Color Background = new Color32(12, 19, 32, 255);
        private static readonly Color Surface = new Color32(23, 35, 54, 255);
        private static readonly Color Accent = new Color32(48, 209, 196, 255);

        public static void PrepareResources()
        {
            if (!Directory.Exists("Assets/TextMesh Pro") && !Directory.Exists(ExampleRoot + "/TextMesh Pro"))
            {
                if (Application.isBatchMode) AssetDatabase.importPackageCompleted += FinishResourceImport;
                TMP_PackageResourceImporter.ImportResources(true, false, false);
            }
            else if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        private static void FinishResourceImport(string package)
        {
            if (package != "TMP Essential Resources") return;
            AssetDatabase.importPackageCompleted -= FinishResourceImport;
            EditorApplication.Exit(0);
        }

        [MenuItem("自动绑定UI组件/框架实验台/重建正式示例")]
        public static void Rebuild()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (Directory.Exists("Assets/TextMesh Pro") && !Directory.Exists(ExampleRoot + "/TextMesh Pro"))
            {
                string error = AssetDatabase.MoveAsset("Assets/TextMesh Pro", ExampleRoot + "/TextMesh Pro");
                if (!string.IsNullOrEmpty(error)) throw new IOException(error);
            }
            BuildFont();
            var scene = EditorSceneManager.OpenScene(ScenePath);
            foreach (var root in scene.GetRootGameObjects()) Object.DestroyImmediate(root);
            var canvas = new GameObject("FrameworkLab", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            var full = Rect("Background", canvas.transform, Vector2.zero, Vector2.one);
            Paint(full, Background);
            var safe = Rect("SafeArea", canvas.transform, Vector2.zero, Vector2.one);
            var shell = safe.gameObject.AddComponent<LabShellView>();
            shell.GetComponent<Core.ComponentAutoBindTool>().m_targetScript = shell;
            BuildShell(safe);
            var stage = safe.Find("Rtf_Stage");
            var roots = new Transform[4];
            var windows = Rect("NonRoot ==> Windows", stage, Vector2.zero, Vector2.one);
            for (int i = 0; i < roots.Length; i++)
                roots[i] = Rect(((UILayer)i).ToString(), windows, Vector2.zero, Vector2.one);
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            var keyMap = AutoBindEditorAssetLocator.LoadSingletonAsset<AutoBindKeyMapSetting>();
            if (keyMap != null && keyMap.extraComponentKeyMap != null)
            {
                bool changed = RemoveOldExampleMapping(keyMap, "Widget", typeof(AutoBindExampleWidget));
                changed |= RemoveOldExampleMapping(keyMap, "WidgetGroup", typeof(LabWidgetGroup));
                if (changed)
                {
                    EditorUtility.SetDirty(keyMap);
                    AssetDatabase.SaveAssetIfDirty(keyMap);
                }
            }

            var template = BuildWindow(stage);
            EditorSceneManager.SaveScene(scene);
            foreach (var tool in template.GetComponentsInChildren<Core.ComponentAutoBindTool>(true).Reverse())
                GenerateThroughInspector(tool);
            GenerateThroughInspector(shell.GetComponent<Core.ComponentAutoBindTool>());
            Directory.CreateDirectory(ExampleRoot + "/Prefabs");
            var prefabs = new GameObject[Catalog.Length];
            for (int i = 0; i < Catalog.Length; i++)
            {
                var host = template.GetComponent<AutoBindTest>();
                template.name = Catalog[i];
                host.Layer = i == 4 ? UILayer.HUD : i == 5 ? UILayer.Top : i == 6 ? UILayer.System : UILayer.Normal;
                host.InstanceMode = i == 2 ? WindowInstanceMode.Multiple : WindowInstanceMode.Singleton;
                host.CoverMode = i == 1 ? ViewCoverMode.PausePrevious : ViewCoverMode.Overlay;
                host.CloseStrategy = i == 3 ? ViewCloseStrategy.DestroyDelayed :
                    i == 2 || i == 5 || i == 6 ? ViewCloseStrategy.DestroyImmediate : ViewCloseStrategy.NonDestroy;
                host.DestroyDelaySeconds = 3;
                host.AllowNavigateBack = i != 4;
                template.GetComponentInChildren<LabWidgetGroup>(true).gameObject.SetActive(i == 7);
                prefabs[i] = PrefabUtility.SaveAsPrefabAsset(template, ExampleRoot + "/Prefabs/" + Catalog[i] + ".prefab", out bool saved);
                if (!saved) throw new IOException("保存实验窗口失败：" + Catalog[i]);
            }
            Object.DestroyImmediate(template);
            var scope = new GameObject("ExampleScope").AddComponent<AutoBindExampleScope>();
            scope.Shell = shell;
            scope.LabPrefabs = prefabs;
            scope.LabRoots = roots;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("FRAMEWORK_LAB_BUILD_PASS：场景、8 个窗口 Prefab 和 4 个绑定宿主已生成。");
        }

        private static bool RemoveOldExampleMapping(AutoBindKeyMapSetting map, string key, Type oldType)
        {
            return map.extraComponentKeyMap.TryGetValue(key, out string value) &&
                   value == oldType.FullName && map.extraComponentKeyMap.Remove(key);
        }

        private static void BuildFont()
        {
            string path = ExampleRoot + "/Fonts/LabChinese.asset";
            m_Font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            bool created = m_Font == null;
            if (created)
            {
                var font = AssetDatabase.LoadAssetAtPath<Font>(ExampleRoot + "/Fonts/NotoSansCJKsc-Regular.otf");
                m_Font = TMP_FontAsset.CreateFontAsset(font, 42, 5, GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, true);
                m_Font.name = "LabChinese";
            }
            m_Font.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            if (created)
            {
                AssetDatabase.CreateAsset(m_Font, path);
                AssetDatabase.AddObjectToAsset(m_Font.material, m_Font);
            }
            string characters = string.Concat(Directory.GetFiles(ExampleRoot, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
            characters += string.Concat(Enumerable.Range(32, 95).Select(c => (char)c));
            characters = new string(characters.Where(c => !char.IsControl(c)).Distinct().ToArray());
            m_Font.TryAddCharacters(characters, out _);
            foreach (var texture in m_Font.atlasTextures)
            {
                if (texture != null && !AssetDatabase.Contains(texture)) AssetDatabase.AddObjectToAsset(texture, m_Font);
                if (texture != null) EditorUtility.SetDirty(texture);
            }
            string required = "自动绑定窗口层级与遮挡实例参数生命周期导航排队依赖注入观察重置计数" +
                string.Concat(Enumerable.Range(32, 95).Select(c => (char)c));
            if (required.Any(c => !m_Font.HasCharacter(c)))
                throw new InvalidOperationException("字体预烘焙未完成。请在启用图形设备的 Unity 中重建示例，命令行不要使用 -nographics。");
            EditorUtility.SetDirty(m_Font);
            m_Font.atlasPopulationMode = AtlasPopulationMode.Static;
            AssetDatabase.SaveAssetIfDirty(m_Font);
        }

        private static void BuildShell(RectTransform parent)
        {
            var header = Rect("Header", parent, new Vector2(0, 1), Vector2.one, new Vector2(24, -96), new Vector2(-24, -16));
            Label("Brand", header, "AUTOBIND / FRAMEWORK LAB", 20, Accent, new Vector2(0, 0.5f), Vector2.one);
            Label("TMTxt_Topic", header, "窗口层级与遮挡", 32, Color.white, Vector2.zero, new Vector2(0.7f, 0.55f));
            var observe = Button("Btn_Observe", header, "观察运行状态", new Vector2(0.77f, 0), Vector2.one);
            var nav = Rect("Navigation", parent, new Vector2(0, 0), new Vector2(0.2f, 1), new Vector2(24, 120), new Vector2(-8, -116));
            Paint(nav, Surface);
            string[] names = { "01  层级与遮挡", "02  实例与参数", "03  生命周期", "04  导航与队列", "05  Widget / DI" };
            for (int i = 0; i < 5; i++)
            {
                float top = 1 - i * 0.2f;
                Button("Btn_Nav" + (i + 1), nav, names[i], new Vector2(0, top - 0.19f), new Vector2(1, top));
            }
            var hint = Rect("Hint", parent, new Vector2(0.21f, 0.79f), new Vector2(0.98f, 0.86f));
            Label("TMTxt_Hint", hint, "选择操作，在舞台上观察真实窗口。", 20, new Color32(170, 191, 210, 255), Vector2.zero, Vector2.one);
            var stage = Rect("Rtf_Stage", parent, new Vector2(0.21f, 0.18f), new Vector2(0.98f, 0.78f));
            Paint(stage, new Color32(17, 27, 43, 255));
            var actions = Rect("Actions", parent, new Vector2(0.21f, 0.025f), new Vector2(0.98f, 0.15f));
            for (int i = 0; i < 6; i++)
                Button("Btn_Action" + (i + 1), actions, "操作 " + (i + 1), new Vector2(i / 6f, 0), new Vector2((i + 1) / 6f - 0.008f, 1));
            Button("Btn_Reset", parent, "重置实验", new Vector2(0.018f, 0.025f), new Vector2(0.194f, 0.15f));
            var drawer = Rect("Rtf_Drawer", parent, new Vector2(0.21f, 0.18f), new Vector2(0.98f, 0.78f));
            Paint(drawer, new Color32(22, 38, 57, 255));
            drawer.GetComponent<Image>().raycastTarget = true;
            drawer.gameObject.AddComponent<Canvas>().overrideSorting = true;
            drawer.GetComponent<Canvas>().sortingOrder = 8000;
            drawer.gameObject.AddComponent<GraphicRaycaster>();
            drawer.gameObject.AddComponent<CanvasGroup>();
            Label("DrawerTitle", drawer, "观察台  /  实例与生命周期 · 上下滑动", 24, Accent, new Vector2(0.025f, 0.86f), new Vector2(0.97f, 0.99f));
            var scroll = Rect("SRect_Trace", drawer, new Vector2(0.025f, 0.035f), new Vector2(0.975f, 0.84f));
            var rect = scroll.gameObject.AddComponent<ScrollRect>();
            var viewport = Rect("Viewport", scroll, Vector2.zero, Vector2.one);
            Paint(viewport, Color.clear);
            viewport.GetComponent<Image>().raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();
            var content = Rect("Content", viewport, new Vector2(0, 1), Vector2.one);
            content.pivot = new Vector2(0.5f, 1);
            var text = Label("TMTxt_Trace", content, "等待实验事件…", 20, new Color32(206, 222, 236, 255), Vector2.zero, Vector2.one);
            text.alignment = TextAlignmentOptions.TopLeft;
            rect.content = content;
            rect.viewport = viewport;
            rect.horizontal = false;
            rect.movementType = ScrollRect.MovementType.Clamped;
            drawer.gameObject.SetActive(false);
            var portrait = Rect("Rtf_Portrait", parent, Vector2.zero, Vector2.one);
            Paint(portrait, Background);
            portrait.GetComponent<Image>().raycastTarget = true;
            portrait.gameObject.AddComponent<Canvas>().overrideSorting = true;
            portrait.GetComponent<Canvas>().sortingOrder = 10000;
            portrait.gameObject.AddComponent<GraphicRaycaster>();
            Label("Rotate", portrait, "请横屏体验框架实验台", 32, Color.white, Vector2.zero, Vector2.one);
            portrait.gameObject.SetActive(false);
        }

        private static GameObject BuildWindow(Transform parent)
        {
            var card = Rect("AutoBindTest", parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            card.sizeDelta = new Vector2(620, 350);
            Paint(card, new Color32(34, 52, 74, 255));
            card.GetComponent<Image>().raycastTarget = true;
            var host = card.gameObject.AddComponent<AutoBindTest>();
            card.GetComponent<Core.ComponentAutoBindTool>().m_targetScript = host;
            Label("TMTxt_Title", card, "窗口实例", 28, Accent, new Vector2(0.04f, 0.80f), new Vector2(0.73f, 0.98f));
            Button("Btn_Close", card, "关闭", new Vector2(0.77f, 0.80f), new Vector2(0.98f, 0.99f));
            var status = Label("TMTxt_Status", card, "Instance / Scope / UIView", 22, Color.white, new Vector2(0.04f, 0.05f), new Vector2(0.96f, 0.78f));
            status.alignment = TextAlignmentOptions.TopLeft;
            var groupRect = Rect("Widget_GroupDetails", card, new Vector2(0.03f, 0.02f), new Vector2(0.97f, 0.68f));
            Paint(groupRect, Surface);
            var group = groupRect.gameObject.AddComponent<LabWidgetGroup>();
            groupRect.GetComponent<Core.ComponentAutoBindTool>().m_targetScript = group;
            Label("TMTxt_Scope", groupRect, "嵌套 Widget / 窗口共享 Scope", 20, Accent, new Vector2(0.03f, 0.82f), new Vector2(0.97f, 1));
            BuildLeaf("Widget_Left", groupRect, new Vector2(0.02f, 0.33f), new Vector2(0.49f, 0.81f));
            BuildLeaf("Widget_Right", groupRect, new Vector2(0.51f, 0.33f), new Vector2(0.98f, 0.81f));
            Button("Btn_Toggle", groupRect, "隐藏 / 恢复左侧", new Vector2(0.02f, 0.02f), new Vector2(0.49f, 0.30f));
            Button("Btn_Unbind", groupRect, "解绑整个 Widget", new Vector2(0.51f, 0.02f), new Vector2(0.98f, 0.30f));
            var subCanvas = Rect("SubCanvas", card, new Vector2(0.9f, 0.72f), new Vector2(0.96f, 0.78f)).gameObject.AddComponent<Canvas>();
            subCanvas.overrideSorting = true;
            subCanvas.sortingOrder = 2;
            Paint(subCanvas.GetComponent<RectTransform>(), Accent);
            return card.gameObject;
        }

        private static void BuildLeaf(string name, Transform parent, Vector2 min, Vector2 max)
        {
            var rect = Rect(name, parent, min, max);
            var host = rect.gameObject.AddComponent<AutoBindExampleWidget>();
            host.GetComponent<Core.ComponentAutoBindTool>().m_targetScript = host;
            Label("TMTxt_Counter", rect, "计数 0 · Tick 0", 20, Color.white, new Vector2(0, 0.60f), Vector2.one);
            Button("Btn_Increment", rect, "数据 +1", Vector2.zero, new Vector2(1, 0.60f));
        }

        public static void GenerateThroughInspector(Core.ComponentAutoBindTool target,
            AutoBindGlobalSetting settings = null, AutoBindKeyMapSetting map = null)
        {
            var inspector = (ComponentAutoBindToolInspector)UnityEditor.Editor.CreateEditor(target);
            try
            {
                var report = inspector.RunPipeline(true, false, settings, map);
                if (!report.Succeeded) throw new InvalidOperationException(report.ValidationResult.BuildReport());
            }
            finally { Object.DestroyImmediate(inspector); }
        }

        public static RectTransform Rect(string name, Transform parent, Vector2 min, Vector2 max,
            Vector2 offsetMin = default, Vector2 offsetMax = default)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            return rect;
        }

        private static void Paint(RectTransform rect, Color color)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        private static TextMeshProUGUI Label(string name, Transform parent, string value, float size, Color color, Vector2 min, Vector2 max)
        {
            var rect = Rect(name, parent, min, max);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.font = m_Font;
            label.text = value;
            label.fontSize = size;
            label.color = color;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.margin = new Vector4(12, 0, 10, 0);
            label.raycastTarget = false;
            return label;
        }

        private static Button Button(string name, Transform parent, string text, Vector2 min, Vector2 max)
        {
            var rect = Rect(name, parent, min, max);
            Paint(rect, new Color32(40, 66, 85, 255));
            rect.GetComponent<Image>().raycastTarget = true;
            var button = rect.gameObject.AddComponent<Button>();
            var colors = button.colors;
            colors.highlightedColor = new Color(0.72f, 1, 1);
            colors.pressedColor = new Color(0.40f, 0.78f, 0.78f);
            colors.fadeDuration = 0.1f;
            button.colors = colors;
            Label("Label", rect, text, 22, Color.white, Vector2.zero, Vector2.one).alignment = TextAlignmentOptions.Center;
            return button;
        }
    }
}
