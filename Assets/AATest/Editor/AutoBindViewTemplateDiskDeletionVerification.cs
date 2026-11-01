using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ComponentAutoBindTool.Editor;
using ComponentAutoBindTool.ViewCore;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Tests
{
    // Seed 和 DeleteAfterRestart 由两个独立 Editor 进程调用，仅操作隔离验证副本。
    [InitializeOnLoad]
    public static class AutoBindViewTemplateDiskDeletionVerification
    {
        private const string Key = "AutoBind.TemplateDiskWorkflow.";
        private const string ContextPath = "Library/AutoBindTemplateDiskVerification.json";
        [Serializable]
        private sealed class Context
        {
            public AutoBindViewTemplateDefinition Definition;
            public string Root, ScenePath, SceneContents;
        }

        static AutoBindViewTemplateDiskDeletionVerification()
        {
            if (SessionState.GetBool(Key + "Running", false)) EditorApplication.update += Tick;
        }

        private static void Start(int stage)
        {
            if (!Application.isBatchMode || !Directory.GetCurrentDirectory().Contains("CodexValidation"))
                throw new InvalidOperationException("只允许在隔离 CodexValidation batchmode 副本验证磁盘删除。");
            SessionState.SetBool(Key + "Running", true);
            SessionState.SetInt(Key + "Stage", stage);
            SessionState.SetString(Key + "Deadline", DateTime.UtcNow.AddMinutes(10).Ticks.ToString());
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        public static void Seed()
        {
            Start(0);
            string root = "Assets/AATest/DiskDeleteGenerated/R" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(root);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            var canvas = new GameObject("ExistingCanvas", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.GraphicRaycaster));
            new GameObject("UserMarker");
            var definition = AutoBindViewTemplateDefinition.Create("DiskRestart", root + "/Prefabs", root + "/Scripts", "DiskDeletionVerification." + Path.GetFileName(root), false);
            File.WriteAllText(ContextPath, JsonUtility.ToJson(new Context { Root = root, ScenePath = root + "/Saved.unity", Definition = definition }));
            AutoBindViewTemplateCreation.instance.Begin(definition, canvas);
        }

        public static void DeleteAfterRestart()
        {
            Start(10);
            Require(!AutoBindViewTemplateCreation.instance.CanClearCompleted && !AutoBindViewTemplateCreation.instance.HasPending, "新 Editor 仍依赖上次创建回执。");
            var context = Load();
            EditorSceneManager.OpenScene(context.ScenePath);
            if (!File.Exists(context.Root + "/DuplicateGlobal.asset"))
                AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<AutoBindGlobalSetting>(), context.Root + "/DuplicateGlobal.asset");
        }

        private static Context Load() => JsonUtility.FromJson<Context>(File.ReadAllText(ContextPath));
        private static void Stage(int stage) => SessionState.SetInt(Key + "Stage", stage);
        private static string External(Context context) => context.Root + "/DiskExternalDependency.cs";

        public static void VerifyLinkedDirectory()
        {
            Require(Application.isBatchMode && Directory.GetCurrentDirectory().Contains("CodexValidation"), "链接校验仅允许在隔离副本执行。");
            const string path = "Assets/AATest/TemplateDeletionLinkProbe/Linked";
            Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0, "未准备链接目录夹具。");
            bool blocked = false;
            try { AutoBindViewTemplateDeletion.Locate("Linked", path, "Assets/AATest/TemplateDeletionLinkProbe/LinkedView.prefab"); }
            catch (ArgumentException) { blocked = true; }
            Require(blocked, "链接目录没有被拒绝。");
            Debug.Log("DISK_TEMPLATE_LINK_GUARD_PASS");
        }

        private static void Tick()
        {
            if (!SessionState.GetBool(Key + "Running", false) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            try
            {
                Require(DateTime.UtcNow.Ticks < long.Parse(SessionState.GetString(Key + "Deadline", "0")), "磁盘删除工作流超时。");
                var context = Load();
                var definition = context.Definition;
                var creation = AutoBindViewTemplateCreation.instance;
                var deletion = AutoBindViewTemplateDeletion.instance;
                if (creation.IsBusy || deletion.IsBusy) return;
                int stage = SessionState.GetInt(Key + "Stage", 0);
                if (stage == 0)
                {
                    Require(creation.Phase == AutoBindTemplatePhase.Completed, creation.Message);
                    // 测试夹具显式保存一次，让下一进程能够验证工具不写盘。
                    Require(EditorSceneManager.SaveScene(creation.CreatedInstance.scene, context.ScenePath), "无法保存重启夹具。");
                    context.SceneContents = File.ReadAllText(context.ScenePath);
                    File.WriteAllText(ContextPath, JsonUtility.ToJson(context));
                    Finish("DISK_TEMPLATE_SEED_PASS");
                    return;
                }
                if (stage == 10)
                {
                    var main = ScriptableObject.CreateInstance<AutoBindViewTemplateWindow>();
                    try
                    {
                        var data = new SerializedObject(main);
                        data.FindProperty("m_BaseName").stringValue = definition.BaseName;
                        data.ApplyModifiedPropertiesWithoutUndo();
                        main.CreateGUI();
                        Require(main.rootVisualElement.Q<Button>("deleteLocatedTemplate").enabledSelf, "按位置删除不可用：" + AutoBindViewTemplateDeletion.BlockReason() + "；基础名：" + main.rootVisualElement.Q<TextField>("baseName").value);
                        Require(!main.rootVisualElement.Q<Button>("createTemplate").enabledSelf, "重复配置没有禁用创建。");
                    }
                    finally { Object.DestroyImmediate(main); }
                    Undo.RecordObject(Marker(), "验证磁盘删除保留场景 Undo");
                    Marker().position = new Vector3(33, 0, 0);
                    Undo.FlushUndoRecordObjects();
                    var originalHost = SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<ViewBase>(true)).Single();
                    new GameObject("UserKeptChild").transform.SetParent(originalHost.transform, false);
                    SessionState.SetString(Key + "Graph", Graph());
                    File.AppendAllText(definition.ViewPath, "\n// 用户编辑后的脚本\n");
                    File.AppendAllText(definition.PrefabPath, "\n# 用户编辑后的 Prefab\n");
                    File.WriteAllText(definition.ScriptDirectory + "/Business.txt", "保留的额外业务文件");
                    File.WriteAllText(definition.ArgsPath + ".meta", "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
                    var snapshot = AutoBindViewTemplateDeletion.Locate(definition.BaseName, definition.ScriptDirectory, definition.PrefabPath);
                    Require(!snapshot.HasEmptyDirectory && snapshot.Entries.Single(entry => entry.Path == definition.ArgsPath).HasMeta, "额外文件或孤立 Args 定位错误。");
                    SessionState.SetString(Key + "Snapshot", JsonUtility.ToJson(snapshot));
                    AutoBindViewTemplateDeletionWindow.ConfirmAndDelete(snapshot, (_, _, _) => true);
                    SessionState.SetString(Key + "Backup", deletion.BackupPath);
                    Stage(11);
                    return;
                }
                if (stage == 11)
                {
                    Require(deletion.Phase == AutoBindTemplateDeletionPhase.Completed, deletion.Message);
                    Require(Graph() == SessionState.GetString(Key + "Graph", ""), "磁盘删除改变了场景对象。");
                    Require(File.ReadAllText(context.ScenePath) == context.SceneContents, "磁盘删除保存了场景。");
                    var snapshot = JsonUtility.FromJson<AutoBindTemplateAssetDeletion.Snapshot>(SessionState.GetString(Key + "Snapshot", ""));
                    foreach (var entry in snapshot.Entries)
                    {
                        Require(!File.Exists(entry.Path) && !File.Exists(entry.Path + ".meta"), "遗漏删除：" + entry.Path);
                        if (entry.HasFile) Require(Hash(Path.Combine(deletion.BackupPath, entry.Path)) == entry.Hash, "备份内容错误。");
                        if (entry.HasMeta) Require(Hash(Path.Combine(deletion.BackupPath, entry.Path + ".meta")) == entry.MetaHash, "元数据备份错误。");
                    }
                    Require(File.ReadAllText(definition.ScriptDirectory + "/Business.txt").Contains("保留"), "额外文件被删除。");
                    Require(deletion.BackupPath == SessionState.GetString(Key + "Backup", ""), "重载重复执行了删除。");
                    Undo.PerformUndo();
                    Require(Marker().position == Vector3.zero, "磁盘删除清除了场景 Undo。");
                    Require(AssetDatabase.DeleteAsset(context.Root + "/DuplicateGlobal.asset"), "无法撤除配置夹具。");
                    Debug.Log("DISK_TEMPLATE_RESTART_AND_SCENE_PASS");
                    Stage(20);
                    return;
                }
                if (stage == 20)
                {
                    context.Definition = AutoBindViewTemplateDefinition.Create(definition.BaseName, Path.GetDirectoryName(definition.PrefabPath), Path.GetDirectoryName(definition.ScriptDirectory), definition.Namespace, true);
                    File.WriteAllText(ContextPath, JsonUtility.ToJson(context));
                    creation.Begin(context.Definition, SceneManager.GetActiveScene().GetRootGameObjects().Single(root => root.name == "ExistingCanvas"));
                    Stage(21);
                    return;
                }
                if (stage == 21)
                {
                    Require(creation.Phase == AutoBindTemplatePhase.Completed && File.Exists(definition.ArgsPath), "同名切换 Args 重建失败：" + creation.Message);
                    var canvas = SceneManager.GetActiveScene().GetRootGameObjects().Single(root => root.name == "ExistingCanvas");
                    var kept = canvas.GetComponentsInChildren<Transform>(true).SingleOrDefault(child => child.name == "UserKeptChild");
                    Require(canvas.transform.childCount >= 2 && kept != null && kept.parent != creation.CreatedInstance.transform, "重建未保留旧实例及其用户子节点。");
                    var type = creation.CreatedInstance.GetComponent<ViewBase>().GetType();
                    var route = (ViewRoute)type.Assembly.GetType(definition.Namespace + "." + definition.RoutesName).GetField("Main").GetValue(null);
                    Require(route.ArgsType.FullName == definition.Namespace + "." + definition.ArgsName, "重建路由参数错误。");
                    File.WriteAllText(External(context), "public static class DiskExternal" + Path.GetFileName(context.Root) + " { public static " + definition.FullViewName + " View; }");
                    AssetDatabase.ImportAsset(External(context));
                    UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                    Debug.Log("DISK_TEMPLATE_RECREATE_ARGS_PASS");
                    Stage(22);
                    return;
                }
                if (stage == 22)
                {
                    if (!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetType("DiskExternal" + Path.GetFileName(context.Root)) != null)) return;
                    AutoBindViewTemplateDeletionWindow.ConfirmAndDelete(AutoBindViewTemplateDeletion.Locate(definition.BaseName, definition.ScriptDirectory, definition.PrefabPath), (_, _, _) => true);
                    Require(!creation.CanClearCompleted, "删除后旧成功回执未失效。");
                    Stage(23);
                    return;
                }
                if (stage == 23)
                {
                    Require(deletion.Phase == AutoBindTemplateDeletionPhase.CompletedWithCompileErrors && deletion.Message.Contains("DiskExternalDependency"), "删除编译错误未报告。");
                    Require(!File.Exists(definition.ViewPath) && !File.Exists(definition.PrefabPath), "编译失败自动恢复了资产。");
                    Require(AutoBindViewTemplateDeletion.BlockReason() == null, "已有编译错误阻止了继续清理。");
                    AssetDatabase.CreateFolder(context.Root + "/Scripts", "MetaOnly");
                    string folder = context.Root + "/Scripts/MetaOnly";
                    File.WriteAllText(folder + "/MetaOnlyArgs.cs.meta", "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
                    deletion.Begin(AutoBindViewTemplateDeletion.Locate("MetaOnly", folder, context.Root + "/Prefabs/MetaOnlyView.prefab"));
                    Require(!Directory.Exists(folder) && deletion.Phase == AutoBindTemplateDeletionPhase.CompletedWithCompileErrors, "已有编译错误下清理失败。");
                    Debug.Log("DISK_TEMPLATE_COMPILE_ERRORS_AND_EXISTING_ERRORS_PASS");
                    Stage(24);
                    return;
                }
                if (stage == 24)
                {
                    Require(AssetDatabase.DeleteAsset(External(context)), "无法修复外部引用。");
                    Stage(25);
                    return;
                }
                if (stage == 25)
                {
                    SessionState.SetString(Key + "RetryBackup", deletion.BackupPath);
                    deletion.RetryCompilation();
                    Stage(26);
                    return;
                }
                Require(stage == 26 && deletion.Phase == AutoBindTemplateDeletionPhase.Completed, "编译重试失败：" + deletion.Message);
                Require(deletion.BackupPath == SessionState.GetString(Key + "RetryBackup", ""), "重试重复删除了文件。");
                Require(File.ReadAllText(context.ScenePath) == context.SceneContents, "最终场景被写盘。");
                Finish("DISK_TEMPLATE_DELETE_WORKFLOW_PASS");
            }
            catch (Exception error)
            {
                SessionState.SetBool(Key + "Running", false);
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        private static Transform Marker() => SceneManager.GetActiveScene().GetRootGameObjects().Single(root => root.name == "UserMarker").transform;
        private static string Graph() => string.Join("\n", SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .OrderBy(child => child.GetInstanceID()).Select(child => child.GetInstanceID() + ":" + child.name + ":" + (child.parent != null ? child.parent.GetInstanceID() : 0) + ":" + child.localPosition + ":" + child.localScale));
        private static string Hash(string path) { using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path))); }
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void Finish(string message) { SessionState.SetBool(Key + "Running", false); Debug.Log(message); EditorApplication.Exit(0); }
    }
}
