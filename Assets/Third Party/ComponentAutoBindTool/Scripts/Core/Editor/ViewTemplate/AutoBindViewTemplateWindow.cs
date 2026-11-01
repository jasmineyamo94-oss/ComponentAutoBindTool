using System;
using System.Collections.Generic;
using System.IO;
using ComponentAutoBindTool.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ComponentAutoBindTool.Editor
{
    public sealed class AutoBindViewTemplateWindow : EditorWindow
    {
        [SerializeField] private string _baseName = "";
        [SerializeField] private bool _useArgs = true;
        [SerializeField] private GameObject _context;
        [SerializeReference] private AutoBindViewTemplateDefinition _recreateRecipe;
        private AutoBindGlobalSetting _global;
        private TextField _name;
        private Toggle _args;
        private HelpBox _status;
        private Button _create, _retry, _cancel, _clear, _backup, _prefab, _script, _deleteLocated;
        private string _error;
        private string _previewKey;
        private readonly List<TextField> _outputs = new();
        private readonly List<Label> _outputNames = new();

        [MenuItem("GameObject/UI (Canvas)/UI View Template", false, 10)]
        private static void Open(MenuCommand command)
        {
            var window = GetWindow<AutoBindViewTemplateWindow>("UI View Template");
            if (AutoBindViewTemplateCreation.instance.RecreationRecipe == null)
                window._context = command.context as GameObject ?? Selection.activeGameObject;
            window.Show();
            window.Focus();
        }

        public void CreateGUI()
        {
            var restored = AutoBindViewTemplateCreation.instance;
            if (_recreateRecipe?.IsInitialized != true) _recreateRecipe = null;
            if (_recreateRecipe == null && restored.RecreationRecipe != null)
            {
                _recreateRecipe = restored.RecreationRecipe;
                _baseName = _recreateRecipe.BaseName;
                _useArgs = _recreateRecipe.UseArgs;
                _context = restored.RecreationParent;
            }
            minSize = new Vector2(680, 630);
            rootVisualElement.Clear();
            AutoBindViewTemplateUI.Load(rootVisualElement, this);
            var scroll = new ScrollView();
            scroll.AddToClassList("template-body");
            rootVisualElement.Add(scroll);
            var title = new Label("创建 UI 界面模板"); title.AddToClassList("template-title"); scroll.Add(title);
            var hint = new Label("输入基础名，其余名称按固定规则生成。例：Inventory → InventoryView。");
            hint.AddToClassList("template-description"); scroll.Add(hint);
            _name = new TextField("界面基础名") { name = "baseName", value = _baseName };
            _name.RegisterValueChangedCallback(evt => { _baseName = evt.newValue; _error = null; Refresh(); });
            scroll.Add(_name);
            _args = new Toggle("使用 Args 传递数据") { name = "useArgs", value = _useArgs };
            _args.RegisterValueChangedCallback(evt => { _useArgs = evt.newValue; _error = null; Refresh(); });
            scroll.Add(_args);
            _outputs.Clear();
            _outputNames.Clear();
            _previewKey = null;
            foreach (string label in new[] { "View 脚本", "Presenter 脚本", "Args 脚本", "Routes 脚本", "绑定脚本", "界面 Prefab" })
            {
                var name = new Label(label) { name = "outputName" + _outputs.Count };
                name.AddToClassList("template-file-name"); scroll.Add(name); _outputNames.Add(name);
                var field = new TextField { isReadOnly = true, name = "output" + _outputs.Count };
                field.AddToClassList("template-output"); scroll.Add(field); _outputs.Add(field);
            }
            var paths = new VisualElement(); paths.AddToClassList("template-actions");
            paths.Add(new Button(() => AutoBindViewTemplateSettingsWindow.Open(
                _recreateRecipe == null ? null : Path.GetDirectoryName(_recreateRecipe.PrefabPath)?.Replace('\\', '/'),
                _recreateRecipe == null ? null : Path.GetDirectoryName(_recreateRecipe.ScriptDirectory)?.Replace('\\', '/'))) { text = "路径设置" });
            scroll.Add(paths);
            _status = new HelpBox("", HelpBoxMessageType.Info) { name = "creationStatus" }; scroll.Add(_status);
            var actions = new VisualElement(); actions.AddToClassList("template-actions");
            _create = new Button(() => Execute(() => AutoBindViewTemplateCreation.instance.Begin(CurrentDefinition(), _context))) { text = "创建", name = "createTemplate" };
            _retry = new Button(() => Execute(() => AutoBindViewTemplateCreation.instance.Retry())) { text = "重试", name = "retryTemplate" };
            _cancel = new Button(() => Execute(() => AutoBindViewTemplateCreation.instance.Cancel())) { text = "取消并清理本次产物", name = "cancelTemplate" };
            actions.Add(_create); actions.Add(_retry); actions.Add(_cancel); scroll.Add(actions);
            var cleanup = new VisualElement(); cleanup.AddToClassList("template-actions");
            _clear = new Button(() => Execute(ClearAndPrepareRecreation)) { text = "清除并重新创建", name = "clearTemplate" };
            _backup = new Button(() => EditorUtility.RevealInFinder(AutoBindViewTemplateCreation.instance.ClearBackupPath)) { text = "打开清除备份", name = "clearBackup" };
            cleanup.Add(_clear); cleanup.Add(_backup); scroll.Add(cleanup);
            _deleteLocated = new Button(() => Execute(OpenLocatedDeletion)) { text = "按位置删除已有模板…", name = "deleteLocatedTemplate" };
            var located = new VisualElement(); located.AddToClassList("template-actions"); located.Add(_deleteLocated); scroll.Add(located);
            var deletionHint = new Label("没有本次生成记录时，可按位置定位文件；仅删除磁盘资产，不清除场景实例。");
            deletionHint.AddToClassList("template-description"); scroll.Add(deletionHint);
            var links = new VisualElement(); links.AddToClassList("template-actions");
            _prefab = new Button(() => EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(AutoBindViewTemplateCreation.instance.Definition.PrefabPath))) { text = "定位 Prefab" };
            _script = new Button(() => AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<MonoScript>(AutoBindViewTemplateCreation.instance.Definition.ViewPath))) { text = "打开 View 脚本" };
            links.Add(_prefab); links.Add(_script); scroll.Add(links);
            ReadGlobal();
            Refresh();
            rootVisualElement.schedule.Execute(Refresh).Every(200);
        }

        private void OnFocus() { ReadGlobal(); Refresh(); }
        private void OnProjectChange() { ReadGlobal(); Refresh(); }
        private void ReadGlobal()
        {
            try { _global = AutoBindViewTemplateCreation.ReadConfiguration(); _error = null; }
            catch (Exception error) { _global = null; _error = error.Message; }
        }

        private AutoBindViewTemplateDefinition CurrentDefinition()
        {
            var settings = AutoBindViewTemplateSettings.instance;
            return AutoBindViewTemplateDefinition.Create(_baseName,
                _recreateRecipe == null ? settings.PrefabRoot : Path.GetDirectoryName(_recreateRecipe.PrefabPath),
                _recreateRecipe == null ? settings.ScriptRoot : Path.GetDirectoryName(_recreateRecipe.ScriptDirectory),
                _recreateRecipe == null ? _global != null ? _global.Namespace : "" : _recreateRecipe.Namespace, _useArgs);
        }

        private void ClearAndPrepareRecreation()
        {
            var job = AutoBindViewTemplateCreation.instance;
            var paths = job.PreviewCompletedClear();
            var recipe = job.LastCreatedDefinition;
            var parent = job.LastCreatedParent;
            if (!EditorUtility.DisplayDialog("清除并重新创建 " + recipe.ViewName,
                "将删除最近一次成功生成的以下产物及其 .meta：\n\n" + string.Join("\n", paths) +
                "\n\n清除不可通过 Undo 恢复，备份保存在 Library。不会保存场景；清除并等待编译后，可调整 Args 再点击创建。", "清除并返回表单", "取消")) return;
            job.ClearCompleted();
            _recreateRecipe = recipe;
            _baseName = recipe.BaseName;
            _useArgs = recipe.UseArgs;
            _context = parent;
            _name.SetValueWithoutNotify(_baseName);
            _args.SetValueWithoutNotify(_useArgs);
        }

        private void OpenLocatedDeletion()
        {
            string basis = AutoBindViewTemplateDefinition.ValidateBaseName(_baseName);
            var settings = AutoBindViewTemplateSettings.instance;
            string scripts = _recreateRecipe == null ? settings.ScriptRoot : Path.GetDirectoryName(_recreateRecipe.ScriptDirectory);
            string prefabs = _recreateRecipe == null ? settings.PrefabRoot : Path.GetDirectoryName(_recreateRecipe.PrefabPath);
            AutoBindViewTemplateDeletionWindow.Open(basis, scripts + "/" + basis, prefabs + "/" + basis + "View.prefab");
        }

        internal static void UseSavedPaths()
        {
            AutoBindViewTemplateCreation.instance.UseSavedPaths();
            foreach (var window in Resources.FindObjectsOfTypeAll<AutoBindViewTemplateWindow>())
            {
                window._recreateRecipe = null;
                window.Refresh();
            }
        }

        private void Execute(Action action)
        {
            _error = null;
            try { action(); }
            catch (Exception error) { _error = error.Message; }
            Refresh();
        }

        private void Refresh()
        {
            if (_name == null) return;
            var job = AutoBindViewTemplateCreation.instance;
            AutoBindViewTemplateDefinition definition = null;
            string validation = null;
            try { definition = job.HasPending ? job.Definition : CurrentDefinition(); }
            catch (Exception error) { validation = error.Message; }
            if (_global == null) validation = "请先创建唯一的 AutoBind 全局配置和组件映射配置；配置就绪后才能创建。";
            string key = definition == null ? "" : string.Join("\n", definition.OutputPaths);
            if (key != _previewKey)
            {
                _previewKey = key;
                string[] paths = definition == null ? new string[6] : new[]
                {
                    definition.ViewPath, definition.PresenterPath, definition.UseArgs ? definition.ArgsPath : null,
                    definition.RoutesPath, definition.BindingPath, definition.PrefabPath
                };
                for (int i = 0; i < _outputs.Count; i++)
                {
                    bool omittedArgs = definition != null && !definition.UseArgs && i == 2;
                    _outputs[i].SetValueWithoutNotify(omittedArgs ? "不生成专属 Args，使用 EmptyViewArgs" : paths[i] ?? "—");
                    _outputNames[i].text = omittedArgs ? "Args 脚本（不生成）" : paths[i] == null ? "—" : Path.GetFileName(paths[i]);
                }
            }
            _name.SetEnabled(!job.HasPending);
            _args.SetEnabled(!job.HasPending);
            if (job.HasPending && definition != null)
            {
                _baseName = definition.BaseName;
                _useArgs = definition.UseArgs;
                _name.SetValueWithoutNotify(_baseName);
                _args.SetValueWithoutNotify(_useArgs);
            }
            bool idle = !EditorApplication.isCompiling && !EditorApplication.isUpdating;
            string blocked = AutoBindViewTemplateCreation.BlockReason();
            _create.SetEnabled(!job.HasPending && blocked == null && definition != null && _global != null);
            _retry.SetEnabled(idle && job.Phase == AutoBindTemplatePhase.Failed);
            _cancel.SetEnabled(idle && job.CanCancel && !EditorApplication.isPlayingOrWillChangePlaymode && UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() == null);
            _clear.SetEnabled(job.CanClearCompleted && AutoBindViewTemplateCreation.BlockReason(true) == null);
            _backup.SetEnabled(!string.IsNullOrEmpty(job.ClearBackupPath) && Directory.Exists(job.ClearBackupPath));
            bool validName;
            try { AutoBindViewTemplateDefinition.ValidateBaseName(_baseName); validName = true; }
            catch (ArgumentException) { validName = false; }
            _deleteLocated.SetEnabled(validName && AutoBindViewTemplateDeletion.BlockReason() == null);
            _prefab.SetEnabled(job.Phase == AutoBindTemplatePhase.Completed && job.Definition != null && File.Exists(job.Definition.PrefabPath));
            _script.SetEnabled(job.Definition != null && File.Exists(job.Definition.ViewPath));
            _status.text = _error ?? (job.HasPending || job.Phase == AutoBindTemplatePhase.Completed ? job.Message : validation ?? blocked ?? job.Message);
            _status.messageType = _error != null || job.Phase == AutoBindTemplatePhase.Failed ? HelpBoxMessageType.Error : HelpBoxMessageType.Info;
        }
    }

    internal static class AutoBindViewTemplateUI
    {
        internal static void Load(VisualElement root, ScriptableObject owner)
        {
            string scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(owner));
            string stylePath = Path.GetDirectoryName(scriptPath)?.Replace('\\', '/') + "/AutoBindViewTemplate.uss";
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(stylePath);
            if (sheet != null) root.styleSheets.Add(sheet);
            root.AddToClassList("view-template");
        }
    }
}
