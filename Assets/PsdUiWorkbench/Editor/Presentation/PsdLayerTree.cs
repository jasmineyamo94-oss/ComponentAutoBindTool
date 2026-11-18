using System;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEngine;

namespace PsdUiWorkbench.Presentation
{
    [UxmlElement]
    public sealed partial class PsdLayerTree : MultiColumnTreeView
    {
        private PsdSession _session;
        private string _documentName;
        private int _documentRootId;
        public int DocumentRootId => _documentRootId;
        public event Action<int?, bool> VisibilityChanged;
        public event Action<IReadOnlyList<int>, bool> NodesVisibilityChanged;
        public event Action<IReadOnlyList<int>, bool> NodesExportChanged;
        public event Action<IReadOnlyList<int>, bool> AiNamingRequested;
        public event Action<IReadOnlyList<int>> NamesResetRequested;
        public event Action<IReadOnlyList<int>> RenameRequested;

        public PsdLayerTree()
        {
            showAlternatingRowBackgrounds = AlternatingRowBackground.All;
            showBorder = true;
            fixedItemHeight = 22;
            selectionType = SelectionType.Multiple;
            columns.Add(new Column
            {
                name = "layerName", title = "Layers", stretchable = true, minWidth = 160,
                makeCell = () =>
                {
                    var row = new VisualElement();
                    row.AddToClassList("layer-name-cell");
                    row.Add(CreateVisibilityToggle());
                    var icon = new Image { name = "icon", pickingMode = PickingMode.Ignore };
                    icon.AddToClassList("layer-folder-icon");
                    row.Add(icon);
                    var kind = new Label { name = "kind", pickingMode = PickingMode.Ignore };
                    kind.AddToClassList("layer-kind-icon");
                    row.Add(kind);
                    row.Add(new Label { name = "name", pickingMode = PickingMode.Ignore });
                    var badge = new Label("不导出") { name = "exportBadge" };
                    badge.AddToClassList("layer-export-badge");
                    badge.style.display = DisplayStyle.None;
                    row.Add(badge);
                    var annotationBadge = new Label("注") { name = "annotationBadge", pickingMode = PickingMode.Ignore };
                    annotationBadge.AddToClassList("layer-annotation-badge");
                    row.Add(annotationBadge);
                    row.RegisterCallback<PointerDownEvent>(evt =>
                    {
                        if (evt.button != 1 || !(row.userData is int id)) return;
                        Focus();
                        if (id == _documentRootId || !selectedIds.Contains(id)) SetSelectionById(new[] { id });
                        evt.StopPropagation();
                    }, TrickleDown.TrickleDown);
                    row.AddManipulator(new ContextualMenuManipulator(evt =>
                    {
                        if (!(row.userData is int id)) return;
                        PopulateNodeMenu(evt.menu, id);
                        evt.StopPropagation();
                    }));
                    return row;
                },
                bindCell = (element, index) =>
                {
                    int id = GetIdForIndex(index);
                    var node = GetItemDataForIndex<PsdNode>(index);
                    var toggle = element.Q<Toggle>("visibility");
                    element.userData = id;
                    toggle.userData = id;
                    toggle.showMixedValue = false;
                    toggle.SetValueWithoutNotify(id == _documentRootId ? _session.DocumentVisible : _session.IsNodeEnabled(id));
                    toggle.SetEnabled(id == _documentRootId || _session.CanToggle(id));
                    toggle.tooltip = Describe(id);
                    element.Q<Label>("name").text = node != null ? _session.GetNodeName(id) : _session.GetDocumentRootName();
                    var icon = element.Q<Image>("icon");
                    icon.image = node?.IsGroup == true ? EditorGUIUtility.IconContent("Folder Icon").image : null;
                    icon.style.display = node?.IsGroup == true ? DisplayStyle.Flex : DisplayStyle.None;
                    var kind = element.Q<Label>("kind");
                    kind.text = node?.SourceKind == PsdLayerKind.Text ? "T" : node?.SourceKind == PsdLayerKind.NonText ? "▧" : "?";
                    kind.style.display = node == null || node.IsGroup ? DisplayStyle.None : DisplayStyle.Flex;
                    bool visible = node == null ? _session.DocumentVisible : _session.IsEffectivelyVisible(id);
                    element.EnableInClassList("layer-hidden", !visible);
                    element.tooltip = (node == null ? _session.GetDocumentRootName() + "\nPSD 文件：" + _documentName :
                        $"{_session.GetNodeName(id)}\nPSD 原名：{node.Name}\n图层 ID：{id}\n源类型：{KindName(node.SourceKind)}") + "\n" + Describe(id);
                    BindExportBadge(element.Q<Label>("exportBadge"), node);
                    BindAnnotationBadge(element.Q<Label>("annotationBadge"), node);
                },
                unbindCell = (element, _) =>
                {
                    element.userData = null;
                    element.Q<Toggle>("visibility").userData = null;
                    BindExportBadge(element.Q<Label>("exportBadge"), null);
                    BindAnnotationBadge(element.Q<Label>("annotationBadge"), null);
                    element.RemoveFromClassList("layer-hidden");
                    element.tooltip = null;
                }
            });
            columns.primaryColumnName = "layerName";
            RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
        }

        private void BindExportBadge(Label badge, PsdNode node)
        {
            bool own = node != null && !_session.IsNodeExportEnabled(node.Id);
            var ancestor = node != null ? _session.GetExportExcludedAncestor(node.Id) : null;
            badge.style.display = own || ancestor.HasValue ? DisplayStyle.Flex : DisplayStyle.None;
            badge.EnableInClassList("layer-export-inherited", !own && ancestor.HasValue);
            badge.tooltip = own ? "自身设为不导出；不影响工作台预览和激活状态。" : ancestor.HasValue ?
                "受上级分组排除：" + _session.GetNodeName(ancestor.Value) + "；保留自身导出设置。" : null;
        }
        private void BindAnnotationBadge(Label badge, PsdNode node)
        {
            bool hasDescription = node != null && !string.IsNullOrEmpty(_session.GetNodeAnnotation(node.Id));
            badge.EnableInClassList("layer-annotation-hidden", !hasDescription);
            badge.tooltip = hasDescription ? "此节点有描述；选中节点可在 AI 命名侧栏查看和修改。" : null;
        }


        private void PopulateNodeMenu(DropdownMenu menu, int id)
        {
            if (_session?.Document == null) return;
            if (id == _documentRootId)
            {
                menu.AppendAction("设为激活", _ => VisibilityChanged?.Invoke(null, true),
                    _session.DocumentVisible ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
                menu.AppendAction("设为不激活", _ => VisibilityChanged?.Invoke(null, false),
                    !_session.DocumentVisible ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
                menu.AppendAction("重命名根节点…", _ => RenameRequested?.Invoke(new[] { _documentRootId }),
                    RenameRequested != null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                menu.AppendAction("AI 命名/分析所选子树", _ => AiNamingRequested?.Invoke(new[] { _documentRootId }, true),
                    AiNamingRequested != null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                menu.AppendSeparator();
                menu.AppendAction("文档根不参与图层导出设置", _ => { }, DropdownMenuAction.Status.Disabled);
                return;
            }
            if (!selectedIds.Contains(id)) SetSelectionById(new[] { id });
            var selected = selectedIds.Where(candidate => _session.Document.Find(candidate) != null).ToArray();
            var visibleIds = selected.Where(_session.CanToggle).ToArray();
            var exportIds = selected.Where(_session.CanToggleExport).ToArray();
            menu.AppendAction("设为激活", _ => NodesVisibilityChanged?.Invoke(visibleIds, true),
                visibleIds.Any(candidate => !_session.IsNodeEnabled(candidate)) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendAction("设为不激活", _ => NodesVisibilityChanged?.Invoke(visibleIds, false),
                visibleIds.Any(_session.IsNodeEnabled) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendSeparator();
            menu.AppendAction("设为导出", _ => NodesExportChanged?.Invoke(exportIds, true),
                exportIds.Any(candidate => !_session.IsNodeExportEnabled(candidate)) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendAction("设为不导出", _ => NodesExportChanged?.Invoke(exportIds, false),
                exportIds.Any(_session.IsNodeExportEnabled) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendSeparator();
            menu.AppendAction(selected.Length > 1 ? "批量重命名…" : "重命名…", _ => RenameRequested?.Invoke(selected),
                RenameRequested != null && selected.Length > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendAction("AI 命名/分析所选节点", _ => AiNamingRequested?.Invoke(selected, false),
                AiNamingRequested != null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendAction("AI 命名/分析所选子树", _ => AiNamingRequested?.Invoke(selected, true),
                AiNamingRequested != null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            var names = _session.CaptureNames();
            menu.AppendAction("恢复 PSD 原名", _ => NamesResetRequested?.Invoke(selected),
                NamesResetRequested != null && selected.Any(candidate => names.Overrides.ContainsKey(candidate)) ?
                    DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            if (exportIds.Length == selected.Length) return;
            menu.AppendSeparator();
            string reason = exportIds.Length > 0 ? $"仅处理 {exportIds.Length} 个有独立导出内容的节点" :
                _session.GetMergedAncestor(id).HasValue ? "已合并到组，请操作对应分组" : "没有独立导入内容，请先在 Importer 中设置";
            menu.AppendAction(reason, _ => { }, DropdownMenuAction.Status.Disabled);
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.F2 || evt.altKey || evt.ctrlKey || evt.commandKey || evt.shiftKey ||
                RenameRequested == null || _session?.Document == null) return;
            for (var target = evt.target as VisualElement; target != null && target != this; target = target.parent)
                if (target is TextField || target is TextElement text && text.selection.isSelectable) return;
            var selected = selectedIds.ToArray();
            if (selected.Length != 1 || selected[0] != _documentRootId &&
                _session.Document.Find(selected[0]) == null) return;
            RenameRequested.Invoke(selected);
            evt.StopPropagation();
            evt.PreventDefault();
        }

        private static string KindName(PsdLayerKind kind) => kind == PsdLayerKind.Text ? "文本（预览仍为 Sprite）" :
            kind == PsdLayerKind.Group ? "分组" : kind == PsdLayerKind.NonText ? "非文本（图像、形状等未细分）" : "未知，请查看窗口状态提示";

        private Toggle CreateVisibilityToggle()
        {
            var toggle = new Toggle { name = "visibility", focusable = false };
            toggle.AddToClassList("layer-visibility");
            toggle.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            toggle.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (!(toggle.userData is int id)) return;
                VisibilityChanged?.Invoke(id == _documentRootId ? (int?)null : id, evt.newValue);
                evt.StopPropagation();
            });
            return toggle;
        }

        private string Describe(int id)
        {
            if (id == _documentRootId) return "工作台文档显示开关；保留所有子节点自己的勾选。";
            if (!_session.CanToggle(id))
            {
                var merged = _session.GetMergedAncestor(id);
                return merged.HasValue ? "已合并到组：" + _session.GetNodeName(merged.Value) + "；没有独立 Sprite。" : "未导入：没有可显示的独立 Sprite。";
            }
            return _session.IsHiddenByAncestor(id) ? "受父级隐藏；仍可修改自身开关，父级开启后生效。" : "工作台独立显示开关；不修改官方导入设置。";
        }

        public void SetDocument(PsdSession session, string documentName)
        {
            _session = session;
            _documentName = documentName;
            var document = session.Document;
            if (document == null)
            {
                SetRootItems(new List<TreeViewItemData<PsdNode>>());
                Rebuild();
                return;
            }
            var children = document.Nodes.ToLookup(node => node.ParentId);
            List<TreeViewItemData<PsdNode>> Build(int? parent)
            {
                return children[parent].Select(node => new TreeViewItemData<PsdNode>(node.Id, node, Build(node.Id))).ToList();
            }
            _documentRootId = int.MinValue;
            while (document.Find(_documentRootId) != null) _documentRootId++;
            SetRootItems(new List<TreeViewItemData<PsdNode>>
            {
                new TreeViewItemData<PsdNode>(_documentRootId, null, Build(null))
            });
            Rebuild();
            ExpandItem(_documentRootId);
        }
    }
}
