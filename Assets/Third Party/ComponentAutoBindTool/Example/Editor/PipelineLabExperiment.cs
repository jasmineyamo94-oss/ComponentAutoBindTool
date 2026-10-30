using System;
using System.IO;
using System.Linq;
using System.Text;
using ComponentAutoBindTool.Editor;
using ComponentAutoBindTool.Core;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Example.Editor
{
    public enum PipelineLabCase
    {
        FirstGeneration, RenameThenGenerate, NestedWidgetAndTMP, UnknownPrefix, MissingComponent,
        DuplicateField, InvalidName, InvalidPath, NoOpGeneration, SceneSave, PrefabSave, InstanceOverride, Rollback
    }

    [Serializable]
    public sealed class PipelineLabState
    {
        public int Experiment;
        public string TargetId, Report, Diff, Hierarchy, GeneratedPath;
        public bool Succeeded, BindingsChanged, CodeChanged, ComponentRestored, CodeRestored, AssetRestored;
        public long BeforeTime, AfterTime;
        public void Save() => SessionState.SetString("AutoBind.PipelineLab", JsonUtility.ToJson(this));
        public static PipelineLabState Load()
        {
            var json = SessionState.GetString("AutoBind.PipelineLab", "");
            return string.IsNullOrEmpty(json) ? new PipelineLabState() : JsonUtility.FromJson<PipelineLabState>(json);
        }
    }

    // 所有故障与写入都限定在此目录。配置仅为内存副本，不进入全局资产索引。
    public static class PipelineLabExperiment
    {
        public const string Workspace = AutoBindExampleSetup.ExampleRoot + "/LocalPipelineLab";
        public const string ScenePath = Workspace + "/Experiment.unity";
        public const string CodePath = Workspace + "/PipelineLabHost.BindComponents.cs";
        public static readonly string[] Names = { "正常扫描 / 首次生成", "节点改名后直接生成", "嵌套边界 / Widget / TMP 跳过", "失败：未知前缀", "失败：缺失组件",
            "失败：重复字段", "失败：非法名称", "失败：无效路径", "连续生成不改写", "保存场景", "保存 Prefab", "场景实例仅产生覆盖", "提交失败与完整回滚" };

        public static Core.ComponentAutoBindTool Resolve(PipelineLabState state)
        {
            return GlobalObjectId.TryParse(state.TargetId, out var id)
                ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as Core.ComponentAutoBindTool : null;
        }

        public static PipelineLabState Prepare(PipelineLabCase experiment)
        {
            EnsureIdle();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var openScene = SceneManager.GetSceneAt(i);
                if (string.IsNullOrEmpty(openScene.path))
                    throw new InvalidOperationException("请先保存当前未命名场景，再创建隔离实验场景。");
            }
            var oldScene = SceneManager.GetActiveScene();
            var existing = SceneManager.GetSceneByPath(ScenePath);
            if (existing.IsValid() && existing.isLoaded) EditorSceneManager.CloseScene(existing, true);
            Directory.CreateDirectory(Workspace);
            AssetDatabase.ImportAsset(Workspace);
            if (File.Exists(CodePath)) AssetDatabase.DeleteAsset(CodePath);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            var root = new GameObject("PipelineLabTarget");
            var host = root.AddComponent<PipelineLabHost>();
            var tool = root.AddComponent<Core.ComponentAutoBindTool>();
            tool.m_targetScript = host;
            Child(root.transform, "Btn_First", typeof(Button));
            EditorSceneManager.SaveScene(scene, ScenePath);
            var state = new PipelineLabState { Experiment = (int)experiment, GeneratedPath = CodePath };
            state.TargetId = GlobalObjectId.GetGlobalObjectIdSlow(tool).ToString();
            if (experiment == PipelineLabCase.RenameThenGenerate || experiment == PipelineLabCase.NoOpGeneration || experiment == PipelineLabCase.Rollback)
                Execute(state, true);
            switch (experiment)
            {
                case PipelineLabCase.RenameThenGenerate: root.transform.GetChild(0).name = "Btn_Renamed"; break;
                case PipelineLabCase.UnknownPrefix: root.transform.GetChild(0).name = "Unknown_Field"; break;
                case PipelineLabCase.MissingComponent: root.transform.GetChild(0).name = "Txt_Missing"; break;
                case PipelineLabCase.DuplicateField: Child(root.transform, "Btn_First", typeof(Button)); break;
                case PipelineLabCase.InvalidName: root.transform.GetChild(0).name = "Btn_Bad-Name"; break;
                case PipelineLabCase.NestedWidgetAndTMP:
                    var nested = Child(root.transform, "Widget_GroupDetails", typeof(LabWidgetGroup));
                    Child(nested.transform, "Unknown_InsideBoundary", typeof(Button));
                    var tmp = new GameObject("Unknown_TMPGenerated", typeof(RectTransform));
                    tmp.SetActive(false);
                    tmp.transform.SetParent(root.transform);
                    tmp.AddComponent<TMP_SubMeshUI>();
                    break;
                case PipelineLabCase.Rollback:
                    root.transform.GetChild(0).name = "Btn_Renamed";
                    new GameObject("保留这项既有未保存编辑");
                    break;
                case PipelineLabCase.PrefabSave:
                case PipelineLabCase.InstanceOverride:
                    var prefab = PrefabUtility.SaveAsPrefabAsset(root, Workspace + "/Experiment.prefab");
                    Object.DestroyImmediate(root);
                    if (experiment == PipelineLabCase.InstanceOverride)
                    {
                        root = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                        tool = root.GetComponent<Core.ComponentAutoBindTool>();
                        root.transform.GetChild(0).name = "Btn_Override";
                        PrefabUtility.RecordPrefabInstancePropertyModifications(root.transform.GetChild(0).gameObject);
                    }
                    else tool = prefab.GetComponent<Core.ComponentAutoBindTool>();
                    state.TargetId = GlobalObjectId.GetGlobalObjectIdSlow(tool).ToString();
                    break;
            }
            if (experiment != PipelineLabCase.Rollback) EditorSceneManager.SaveScene(scene);
            else EditorSceneManager.MarkSceneDirty(scene);
            state.TargetId = GlobalObjectId.GetGlobalObjectIdSlow(tool).ToString();
            if (oldScene.IsValid() && oldScene.isLoaded) SceneManager.SetActiveScene(oldScene);
            state.Report = "实验已准备。点击扫描或生成，查看实际结果。";
            state.Diff = "尚未执行本步骤。";
            state.Hierarchy = Describe(tool);
            state.Save();
            return state;
        }

        public static AutoBindPipelineReport Execute(PipelineLabState state, bool generate)
        {
            EnsureIdle();
            var tool = Resolve(state);
            if (tool == null) throw new InvalidOperationException("实验对象不可用，请恢复实验初始状态。");
            var setting = Object.Instantiate(AutoBindEditorAssetLocator.LoadSingletonAsset<AutoBindGlobalSetting>());
            var map = Object.Instantiate(AutoBindEditorAssetLocator.LoadSingletonAsset<AutoBindKeyMapSetting>());
            var data = new SerializedObject(setting);
            data.FindProperty("m_UseGlobalDefaultSavePath").boolValue = false;
            data.FindProperty("m_CodePath").stringValue = state.Experiment == (int)PipelineLabCase.InvalidPath ? "../OutsideAssets" : Workspace;
            data.ApplyModifiedPropertiesWithoutUndo();
            var inspector = (ComponentAutoBindToolInspector)UnityEditor.Editor.CreateEditor(tool);
            string before = EditorJsonUtility.ToJson(tool);
            string source = Read(CodePath);
            string owner = EditorUtility.IsPersistent(tool) ? AssetDatabase.GetAssetPath(tool) : tool.gameObject.scene.path;
            byte[] ownerBytes = File.ReadAllBytes(owner);
            string prefabPath = Workspace + "/Experiment.prefab";
            byte[] prefabBefore = File.Exists(prefabPath) ? File.ReadAllBytes(prefabPath) : Array.Empty<byte>();
            state.BeforeTime = Timestamp(CodePath);
            var originalAttributes = File.Exists(CodePath) ? File.GetAttributes(CodePath) : FileAttributes.Normal;
            var scene = tool.gameObject.scene;
            void FailCommit(Scene saved)
            {
                if (saved == scene && File.Exists(CodePath)) File.SetAttributes(CodePath, FileAttributes.ReadOnly);
            }
            bool inject = generate && state.Experiment == (int)PipelineLabCase.Rollback;
            try
            {
                if (inject) EditorSceneManager.sceneSaved += FailCommit;
                var report = inspector.RunPipeline(generate, false, setting, map);
                state.Succeeded = report.Succeeded;
                state.BindingsChanged = report.BindingsUpdated;
                state.CodeChanged = report.CodeFileUpdated;
                state.AfterTime = Timestamp(CodePath);
                state.ComponentRestored = before == EditorJsonUtility.ToJson(tool);
                state.CodeRestored = source == Read(CodePath);
                state.AssetRestored = ownerBytes.SequenceEqual(File.ReadAllBytes(owner));
                state.GeneratedPath = report.GeneratedFilePath ?? CodePath;
                state.Report = $"成功：{report.Succeeded}\n绑定变化：{report.BindingsUpdated} / 代码变化：{report.CodeFileUpdated}\n" +
                    report.ValidationResult.BuildReport() + $"\n文件 UTC ticks：{state.BeforeTime} → {state.AfterTime}\n" +
                    $"与执行前相同：组件={state.ComponentRestored}，代码={state.CodeRestored}，所属资产={state.AssetRestored}";
                if (state.Experiment == (int)PipelineLabCase.InstanceOverride)
                    state.Report += "\n源 Prefab 保持不变：" + prefabBefore.SequenceEqual(File.ReadAllBytes(prefabPath));
                if (inject)
                    state.Report += "\n既有编辑保留：" + (scene.GetRootGameObjects().Any(o => o.name == "保留这项既有未保存编辑") && scene.isDirty);
                state.Diff = Diff(source, Read(CodePath));
                state.Hierarchy = Describe(tool);
                state.Save();
                return report;
            }
            finally
            {
                EditorSceneManager.sceneSaved -= FailCommit;
                if (inject && File.Exists(CodePath)) File.SetAttributes(CodePath, originalAttributes);
                Object.DestroyImmediate(inspector);
                Object.DestroyImmediate(setting);
                Object.DestroyImmediate(map);
            }
        }

        private static GameObject Child(Transform parent, string name, Type type)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.AddComponent(type);
            return go;
        }
        private static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : "";
        private static long Timestamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        private static string Diff(string before, string after)
        {
            if (before == after) return "源码完全一致；未产生内容差异。";
            var text = new StringBuilder("− 执行前 / + 执行后\n");
            var a = before.Split('\n'); var b = after.Split('\n');
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                string left = i < a.Length ? a[i] : "", right = i < b.Length ? b[i] : "";
                if (left == right) continue;
                if (left.Length > 0) text.AppendLine("− " + left);
                if (right.Length > 0) text.AppendLine("+ " + right);
            }
            return text.ToString();
        }
        private static string Describe(Core.ComponentAutoBindTool tool)
        {
            var text = new StringBuilder("目标层级\n");
            foreach (var child in tool.GetComponentsInChildren<Transform>(true)) text.AppendLine("  " + child.name);
            text.AppendLine("\n已提交绑定表");
            foreach (var binding in tool.BindDatas) text.AppendLine(binding.Name + " → " + binding.BindCom);
            return text.ToString();
        }
        private static void EnsureIdle()
        {
            // UI 入口在编译和导入期间禁用；无 GUI 测试可持有自己的程序集重载锁。
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Play Mode 期间禁止写入实验资产。");
        }
    }
}
