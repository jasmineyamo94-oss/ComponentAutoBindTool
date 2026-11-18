using System;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.AiNaming;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace PsdUiWorkbench.Presentation
{
    public sealed class PsdWorkbenchWindow : EditorWindow
    {
        [SerializeField] private string _assetGuid;
        [SerializeField] private int _selectedLayer;
        [SerializeField] private bool _hasSelection;
        [SerializeField] private int[] _selectedLayers = new int[0];
        [SerializeField] private bool _documentRootSelected;
        [SerializeField] private PsdAiNamingSettings _aiSettings;
        [SerializeField] private bool _showAiNaming;
        private PsdSession _session;
        private PsdVisibilityRepository _visibilityRepository;
        private PsdNodeNameRepository _nameRepository;
        private PsdNodeAnnotationRepository _annotationRepository;
        private PsdNodeNameState _displayedNames;
        private PsdNodeAnnotationState _displayedAnnotations;
        private PsdAiNamingController _aiController;
        private PsdAiNamingPanel _aiPanel;
        private VisualElement _aiHost;
        private PsdNodeRenameWindow _renameWindow;
        private readonly UnityPsdDocumentSource _source = new UnityPsdDocumentSource();
        private ObjectField _assetField;
        private PsdLayerTree _tree;
        private PsdPreview _preview;
        private Label _status;
        private Button _exportButton;
        private bool _synchronizing, _refreshQueued;
        private string _lastPath;
        public string Status => _status?.text;
        public int NodeCount => _session?.Document?.Nodes.Count ?? 0;
        public int? SelectedLayerId => _session?.SelectedId;
        public bool IsBusy => _refreshQueued;
        public bool DocumentRootSelected => _documentRootSelected;
        public PsdSession Session => _session;
        public PsdAiNamingController AiNaming => _aiController;
        private static string StateKey => "PsdUiWorkbench:" + Application.dataPath;

        private void RestoreSelection()
        {
            _documentRootSelected = SessionState.GetBool(StateKey + ":" + _assetGuid + ":rootSelected", false);
            _hasSelection = SessionState.GetBool(StateKey + ":" + _assetGuid + ":hasSelection", false);
            _selectedLayer = SessionState.GetInt(StateKey + ":" + _assetGuid + ":selection", 0);
            _selectedLayers = SessionState.GetIntArray(StateKey + ":" + _assetGuid + ":selections",
                _hasSelection ? new[] { _selectedLayer } : new int[0]);
        }

        [MenuItem("Tools/PSD UI Workbench")]
        public static void Open() => GetWindow<PsdWorkbenchWindow>("PSD UI Workbench");

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(_assetGuid))
            {
                _assetGuid = SessionState.GetString(StateKey + ":asset", "");
                RestoreSelection();
            }
            _visibilityRepository = new PsdVisibilityRepository();
            _nameRepository = new PsdNodeNameRepository();
            _annotationRepository = new PsdNodeAnnotationRepository();
            _session = new PsdSession(_visibilityRepository, nameRepository: _nameRepository,
                annotationRepository: _annotationRepository);
            _displayedNames = _session.CaptureNames();
            _displayedAnnotations = _session.CaptureAnnotations();
            _session.ContextChanged += RefreshNames;
            _nameRepository.Changed += RestoreNames;
            _annotationRepository.Changed += _session.RestoreAnnotations;
            _visibilityRepository.Changed += RestoreVisibility;
            PsdAssetChanges.Changed += OnAssetsChanged;
        }

        private void OnDisable()
        {
            CloseNodeRename();
            _aiPanel?.Dispose();
            _aiController?.Dispose();
            _session.ContextChanged -= RefreshNames;
            _nameRepository.Changed -= RestoreNames;
            _annotationRepository.Changed -= _session.RestoreAnnotations;
            _annotationRepository.Dispose();
            _nameRepository.Dispose();
            PsdAssetChanges.Changed -= OnAssetsChanged;
            EditorApplication.delayCall -= RefreshQueued;
            _refreshQueued = false;
            _visibilityRepository.Changed -= RestoreVisibility;
            _visibilityRepository.Dispose();
        }

        public void CreateGUI()
        {
            rootVisualElement.UnregisterCallback<KeyDownEvent>(OnPreviewFrameKey, TrickleDown.TrickleDown);
            CloseNodeRename();
            _aiPanel?.Dispose();
            _aiController?.Dispose();
            _aiPanel = null;
            _aiController = null;
            minSize = new Vector2(_showAiNaming ? 1100 : 750, 500);
            var layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/PsdUiWorkbench/Editor/Presentation/PsdWorkbenchWindow.uxml");
            rootVisualElement.Clear();
            layout.CloneTree(rootVisualElement);
            _assetField = rootVisualElement.Q<ObjectField>("psdAsset");
            _assetField.objectType = typeof(UnityEngine.Object);
            _assetField.allowSceneObjects = false;
            _status = rootVisualElement.Q<Label>("status");
            _preview = rootVisualElement.Q<PsdPreview>();
            _tree = rootVisualElement.Q<PsdLayerTree>("layers");
            _tree.selectionChanged += values =>
            {
                if (_synchronizing) return;
                var ids = values.OfType<PsdNode>().Select(node => node.Id).ToArray();
                _documentRootSelected = ids.Length == 0 && _tree.selectedIds.Contains(_tree.DocumentRootId);
                _session.SelectMany(ids, ids.Length > 0 ? (int?)ids[ids.Length - 1] : null);
                RememberSelection();
                _preview.UpdateVisibility(_session);
                _aiPanel?.RefreshSelection();
            };
            _tree.VisibilityChanged += (id, visible) =>
            {
                if (id.HasValue) SetNodeVisible(id.Value, visible);
                else SetDocumentVisible(visible);
            };
            _tree.NodesVisibilityChanged += SetNodesVisible;
            _tree.NodesExportChanged += SetNodesExportable;
            _tree.NamesResetRequested += ResetNodeNames;
            _tree.RenameRequested += ids =>
            {
                if (ids.Count == 1 && ids[0] == _tree.DocumentRootId) OpenDocumentRootRename();
                else OpenNodeRename(ids);
            };
            _tree.AiNamingRequested += (ids, descendants) =>
            {
                ShowAiNaming();
                if (ids.Count == 1 && ids[0] == _tree.DocumentRootId) _aiController.StartWholeDocument();
                else _aiController.Start(ids, descendants);
            };
            _tree.tooltip = "Ctrl/Cmd 多选，Shift 连续选择；选中文档根并分析子树可覆盖整份 PSD；F 聚焦预览，F2 重命名；右键可设置激活和是否导出。";
            rootVisualElement.RegisterCallback<KeyDownEvent>(OnPreviewFrameKey, TrickleDown.TrickleDown);
            rootVisualElement.Q<Button>("frameSelection").clicked += () => FocusSelected();
            rootVisualElement.Q<Button>("fit").clicked += _preview.Fit;
            rootVisualElement.Q<Button>("openInspector").clicked += OpenOfficialInspector;
            rootVisualElement.Q<Button>("resetVisibility").clicked += ResetVisibility;
            rootVisualElement.Q<Button>("aiNaming").clicked += () => ShowAiNaming(!_showAiNaming);
            _exportButton = rootVisualElement.Q<Button>("exportPrefab");
            _exportButton.clicked += ShowExportMenu;
            _exportButton.tooltip = "每个文本层导出一个 TMP，优先使用层内可用字体；不支持的文本层跳过且不保留 Image。";
            _exportButton.schedule.Execute(UpdateExportAvailability).Every(250);
            _preview.Picked += (x, y) => SelectLayer(_session.HitTest(x, y));
            _assetField.RegisterValueChangedCallback(evt =>
            {
                if (_synchronizing) return;
                string path = AssetDatabase.GetAssetPath(evt.newValue);
                if (IsPsd(path)) OpenAsset(path);
                else
                {
                    _status.text = "请选择 Assets 目录中的 PSD/PSB 资源。";
                    SyncAssetField();
                }
            });
            RefreshDocument(true);
            CreateAiNamingPanel();
        }

        private void CreateAiNamingPanel()
        {
            _aiPanel?.Dispose();
            _aiController?.Dispose();
            _aiController = new PsdAiNamingController(_session, _source.Resolve, _aiSettings);
            _aiPanel = new PsdAiNamingPanel(_session, _aiController, () => _session.SelectedIds,
                () => _documentRootSelected);
            _aiPanel.CloseRequested += () => ShowAiNaming(false);
            _aiPanel.SettingsChanged += settings => _aiSettings = settings;
            _aiPanel.NamesApplied += RefreshNames;
            _aiHost = rootVisualElement.Q("aiHost");
            _aiHost.Clear();
            _aiHost.Add(_aiPanel);
            _aiHost.style.display = _showAiNaming ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public void ShowAiNaming(bool show = true)
        {
            _showAiNaming = show;
            if (_aiHost == null) return;
            _aiHost.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            minSize = new Vector2(show ? 1100 : 750, 500);
            if (!show && _aiController.IsBusy) _aiController.Cancel();
            _aiPanel.RefreshSelection();
            _preview.schedule.Execute(_preview.Fit);
        }

        private void RestoreNames() => _session.RestoreNames();

        private void RefreshNames()
        {
            var names = _session.CaptureNames();
            var annotations = _session.CaptureAnnotations();
            bool changed = !Equals(names, _displayedNames) || !Equals(annotations, _displayedAnnotations);
            if (changed)
            {
                _displayedNames = names;
                _displayedAnnotations = annotations;
                _tree?.RefreshItems();
            }
            _aiPanel?.RefreshSelection();
        }

        public bool SetNodeNames(IReadOnlyDictionary<int, string> names)
        {
            bool changed = _session.SetNodeNames(names);
            RefreshNames();
            return changed;
        }
        public bool SetDocumentRootName(string name)
        {
            bool changed = _session.SetDocumentRootName(name);
            RefreshNames();
            return changed;
        }
        public bool FocusSelected()
        {
            if (_preview == null || _session?.Document == null || _session.SelectedIds.Count == 0) return false;
            var bounds = default(PixelRect);
            foreach (int id in _session.SelectedIds)
            {
                var nodeBounds = _session.Document.GetBounds(id);
                if (!nodeBounds.IsValid) nodeBounds = _session.Document.Find(id)?.Bounds ?? default;
                bounds = PixelRect.Union(bounds, nodeBounds);
            }
            return _preview.Frame(bounds);
        }

        private void OnPreviewFrameKey(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.F || evt.altKey || evt.ctrlKey || evt.commandKey || evt.shiftKey ||
                !string.IsNullOrEmpty(Input.compositionString)) return;
            var target = evt.target as VisualElement;
            if (target == null || (_tree != target && !_tree.Contains(target) &&
                _preview != target && !_preview.Contains(target))) return;
            for (var element = target; element != null; element = element.parent)
                if (element is TextField || element is TextElement text && text.selection.isSelectable) return;
            if (!FocusSelected()) return;
            evt.StopPropagation();
            evt.PreventDefault();
        }

        public void ResetNodeNames(IReadOnlyList<int> ids)
        {
            _session.ResetNodeNames(ids);
            RefreshNames();
        }

        public PsdNodeRenameWindow OpenDocumentRootRename()
        {
            if (_session?.Document == null) return null;
            CloseNodeRename();
            _renameWindow = PsdNodeRenameWindow.OpenRoot(this, _session,
                System.IO.Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(_assetGuid)));
            return _renameWindow;
        }

        public PsdNodeRenameWindow OpenNodeRename(IReadOnlyList<int> ids)
        {
            if (_session?.Document == null || ids == null || ids.Count == 0 ||
                ids.Any(id => _session.Document.Find(id) == null)) return null;
            CloseNodeRename();
            _renameWindow = PsdNodeRenameWindow.Open(this, _session, ids.Distinct().ToArray());
            return _renameWindow;
        }

        private void CloseNodeRename()
        {
            if (_renameWindow != null) _renameWindow.Close();
            _renameWindow = null;
        }

        private static bool IsPsd(string path) => !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal) &&
            (path.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".psb", StringComparison.OrdinalIgnoreCase));

        public void OpenAsset(string path)
        {
            if (!IsPsd(path)) return;
            string guid = AssetDatabase.AssetPathToGUID(path);
            bool changed = guid != _assetGuid;
            _assetGuid = guid;
            SessionState.SetString(StateKey + ":asset", guid);
            if (changed) RestoreSelection();
            RefreshDocument(true);
        }

        public void OpenOfficialInspector()
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(_assetGuid));
            if (asset != null) EditorUtility.OpenPropertyEditor(asset);
        }

        private void QueueRefresh()
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            EditorApplication.delayCall += RefreshQueued;
        }

        private void OnAssetsChanged(string[] paths)
        {
            string current = AssetDatabase.GUIDToAssetPath(_assetGuid);
            if (paths.Any(path => string.Equals(path, current, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, _lastPath, StringComparison.OrdinalIgnoreCase))) QueueRefresh();
        }

        private void RefreshQueued()
        {
            _refreshQueued = false;
            if (this != null && _status != null) RefreshDocument(false);
        }

        private void RefreshDocument(bool fit)
        {
            _lastPath = AssetDatabase.GUIDToAssetPath(_assetGuid);
            var result = _session.Load(_source, _assetGuid);
            if (_hasSelection) _session.SelectMany(_selectedLayers?.Length > 0 ? _selectedLayers : new[] { _selectedLayer }, _selectedLayer);
            _status.text = result.Diagnostic + "　激活与是否导出分别保存，使用已应用的导入结果。";
            SyncAssetField();
            _synchronizing = true;
            _tree.SetDocument(_session, System.IO.Path.GetFileNameWithoutExtension(_lastPath));
            _synchronizing = false;
            _preview.SetDocument(result.Document, _source.Resolve);
            if (_documentRootSelected && result.Document != null) SelectDocumentRoot();
            else SelectLayers(_session.SelectedIds, _session.SelectedId);
            UpdateVisibility();
            UpdateExportAvailability();
            if (fit) _preview.schedule.Execute(_preview.Fit);
        }

        private void SyncAssetField()
        {
            _assetField.SetValueWithoutNotify(AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(_assetGuid)));
        }

        public void SelectLayer(int? id) => SelectLayers(id.HasValue ? new[] { id.Value } : null, id);

        public void SelectDocumentRoot()
        {
            if (_tree == null || _session?.Document == null) return;
            _session.Select(null);
            _documentRootSelected = true;
            RememberSelection();
            _synchronizing = true;
            _tree.SetSelectionById(new[] { _tree.DocumentRootId });
            _tree.ScrollToItemById(_tree.DocumentRootId);
            _synchronizing = false;
            _preview.UpdateVisibility(_session);
            _aiPanel?.RefreshSelection();
        }

        public void SelectLayers(IEnumerable<int> ids, int? primaryId = null)
        {
            _session.SelectMany(ids, primaryId);
            _documentRootSelected = false;
            RememberSelection();
            _synchronizing = true;
            if (_hasSelection)
            {
                _tree.SetSelectionById(_selectedLayers);
                _tree.ScrollToItemById(_selectedLayer);
            }
            else _tree.ClearSelection();
            _synchronizing = false;
            _preview.UpdateVisibility(_session);
            _aiPanel?.RefreshSelection();
        }

        private void RememberSelection()
        {
            _selectedLayers = _session.SelectedIds.ToArray();
            _hasSelection = _session.SelectedId.HasValue;
            _selectedLayer = _session.SelectedId ?? 0;
            SessionState.SetBool(StateKey + ":" + _assetGuid + ":rootSelected", _documentRootSelected);
            SessionState.SetBool(StateKey + ":" + _assetGuid + ":hasSelection", _hasSelection);
            SessionState.SetInt(StateKey + ":" + _assetGuid + ":selection", _selectedLayer);
            SessionState.SetIntArray(StateKey + ":" + _assetGuid + ":selections", _selectedLayers);
        }

        public void SetNodeVisible(int id, bool visible) => SetNodesVisible(new[] { id }, visible);
        public void SetNodesVisible(IReadOnlyList<int> ids, bool visible)
        {
            _session.SetNodesVisible(ids, visible);
            UpdateVisibility();
        }

        public void SetNodeExportable(int id, bool exportable) => SetNodesExportable(new[] { id }, exportable);
        public void SetNodesExportable(IReadOnlyList<int> ids, bool exportable)
        {
            _session.SetNodesExportable(ids, exportable);
            UpdateVisibility();
        }

        public void SetDocumentVisible(bool visible)
        {
            _session.SetDocumentVisible(visible);
            UpdateVisibility();
        }

        public void ResetVisibility()
        {
            _session.ResetVisibility();
            UpdateVisibility();
        }

        private void RestoreVisibility()
        {
            _session.RestoreVisibility();
            if (_tree != null) UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            _tree.RefreshItems();
            _preview.UpdateVisibility(_session);
            UpdateExportAvailability();
        }

        private bool CanExport => !_refreshQueued && !EditorApplication.isCompiling && !EditorApplication.isUpdating &&
            _session?.ExportableDrawNodeCount > 0;

        private void UpdateExportAvailability()
        {
            if (_exportButton == null) return;
            _exportButton.SetEnabled(CanExport);
            _exportButton.tooltip = _session?.Document?.DrawNodes.Count > 0 && _session.ExportableDrawNodeCount == 0 ?
                "所有可导出内容均已设为不导出；请通过节点右键恢复导出。" :
                "只导出允许的节点；每层一个 TMP，优先使用层内可用字体。不支持的文本跳过且不保留 Image。";
        }

        private void ShowExportMenu()
        {
            if (!CanExport) return;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("TMP＋外描边（单层单组件）"), false, () => ChoosePrefabPath(PsdTmpRenderingMode.OuterOutline));
            menu.AddItem(new GUIContent("标准 TMP（兼容选项）"), false, () => ChoosePrefabPath(PsdTmpRenderingMode.Standard));
            menu.ShowAsContext();
        }

        private void ChoosePrefabPath(PsdTmpRenderingMode renderingMode)
        {
            if (!CanExport) return;
            // Unity 保存 Prefab 时会使用资源文件名作为根 GameObject 名称；默认文件名与文档根保持一致。
            string path = EditorUtility.SaveFilePanelInProject("导出 UGUI Prefab", _session.GetDocumentRootName(),
                "prefab", "仅新建 Prefab；同名时自动使用唯一文件名。", "Assets/PsdUiWorkbench/Exports");
            if (string.IsNullOrEmpty(path)) return;
            try { ExportTextPrefabTo(path, renderingMode); }
            catch (Exception e) { _status.text = "导出失败：" + e.Message; }
        }

        public PsdPrefabExportResult ExportPrefabTo(string assetPath)
        {
            if (!CanExport) throw new InvalidOperationException("当前没有可导出的文档，或编辑器正在导入／编译，请稍后再试。");
            var source = new UnityPsdDocumentSource();
            var loaded = source.Load(_assetGuid);
            if (loaded.Document == null) throw new InvalidOperationException(loaded.Diagnostic);
            var plan = PsdPrefabPlanner.Build(loaded.Document, _session.CaptureVisibility(), _session.CaptureExport(), _session.CaptureNames());
            var result = PsdPrefabExporter.Export(plan, assetPath, source.Resolve);
            _status.text = $"已导出 {result.AssetPath} · {result.ImageCount} 个 Image，{result.NodeCount} 个 PSD 节点。" +
                (string.IsNullOrEmpty(result.Diagnostic) ? "" : "\n" + result.Diagnostic);
            EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(result.AssetPath));
            return result;
        }

        public PsdTextPrefabExportResult ExportTextPrefabTo(string assetPath, PsdTmpRenderingMode renderingMode = PsdTmpRenderingMode.OuterOutline)
        {
            if (!CanExport) throw new InvalidOperationException("当前没有可导出的文档，或编辑器正在导入／编译，请稍后再试。");
            var result = PsdTextPrefabExport.Export(_assetGuid, _session.CaptureVisibility(), assetPath, renderingMode, _session.CaptureExport(), _session.CaptureNames());
            _status.text = $"已导出 {result.AssetPath} · {result.TextLayers} 个文本层／{result.TextComponents} 个 TMP，" +
                $"{result.FontSubstitutedLayers} 层字体替代，{result.SkippedTextLayers} 层跳过，{result.ImageCount} 个 Image。" +
                (string.IsNullOrEmpty(result.Diagnostic) ? "" : "\n" + result.Diagnostic);
            EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(result.AssetPath));
            return result;
        }
    }
}
