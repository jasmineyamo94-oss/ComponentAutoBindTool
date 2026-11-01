using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ComponentAutoBindTool.Core;
using ComponentAutoBindTool.ViewCore;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Editor
{
    internal enum AutoBindTemplatePhase { None, HostCompilation, FinalCompilation, Completed, Failed, Clearing }

    // 不写入磁盘：只在当前 Editor 会话的脚本重载间保留任务和场景对象引用。
    internal sealed class AutoBindViewTemplateCreation : ScriptableSingleton<AutoBindViewTemplateCreation>
    {
        [Serializable]
        private sealed class OwnedFile
        {
            public string Path;
            public string Hash;
            public string MetaHash;
        }

        [Serializable]
        private sealed class CreationReceipt
        {
            public AutoBindViewTemplateDefinition Definition;
            public List<OwnedFile> Files;
            public GameObject Instance;
            public GameObject Parent;
            public int SceneHandle;
            public string SceneSnapshot;
        }

        [SerializeField] private AutoBindViewTemplateDefinition m_Definition;
        [SerializeField] private AutoBindTemplatePhase m_Phase;
        [SerializeField] private AutoBindTemplatePhase m_ResumePhase;
        [SerializeField] private string m_Message;
        [SerializeField] private GameObject m_Parent;
        [SerializeField] private bool m_HadParent;
        [SerializeField] private int m_SceneHandle;
        [SerializeField] private GameObject m_Instance;
        [SerializeField] private List<OwnedFile> m_Files = new();
        [SerializeField] private List<string> m_Directories = new();
        [SerializeField] private List<string> m_CompileErrors = new();
        [SerializeField] private bool m_CompilationFinished;
        [SerializeField] private bool m_Reloaded;
        // 可空会话数据必须按引用序列化，避免重载后 null 被 Unity 内联序列化还原为空配方/回执。
        [SerializeReference] private CreationReceipt m_LastSuccess;
        [SerializeField] private string m_ClearBackupPath;
        [SerializeReference] private AutoBindViewTemplateDefinition m_RecreationRecipe;
        [SerializeField] private GameObject m_RecreationParent;
        private bool m_Executing;

        internal AutoBindTemplatePhase Phase => m_Phase;
        internal bool IsBusy => m_Phase == AutoBindTemplatePhase.HostCompilation || m_Phase == AutoBindTemplatePhase.FinalCompilation || m_Phase == AutoBindTemplatePhase.Clearing;
        internal bool HasPending => IsBusy || m_Phase == AutoBindTemplatePhase.Failed;
        internal string Message => m_Message ?? "输入不含 View 后缀的基础名，例如 Inventory。";
        internal AutoBindViewTemplateDefinition Definition => m_Definition;
        internal GameObject CreatedInstance => m_Instance;
        internal bool CanClearCompleted => !HasPending && m_LastSuccess?.Definition?.IsInitialized == true && m_LastSuccess.Files != null && m_LastSuccess.Files.Count > 0;
        internal bool CanCancel => HasPending && m_ResumePhase != AutoBindTemplatePhase.Clearing;
        internal AutoBindViewTemplateDefinition LastCreatedDefinition => m_LastSuccess?.Definition;
        internal GameObject LastCreatedParent => m_LastSuccess?.Parent;
        internal string ClearBackupPath => m_ClearBackupPath;
        internal AutoBindViewTemplateDefinition RecreationRecipe => m_RecreationRecipe?.IsInitialized == true ? m_RecreationRecipe : null;
        internal GameObject RecreationParent => m_RecreationParent;
        internal void UseSavedPaths() { m_RecreationRecipe = null; m_RecreationParent = null; }

        internal static string BlockReason(bool allowCompilationErrors = false)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "请退出 Play Mode 后创建。";
            if (PrefabStageUtility.GetCurrentPrefabStage() != null) return "请返回普通场景后创建 UI 模板。";
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return "Unity 正在编译或导入，请稍候。";
            if (AutoBindViewTemplateDeletion.instance.IsBusy) return "按位置删除任务正在处理，请等待完成。";
            if (!allowCompilationErrors && EditorUtility.scriptCompilationFailed) return "项目存在编译错误，请修复后再创建。";
            return null;
        }

        internal void Begin(AutoBindViewTemplateDefinition definition, GameObject context)
        {
            if (HasPending) throw new InvalidOperationException("已有创建任务，请先完成或清理该任务。");
            string blocked = BlockReason();
            if (blocked != null) throw new InvalidOperationException(blocked);
            ReadConfiguration();
            definition.ValidateNew();
            Scene scene = context != null && context.scene.IsValid() ? context.scene : SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                throw new InvalidOperationException("当前没有可用的普通场景。");
            UseSavedPaths();
            m_Definition = definition;
            m_Parent = ResolveParent(context, scene);
            m_HadParent = m_Parent != null;
            m_SceneHandle = scene.handle;
            m_Instance = null;
            m_Files.Clear();
            m_Directories.Clear();
            Run(() =>
            {
                PrepareCompilation(AutoBindTemplatePhase.HostCompilation, "第 1/2 轮：等待宿主骨架编译……");
                WriteBatch(() =>
                {
                    Write(definition.ViewPath, definition.ViewSource(false));
                    if (definition.UseArgs) Write(definition.ArgsPath, definition.ArgsSource());
                    Write(definition.RoutesPath, definition.RoutesSource());
                });
                CompilationPipeline.RequestScriptCompilation();
            });
        }

        private static GameObject ResolveParent(GameObject context, Scene scene)
        {
            if (context != null && context.scene == scene && !EditorUtility.IsPersistent(context) && context.GetComponentInParent<Canvas>(true) != null) return context;
            return scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Canvas>(true))
                .FirstOrDefault(canvas => canvas.isRootCanvas)?.gameObject;
        }

        internal static AutoBindGlobalSetting ReadConfiguration()
        {
            string[] settings = AssetDatabase.FindAssets("t:AutoBindGlobalSetting");
            string[] mappings = AssetDatabase.FindAssets("t:AutoBindKeyMapSetting");
            if (settings.Length != 1 || mappings.Length != 1)
                throw new InvalidOperationException("项目需要唯一的 AutoBind 全局配置和组件映射配置，请先通过自动绑定UI组件菜单配置。");
            return AssetDatabase.LoadAssetAtPath<AutoBindGlobalSetting>(AssetDatabase.GUIDToAssetPath(settings[0]));
        }

        internal void CompilationStarted()
        {
            if (!IsBusy) return;
            m_CompileErrors.Clear();
            m_CompilationFinished = false;
        }

        internal void AssemblyFinished(CompilerMessage[] messages)
        {
            if (!IsBusy) return;
            foreach (var message in messages)
                if (message.type == CompilerMessageType.Error)
                    m_CompileErrors.Add(message.file + ":" + message.line + " " + message.message);
        }

        internal void CompilationFinished()
        {
            if (!IsBusy) return;
            m_CompilationFinished = true;
            // 终止状态只更新会话数据，不依赖资产导入结束，也不会在编译回调里创建对象或写文件。
            if (m_CompileErrors.Count > 0) FailCompilation();
        }

        private void FailCompilation()
        {
            Fail((m_ResumePhase == AutoBindTemplatePhase.Clearing ? "文件已清除，但项目仍有编译错误。请修复相关引用后点击重试。备份：" + m_ClearBackupPath : "编译失败，修复错误后点击重试：") + "\n" + string.Join("\n", m_CompileErrors.Take(8)));
        }
        internal void Reloaded() { if (IsBusy) m_Reloaded = true; }

        internal void Tick()
        {
            if (!IsBusy || m_Executing || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (m_CompilationFinished && m_CompileErrors.Count > 0)
            {
                FailCompilation();
                return;
            }
            if (!m_Reloaded) return;
            Run(() =>
            {
                if (m_Phase == AutoBindTemplatePhase.Clearing)
                {
                    string remainingType = RemainingGeneratedType();
                    if (remainingType != null) throw new InvalidOperationException("清除后的类型仍被其他脚本定义，请检查后重试：" + remainingType);
                    m_LastSuccess = null;
                    m_Phase = AutoBindTemplatePhase.None;
                    m_ResumePhase = AutoBindTemplatePhase.None;
                    m_Message = "清除完成。已保留名称、路径和 Args 选项，可调整后重新创建；场景尚未保存。";
                    return;
                }
                ValidateContext();
                if (m_Phase == AutoBindTemplatePhase.HostCompilation) GenerateFinalScripts();
                else CompletePrefab();
            });
        }

        private void PrepareCompilation(AutoBindTemplatePhase phase, string message)
        {
            m_Phase = phase;
            m_ResumePhase = phase;
            m_Message = message;
            m_Reloaded = false;
            m_CompilationFinished = false;
            m_CompileErrors.Clear();
        }

        private string RemainingGeneratedType()
        {
            var names = new List<string> { m_Definition.ViewName, m_Definition.PresenterName, m_Definition.RoutesName };
            if (m_Definition.UseArgs) names.Add(m_Definition.ArgsName);
            foreach (string name in names)
            {
                string fullName = string.IsNullOrEmpty(m_Definition.Namespace) ? name : m_Definition.Namespace + "." + name;
                if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetType(fullName) != null)) return fullName;
            }
            return null;
        }

        internal void Retry()
        {
            if (m_Phase != AutoBindTemplatePhase.Failed) return;
            string blocked = BlockReason(true);
            if (blocked != null) throw new InvalidOperationException(blocked);
            Run(() =>
            {
                if (m_ResumePhase == AutoBindTemplatePhase.Clearing)
                {
                    PrepareCompilation(AutoBindTemplatePhase.Clearing, "等待清除后的脚本编译……");
                    // 修复外部引用时，Unity 可能已成功重载；避免等待一次不会发生的空编译重载。
                    if (!EditorUtility.scriptCompilationFailed && RemainingGeneratedType() == null)
                    {
                        m_Reloaded = true;
                        return;
                    }
                    CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
                    return;
                }
                ValidateContext();
                AssertUnchanged();
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(m_Definition.ViewPath);
                Type loaded = script != null ? script.GetClass() : null;
                bool ready = loaded != null && (m_ResumePhase == AutoBindTemplatePhase.HostCompilation ||
                    (loaded.GetNestedType("UIView") != null && loaded.Assembly.GetType(
                        string.IsNullOrEmpty(m_Definition.Namespace) ? m_Definition.PresenterName :
                        m_Definition.Namespace + "." + m_Definition.PresenterName) != null));
                if (!EditorUtility.scriptCompilationFailed && ready)
                {
                    PrepareCompilation(m_ResumePhase, "重试：继续已编译的创建阶段……");
                    m_Reloaded = true;
                    return;
                }
                if (m_ResumePhase == AutoBindTemplatePhase.HostCompilation)
                    WriteBatch(() =>
                    {
                        Write(m_Definition.ViewPath, m_Definition.ViewSource(false));
                        if (m_Definition.UseArgs) Write(m_Definition.ArgsPath, m_Definition.ArgsSource());
                        Write(m_Definition.RoutesPath, m_Definition.RoutesSource());
                    });
                PrepareCompilation(m_ResumePhase, "重试：等待脚本编译……");
                CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
            });
        }

        private ViewBase BuildRoot(Scene preview)
        {
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(m_Definition.ViewPath);
            Type type = script != null ? script.GetClass() : null;
            if (type == null || !typeof(ViewBase).IsAssignableFrom(type) || type.FullName != m_Definition.FullViewName)
                throw new InvalidOperationException("无法加载新宿主类型，请确认脚本编译成功：" + m_Definition.FullViewName);
            var root = new GameObject(m_Definition.ViewName, typeof(RectTransform));
            SceneManager.MoveGameObjectToScene(root, preview);
            var host = (ViewBase)root.AddComponent(type);
            root.GetComponent<Core.ComponentAutoBindTool>().m_targetScript = host;
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(root.transform, false);
            foreach (var rect in new[] { (RectTransform)root.transform, (RectTransform)content.transform })
            {
                rect.gameObject.layer = LayerMask.NameToLayer("UI");
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
            return host;
        }

        private void GenerateBinding(ViewBase host)
        {
            EnsureAvailable(m_Definition.BindingPath);
            var setting = Object.Instantiate(ReadConfiguration());
            try
            {
                var data = new SerializedObject(setting);
                data.FindProperty("m_UseGlobalDefaultSavePath").boolValue = true;
                data.ApplyModifiedPropertiesWithoutUndo();
                var map = AutoBindEditorAssetLocator.LoadSingletonAsset<AutoBindKeyMapSetting>();
                var report = AutoBindBuildPipeline.GenerateCode(host.GetComponent<Core.ComponentAutoBindTool>(), setting, map);
                if (!report.Succeeded) throw new InvalidOperationException(report.ValidationResult.BuildReport());
                Remember(m_Definition.BindingPath);
            }
            finally { Object.DestroyImmediate(setting); }
        }

        private void GenerateFinalScripts()
        {
            AssertUnchanged();
            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                ViewBase host = BuildRoot(preview);
                WriteBatch(() =>
                {
                    GenerateBinding(host);
                    Write(m_Definition.ViewPath, m_Definition.ViewSource(true));
                    Write(m_Definition.PresenterPath, m_Definition.PresenterSource());
                });
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
            PrepareCompilation(AutoBindTemplatePhase.FinalCompilation, "第 2/2 轮：等待绑定代码与 Presenter 编译……");
            CompilationPipeline.RequestScriptCompilation();
        }

        private void CompletePrefab()
        {
            AssertUnchanged();
            EnsureAvailable(m_Definition.PrefabPath);
            Scene preview = EditorSceneManager.NewPreviewScene();
            GameObject prefab;
            try
            {
                ViewBase host = BuildRoot(preview);
                GenerateBinding(host);
                ((IAutoBindHost)host).EnsureAutoBind(host.gameObject);
                if (!((IAutoBindHost)host).IsAutoBound) throw new InvalidOperationException("模板宿主绑定验证失败。");
                EnsureDirectory(Path.GetDirectoryName(m_Definition.PrefabPath));
                try
                {
                    prefab = PrefabUtility.SaveAsPrefabAsset(host.gameObject, m_Definition.PrefabPath, out bool saved);
                    if (!saved || prefab == null) throw new IOException("模板 Prefab 保存失败。");
                }
                finally { if (File.Exists(m_Definition.PrefabPath)) Remember(m_Definition.PrefabPath); }
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
            CreateSceneInstance(prefab);
            CaptureMetas();
            m_LastSuccess = new CreationReceipt
            {
                Definition = JsonUtility.FromJson<AutoBindViewTemplateDefinition>(JsonUtility.ToJson(m_Definition)),
                Files = m_Files.Select(file => new OwnedFile { Path = file.Path, Hash = file.Hash, MetaHash = file.MetaHash }).ToList(),
                Instance = m_Instance, Parent = m_Instance.transform.parent.gameObject,
                SceneHandle = m_Instance.scene.handle, SceneSnapshot = CaptureInstance(m_Instance)
            };
            m_Phase = AutoBindTemplatePhase.Completed;
            m_Message = "创建完成：" + (m_Definition.UseArgs ? "五" : "四") + "份脚本与 Prefab 已生成。场景尚未保存，可按需保存。";
            Selection.activeGameObject = m_Instance;
            EditorGUIUtility.PingObject(m_Instance);
        }

        private void CreateSceneInstance(GameObject prefab)
        {
            Scene scene = ValidateContext();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("创建 UI View Template");
            try
            {
                Transform parent = m_Parent != null ? m_Parent.transform : null;
                if (parent == null)
                {
                    var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
                    SceneManager.MoveGameObjectToScene(canvas, scene);
                    Undo.RegisterCreatedObjectUndo(canvas, "创建 UI Canvas");
                    canvas.layer = LayerMask.NameToLayer("UI");
                    canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                    var scaler = canvas.GetComponent<UnityEngine.UI.CanvasScaler>();
                    scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    scaler.referenceResolution = new Vector2(1280, 720);
                    scaler.matchWidthOrHeight = 0.5f;
                    parent = canvas.transform;
                }
                if (!scene.GetRootGameObjects().Any(root => root.GetComponentInChildren<EventSystem>(true) != null))
                {
                    var events = new GameObject("EventSystem", typeof(EventSystem));
                    SceneManager.MoveGameObjectToScene(events, scene);
                    Undo.RegisterCreatedObjectUndo(events, "创建 UI EventSystem");
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
                    Type module = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
                    if (module == null) throw new InvalidOperationException("无法加载 Input System 的 UI 输入模块。");
                    events.AddComponent(module);
#else
                    events.AddComponent<StandaloneInputModule>();
#endif
                }
                m_Instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                Undo.RegisterCreatedObjectUndo(m_Instance, "创建 UI 界面实例");
                Undo.SetTransformParent(m_Instance.transform, parent, "创建 UI 界面实例");
                var rect = (RectTransform)m_Instance.transform;
                rect.localScale = Vector3.one;
                rect.localRotation = Quaternion.identity;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                int layer = parent.GetComponentInParent<Canvas>(true).gameObject.layer;
                foreach (Transform child in m_Instance.GetComponentsInChildren<Transform>(true))
                {
                    child.gameObject.layer = layer;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(child.gameObject);
                }
                PrefabUtility.RecordPrefabInstancePropertyModifications(rect);
                Undo.RegisterFullObjectHierarchyUndo(m_Instance, "创建 UI 界面实例");
                Undo.CollapseUndoOperations(group);
                EditorSceneManager.MarkSceneDirty(scene);
            }
            catch { Undo.RevertAllDownToGroup(group); m_Instance = null; throw; }
        }

        private Scene ValidateContext()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || PrefabStageUtility.GetCurrentPrefabStage() != null)
                throw new InvalidOperationException("创建上下文已改变，请返回原普通场景后重试。");
            Scene scene = default;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene candidate = SceneManager.GetSceneAt(i);
                if (candidate.handle == m_SceneHandle) { scene = candidate; break; }
            }
            if (!scene.IsValid() || !scene.isLoaded || (m_HadParent && (m_Parent == null || m_Parent.scene != scene)))
                throw new InvalidOperationException("原场景或父节点已失效，任务已停止，不会改到其他场景创建。");
            return scene;
        }

        private void Write(string path, string source)
        {
            EnsureAvailable(path);
            EnsureDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, source, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                Remember(path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            AssetDatabase.ImportAsset(path);
        }

        private void WriteBatch(Action action)
        {
            AssetDatabase.DisallowAutoRefresh();
            AssetDatabase.StartAssetEditing();
            try { action(); }
            finally
            {
                try { AssetDatabase.StopAssetEditing(); CaptureMetas(); }
                finally { AssetDatabase.AllowAutoRefresh(); }
            }
        }

        private void EnsureDirectory(string path)
        {
            path = path.Replace('\\', '/');
            if (Directory.Exists(path)) return;
            EnsureDirectory(Path.GetDirectoryName(path));
            Directory.CreateDirectory(path);
            m_Directories.Add(path);
        }

        private void EnsureAvailable(string path)
        {
            OwnedFile owned = m_Files.Find(item => item.Path == path);
            if (owned != null) { CheckFile(owned); return; }
            if (File.Exists(path) || Directory.Exists(path) || File.Exists(path + ".meta"))
                throw new IOException("目标在创建期间被占用，不会覆盖：" + path);
        }

        private void Remember(string path)
        {
            var owned = m_Files.Find(item => item.Path == path);
            if (owned == null) { owned = new OwnedFile { Path = path }; m_Files.Add(owned); }
            owned.Hash = Hash(path);
            if (owned.MetaHash == null && File.Exists(path + ".meta")) owned.MetaHash = Hash(path + ".meta");
        }

        private static string Hash(string path)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
        }

        private void CaptureMetas()
        {
            foreach (var file in m_Files)
                if (file.MetaHash == null && File.Exists(file.Path + ".meta")) file.MetaHash = Hash(file.Path + ".meta");
        }

        private static void CheckFile(OwnedFile file)
        {
            if (!File.Exists(file.Path) || Hash(file.Path) != file.Hash ||
                (file.MetaHash == null && File.Exists(file.Path + ".meta")) ||
                (file.MetaHash != null && (!File.Exists(file.Path + ".meta") || Hash(file.Path + ".meta") != file.MetaHash)))
                throw new IOException("产物已被修改或移走，不会覆盖或删除：" + file.Path);
        }

        private void AssertUnchanged() { foreach (var file in m_Files) CheckFile(file); }

        private static string CaptureInstance(GameObject instance)
        {
            var snapshot = new StringBuilder();
            foreach (Transform child in instance.GetComponentsInChildren<Transform>(true))
            {
                snapshot.AppendLine(child.GetInstanceID() + ":" + (child.parent != null ? child.parent.GetInstanceID() : 0) + ":" + child.GetSiblingIndex());
                snapshot.AppendLine(EditorJsonUtility.ToJson(child.gameObject));
                foreach (Component component in child.GetComponents<Component>())
                    snapshot.AppendLine(component == null ? "<missing-script>" : EditorJsonUtility.ToJson(component));
            }
            snapshot.AppendLine(AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(instance)));
            foreach (var modification in PrefabUtility.GetPropertyModifications(instance) ?? Array.Empty<PropertyModification>())
                snapshot.AppendLine((modification.target != null ? modification.target.GetInstanceID() : 0) + ":" + modification.propertyPath + ":" + modification.value + ":" + (modification.objectReference != null ? modification.objectReference.GetInstanceID() : 0));
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(snapshot.ToString())));
        }

        internal IReadOnlyList<string> PreviewCompletedClear()
        {
            if (!CanClearCompleted) throw new InvalidOperationException("当前没有可清除的成功创建记录，或仍有任务未完成。");
            string blocked = BlockReason(true);
            if (blocked != null) throw new InvalidOperationException(blocked);
            var receipt = m_LastSuccess;
            foreach (var file in receipt.Files) CheckFile(file);
            if (receipt.Instance == null || !receipt.Instance.scene.isLoaded || receipt.Instance.scene.handle != receipt.SceneHandle ||
                CaptureInstance(receipt.Instance) != receipt.SceneSnapshot)
                throw new InvalidOperationException("场景实例已修改、删除或移走，已停止自动清除，请手动检查。");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(receipt.Definition.PrefabPath);
            if (prefab == null || prefab.GetComponentsInChildren<Transform>(true).Any(child =>
                EditorUtility.IsDirty(child.gameObject) || child.GetComponents<Component>().Any(component => component != null && EditorUtility.IsDirty(component))))
                throw new InvalidOperationException("Prefab 存在未保存修改，已停止自动清除。");
            foreach (var host in Object.FindObjectsByType<ViewBase>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (host.gameObject != receipt.Instance && !EditorSceneManager.IsPreviewScene(host.gameObject.scene) && host.GetType().FullName == receipt.Definition.FullViewName)
                    throw new InvalidOperationException("该界面还有其他场景实例，已停止清除：" + host.name);
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.isLoaded || EditorSceneManager.IsPreviewScene(scene)) continue;
                foreach (var child in scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Transform>(true)))
                {
                    if (child.IsChildOf(receipt.Instance.transform)) continue;
                    var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(child.gameObject);
                    if (source != null && AssetDatabase.GetAssetPath(source) == receipt.Definition.PrefabPath)
                        throw new InvalidOperationException("该 Prefab 还有其他场景实例，已停止清除：" + child.name);
                }
            }
            var paths = receipt.Files.Select(file => file.Path).ToList();
            paths.Add("场景实例：" + receipt.Instance.name + "（保留 Canvas / EventSystem）");
            return paths;
        }

        internal void ClearCompleted()
        {
            PreviewCompletedClear();
            var receipt = m_LastSuccess;
            var snapshot = AutoBindTemplateAssetDeletion.Capture(receipt.Files.Select(file => file.Path));
            foreach (var file in receipt.Files) CheckFile(file);
            WriteBatch(() =>
            {
                AutoBindTemplateAssetDeletion.Delete(snapshot, out m_ClearBackupPath);
                var scene = receipt.Instance.scene;
                foreach (Transform child in receipt.Instance.GetComponentsInChildren<Transform>(true))
                {
                    foreach (Component component in child.GetComponents<Component>()) if (component != null) Undo.ClearUndo(component);
                    Undo.ClearUndo(child.gameObject);
                }
                Object.DestroyImmediate(receipt.Instance);
                EditorSceneManager.MarkSceneDirty(scene);
                m_Definition = receipt.Definition;
                m_RecreationRecipe = receipt.Definition;
                m_RecreationParent = receipt.Parent;
                m_Instance = null;
                m_Files.Clear();
                PrepareCompilation(AutoBindTemplatePhase.Clearing, "产物已清除，等待脚本重载后可重新创建……");
            });
            CompilationPipeline.RequestScriptCompilation();
        }

        internal void InvalidateDeletedOutputs(IEnumerable<string> deletedPaths)
        {
            var paths = new HashSet<string>(deletedPaths, StringComparer.OrdinalIgnoreCase);
            if (m_LastSuccess?.Files != null && m_LastSuccess.Files.Any(file => paths.Contains(file.Path))) m_LastSuccess = null;
            if (m_Phase == AutoBindTemplatePhase.Completed && m_Definition?.IsInitialized == true && m_Definition.OutputPaths.Any(paths.Contains))
            {
                m_Phase = AutoBindTemplatePhase.None;
                m_Instance = null;
                m_Message = "对应磁盘产物已删除。场景实例未清除，请按需手动处理。";
            }
        }

        internal void Cancel()
        {
            if (!CanCancel) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || PrefabStageUtility.GetCurrentPrefabStage() != null)
                throw new InvalidOperationException("请回到普通编辑场景，并等待编译或导入结束后清理。");
            try { AssertUnchanged(); }
            catch (IOException error)
            {
                m_Phase = AutoBindTemplatePhase.None;
                m_Message = "任务已取消。为保护已修改的产物，本次文件全部保留：\n" + error.Message;
                return;
            }
            Run(() =>
            {
                WriteBatch(() =>
                {
                    foreach (var file in m_Files.AsEnumerable().Reverse())
                    {
                        if (!AssetDatabase.DeleteAsset(file.Path))
                        {
                            File.Delete(file.Path);
                            if (File.Exists(file.Path + ".meta")) File.Delete(file.Path + ".meta");
                        }
                    }
                    foreach (string directory in m_Directories.AsEnumerable().Reverse())
                        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                        {
                            if (!AssetDatabase.DeleteAsset(directory)) Directory.Delete(directory);
                        }
                });
                m_Files.Clear();
                m_Phase = AutoBindTemplatePhase.None;
                m_Message = "已取消并清理本次未修改的产物。";
            });
        }

        private void Run(Action action)
        {
            m_Executing = true;
            try { action(); }
            catch (Exception error) { Fail(error.Message); }
            finally { m_Executing = false; }
        }

        private void Fail(string message) { m_Phase = AutoBindTemplatePhase.Failed; m_Message = message; }
    }

    [InitializeOnLoad]
    internal static class AutoBindViewTemplateRunner
    {
        static AutoBindViewTemplateRunner()
        {
            EditorApplication.update += () => AutoBindViewTemplateCreation.instance.Tick();
            var restored = AutoBindViewTemplateCreation.instance;
            if (restored.IsBusy) EditorApplication.delayCall += restored.Reloaded;
            CompilationPipeline.compilationStarted += _ => AutoBindViewTemplateCreation.instance.CompilationStarted();
            CompilationPipeline.assemblyCompilationFinished += (_, messages) => AutoBindViewTemplateCreation.instance.AssemblyFinished(messages);
            CompilationPipeline.compilationFinished += _ => AutoBindViewTemplateCreation.instance.CompilationFinished();
        }
    }
}
