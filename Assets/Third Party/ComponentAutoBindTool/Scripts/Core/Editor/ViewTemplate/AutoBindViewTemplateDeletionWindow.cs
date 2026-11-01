using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ComponentAutoBindTool.Editor
{
    public sealed class AutoBindViewTemplateDeletionWindow : EditorWindow
    {
        [SerializeField] private string _baseName;
        [SerializeField] private string _scriptDirectory;
        [SerializeField] private string _prefabPath;
        [SerializeReference] private AutoBindTemplateAssetDeletion.Snapshot _preview;
        private HelpBox _status;
        private VisualElement _files;
        private Button _delete, _backup, _retry;
        private TextField _scripts, _prefab;
        private string _error;

        internal static void Open(string name, string scriptDirectory, string prefabPath)
        {
            var window = GetWindow<AutoBindViewTemplateDeletionWindow>("按位置删除 UI 模板");
            window._baseName = AutoBindViewTemplateDefinition.ValidateBaseName(name);
            window._scriptDirectory = scriptDirectory;
            window._prefabPath = prefabPath;
            window.CreateGUI();
            window.Show();
            window.Focus();
        }

        public void CreateGUI()
        {
            minSize = new Vector2(740, 620);
            rootVisualElement.Clear();
            AutoBindViewTemplateUI.Load(rootVisualElement, this);
            var body = new ScrollView(); body.AddToClassList("template-body"); rootVisualElement.Add(body);
            var title = new Label("按位置删除已有模板"); title.AddToClassList("template-title"); body.Add(title);
            body.Add(new HelpBox("只删除以下磁盘产物及元数据，包括已经编辑过的内容。不会清除任何场景实例，也不会保存场景。", HelpBoxMessageType.Warning));
            body.Add(new TextField("界面基础名") { value = _baseName ?? "", isReadOnly = true, name = "deleteBaseName" });
            _scripts = AddPath(body, "脚本所在目录", "deleteScriptDirectory", _scriptDirectory, value => _scriptDirectory = value, true);
            _prefab = AddPath(body, "Prefab 文件路径", "deletePrefabPath", _prefabPath, value => _prefabPath = value, false);
            var refresh = new VisualElement(); refresh.AddToClassList("template-actions"); body.Add(refresh);
            refresh.Add(new Button(RebuildPreview) { text = "刷新删除清单", name = "refreshDeletionPreview" });
            _files = new VisualElement(); body.Add(_files);
            _status = new HelpBox("", HelpBoxMessageType.Info) { name = "deletionStatus" }; body.Add(_status);
            var actions = new VisualElement(); actions.AddToClassList("template-actions"); body.Add(actions);
            _delete = new Button(() => Execute(() => ConfirmAndDelete(_preview, AutoBindViewTemplateConfirmationWindow.Confirm))) { text = "删除…", name = "deleteLocatedTemplate" };
            _backup = new Button(() => EditorUtility.RevealInFinder(AutoBindViewTemplateDeletion.instance.BackupPath)) { text = "打开删除备份", name = "openDeletionBackup" };
            _retry = new Button(() => Execute(AutoBindViewTemplateDeletion.instance.RetryCompilation)) { text = "重新检测编译", name = "retryDeletionCompilation" };
            actions.Add(_delete); actions.Add(_backup); actions.Add(_retry);
            RebuildPreview();
            rootVisualElement.schedule.Execute(UpdateState).Every(200);
        }

        private TextField AddPath(VisualElement body, string label, string name, string value, Action<string> changed, bool folder)
        {
            var row = new VisualElement(); row.AddToClassList("template-folder"); body.Add(row);
            var field = new TextField(label) { name = name, value = value ?? "" }; row.Add(field);
            field.RegisterValueChangedCallback(evt => { changed(evt.newValue); RebuildPreview(); });
            row.Add(new Button(() => Execute(() =>
            {
                string selected = folder ? EditorUtility.OpenFolderPanel(label, Application.dataPath, "") : EditorUtility.OpenFilePanel(label, Application.dataPath, "prefab");
                if (!string.IsNullOrEmpty(selected)) field.value = folder ? AutoBindViewTemplateDefinition.NormalizeFolder(selected) : AutoBindTemplateAssetDeletion.NormalizeFile(selected);
            })) { text = "选择…" });
            row.Add(new Button(() => Execute(() => Ping(field.value))) { text = "定位" });
            return field;
        }

        private static void Ping(string path)
        {
            string normalized = Directory.Exists(path) ? AutoBindViewTemplateDefinition.NormalizeFolder(path) : AutoBindTemplateAssetDeletion.NormalizeFile(path);
            var asset = AssetDatabase.LoadMainAssetAtPath(normalized);
            if (asset != null) EditorGUIUtility.PingObject(asset);
        }

        private void OnProjectChange() { if (_files != null) RebuildPreview(); }

        private void RebuildPreview()
        {
            if (_files == null) return;
            _error = null;
            _preview = null;
            _files.Clear();
            try
            {
                _preview = AutoBindViewTemplateDeletion.Locate(_baseName, _scriptDirectory, _prefabPath);
                int index = 0;
                foreach (var entry in _preview.Entries)
                {
                    var name = new Label(Path.GetFileName(entry.Path)); name.AddToClassList("template-file-name"); _files.Add(name);
                    var row = new VisualElement(); row.AddToClassList("template-folder"); _files.Add(row);
                    row.Add(new TextField { isReadOnly = true, value = Path.GetFullPath(entry.Path), name = "deleteOutput" + index++ });
                    row.Add(new Label(entry.HasFile ? entry.HasMeta ? "文件及 .meta 存在" : "文件存在" : entry.HasMeta ? "仅 .meta 存在" : "不存在，跳过"));
                    row.Add(new Button(() => Ping(entry.Path)) { text = "定位" });
                }
                _files.Add(new HelpBox(_preview.HasEmptyDirectory ? "清单同时包含空叶目录及其元数据：" + _preview.EmptyDirectory : "脚本目录保留；额外文件、子目录和共享目录不会删除。", HelpBoxMessageType.Info));
            }
            catch (Exception error) { _error = error.Message; }
            UpdateState();
        }

        internal static bool ConfirmAndDelete(AutoBindTemplateAssetDeletion.Snapshot preview, Func<string, string, string, bool> confirm)
        {
            string blocked = AutoBindViewTemplateDeletion.BlockReason();
            if (blocked != null) throw new InvalidOperationException(blocked);
            if (!ConfirmDeletion(preview, confirm)) return false;
            AutoBindViewTemplateDeletion.instance.Begin(preview);
            return true;
        }

        internal static bool ConfirmDeletion(AutoBindTemplateAssetDeletion.Snapshot preview, Func<string, string, string, bool> confirm)
        {
            if (preview == null || !preview.HasAnything) throw new InvalidOperationException("没有可删除的产物，请先定位文件。");
            preview.AssertUnchanged();
            string list = string.Join("\n", preview.PhysicalFiles.Select(Path.GetFullPath));
            if (preview.HasEmptyDirectory) list += "\n空脚本目录：" + Path.GetFullPath(preview.EmptyDirectory);
            if (!confirm("第一次确认：删除清单", "将删除以下固定产物及元数据，包括已经编辑过的内容：\n\n" + list + "\n\n其他文件保留。继续后还需再次确认。", "继续到危险操作警告")) return false;
            return confirm("第二次确认：危险操作", "此操作不能通过 Undo 恢复，执行前会备份到 Library。\n\n仅删除磁盘资产，任何场景实例都不会自动移除，可能留下 Missing Script、Missing Prefab。其他源码或资产中的引用也可能失效并导致编译错误。\n\n确认仍要删除上述文件？", "仍然删除");
        }

        private void Execute(Action action)
        {
            _error = null;
            try { action(); }
            catch (Exception error) { _error = error.Message; }
            UpdateState();
        }

        private void UpdateState()
        {
            if (_status == null) return;
            var task = AutoBindViewTemplateDeletion.instance;
            string blocked = AutoBindViewTemplateDeletion.BlockReason();
            _delete.SetEnabled(blocked == null && _preview?.HasAnything == true);
            _scripts.SetEnabled(!task.IsBusy);
            _prefab.SetEnabled(!task.IsBusy);
            _backup.SetEnabled(!string.IsNullOrEmpty(task.BackupPath) && Directory.Exists(task.BackupPath));
            _retry.SetEnabled(blocked == null && task.Phase == AutoBindTemplateDeletionPhase.CompletedWithCompileErrors);
            _status.text = _error ?? blocked ?? task.Message ?? (_preview?.HasAnything == true ? "请核对完整路径；删除前会连续显示两次确认。" : "未找到可删除的固定产物。");
            _status.messageType = _error != null || task.Phase == AutoBindTemplateDeletionPhase.Failed || task.Phase == AutoBindTemplateDeletionPhase.CompletedWithCompileErrors ? HelpBoxMessageType.Error : HelpBoxMessageType.Info;
        }
    }
}
