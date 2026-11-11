#if USE_ADDRESSABLES
using System;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.AddressableAssets.Build;
using UnityEngine;
namespace AAGame.Editor.Assets
{
    public static class AddressablesDemoBuild
    {
        [MenuItem("Tools/项目工具/资源后端/Addressables/配置本地资源")]
        public static void Configure()
        {
            // 先完整校验再修改 SDK 配置，重复 Key 不留下部分迁移结果。
            var assets = DemoAssetKeySetup.GetManagedAssets();
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            ProjectConfigData.ShowGroupsAsHierarchy = true;
            var uiGroup = ConfigureGroup(settings, "AAGameDemo-UI");
            var dataGroup = ConfigureGroup(settings, "AAGameDemo-Data");
            foreach (var asset in assets)
            {
                var guid = AssetDatabase.AssetPathToGUID(asset.Path);
                settings.CreateOrMoveEntry(guid, asset.IsUi ? uiGroup : dataGroup).address = asset.Key;
            }
            // 只清理迁移后确实为空的旧演示组；其他用户条目仍留在原组。
            var legacyGroup = settings.FindGroup("AAGameDemo");
            if (legacyGroup != null && legacyGroup.entries.Count == 0)
            {
                if (settings.DefaultGroup == legacyGroup) settings.DefaultGroup = uiGroup;
                settings.RemoveGroup(legacyGroup);
            }
            var packed = settings.DataBuilders.FindIndex(builder => builder != null && builder.GetType().Name == "BuildScriptPackedPlayMode");
            if (packed >= 0) settings.ActivePlayModeDataBuilderIndex = packed;
            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(uiGroup);
            EditorUtility.SetDirty(dataGroup);
            AssetDatabase.SaveAssets();
        }
        private static AddressableAssetGroup ConfigureGroup(AddressableAssetSettings settings, string name)
        {
            var group = settings.FindGroup(name) ?? settings.CreateGroup(name, false, false, true, null,
                typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            var schema = group.GetSchema<BundledAssetGroupSchema>() ?? group.AddSchema<BundledAssetGroupSchema>();
            schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            EditorUtility.SetDirty(schema);
            return group;
        }
        [MenuItem("Tools/项目工具/资源后端/Addressables/构建本地内容")]
        public static void Build()
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("存在未保存场景，请先保存或放弃更改后再构建资源。");
            Configure();
            // 显式生成报告，避免 SDK 首次构建弹出模态确认阻塞自动构建。
            ProjectConfigData.GenerateBuildLayout = true;
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
            if (!string.IsNullOrEmpty(result.Error)) throw new InvalidOperationException(result.Error);
            Debug.Log("Addressables 本地资源构建完成。");
        }
    }
}
#endif
