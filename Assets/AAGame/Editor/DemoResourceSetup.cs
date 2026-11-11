using System;
using System.IO;
using System.Linq;
using AAGame.Assets;
using AAGame.Editor.Assets;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>只迁移资源接入，不重建已有场景、布局或绑定代码。</summary>
public static class DemoResourceSetup
{
    public const string SettingsPath = "Assets/AAGame/Settings/AssetBackendSettings.asset";
    public const string SourcePrefabs = "Assets/AAGame/UI/Prefabs/";
    public const string ResourcesRoot = "Assets/AAGame/Resources/AAGameDemo/";
    public static readonly string[] ViewNames =
    {
        "StartMenuView", "MainRootMenuView", "LevelHudView", "LevelLoadingView"
    };

    public static AssetBackendSettings EnsureSettings()
    {
        var settings = AssetDatabase.LoadAssetAtPath<AssetBackendSettings>(SettingsPath);
        if (settings != null)
            return settings;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
        settings = ScriptableObject.CreateInstance<AssetBackendSettings>();
        AssetDatabase.CreateAsset(settings, SettingsPath);
        return settings;
    }

    [MenuItem("Tools/项目工具/资源后端/同步 Resources 演示资源")]
    public static void SyncResources()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("请停止运行后同步资源。");
        if (!BackendSwitchCoordinator.IsBusy && EnsureSettings().Backend != AssetBackendKind.Resources)
            throw new InvalidOperationException("当前使用 SDK 后端，请先完整切换到 Resources 后再同步演示副本。");
        DemoAssetKeySetup.ValidateUniqueKeys();
        MigrateLegacyResourceCopies();
        Directory.CreateDirectory(ResourcesRoot);
        Directory.CreateDirectory("Assets/AAGame/Content");
        const string textPath = "Assets/AAGame/Content/DemoText.txt";
        if (!File.Exists(textPath))
            File.WriteAllText(textPath, "AAGame resource backend demo\n");
        AssetDatabase.Refresh();
        foreach (string name in ViewNames)
        {
            string source = SourcePrefabs + name + ".prefab";
            string target = ResourcesRoot + DemoAssetKeys.Ui(name) + ".prefab";
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(source);
            if (asset == null)
                throw new InvalidOperationException("演示 Prefab 不存在：" + source);
            // SaveAsPrefabAsset 覆盖既有副本时保留 GUID；不复制脚本或编辑生成代码。
            var contents = PrefabUtility.LoadPrefabContents(source);
            try
            {
                PrefabUtility.SaveAsPrefabAsset(contents, target);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }
        string textTarget = ResourcesRoot + DemoAssetKeys.DemoText + ".txt";
        File.Copy(textPath, textTarget, true);
        AssetDatabase.ImportAsset(textTarget);
        EnsureSettings();
        AssetDatabase.SaveAssets();
        Debug.Log("Resources 演示副本已同步。");
    }

    /// <summary>只迁移已有副本，未启用 Resources 时不主动生成资源。</summary>
    public static void MigrateLegacyResourceCopies()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("请停止运行后迁移 Resources 副本。");
        if (!Directory.Exists(ResourcesRoot)) return;
        var moves = ViewNames.Select(name => (
            Old: ResourcesRoot + "UI/" + name + ".prefab",
            New: ResourcesRoot + DemoAssetKeys.Ui(name) + ".prefab"))
            .Append((Old: ResourcesRoot + "Data/DemoText.txt", New: ResourcesRoot + DemoAssetKeys.DemoText + ".txt"))
            .ToArray();
        // 先检查全部冲突，不能迁移到一半才发现另一对文件同时存在。
        foreach (var move in moves)
        {
            bool oldExists = File.Exists(move.Old) || File.Exists(move.Old + ".meta");
            bool newExists = File.Exists(move.New) || File.Exists(move.New + ".meta");
            if (oldExists && newExists)
                throw new InvalidOperationException("Resources 新旧副本同时存在，请先核对，工具不会覆盖：" + move.Old + " / " + move.New);
            if (oldExists && !File.Exists(move.Old))
                throw new InvalidOperationException("旧副本只剩 .meta，工具已保留，请先恢复或核对：" + move.Old);
        }
        foreach (var move in moves)
        {
            if (!File.Exists(move.Old)) continue;
            string error = AssetDatabase.MoveAsset(move.Old, move.New);
            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException("Resources 副本迁移失败：" + move.Old + "；" + error);
        }
        foreach (string folder in new[] { ResourcesRoot + "UI", ResourcesRoot + "Data" })
        {
            if (AssetDatabase.IsValidFolder(folder) && Directory.GetFileSystemEntries(folder).Length == 0)
                if (!AssetDatabase.DeleteAsset(folder))
                    throw new InvalidOperationException("无法移除空的旧副本目录：" + folder);
        }
    }

    [MenuItem("Tools/项目工具/资源后端/迁移现有 Bootstrap 资源配置")]
    public static void MigrateBootstrap()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("请停止运行后迁移配置。");
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        SyncResources();
        var previous = EditorSceneManager.GetSceneManagerSetup();
        try
        {
            var scene = EditorSceneManager.OpenScene("Assets/AAGame/Scenes/BootstrapScene.unity");
            var root = VContainer.Unity.LifetimeScope.Find<GameRootLifetimeScope>(scene) as GameRootLifetimeScope;
            if (root == null)
                throw new InvalidOperationException("Bootstrap 缺少根 Scope。");
            root.AssetSettings = EnsureSettings();
            EditorUtility.SetDirty(root);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.DeleteAsset("Assets/AAGame/Settings/FlowCatalog.asset");
        }
        finally
        {
            EditorSceneManager.RestoreSceneManagerSetup(previous);
        }
    }

    public static void Select(AssetBackendKind backend)
    {
        BackendSwitchCoordinator.Start(backend);
    }
}
