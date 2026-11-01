using System;
using System.IO;
using System.Linq;
using ComponentAutoBindTool.Editor;
using ComponentAutoBindTool.ViewCore;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using VContainer;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Tests
{
    // 仅在隔离 batchmode 副本执行；同一个 Editor 进程真实跨过两轮编译。
    [InitializeOnLoad]
    public static class AutoBindViewTemplateVerification
    {
        private const string Key = "AutoBind.TemplateVerification.";
        private const int CaseCount = 15;
        static AutoBindViewTemplateVerification()
        {
            if (SessionState.GetBool(Key + "Running", false)) EditorApplication.update += Tick;
        }

        public static void Start()
        {
            if (!Application.isBatchMode || !Directory.GetCurrentDirectory().Contains("CodexValidation"))
                throw new InvalidOperationException("此验证只允许在 CodexValidation 隔离 batchmode 副本执行。");
            SessionState.SetString(Key + "Root", "Assets/AATest/ViewTemplateGenerated/R" + Guid.NewGuid().ToString("N"));
            SessionState.SetInt(Key + "Case", 0);
            SessionState.SetBool(Key + "Started", false);
            SessionState.SetBool(Key + "Retried", false);
            SessionState.SetInt(Key + "CleanupStage", 0);
            SessionState.SetBool(Key + "Running", true);
            SessionState.SetString(Key + "Deadline", DateTime.UtcNow.AddMinutes(18).Ticks.ToString());
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (!SessionState.GetBool(Key + "Running", false) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            try
            {
                if (DateTime.UtcNow.Ticks > long.Parse(SessionState.GetString(Key + "Deadline", "0")))
                    throw new TimeoutException("模板真实重载验证超时。");
                int index = SessionState.GetInt(Key + "Case", 0);
                if (!SessionState.GetBool(Key + "Started", false)) { Prepare(index); return; }
                var job = AutoBindViewTemplateCreation.instance;
                if (job.IsBusy) return;
                if (index == 6 && job.Phase == AutoBindTemplatePhase.Failed && !SessionState.GetBool(Key + "Retried", false))
                {
                    Require(AssetDatabase.DeleteAsset(job.Definition.PrefabPath), "无法撤除 Prefab 路径目录故障。");
                    SessionState.SetBool(Key + "Retried", true);
                    job.Retry();
                    return;
                }
                if (index >= 9)
                {
                    if (!VerifyCleanup(index)) return;
                }
                else if (index < 3 || index == 6 || index == 8) VerifySuccess(index);
                else VerifyFailure(index);
                SessionState.SetInt(Key + "Case", index + 1);
                SessionState.SetBool(Key + "Started", false);
                SessionState.SetInt(Key + "CleanupStage", 0);
                Debug.Log("UI_VIEW_TEMPLATE_CASE_PASS: " + index);
                if (index + 1 < CaseCount) return;
                SessionState.SetBool(Key + "Running", false);
                Debug.Log("UI_VIEW_TEMPLATE_WORKFLOW_PASS: " + CaseCount + "，两轮编译、未命名场景、父节点、Undo、路由与清理验证通过。");
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                SessionState.SetBool(Key + "Running", false);
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        private static void Prepare(int index)
        {
            string workspace = SessionState.GetString(Key + "Root", "");
            string basis = "TemplateCase" + index;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            GameObject parent = null;
            if (index != 2)
            {
                var canvas = new GameObject("ExistingCanvas", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.GraphicRaycaster));
                canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                parent = new GameObject("SelectedParent", typeof(RectTransform));
                parent.transform.SetParent(canvas.transform, false);
            }
            var existing = new GameObject("既有未保存编辑");
            existing.transform.position = new Vector3(13, 27, 0);
            Directory.CreateDirectory(workspace);
            if (index == 0)
            {
                string scenePath = workspace + "/Saved.unity";
                Require(EditorSceneManager.SaveScene(scene, scenePath), "无法准备已保存场景。");
                SessionState.SetString(Key + "SceneContents", File.ReadAllText(scenePath));
                existing.transform.position = new Vector3(42, 27, 0);
                EditorSceneManager.MarkSceneDirty(scene);
            }
            var definition = AutoBindViewTemplateDefinition.Create(basis, workspace + "/Prefabs", workspace + "/Scripts", "TemplateVerification." + Path.GetFileName(workspace), index != 8 && index != 9);
            AutoBindViewTemplateCreation.instance.Begin(definition, parent);
            bool duplicateBlocked = false;
            try { AutoBindViewTemplateCreation.instance.Begin(definition, parent); }
            catch (InvalidOperationException) { duplicateBlocked = true; }
            Require(duplicateBlocked, "重复提交没有被阻止。");
            if (index == 3) Object.DestroyImmediate(parent);
            if (index == 4) File.WriteAllText(definition.BindingPath, "// 外部文件，不属于模板任务\n");
            if (index == 5) File.AppendAllText(definition.ViewPath, "// 用户修改，必须保留\n");
            if (index == 6) { Directory.CreateDirectory(definition.PrefabPath); AssetDatabase.ImportAsset(definition.PrefabPath); }
            if (index == 7) File.AppendAllText(definition.ViewPath + ".meta", "\n# 用户修改元数据\n");
            SessionState.SetBool(Key + "Started", true);
        }

        private static void VerifySuccess(int index, bool verifyUndo = true)
        {
            var job = AutoBindViewTemplateCreation.instance;
            Require(job.Phase == AutoBindTemplatePhase.Completed, "模板创建失败：" + job.Message);
            var definition = job.Definition;
            foreach (string path in definition.OutputPaths) Require(File.Exists(path), "缺少产物：" + path);
            Require(Directory.GetFiles(definition.ScriptDirectory, "*.cs").Length == (definition.UseArgs ? 5 : 4), "脚本数量与 Args 选项不一致。");
            if (!definition.UseArgs) Require(!File.Exists(definition.ArgsPath), "未勾选 Args 时仍创建了 Args 文件。");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(definition.PrefabPath);
            Require(prefab != null && GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab) == 0, "Prefab 存在脚本缺失。");
            Require(prefab.layer == LayerMask.NameToLayer("UI") && prefab.transform.GetChild(0).gameObject.layer == prefab.layer, "Prefab 未使用 UI 层。");
            var instance = job.CreatedInstance;
            Require(instance != null && PrefabUtility.GetCorrespondingObjectFromSource(instance) == prefab, "场景实例未连接到新 Prefab。");
            Require(instance.transform.parent.name == (index == 2 ? "Canvas" : "SelectedParent"), "实例挂载到错误父节点。");
            var host = instance.GetComponent<ViewBase>();
            Require(host != null && instance.GetComponent<Core.ComponentAutoBindTool>().m_targetScript == host, "框架组件或目标引用缺失。");
            Require(instance.GetComponent<Canvas>() != null && instance.GetComponent<UnityEngine.UI.GraphicRaycaster>() != null, "缺少窗口 Canvas 或 Raycaster。");
            var content = instance.transform.Find("Content") as RectTransform;
            Require(content != null && content.anchorMin == Vector2.zero && content.anchorMax == Vector2.one, "Content 没有铺满根节点。");
            Require(host.InstanceMode == WindowInstanceMode.Singleton && host.CloseStrategy == ViewCloseStrategy.NonDestroy, "窗口默认策略改变。");
            Scene scene = instance.scene;
            Require(scene.isDirty, "创建后场景没有保持未保存状态。");
            if (index == 0)
            {
                Require(File.ReadAllText(scene.path) == SessionState.GetString(Key + "SceneContents", ""), "创建过程保存了整个场景。");
                Require(scene.GetRootGameObjects().Single(root => root.name == "既有未保存编辑").transform.position.x == 42, "丢失既有未保存编辑。");
            }
            else Require(string.IsNullOrEmpty(scene.path), "未命名场景被自动保存。");
            Require(scene.GetRootGameObjects().Sum(root => root.GetComponentsInChildren<EventSystem>(true).Length) == 1, "EventSystem 缺失或重复。");
            ((IAutoBindHost)host).EnsureAutoBind(host.gameObject);
            Require(((IAutoBindHost)host).IsAutoBound, "新宿主绑定失败。");
            VerifyRoute(definition, prefab, instance.transform.parent);
            if (!verifyUndo) return;
            Undo.PerformUndo();
            Require(instance == null, "Undo 没有撤销场景实例。");
            Require(File.Exists(definition.PrefabPath), "Undo 删除了磁盘 Prefab。");
            Undo.PerformRedo();
            var restored = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<ViewBase>(true))
                .Single(view => view.GetType().FullName == definition.FullViewName);
            Require(restored.transform.parent.name == (index == 2 ? "Canvas" : "SelectedParent"), "Redo 丢失父节点。");
            var restoredRect = (RectTransform)restored.transform;
            Require(restoredRect.offsetMin == Vector2.zero && restoredRect.offsetMax == Vector2.zero, "Redo 丢失铺满布局。");
            Require(PrefabUtility.IsPartOfPrefabInstance(restored), "Redo 丢失 Prefab 连接。");
        }

        private static bool VerifyCleanup(int index)
        {
            var job = AutoBindViewTemplateCreation.instance;
            int stage = SessionState.GetInt(Key + "CleanupStage", 0);
            if (stage == 0)
            {
                VerifySuccess(index, false);
                if (index == 9)
                {
                    // 未生成的 Args 即使后来出现，也不是本次清除的所有物。
                    File.WriteAllText(job.Definition.ArgsPath, "// 外部未使用 Args 文件\n");
                    AssetDatabase.ImportAsset(job.Definition.ArgsPath);
                }
                // 对同一成功实例逐项修改与还原，所有拒绝必须发生在任何文件删除之前。
                if (index == 10)
                {
                    string source = File.ReadAllText(job.Definition.ViewPath);
                    File.AppendAllText(job.Definition.ViewPath, "\n// 用户编辑\n");
                    RequireClearRejected();
                    File.WriteAllText(job.Definition.ViewPath, source);
                    string meta = File.ReadAllText(job.Definition.ViewPath + ".meta");
                    File.AppendAllText(job.Definition.ViewPath + ".meta", "\n# 用户编辑\n");
                    RequireClearRejected();
                    File.WriteAllText(job.Definition.ViewPath + ".meta", meta);
                    string prefab = File.ReadAllText(job.Definition.PrefabPath);
                    File.AppendAllText(job.Definition.PrefabPath, "\n# 用户编辑\n");
                    RequireClearRejected();
                    File.WriteAllText(job.Definition.PrefabPath, prefab);
                    var content = job.CreatedInstance.transform.Find("Content");
                    var extra = new GameObject("用户新增子节点", typeof(RectTransform));
                    extra.transform.SetParent(content, false);
                    RequireClearRejected();
                    Object.DestroyImmediate(extra);
                    var canvas = job.CreatedInstance.GetComponent<Canvas>();
                    bool original = canvas.overrideSorting;
                    canvas.overrideSorting = !original;
                    RequireClearRejected();
                    canvas.overrideSorting = original;
                    job.PreviewCompletedClear();
                    return true;
                }
                if (index == 11)
                {
                    var extra = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(job.Definition.PrefabPath), job.CreatedInstance.scene);
                    RequireClearRejected();
                    Object.DestroyImmediate(extra.GetComponent<ViewBase>());
                    Require(extra.GetComponent<ViewBase>() == null, "测试未能移除副本的 View 组件。");
                    RequireClearRejected();
                    Object.DestroyImmediate(extra);
                    job.PreviewCompletedClear();
                    Undo.PerformUndo();
                    Require(job.CreatedInstance == null, "测试未能撤销原场景实例。");
                    RequireClearRejected();
                    return true;
                }
                if (index == 12)
                {
                    var earlier = job.Definition;
                    SessionState.SetString(Key + "Earlier", JsonUtility.ToJson(earlier));
                    var context = new GameObject("随后失败的任务父节点", typeof(RectTransform));
                    context.transform.SetParent(job.CreatedInstance.transform.parent, false);
                    var next = AutoBindViewTemplateDefinition.Create("LaterFailed", Path.GetDirectoryName(earlier.PrefabPath), Path.GetDirectoryName(earlier.ScriptDirectory), earlier.Namespace);
                    SessionState.SetInt(Key + "CleanupStage", 1);
                    job.Begin(next, context);
                    Object.DestroyImmediate(context);
                    return false;
                }
                if (index == 13)
                {
                    SessionState.SetString(Key + "DeleteBlocked", job.Definition.ViewPath);
                    bool failed = false;
                    try { job.ClearCompleted(); }
                    catch (IOException) { failed = true; }
                    finally { SessionState.SetString(Key + "DeleteBlocked", ""); }
                    Require(failed, "删除故障没有传播给调用窗口。");
                    Require(job.CreatedInstance != null && job.CanClearCompleted, "删除故障丢失场景实例或成功回执。");
                    foreach (string path in job.Definition.OutputPaths)
                        Require(File.Exists(path) && File.Exists(path + ".meta"), "部分删除故障没有完整恢复资产：" + path);
                }
                if (index == 14)
                {
                    string external = Path.GetDirectoryName(job.Definition.ScriptDirectory).Replace('\\', '/') + "/ExternalDependency.cs";
                    File.WriteAllText(external, "public static class ExternalDependency { public static " + job.Definition.FullViewName + " View; }");
                    AssetDatabase.ImportAsset(external);
                    UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                    SessionState.SetString(Key + "External", external);
                }
                SessionState.SetInt(Key + "CleanupStage", 6);
                return false;
            }
            if (index == 12 && stage == 1)
            {
                Require(job.Phase == AutoBindTemplatePhase.Failed, "后续失败任务没有停止。");
                job.Cancel();
                Require(JsonUtility.ToJson(job.LastCreatedDefinition) == SessionState.GetString(Key + "Earlier", ""), "后续失败/取消覆盖了此前成功回执。");
                SessionState.SetInt(Key + "CleanupStage", 6);
                return false;
            }
            if (stage == 6)
            {
                if (index == 14 && !AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                    assembly.GetType("ExternalDependency")?.GetField("View")?.FieldType.FullName == job.Definition.FullViewName)) return false;
                StartClear();
                return false;
            }
            if (index == 14 && stage == 2)
            {
                Require(job.Phase == AutoBindTemplatePhase.Failed && !job.CanCancel && !job.CanClearCompleted, "清除编译失败未保持互斥状态。");
                Require(job.Message.Contains("ExternalDependency") && Directory.Exists(job.ClearBackupPath), "清除编译失败没有显示引用错误和备份。");
                Require(job.LastCreatedDefinition != null, "清除编译失败丢失回执。");
                Require(AssetDatabase.DeleteAsset(SessionState.GetString(Key + "External", "")), "无法修复测试外部引用。");
                SessionState.SetInt(Key + "CleanupStage", 8);
                return false;
            }
            if (index == 14 && stage == 8)
            {
                SessionState.SetInt(Key + "CleanupStage", 3);
                job.Retry();
                return false;
            }
            if (stage == 2 || (index == 14 && stage == 3))
            {
                Require(job.Phase == AutoBindTemplatePhase.None, "清除任务未完成：" + job.Message);
                var cleared = JsonUtility.FromJson<AutoBindViewTemplateDefinition>(SessionState.GetString(Key + "Cleared", ""));
                foreach (string path in cleared.OutputPaths)
                {
                    Require(!File.Exists(path) && !File.Exists(path + ".meta"), "清除遗漏资产：" + path);
                    Require(File.Exists(Path.Combine(job.ClearBackupPath, path)), "清除备份缺失：" + path);
                }
                Require(job.CreatedInstance == null && !job.CanClearCompleted, "清除后仍残留实例或可重复清除。");
                Scene scene = SceneManager.GetActiveScene();
                Require(string.IsNullOrEmpty(scene.path) && scene.isDirty, "清除保存了未命名场景。");
                Require(scene.GetRootGameObjects().Any(root => root.name == "ExistingCanvas") && scene.GetRootGameObjects().Any(root => root.name == "既有未保存编辑"), "清除删除了其他场景对象。");
                Require(scene.GetRootGameObjects().Sum(root => root.GetComponentsInChildren<EventSystem>(true).Length) == 1, "清除删除了 EventSystem。");
                if (index != 9) return true;
                Require(File.ReadAllText(cleared.ArgsPath).Contains("外部未使用"), "无 Args 模板清除误删了外部 Args 文件。");
                Require(AssetDatabase.DeleteAsset(cleared.ArgsPath), "无法撤除测试所有的外部 Args 文件。");
                var window = ScriptableObject.CreateInstance<AutoBindViewTemplateWindow>();
                try
                {
                    window.CreateGUI();
                    Require(window.rootVisualElement.Q<TextField>("baseName").value == cleared.BaseName, "重新打开窗口丢失原基础名。");
                    Require(!window.rootVisualElement.Q<Toggle>("useArgs").value, "重新打开窗口丢失 Args 选项。");
                    Require(window.rootVisualElement.Q<TextField>("output0").value == cleared.ViewPath, "重新打开窗口丢失原输出路径。");
                }
                finally { Object.DestroyImmediate(window); }
                SessionState.SetInt(Key + "CleanupStage", 7);
                return false;
            }
            if (index == 9 && stage == 7)
            {
                var cleared = JsonUtility.FromJson<AutoBindViewTemplateDefinition>(SessionState.GetString(Key + "Cleared", ""));
                var recreate = AutoBindViewTemplateDefinition.Create(cleared.BaseName, Path.GetDirectoryName(cleared.PrefabPath), Path.GetDirectoryName(cleared.ScriptDirectory), cleared.Namespace, true);
                Require(!cleared.UseArgs && recreate.UseArgs, "重建没有切换 Args 模式。");
                recreate.ValidateNew();
                SessionState.SetInt(Key + "CleanupStage", 4);
                job.Begin(recreate, job.RecreationParent);
                return false;
            }
            Require(index == 9 && stage == 4, "未知清除验证阶段。");
            VerifySuccess(index);
            return true;
        }

        private static void StartClear()
        {
            var job = AutoBindViewTemplateCreation.instance;
            var original = job.LastCreatedDefinition;
            Require(job.PreviewCompletedClear().Count == original.OutputPaths.Count() + 1, "清除清单没有按实际 Args 模式列出产物。");
            SessionState.SetString(Key + "Cleared", JsonUtility.ToJson(original));
            SessionState.SetInt(Key + "CleanupStage", 2);
            job.ClearCompleted();
            Require(job.IsBusy && !job.CanCancel && !job.CanClearCompleted, "清除编译期间未锁定重复操作。");
        }

        private static void RequireClearRejected()
        {
            var job = AutoBindViewTemplateCreation.instance;
            bool blocked = false;
            try { job.ClearCompleted(); }
            catch (Exception error) when (error is IOException || error is InvalidOperationException) { blocked = true; }
            Require(blocked, "修改后的产物仍可自动清除。");
            foreach (string path in job.LastCreatedDefinition.OutputPaths) Require(File.Exists(path), "拒绝清除时已删除产物：" + path);
        }

        private static void VerifyRoute(AutoBindViewTemplateDefinition definition, GameObject prefab, Transform parent)
        {
            var assembly = prefab.GetComponent<ViewBase>().GetType().Assembly;
            string prefix = definition.Namespace + ".";
            var route = (ViewRoute)assembly.GetType(prefix + definition.RoutesName).GetField("Main").GetValue(null);
            var args = definition.UseArgs ? (IViewArgs)Activator.CreateInstance(assembly.GetType(prefix + definition.ArgsName)) : EmptyViewArgs.Instance;
            Require(route.ArgsType == args.GetType(), "路由参数类型与 Args 选项不一致。");
            if (!definition.UseArgs) Require(assembly.GetType(prefix + definition.ArgsName) == null, "未勾选 Args 时仍生成了专属参数类型。");
            using var scope = new ContainerBuilder().Build();
            var helper = new Resources(prefab, parent);
            using var manager = new ViewManager(scope, helper, helper);
            Require(((IViewRouteManager)manager).ShowView(route, args) > 0, "生成的路由、参数与 Presenter 不能正常开窗。");
            manager.CloseAllViews(true);
        }

        private static void VerifyFailure(int index)
        {
            var job = AutoBindViewTemplateCreation.instance;
            Require(job.Phase == AutoBindTemplatePhase.Failed, "故障场景没有停止任务。");
            Require(!File.Exists(job.Definition.PrefabPath), "失败后仍保存了 Prefab。");
            var definition = job.Definition;
            job.Cancel();
            if (index == 5 || index == 7)
            {
                Require(File.ReadAllText(index == 5 ? definition.ViewPath : definition.ViewPath + ".meta").Contains("用户修改"), "清理覆盖了用户修改。");
                Require(File.Exists(definition.ArgsPath), "被修改的关联脚本组未被保护。");
            }
            else
            {
                Require(!File.Exists(definition.ViewPath) && !File.Exists(definition.ArgsPath), "未清理任务创建的骨架。");
                if (index == 4) Require(File.ReadAllText(definition.BindingPath).Contains("外部文件"), "清理删除了外部文件。");
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Resources : IViewPrefabHelper, IViewRootHelper
        {
            private readonly GameObject _prefab;
            private readonly Transform _parent;
            internal Resources(GameObject prefab, Transform parent) { _prefab = prefab; _parent = parent; }
            public GameObject GetViewPrefab(string name) => _prefab;
            public Transform GetViewRoot(string name, UILayer layer = UILayer.Normal) => _parent;
            public void ReleaseViewInstance(string name, GameObject instance) => Object.DestroyImmediate(instance);
        }
    }

    internal sealed class AutoBindTemplateDeletionFault : AssetModificationProcessor
    {
        private static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions options)
        {
            return Application.isBatchMode && SessionState.GetString("AutoBind.TemplateVerification.DeleteBlocked", "") == path
                ? AssetDeleteResult.FailedDelete : AssetDeleteResult.DidNotDelete;
        }
    }
}
