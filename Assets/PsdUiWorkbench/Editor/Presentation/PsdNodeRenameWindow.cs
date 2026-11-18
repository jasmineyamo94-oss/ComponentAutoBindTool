using System;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PsdUiWorkbench.Presentation
{
    public sealed class PsdNodeRenameWindow : EditorWindow
    {
        private PsdWorkbenchWindow _owner;
        private PsdSession _session;
        private string _assetGuid;
        private long _contextVersion;
        private int[] _ids;
        private readonly Dictionary<int, string> _initialNames = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _sourceNames = new Dictionary<int, string>();
        private readonly Dictionary<int, TextField> _inputs = new Dictionary<int, TextField>();
        private bool _renameRoot;
        private TextField _rootNameInput;
        private string _rootSourceName;
        private string _initialRootName;
        private Label _error;
        private Button _confirm;
        private bool _invalidated, _submitting, _closed;
        private string _message;
        public string Error => _message;

        internal static PsdNodeRenameWindow Open(PsdWorkbenchWindow owner, PsdSession session, int[] ids)
        {
            var window = CreateInstance<PsdNodeRenameWindow>();
            window._owner = owner;
            window._session = session;
            window._assetGuid = session.Document.AssetGuid;
            window._contextVersion = session.ContextVersion;
            window._ids = ids;
            foreach (int id in ids)
            {
                window._initialNames.Add(id, session.GetNodeName(id));
                window._sourceNames.Add(id, session.Document.Find(id).Name);
            }
            session.ContextChanged += window.OnContextChanged;
            window.titleContent = new GUIContent(ids.Length > 1 ? "批量重命名" : "重命名");
            window.minSize = new Vector2(420, 280);
            window.position = new Rect(owner.position.x + 60, owner.position.y + 60, 520,
                ids.Length > 1 ? 480 : 280);
            window.ShowUtility();
            return window;
        }

        internal static PsdNodeRenameWindow OpenRoot(PsdWorkbenchWindow owner, PsdSession session, string sourceName)
        {
            var window = CreateInstance<PsdNodeRenameWindow>();
            window._owner = owner;
            window._session = session;
            window._assetGuid = session.Document.AssetGuid;
            window._contextVersion = session.ContextVersion;
            window._ids = Array.Empty<int>();
            window._renameRoot = true;
            window._rootSourceName = string.IsNullOrEmpty(sourceName) ? "PSD" : sourceName;
            window._initialRootName = session.GetDocumentRootName();
            session.ContextChanged += window.OnContextChanged;
            window.titleContent = new GUIContent("重命名文档根");
            window.minSize = new Vector2(420, 280);
            window.position = new Rect(owner.position.x + 60, owner.position.y + 60, 520, 280);
            window.ShowUtility();
            return window;
        }

        public void CreateGUI()
        {
            var pending = _inputs.ToDictionary(pair => pair.Key, pair => pair.Value.value);
            string pendingRoot = _rootNameInput?.value;
            _inputs.Clear();
            _rootNameInput = null;
            rootVisualElement.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            rootVisualElement.Clear();
            var layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Assets/PsdUiWorkbench/Editor/Presentation/PsdNodeRenameWindow.uxml");
            layout.CloneTree(rootVisualElement);
            _error = rootVisualElement.Q<Label>("renameError");
            _confirm = rootVisualElement.Q<Button>("confirmRename");
            _confirm.clicked += () => Submit();
            rootVisualElement.Q<Button>("cancelRename").clicked += Close;
            rootVisualElement.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            rootVisualElement.Q<Label>("renameHint").text = _renameRoot ?
                "文档根名称用于工作台和新导出的 Prefab；请使用英文 PascalCase，不修改 PSD 文件名。" :
                _ids?.Length > 1 ?
                "请分别填写每个节点的名称；确认后整批保存，任何一项无效都不会部分应用。" :
                "仅修改工作台有效名称，不修改 PSD 原名。支持中文、空格和小写名称。";
            var rows = rootVisualElement.Q<ScrollView>("renameRows");
            if (_renameRoot)
            {
                var row = new VisualElement();
                row.AddToClassList("node-rename-row");
                row.Add(new Label("文档根"));
                row.Add(ReadOnlyName("sourceName_root", "PSD 文件名", _rootSourceName));
                row.Add(ReadOnlyName("effectiveName_root", "当前有效名", _initialRootName));
                _rootNameInput = new TextField("新名称")
                {
                    name = "nodeName_root",
                    value = pendingRoot ?? _initialRootName,
                    tooltip = "英文 PascalCase：大写字母开头，仅允许英文字母和数字，最长 128 字符。"
                };
                row.Add(_rootNameInput);
                rows.Add(row);
            }
            if (_ids != null)
            {
                foreach (int id in _ids)
                {
                    var row = new VisualElement();
                    row.AddToClassList("node-rename-row");
                    row.Add(new Label("图层 ID：" + id));
                    row.Add(ReadOnlyName("sourceName_" + id, "PSD 原名", _sourceNames[id]));
                    row.Add(ReadOnlyName("effectiveName_" + id, "当前有效名", _initialNames[id]));
                    var input = new TextField("新名称")
                    {
                        name = "nodeName_" + id,
                        value = pending.TryGetValue(id, out var value) ? value : _initialNames[id],
                        tooltip = "不限制 PascalCase；名称不能为空，不能包含 /、\\ 或控制字符。同级节点不能新增重名。"
                    };
                    _inputs.Add(id, input);
                    row.Add(input);
                    rows.Add(row);
                }
            }
            if (!HasCurrentContext()) Invalidate();
            else ShowError(_message);
            if (!_invalidated && (_inputs.Count > 0 || _renameRoot))
            {
                var first = _renameRoot ? _rootNameInput : _inputs[_ids[0]];
                first.schedule.Execute(() =>
                {
                    if (_closed || _invalidated) return;
                    first.Focus();
                    first.SelectAll();
                });
            }
        }

        private static TextField ReadOnlyName(string name, string label, string value)
            => new TextField(label) { name = name, value = value, isReadOnly = true, tooltip = value };

        private bool HasCurrentContext()
            => !_closed && !_invalidated && _owner != null && ReferenceEquals(_owner.Session, _session) &&
                _session?.Document != null && _session.Document.AssetGuid == _assetGuid &&
                _session.ContextVersion == _contextVersion && _ids != null &&
                _ids.All(id => _session.Document.Find(id) != null);

        private void OnContextChanged()
        {
            if (!_submitting && !HasCurrentContext()) Invalidate();
        }

        private void Invalidate()
        {
            _invalidated = true;
            ShowError("文档或节点上下文已改变，本窗口不能继续提交。请取消后从当前节点重新打开重命名。");
            _confirm?.SetEnabled(false);
        }

        private void ShowError(string message)
        {
            _message = message;
            if (_error == null) return;
            _error.text = message ?? "";
            _error.EnableInClassList("node-rename-error-visible", !string.IsNullOrEmpty(message));
        }

        public bool Submit()
        {
            if (!HasCurrentContext() || _owner.IsBusy)
            {
                Invalidate();
                return false;
            }
            if (_renameRoot)
            {
                string name = _rootNameInput?.value;
                if (string.Equals(name, _initialRootName, StringComparison.Ordinal))
                {
                    Close();
                    return true;
                }
                if (!PsdNodeNaming.IsValidRoot(name))
                {
                    ShowError("文档根名称无效：请使用大写英文字母开头、只含英文字母和数字且不超过 128 字符。");
                    _rootNameInput?.Focus();
                    return false;
                }
                _submitting = true;
                try
                {
                    if (!_owner.SetDocumentRootName(name))
                    {
                        ShowError("保存失败：命名配置无法写入，输入已保留，请调整后重试。");
                        return false;
                    }
                    Close();
                    return true;
                }
                finally { _submitting = false; }
            }
            if (_inputs.Count != _ids.Length)
            {
                ShowError("节点输入尚未准备完成，请稍后重试。");
                return false;
            }
            var changes = _inputs.Where(pair => !string.Equals(pair.Value.value, _initialNames[pair.Key], StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value.value);
            if (changes.Count == 0)
            {
                Close();
                return true;
            }
            foreach (var pair in changes)
            {
                if (PsdNodeNaming.IsValid(pair.Value)) continue;
                ShowError($"节点“{_initialNames[pair.Key]}”的名称无效：不能为空或仅含空白，也不能包含 /、\\ 或控制字符。整批未应用。");
                _inputs[pair.Key].Focus();
                return false;
            }
            _submitting = true;
            try
            {
                if (!_owner.SetNodeNames(changes))
                {
                    ShowError("保存失败：存在新增的同级重名，或命名配置无法写入。整批未应用，输入已保留，请调整后重试。");
                    return false;
                }
                Close();
                return true;
            }
            finally { _submitting = false; }
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (!string.IsNullOrEmpty(Input.compositionString)) return;
            if (evt.keyCode == KeyCode.Escape)
            {
                evt.StopPropagation();
                evt.PreventDefault();
                Close();
                return;
            }
            if ((evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) ||
                evt.altKey || evt.ctrlKey || evt.commandKey || evt.shiftKey) return;
            // 按钮保留自己的键盘操作；单行输入框及其内部文本元素允许 Enter 提交。
            for (var target = evt.target as VisualElement; target != null; target = target.parent)
                if (target is Button) return;
            evt.StopPropagation();
            evt.PreventDefault();
            Submit();
        }

        private void OnDisable()
        {
            _closed = true;
            if (_session != null) _session.ContextChanged -= OnContextChanged;
        }
    }
}
