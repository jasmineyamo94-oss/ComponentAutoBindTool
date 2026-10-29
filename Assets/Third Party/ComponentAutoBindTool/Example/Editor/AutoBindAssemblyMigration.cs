using System;
using System.IO;
using System.Linq;
using ComponentAutoBindTool.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Example.Editor
{
    /// <summary>
    /// 将正式实验台迁移到跟随宿主的输出目录；所有绑定内容仍由 Inspector 流水线生成。
    /// </summary>
    public static class AutoBindAssemblyMigration
    {
        private const string PluginRoot = "Assets/Third Party/ComponentAutoBindTool";
        private static readonly string[] Hosts =
            { "AutoBindTest", "AutoBindExampleWidget", "LabWidgetGroup", "LabShellView" };

        [MenuItem("自动绑定UI组件/框架实验台/迁移示例生成目录（保留GUID）")]
        public static void MigrateExample()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("请等待编辑器空闲并退出运行模式后迁移。");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            EditorApplication.LockReloadAssemblies();
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                foreach (string host in Hosts)
                {
                    string oldPath = PluginRoot + "/Scripts/ViewBindComponents/" + host + ".BindComponents.cs";
                    string newPath = AutoBindExampleSetup.ExampleRoot + "/Scripts/" + host + ".BindComponents.cs";
                    if (!File.Exists(oldPath))
                    {
                        if (!File.Exists(newPath)) throw new FileNotFoundException("缺少待迁移生成文件。", oldPath);
                        continue;
                    }
                    if (File.Exists(newPath)) throw new InvalidOperationException("新旧位置同时存在生成文件：" + host);
                    string guid = AssetDatabase.AssetPathToGUID(oldPath);
                    string error = AssetDatabase.MoveAsset(oldPath, newPath);
                    if (!string.IsNullOrEmpty(error)) throw new IOException(error);
                    if (AssetDatabase.AssetPathToGUID(newPath) != guid) throw new InvalidOperationException("生成文件 GUID 发生变化。");
                }

                var setting = AutoBindEditorAssetLocator.LoadSingletonAsset<AutoBindGlobalSetting>();
                if (setting == null) throw new InvalidOperationException("未找到唯一全局配置。");
                var data = new SerializedObject(setting);
                data.FindProperty("m_CodePath").stringValue = PluginRoot + "/Scripts/ViewBindComponents";
                data.FindProperty("m_UseGlobalDefaultSavePath").boolValue = true;
                data.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssetIfDirty(setting);

                int count = 0;
                foreach (string name in AutoBindExampleSetup.Catalog)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AutoBindExampleSetup.ExampleRoot + "/Prefabs/" + name + ".prefab");
                    if (prefab == null) throw new InvalidOperationException("示例 Prefab 不存在：" + name);
                    foreach (var tool in prefab.GetComponentsInChildren<Core.ComponentAutoBindTool>(true).Reverse())
                    {
                        Generate(tool);
                        count++;
                    }
                }

                var scene = EditorSceneManager.OpenScene(AutoBindExampleSetup.ScenePath);
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var tool in root.GetComponentsInChildren<Core.ComponentAutoBindTool>(true).Reverse())
                    {
                        Generate(tool);
                        count++;
                    }
                Debug.Log("AUTOBIND_ASSEMBLY_MIGRATION_PASS：已迁移 4 个生成文件并保存 " + count + " 个绑定宿主。");
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
                EditorApplication.UnlockReloadAssemblies();
            }
        }

        private static void Generate(Core.ComponentAutoBindTool tool)
        {
            var inspector = (ComponentAutoBindToolInspector)UnityEditor.Editor.CreateEditor(tool);
            try
            {
                var report = inspector.RunPipeline(true, false);
                if (!report.Succeeded) throw new InvalidOperationException(report.ValidationResult.BuildReport());
            }
            finally { Object.DestroyImmediate(inspector); }
        }
    }
}
