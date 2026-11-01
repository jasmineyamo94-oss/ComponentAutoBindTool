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

        [SerializeField] private AutoBindViewTemplateDefinition _definition;
        [SerializeField] private AutoBindTemplatePhase _phase;
        [SerializeField] private AutoBindTemplatePhase _resumePhase;
        [SerializeField] private string _message;
        [SerializeField] private GameObject _parent;
        [SerializeField] private bool _hadParent;
        [SerializeField] private int _sceneHandle;
        [SerializeField] private GameObject _instance;
        [SerializeField] private List<OwnedFile> _files = new();
        [SerializeField] private List<string> _directories = new();
        [SerializeField] private List<string> _compileErrors = new();
        [SerializeField] private bool _compilationFinished;
        [SerializeField] private bool _reloaded;
        // 可空会话数据必须按引用序列化，避免重载后 null 被 Unity 内联序列化还原为空配方/回执。
        [SerializeReference] private CreationReceipt _lastSuccess;
        [SerializeField] private string _clearBackupPath;
        [SerializeReference] private AutoBindViewTemplateDefinition _recreationRecipe;
        [SerializeField] private GameObject _recreationParent;
        private bool _executing;

        internal AutoBindTemplatePhase Phase => _phase;
        internal bool IsBusy => _phase == AutoBindTemplatePhase.HostCompilation || _phase == AutoBindTemplatePhase.FinalCompilation || _phase == AutoBindTemplatePhase.Clearing;
        internal bool HasPending => IsBusy || _phase == AutoBindTemplatePhase.Failed;
        internal string Message => _message ?? "输入不含 View 后缀的基础名，例如 Inventory。";
        internal AutoBindViewTemplateDefinition Definition => _definition;
        internal GameObject CreatedInstance => _instance;
        internal bool CanClearCompleted => !HasPending && _lastSuccess?.Definition?.IsInitialized == true && _lastSuccess.Files != null && _lastSuccess.Files.Count > 0;
        internal bool CanCancel => HasPending && _resumePhase != AutoBindTemplatePhase.Clearing;
        internal AutoBindViewTemplateDefinition LastCreatedDefinition => _lastSuccess?.Definition;
        internal GameObject LastCreatedParent => _lastSuccess?.Parent;
        internal string ClearBackupPath => _clearBackupPath;
        internal AutoBindViewTemplateDefinition RecreationRecipe => _recreationRecipe?.IsInitialized == true ? _recreationRecipe : null;
        internal GameObject RecreationParent => _recreationParent;
        internal void UseSavedPaths() { _recreationRecipe = null; _recreationParent = null; }

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
            _definition = definition;
            _parent = ResolveParent(context, scene);
            _hadParent = _parent != null;
            _sceneHandle = scene.handle;
            _instance = null;
            _files.Clear();
            _directories.Clear();
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
            _compileErrors.Clear();
            _compilationFinished = false;
        }

        internal void AssemblyFinished(CompilerMessage[] messages)
        {
            if (!IsBusy) return;
            foreach (var message in messages)
                if (message.type == CompilerMessageType.Error)
                    _compileErrors.Add(message.file + ":" + message.line + " " + message.message);
        }

        internal void CompilationFinished()
        {
            if (!IsBusy) return;
            _compilationFinished = true;
            // 终止状态只更新会话数据，不依赖资产导入结束，也不会在编译回调里创建对象或写文件。
            if (_compileErrors.Count > 0) FailCompilation();
        }

        private void FailCompilation()
        {
            Fail((_resumePhase == AutoBindTemplatePhase.Clearing ? "文件已清除，但项目仍有编译错误。请修复相关引用后点击重试。备份：" + _clearBackupPath : "编译失败，修复错误后点击重试：") + "\n" + string.Join("\n", _compileErrors.Take(8)));
        }
        internal void Reloaded() { if (IsBusy) _reloaded = true; }

        internal void Tick()
        {
            if (!IsBusy || _executing || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (_compilationFinished && _compileErrors.Count > 0)
            {
                FailCompilation();
                return;
            }
            if (!_reloaded) return;
            Run(() =>
            {
                if (_phase == AutoBindTemplatePhase.Clearing)
                {
                    string remainingType = RemainingGeneratedType();
                    if (remainingType != null) throw new InvalidOperationException("清除后的类型仍被其他脚本定义，请检查后重试：" + remainingType);
                    _lastSuccess = null;
                    _phase = AutoBindTemplatePhase.None;
                    _resumePhase = AutoBindTemplatePhase.None;
                    _message = "清除完成。已保留名称、路径和 Args 选项，可调整后重新创建；场景尚未保存。";
                    return;
                }
                ValidateContext();
                if (_phase == AutoBindTemplatePhase.HostCompilation) GenerateFinalScripts();
                else CompletePrefab();
            });
        }

        private void PrepareCompilation(AutoBindTemplatePhase phase, string message)
        {
            _phase = phase;
            _resumePhase = phase;
            _message = message;
            _reloaded = false;
            _compilationFinished = false;
            _compileErrors.Clear();
        }

        private string RemainingGeneratedType()
        {
            var names = new List<string> { _definition.ViewName, _definition.PresenterName, _definition.RoutesName };
            if (_definition.UseArgs) names.Add(_definition.ArgsName);
            foreach (string name in names)
            {
                string fullName = string.IsNullOrEmpty(_definition.Namespace) ? name : _definition.Namespace + "." + name;
                if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetType(fullName) != null)) return fullName;
            }
            return null;
        }

        internal void Retry()
        {
            if (_phase != AutoBindTemplatePhase.Failed) return;
            string blocked = BlockReason(true);
            if (blocked != null) throw new InvalidOperationException(blocked);
            Run(() =>
            {
                if (_resumePhase == AutoBindTemplatePhase.Clearing)
                {
                    PrepareCompilation(AutoBindTemplatePhase.Clearing, "等待清除后的脚本编译……");
                    // 修复外部引用时，Unity 可能已成功重载；避免等待一次不会发生的空编译重载。
                    if (!EditorUtility.scriptCompilationFailed && RemainingGeneratedType() == null)
                    {
                        _reloaded = true;
                        return;
                    }
                    CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
                    return;
                }
                ValidateContext();
                AssertUnchanged();
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(_definition.ViewPath);
                Type loaded = script != null ? script.GetClass() : null;
                bool ready = loaded != null && (_resumePhase == AutoBindTemplatePhase.HostCompilation ||
                    (loaded.GetNestedType("UIView") != null && loaded.Assembly.GetType(
                        string.IsNullOrEmpty(_definition.Namespace) ? _definition.PresenterName :
                        _definition.Namespace + "." + _definition.PresenterName) != null));
                if (!EditorUtility.scriptCompilationFailed && ready)
                {
                    PrepareCompilation(_resumePhase, "重试：继续已编译的创建阶段……");
                    _reloaded = true;
                    return;
                }
                if (_resumePhase == AutoBindTemplatePhase.HostCompilation)
                    WriteBatch(() =>
                    {
                        Write(_definition.ViewPath, _definition.ViewSource(false));
                        if (_definition.UseArgs) Write(_definition.ArgsPath, _definition.ArgsSource());
                        Write(_definition.RoutesPath, _definition.RoutesSource());
                    });
                PrepareCompilation(_resumePhase, "重试：等待脚本编译……");
                CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
            });
        }

        private ViewBase BuildRoot(Scene preview)
        {
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(_definition.ViewPath);
            Type type = script != null ? script.GetClass() : null;
            if (type == null || !typeof(ViewBase).IsAssignableFrom(type) || type.FullName != _definition.FullViewName)
                throw new InvalidOperationException("无法加载新宿主类型，请确认脚本编译成功：" + _definition.FullViewName);
            var root = new GameObject(_definition.ViewName, typeof(RectTransform));
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
            EnsureAvailable(_definition.BindingPath);
            var setting = Object.Instantiate(ReadConfiguration());
            try
            {
                var data = new SerializedObject(setting);
                data.FindProperty("_useGlobalDefaultSavePath").boolValue = true;
                data.ApplyModifiedPropertiesWithoutUndo();
                var map = AutoBindEditorAssetLocator.LoadSingletonAsset<AutoBindKeyMapSetting>();
                var report = AutoBindBuildPipeline.GenerateCode(host.GetComponent<Core.ComponentAutoBindTool>(), setting, map);
                if (!report.Succeeded) throw new InvalidOperationException(report.ValidationResult.BuildReport());
                Remember(_definition.BindingPath);
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
                    Write(_definition.ViewPath, _definition.ViewSource(true));
                    Write(_definition.PresenterPath, _definition.PresenterSource());
                });
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
            PrepareCompilation(AutoBindTemplatePhase.FinalCompilation, "第 2/2 轮：等待绑定代码与 Presenter 编译……");
            CompilationPipeline.RequestScriptCompilation();
        }

        private void CompletePrefab()
        {
            AssertUnchanged();
            EnsureAvailable(_definition.PrefabPath);
            Scene preview = EditorSceneManager.NewPreviewScene();
            GameObject prefab;
            try
            {
                ViewBase host = BuildRoot(preview);
                GenerateBinding(host);
                ((IAutoBindHost)host).EnsureAutoBind(host.gameObject);
                if (!((IAutoBindHost)host).IsAutoBound) throw new InvalidOperationException("模板宿主绑定验证失败。");
                EnsureDirectory(Path.GetDirectoryName(_definition.PrefabPath));
                try
                {
                    prefab = PrefabUtility.SaveAsPrefabAsset(host.gameObject, _definition.PrefabPath, out bool saved);
                    if (!saved || prefab == null) throw new IOException("模板 Prefab 保存失败。");
                }
                finally { if (File.Exists(_definition.PrefabPath)) Remember(_definition.PrefabPath); }
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
            CreateSceneInstance(prefab);
            CaptureMetas();
            _lastSuccess = new CreationReceipt
            {
                Definition = JsonUtility.FromJson<AutoBindViewTemplateDefinition>(JsonUtility.ToJson(_definition)),
                Files = _files.Select(file => new OwnedFile { Path = file.Path, Hash = file.Hash, MetaHash = file.MetaHash }).ToList(),
                Instance = _instance, Parent = _instance.transform.parent.gameObject,
                SceneHandle = _instance.scene.handle, SceneSnapshot = CaptureInstance(_instance)
            };
            _phase = AutoBindTemplatePhase.Completed;
            _message = "创建完成：" + (_definition.UseArgs ? "五" : "四") + "份脚本与 Prefab 已生成。场景尚未保存，可按需保存。";
            Selection.activeGameObject = _instance;
            EditorGUIUtility.PingObject(_instance);
        }

        private void CreateSceneInstance(GameObject prefab)
        {
            Scene scene = ValidateContext();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("创建 UI View Template");
            try
            {
                Transform parent = _parent != null ? _parent.transform : null;
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
                _instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                Undo.RegisterCreatedObjectUndo(_instance, "创建 UI 界面实例");
                Undo.SetTransformParent(_instance.transform, parent, "创建 UI 界面实例");
                var rect = (RectTransform)_instance.transform;
                rect.localScale = Vector3.one;
                rect.localRotation = Quaternion.identity;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                int layer = parent.GetComponentInParent<Canvas>(true).gameObject.layer;
                foreach (Transform child in _instance.GetComponentsInChildren<Transform>(true))
                {
                    child.gameObject.layer = layer;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(child.gameObject);
                }
                PrefabUtility.RecordPrefabInstancePropertyModifications(rect);
                Undo.RegisterFullObjectHierarchyUndo(_instance, "创建 UI 界面实例");
                Undo.CollapseUndoOperations(group);
                EditorSceneManager.MarkSceneDirty(scene);
            }
            catch { Undo.RevertAllDownToGroup(group); _instance = null; throw; }
        }

        private Scene ValidateContext()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || PrefabStageUtility.GetCurrentPrefabStage() != null)
                throw new InvalidOperationException("创建上下文已改变，请返回原普通场景后重试。");
            Scene scene = default;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene candidate = SceneManager.GetSceneAt(i);
                if (candidate.handle == _sceneHandle) { scene = candidate; break; }
            }
            if (!scene.IsValid() || !scene.isLoaded || (_hadParent && (_parent == null || _parent.scene != scene)))
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
            _directories.Add(path);
        }

        private void EnsureAvailable(string path)
        {
            OwnedFile owned = _files.Find(item => item.Path == path);
            if (owned != null) { CheckFile(owned); return; }
            if (File.Exists(path) || Directory.Exists(path) || File.Exists(path + ".meta"))
                throw new IOException("目标在创建期间被占用，不会覆盖：" + path);
        }

        private void Remember(string path)
        {
            var owned = _files.Find(item => item.Path == path);
            if (owned == null) { owned = new OwnedFile { Path = path }; _files.Add(owned); }
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
            foreach (var file in _files)
                if (file.MetaHash == null && File.Exists(file.Path + ".meta")) file.MetaHash = Hash(file.Path + ".meta");
        }

        private static void CheckFile(OwnedFile file)
        {
            if (!File.Exists(file.Path) || Hash(file.Path) != file.Hash ||
                (file.MetaHash == null && File.Exists(file.Path + ".meta")) ||
                (file.MetaHash != null && (!File.Exists(file.Path + ".meta") || Hash(file.Path + ".meta") != file.MetaHash)))
                throw new IOException("产物已被修改或移走，不会覆盖或删除：" + file.Path);
        }

        private void AssertUnchanged() { foreach (var file in _files) CheckFile(file); }

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
            var receipt = _lastSuccess;
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
            var receipt = _lastSuccess;
            var snapshot = AutoBindTemplateAssetDeletion.Capture(receipt.Files.Select(file => file.Path));
            foreach (var file in receipt.Files) CheckFile(file);
            WriteBatch(() =>
            {
                AutoBindTemplateAssetDeletion.Delete(snapshot, out _clearBackupPath);
                var scene = receipt.Instance.scene;
                foreach (Transform child in receipt.Instance.GetComponentsInChildren<Transform>(true))
                {
                    foreach (Component component in child.GetComponents<Component>()) if (component != null) Undo.ClearUndo(component);
                    Undo.ClearUndo(child.gameObject);
                }
                Object.DestroyImmediate(receipt.Instance);
                EditorSceneManager.MarkSceneDirty(scene);
                _definition = receipt.Definition;
                _recreationRecipe = receipt.Definition;
                _recreationParent = receipt.Parent;
                _instance = null;
                _files.Clear();
                PrepareCompilation(AutoBindTemplatePhase.Clearing, "产物已清除，等待脚本重载后可重新创建……");
            });
            CompilationPipeline.RequestScriptCompilation();
        }

        internal void InvalidateDeletedOutputs(IEnumerable<string> deletedPaths)
        {
            var paths = new HashSet<string>(deletedPaths, StringComparer.OrdinalIgnoreCase);
            if (_lastSuccess?.Files != null && _lastSuccess.Files.Any(file => paths.Contains(file.Path))) _lastSuccess = null;
            if (_phase == AutoBindTemplatePhase.Completed && _definition?.IsInitialized == true && _definition.OutputPaths.Any(paths.Contains))
            {
                _phase = AutoBindTemplatePhase.None;
                _instance = null;
                _message = "对应磁盘产物已删除。场景实例未清除，请按需手动处理。";
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
                _phase = AutoBindTemplatePhase.None;
                _message = "任务已取消。为保护已修改的产物，本次文件全部保留：\n" + error.Message;
                return;
            }
            Run(() =>
            {
                WriteBatch(() =>
                {
                    foreach (var file in _files.AsEnumerable().Reverse())
                    {
                        if (!AssetDatabase.DeleteAsset(file.Path))
                        {
                            File.Delete(file.Path);
                            if (File.Exists(file.Path + ".meta")) File.Delete(file.Path + ".meta");
                        }
                    }
                    foreach (string directory in _directories.AsEnumerable().Reverse())
                        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                        {
                            if (!AssetDatabase.DeleteAsset(directory)) Directory.Delete(directory);
                        }
                });
                _files.Clear();
                _phase = AutoBindTemplatePhase.None;
                _message = "已取消并清理本次未修改的产物。";
            });
        }

        private void Run(Action action)
        {
            _executing = true;
            try { action(); }
            catch (Exception error) { Fail(error.Message); }
            finally { _executing = false; }
        }

        private void Fail(string message) { _phase = AutoBindTemplatePhase.Failed; _message = message; }
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
