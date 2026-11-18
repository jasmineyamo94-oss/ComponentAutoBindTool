using System;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEditor;
using UnityEngine.UIElements;

namespace PsdUiWorkbench.AiNaming
{
    public sealed class PsdAiNamingPanel : VisualElement, IDisposable
    {
        private const string LayoutPath = "Assets/PsdUiWorkbench/Editor/AiNaming/Presentation/PsdAiNamingPanel.uxml";
        private readonly PsdSession _session;
        private readonly PsdAiNamingController _controller;
        private readonly Func<IReadOnlyList<int>> _getSelectedIds;
        private readonly Func<bool> _isDocumentRootSelected;
        private readonly List<Action> _unbind = new List<Action>();
        private readonly List<PsdAiNamingRow> _rows = new List<PsdAiNamingRow>();
        private readonly ListView _list;
        private PsdAiNamingSettingsWindow _settingsWindow;
        private int? _annotationId;
        private string _annotationGuid;
        private string _shownAnnotation = "";
        private bool _disposed;
        public event Action CloseRequested;
        public event Action NamesApplied;
        public event Action<PsdAiNamingSettings> SettingsChanged;

        public PsdAiNamingPanel(PsdSession session, PsdAiNamingController controller, Func<IReadOnlyList<int>> getSelectedIds,
            Func<bool> isDocumentRootSelected = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _getSelectedIds = getSelectedIds ?? throw new ArgumentNullException(nameof(getSelectedIds));
            _isDocumentRootSelected = isDocumentRootSelected ?? (() => false);
            var layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            if (layout == null) throw new InvalidOperationException("AI 命名面板布局缺失。");
            layout.CloneTree(this);
            Click("close", () => CloseRequested?.Invoke());
            Click("cancel", () => _controller.Cancel());
            Click("discard", () => { _controller.Discard(); Notice(""); });
            Click("analyzeSelection", () => Start(false));
            Click("analyzeSubtree", () => Start(true));
            var annotation = this.Q<TextField>("annotationInput");
            annotation.maxLength = 2048;
            annotation.RegisterValueChangedCallback(_ => RefreshAnnotationStatus());
            Click("saveAnnotation", SaveAnnotation);
            Click("clearAnnotation", ClearAnnotation);
            Click("selectAll", () => _controller.SetSelection(true));
            Click("selectNone", () => _controller.SetSelection(false));
            Click("apply", () =>
            {
                if (_controller.ApplySelected())
                {
                    Notice("");
                    NamesApplied?.Invoke();
                }
                Refresh();
            });
            Click("openSettings", OpenSettings);
            _list = this.Q<ListView>("results");
            _list.itemsSource = _rows;
            _list.selectionType = SelectionType.None;
            _list.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
            _list.makeItem = () => new ResultElement(RefreshApply);
            _list.bindItem = (element, index) => ((ResultElement)element).Bind(_rows[index], !_controller.IsBusy && !_controller.IsStale);
            _list.unbindItem = (element, index) => ((ResultElement)element).Unbind();
            _list.destroyItem = element => ((ResultElement)element).Unbind();
            _controller.Changed += Refresh;
            PsdAiModelCatalog.Changed += Refresh;
            Undo.undoRedoPerformed += Refresh;
            Refresh();
        }

        private void Click(string name, Action action)
        {
            Action guarded = () => { if (!_disposed) action(); };
            this.Q<Button>(name).clicked += guarded;
            _unbind.Add(() => this.Q<Button>(name).clicked -= guarded);
        }
        private void Start(bool descendants)
        {
            if (_disposed || _controller.IsBusy) return;
            Safe(() =>
            {
                if (descendants && _isDocumentRootSelected()) _controller.StartWholeDocument();
                else _controller.Start(_getSelectedIds() ?? Array.Empty<int>(), descendants);
            });
        }
        public void RefreshSelection()
        {
            if (_disposed) return;
            var selected = _getSelectedIds() ?? Array.Empty<int>();
            int count = selected.Distinct().Count();
            this.Q<Label>("uploadNotice").text = "分析会上传图片、文字、已保存的相关节点描述及层级至 " +
                PsdAiModelCatalog.ProviderName(_controller.Settings.Provider) + "，可能产生费用。建议默认不勾选，不会自动应用。";
            bool rootSelected = _isDocumentRootSelected();
            this.Q<Label>("selection").text = rootSelected ? "当前选择：文档根节点（子树包含整份 PSD）" :
                "当前选择：" + count + " 个节点";
            int? id = !rootSelected && count == 1 && _session.Document?.Find(selected[0]) != null ? selected[0] : (int?)null;
            var editor = this.Q<VisualElement>("annotationEditor");
            editor.EnableInClassList("ai-hidden", !id.HasValue);
            this.Q<Label>("annotationStatus").EnableInClassList("ai-hidden", !id.HasValue);
            var annotation = this.Q<TextField>("annotationInput");
            string saved = id.HasValue ? _session.GetNodeAnnotation(id.Value) : "";
            if (_annotationId != id || _annotationGuid != _session.Document?.AssetGuid ||
                (_shownAnnotation != saved && annotation.focusController?.focusedElement != annotation))
            {
                _annotationId = id;
                _annotationGuid = _session.Document?.AssetGuid;
                _shownAnnotation = saved;
                annotation.SetValueWithoutNotify(saved);
            }
            RefreshAnnotationStatus();
            string validation = PsdAiModelCatalog.Validate(_controller.Settings.Provider,
                _controller.Settings.Model, _controller.Settings.ReasoningEffort);
            var modelIssue = this.Q<Label>("modelIssue");
            modelIssue.text = string.IsNullOrEmpty(validation) ? "" :
                "当前模型设置不可用于分析：" + validation + " 请打开「AI 设置」修正；修正前不会发送 AI 请求。";
            modelIssue.EnableInClassList("ai-hidden", string.IsNullOrEmpty(validation));
            bool enabled = _session.Document != null && !_controller.IsBusy;
            this.Q<Button>("analyzeSelection").SetEnabled(enabled && count > 0 && !rootSelected);
            this.Q<Button>("analyzeSubtree").SetEnabled(enabled && (count > 0 || rootSelected &&
                _session.Document.Nodes.Count > 0));
        }
        private void RefreshAnnotationStatus()
        {
            if (!_annotationId.HasValue) return;
            var value = this.Q<TextField>("annotationInput").value ?? "";
            var saved = _session.GetNodeAnnotation(_annotationId.Value) ?? "";
            bool dirty = !string.Equals(value, saved, StringComparison.Ordinal);
            var status = this.Q<Label>("annotationStatus");
            status.text = dirty ? "描述尚未保存；分析仅使用已保存内容。" :
                string.IsNullOrEmpty(saved) ? "此节点暂无描述。" : "描述已保存，分析时作为背景线索。";
            this.Q<Button>("saveAnnotation").SetEnabled(dirty);
            this.Q<Button>("clearAnnotation").SetEnabled(!string.IsNullOrEmpty(saved) || !string.IsNullOrWhiteSpace(value));
        }

        private void SaveAnnotation()
        {
            if (!_annotationId.HasValue || _session.Document?.Find(_annotationId.Value) == null) return;
            int id = _annotationId.Value;
            string value = this.Q<TextField>("annotationInput").value ?? "";
            if (!_session.SetNodeAnnotation(id, value))
            {
                Notice("描述未保存；请检查内容长度或配置文件是否可写。");
                return;
            }
            _shownAnnotation = _session.GetNodeAnnotation(id);
            this.Q<TextField>("annotationInput").SetValueWithoutNotify(_shownAnnotation);
            Notice("");
            RefreshAnnotationStatus();
        }

        private void ClearAnnotation()
        {
            if (!_annotationId.HasValue || _session.Document?.Find(_annotationId.Value) == null) return;
            int id = _annotationId.Value;
            if (!string.IsNullOrEmpty(_session.GetNodeAnnotation(id)) && !_session.SetNodeAnnotation(id, ""))
            {
                Notice("描述未清空；请检查配置文件是否可写。");
                return;
            }
            _shownAnnotation = "";
            this.Q<TextField>("annotationInput").SetValueWithoutNotify("");
            Notice("");
            RefreshAnnotationStatus();
        }

        public void Refresh()
        {
            if (_disposed) return;
            this.Q<Button>("openSettings").SetEnabled(!_disposed);
            this.Q<Button>("cancel").SetEnabled(_controller.IsBusy);
            string status = string.Join("\n", new[] { _controller.Status, _controller.Error,
                _controller.IsStale ? "结果已过期，请重新分析。" : "" }.Where(text => !string.IsNullOrWhiteSpace(text)));
            this.Q<Label>("status").text = status;
            this.Q<Label>("status").EnableInClassList("ai-hidden", string.IsNullOrWhiteSpace(status));
            bool changed = _rows.Count != _controller.Rows.Count || !_rows.SequenceEqual(_controller.Rows);
            if (changed) { _rows.Clear(); _rows.AddRange(_controller.Rows); _list.Rebuild(); }
            else _list.RefreshItems();
            _list.EnableInClassList("ai-hidden", _rows.Count == 0);
            this.Q<VisualElement>("resultsToolbar").EnableInClassList("ai-hidden", _rows.Count == 0);
            this.Q<Label>("emptyResults").EnableInClassList("ai-hidden", _rows.Count > 0);
            RefreshSelection();
            RefreshApply();
        }
        private void RefreshApply()
        {
            this.Q<Button>("apply").SetEnabled(!_disposed && !_controller.IsBusy && !_controller.IsStale && _rows.Any(r => r.Selected && r.CanApply));
            this.Q<Button>("selectAll").SetEnabled(!_disposed && !_controller.IsBusy && !_controller.IsStale && _rows.Count > 0);
            this.Q<Button>("selectNone").SetEnabled(!_disposed && !_controller.IsBusy && _rows.Count > 0);
            this.Q<Button>("discard").SetEnabled(!_disposed && !_controller.IsBusy && _rows.Count > 0);
        }
        private void OpenSettings()
        {
            if (_disposed) return;
            if (_settingsWindow != null)
            {
                _settingsWindow.Focus();
                return;
            }
            _settingsWindow = PsdAiNamingSettingsWindow.Open(_controller,
                settings =>
                {
                    if (_disposed) return;
                    SettingsChanged?.Invoke(settings);
                    Refresh();
                },
                () => _settingsWindow = null);
        }
        private void Notice(string text)
        {
            var notice = this.Q<Label>("notice");
            notice.text = text;
            notice.EnableInClassList("ai-hidden", string.IsNullOrWhiteSpace(text));
        }
        private void Safe(Action action)
        {
            try { action(); }
            catch (Exception) { Notice("操作未完成。请检查文件格式、设置及当前文档后重试；未显示敏感内容。"); }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _controller.Changed -= Refresh;
            PsdAiModelCatalog.Changed -= Refresh;
            if (_settingsWindow != null) _settingsWindow.Close();
            _settingsWindow = null;
            Undo.undoRedoPerformed -= Refresh;
            foreach (var unbind in _unbind) unbind();
            _unbind.Clear();
            foreach (var row in _list.Query<ResultElement>().ToList()) row.Unbind();
            CloseRequested = null; NamesApplied = null; SettingsChanged = null;
            SetEnabled(false);
        }

        private sealed class ResultElement : VisualElement
        {
            private readonly Toggle _selected = new Toggle("应用");
            private readonly Label _source = new Label();
            private readonly TextField _suggestion = new TextField("建议");
            private readonly Label _reason = new Label();
            private readonly Action _changed;
            private PsdAiNamingRow _row;
            public ResultElement(Action changed)
            {
                _changed = changed;
                AddToClassList("ai-result");
                Add(_source); Add(_selected); Add(_suggestion); Add(_reason);
                _selected.RegisterValueChangedCallback(e => { if (_row == null) return; _row.Selected = e.newValue; _changed(); });
                _suggestion.RegisterValueChangedCallback(e => { if (_row == null) return; _row.SuggestedName = e.newValue; _selected.SetEnabled(_row.CanApply); _changed(); });
            }
            public void Bind(PsdAiNamingRow row, bool editable)
            {
                Unbind();
                _row = row; userData = row;
                _source.text = row.Path + "\n原名：" + row.OriginalName + "\n当前：" + row.CurrentName;
                _selected.SetValueWithoutNotify(row.Selected);
                _suggestion.SetValueWithoutNotify(row.SuggestedName);
                _reason.text = (row.NeedsReview ? "【需复核】" : "") + (!row.HasPreview ? "【无图片预览】" : "") + row.Reason;
                _suggestion.SetEnabled(editable);
                _selected.SetEnabled(editable && row.CanApply);
            }
            public void Unbind() { _row = null; userData = null; }
        }
    }
}
