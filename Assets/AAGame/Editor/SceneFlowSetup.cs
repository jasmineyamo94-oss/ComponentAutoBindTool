using System;
using System.IO;
using System.Linq;
using ComponentAutoBindTool.Core;
using ComponentAutoBindTool.Editor;
using ComponentAutoBindTool.ViewCore;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class SceneFlowSetup
{
    private const string Prefabs = "Assets/AAGame/UI/Prefabs/";

    [MenuItem("AAGame/场景流程/生成演示资产")]
    public static void Build()
    {
        Directory.CreateDirectory(Prefabs);
        // 先拆除旧组件，再删除其专属产物，保留 PSD 图层和美术。
        string old = "Assets/PsdToUnityUI Res/View Prefabs/战斗-主菜单界面.prefab";
        var legacy = PrefabUtility.LoadPrefabContents(old);
        foreach (var c in legacy.GetComponents<MonoBehaviour>())
            if (c != null && c.GetType().Name == "LevelMenuView")
                UnityEngine.Object.DestroyImmediate(c);
        PrefabUtility.SaveAsPrefabAsset(legacy, old);
        PrefabUtility.UnloadPrefabContents(legacy);
        foreach (
            var f in new[]
            {
                "Assets/AAGame/Scripts/UI/LevelMenuView.cs",
                "Assets/AAGame/Scripts/UI/Generated/LevelMenuView.Bind.cs",
                "Assets/PsdToUnityUI/PrefabAutoBind/Config/战斗-主菜单界面.PrefabAutoBind.asset",
            }
        )
            AssetDatabase.DeleteAsset(f);
        var setting = AssetDatabase.LoadAssetAtPath<AutoBindGlobalSetting>(
            "Assets/Third Party/ComponentAutoBindTool/AutoBindGlobalSetting.asset"
        );
        var map = AssetDatabase.LoadAssetAtPath<AutoBindKeyMapSetting>(
            "Assets/Third Party/ComponentAutoBindTool/AutoBindKeyMapSetting.asset"
        );
        var temp = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        foreach (
            var type in new[]
            {
                typeof(StartMenuView),
                typeof(MainRootMenuView),
                typeof(LevelHudView),
                typeof(LevelLoadingView),
            }
        )
        {
            var go = new GameObject(
                type.Name,
                typeof(RectTransform),
                typeof(Canvas),
                typeof(GraphicRaycaster)
            );
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var host = (ViewBase)go.AddComponent(type);
            host.Layer = type == typeof(LevelLoadingView) ? UILayer.System : UILayer.HUD;
            host.CoverMode = ViewCoverMode.Overlay;
            host.AllowNavigateBack = false;
            var image = go.AddComponent<Image>();
            image.color = new Color(.06f, .09f, .15f, 1);
            image.raycastTarget = true;
            Label(
                "TMTxt_Title",
                go.transform,
                type == typeof(StartMenuView) ? "开始"
                    : type == typeof(MainRootMenuView) ? "主界面"
                    : type == typeof(LevelHudView) ? "关卡"
                    : "加载中",
                new Vector2(.1f, .72f),
                new Vector2(.9f, .9f),
                40
            );
            Label(
                "TMTxt_Status",
                go.transform,
                "准备就绪",
                new Vector2(.1f, .5f),
                new Vector2(.9f, .7f),
                28
            );
            if (type != typeof(LevelLoadingView))
                Button(
                    "Btn_Primary",
                    go.transform,
                    type == typeof(StartMenuView) ? "开始"
                        : type == typeof(MainRootMenuView) ? "进入关卡"
                        : "原地重开",
                    new Vector2(.15f, .22f),
                    new Vector2(.48f, .4f)
                );
            if (type == typeof(LevelHudView))
                Button(
                    "Btn_Back",
                    go.transform,
                    "返回主界面",
                    new Vector2(.52f, .22f),
                    new Vector2(.85f, .4f)
                );
            if (type == typeof(LevelLoadingView))
            {
                var bar = Rect(
                    "Img_Progress",
                    go.transform,
                    new Vector2(.15f, .3f),
                    new Vector2(.85f, .36f)
                )
                    .gameObject.AddComponent<Image>();
                bar.color = new Color(.2f, .8f, .7f);
                bar.type = Image.Type.Filled;
                bar.fillMethod = Image.FillMethod.Horizontal;
            }
            go.GetComponent<ComponentAutoBindTool.Core.ComponentAutoBindTool>().m_targetScript =
                host;
            setting = AssetDatabase.LoadAssetAtPath<AutoBindGlobalSetting>(
                "Assets/Third Party/ComponentAutoBindTool/AutoBindGlobalSetting.asset"
            );
            map = AssetDatabase.LoadAssetAtPath<AutoBindKeyMapSetting>(
                "Assets/Third Party/ComponentAutoBindTool/AutoBindKeyMapSetting.asset"
            );
            var report = AutoBindBuildPipeline.GenerateCode(
                go.GetComponent<ComponentAutoBindTool.Core.ComponentAutoBindTool>(),
                setting,
                map
            );
            if (!report.Succeeded)
                throw new Exception(report.ValidationResult.BuildReport());
            PrefabUtility.SaveAsPrefabAsset(go, Prefabs + type.Name + ".prefab");
            UnityEngine.Object.DestroyImmediate(go);
        }
        var catalog =
            AssetDatabase.LoadAssetAtPath<FlowCatalog>("Assets/AAGame/Settings/FlowCatalog.asset")
            ?? ScriptableObject.CreateInstance<FlowCatalog>();
        catalog.Prefabs = new[]
        {
            "StartMenuView",
            "MainRootMenuView",
            "LevelHudView",
            "LevelLoadingView",
        }
            .Select(n => AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + n + ".prefab"))
            .ToArray();
        const string catalogPath = "Assets/AAGame/Settings/FlowCatalog.asset";
        if (!AssetDatabase.Contains(catalog))
            AssetDatabase.CreateAsset(catalog, catalogPath);
        else
            EditorUtility.SetDirty(catalog);
        string bootstrap = "Assets/AAGame/Scenes/BootstrapScene.unity";
        if (
            AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/AAGame/Scenes/RootScene.unity")
            != null
        )
            AssetDatabase.MoveAsset("Assets/AAGame/Scenes/RootScene.unity", bootstrap);
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var camera = new GameObject(
            "GameCamera",
            typeof(Camera),
            typeof(AudioListener)
        ).GetComponent<Camera>();
        camera.tag = "MainCamera";
        camera.orthographic = true;
        camera.transform.position = new Vector3(0, 0, -10);
        camera.backgroundColor = new Color(.06f, .09f, .15f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        var baseData = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
        var ui = new GameObject("UICamera", typeof(Camera)).GetComponent<Camera>();
        ui.orthographic = true;
        ui.transform.position = new Vector3(0, 0, -10);
        var uiData = ui.gameObject.AddComponent<UniversalAdditionalCameraData>();
        uiData.renderType = CameraRenderType.Overlay;
        baseData.cameraStack.Add(ui);
        new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        var registry = new GameObject("GlobalUIRoot").AddComponent<GlobalUIRootRegistry>();
        registry.Main = Stage("MainStage", registry.transform, ui);
        registry.Level = Stage("LevelStage", registry.transform, ui);
        registry.Overlay = Stage("GlobalOverlay", registry.transform, ui);
        var scope = new GameObject("GameRootLifetimeScope").AddComponent<GameRootLifetimeScope>();
        scope.Catalog = catalog;
        scope.UIRoots = registry;
        var retryRoot = Rect(
            "BootstrapRetry",
            registry.Overlay.transform,
            Vector2.zero,
            Vector2.one
        );
        var retryCanvas = retryRoot.gameObject.AddComponent<Canvas>();
        retryCanvas.overrideSorting = true;
        retryCanvas.sortingOrder = 30000;
        retryRoot.gameObject.AddComponent<GraphicRaycaster>();
        var retry = retryRoot.gameObject.AddComponent<BootstrapRetryPanel>();
        retry.Canvas = retryCanvas;
        Label("Error", retryRoot, "", new Vector2(.1f, .5f), new Vector2(.9f, .8f), 24);
        retry.Error = retryRoot.GetComponentInChildren<TextMeshProUGUI>();
        retry.Retry = Button(
            "Retry",
            retryRoot,
            "重试",
            new Vector2(.3f, .2f),
            new Vector2(.7f, .4f)
        );
        EditorSceneManager.SaveScene(scene, bootstrap);
        foreach (var name in new[] { "Start", "Main", "Level" })
        {
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var obj = new GameObject(name + "SceneScope");
            var component = (VContainer.Unity.LifetimeScope)
                obj.AddComponent(Type.GetType(name + "SceneScope, Assembly-CSharp"));
            if (name == "Level")
                component.autoRun = false;
            EditorSceneManager.SaveScene(scene, "Assets/AAGame/Scenes/" + name + "Scene.unity");
        }
        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(bootstrap, true),
            new EditorBuildSettingsScene("Assets/AAGame/Scenes/StartScene.unity", true),
            new EditorBuildSettingsScene("Assets/AAGame/Scenes/MainScene.unity", true),
            new EditorBuildSettingsScene("Assets/AAGame/Scenes/LevelScene.unity", true),
        }
            .Concat(
                EditorBuildSettings.scenes.Where(x => !x.path.StartsWith("Assets/AAGame/Scenes/"))
            )
            .ToArray();
        AssetDatabase.SaveAssets();
        EditorSceneManager.OpenScene(bootstrap);
        AssetDatabase.Refresh();
    }

    private static StageViewRootHelper Stage(string name, Transform parent, Camera camera)
    {
        var rect = Rect(name, parent, Vector2.zero, Vector2.one);
        var canvas = rect.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = camera;
        canvas.planeDistance = 1;
        var scaler = rect.gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        scaler.matchWidthOrHeight = .5f;
        rect.gameObject.AddComponent<CanvasGroup>();
        return rect.gameObject.AddComponent<StageViewRootHelper>();
    }

    private static RectTransform Rect(string name, Transform parent, Vector2 min, Vector2 max)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        return rect;
    }

    private static void Label(
        string name,
        Transform parent,
        string text,
        Vector2 min,
        Vector2 max,
        int size
    )
    {
        var label = Rect(name, parent, min, max).gameObject.AddComponent<TextMeshProUGUI>();
        label.font = EnsureFont();
        label.text = text;
        label.fontSize = size;
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
    }

    private static TMP_FontAsset EnsureFont()
    {
        const string path = "Assets/AAGame/UI/Fonts/FlowChinese.asset";
        var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
        if (existing != null)
            return existing;
        Directory.CreateDirectory("Assets/AAGame/UI/Fonts");
        var source = AssetDatabase.LoadAssetAtPath<Font>(
            "Assets/Third Party/ComponentAutoBindTool/Example/Fonts/NotoSansCJKsc-Regular.otf"
        );
        var font = TMP_FontAsset.CreateFontAsset(
            source,
            48,
            5,
            UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
            2048,
            2048,
            AtlasPopulationMode.Dynamic
        );
        font.name = "FlowChinese";
        AssetDatabase.CreateAsset(font, path);
        var content = string.Concat(
            Directory
                .GetFiles("Assets/AAGame", "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText)
        );
        var characters = new string(
            content.Concat(Enumerable.Range(32, 95).Select(x => (char)x)).Distinct().ToArray()
        );
        font.TryAddCharacters(characters, out string missing);
        font.atlasPopulationMode = AtlasPopulationMode.Static;
        foreach (var texture in font.atlasTextures)
            AssetDatabase.AddObjectToAsset(texture, font);
        AssetDatabase.AddObjectToAsset(font.material, font);
        EditorUtility.SetDirty(font);
        AssetDatabase.SaveAssets();
        return font;
    }

    private static Button Button(
        string name,
        Transform parent,
        string label,
        Vector2 min,
        Vector2 max
    )
    {
        var rect = Rect(name, parent, min, max);
        var image = rect.gameObject.AddComponent<Image>();
        image.color = new Color(.12f, .3f, .4f);
        rect.gameObject.AddComponent<Button>().targetGraphic = image;
        Label("Label", rect, label, Vector2.zero, Vector2.one, 28);
        return rect.GetComponent<Button>();
    }
}
