using System;
using System.Collections.Generic;
using System.Linq;

namespace PsdUiWorkbench.Core
{
    public interface IPsdDocumentSource
    {
        PsdLoadResult Load(string assetGuid);
    }
    public sealed class PsdLoadResult
    {
        public PsdDocumentSnapshot Document { get; }
        public string Diagnostic { get; }
        public PsdLoadResult(PsdDocumentSnapshot document, string diagnostic)
        {
            Document = document; Diagnostic = diagnostic;
        }
    }
    public sealed class PsdSession
    {
        private readonly IPsdVisibilityRepository _visibilityRepository;
        private readonly IPsdExportRepository _exportRepository;
        private readonly IPsdNodeNameRepository _nameRepository;
        private PsdNodeNameState _names = new PsdNodeNameState();
        private readonly IPsdNodeAnnotationRepository _annotationRepository;
        private PsdNodeAnnotationState _annotations = new PsdNodeAnnotationState();
        private bool _refreshing;
        private readonly HashSet<int> _excluded = new HashSet<int>();
        private readonly HashSet<int> _exportable = new HashSet<int>();
        private readonly HashSet<int> _hidden = new HashSet<int>();
        private readonly HashSet<int> _controllable = new HashSet<int>();
        private readonly HashSet<int> _visible = new HashSet<int>();
        private IReadOnlyList<PsdNode> _visibleDrawNodes = new PsdNode[0];
        public PsdDocumentSnapshot Document { get; private set; }
        public int? SelectedId { get; private set; }
        public IReadOnlyList<int> SelectedIds { get; private set; } = new int[0];
        public int ExportableDrawNodeCount { get; private set; }
        public bool DocumentVisible { get; private set; } = true;
        public IReadOnlyList<PsdNode> VisibleDrawNodes => _visibleDrawNodes;
        public long ContextVersion { get; private set; }
        public event Action ContextChanged;

        public PsdSession(IPsdVisibilityRepository visibilityRepository = null, IPsdExportRepository exportRepository = null,
            IPsdNodeNameRepository nameRepository = null, IPsdNodeAnnotationRepository annotationRepository = null)
        {
            _visibilityRepository = visibilityRepository;
            _exportRepository = exportRepository ?? visibilityRepository as IPsdExportRepository;
            _nameRepository = nameRepository;
            _annotationRepository = annotationRepository;
        }
        public PsdLoadResult Load(IPsdDocumentSource source, string assetGuid)
        {
            var result = source.Load(assetGuid);
            Refresh(result.Document);
            return result;
        }
        public void Refresh(PsdDocumentSnapshot document)
        {
            bool same = Document != null && document != null && Document.AssetGuid == document.AssetGuid;
            // 先读取完整批注快照；读取失败时，文档与已有批注都不切换。
            var annotations = document != null
                ? (_annotationRepository != null ? _annotationRepository.Load(document.AssetGuid) :
                    same ? _annotations : null) ?? new PsdNodeAnnotationState()
                : new PsdNodeAnnotationState();
            Document = document;
            _annotations = annotations;
            SelectMany(same ? SelectedIds : null, same ? SelectedId : null);
            _controllable.Clear();
            if (document != null)
            {
                foreach (var node in document.DrawNodes)
                    for (PsdNode current = node; current != null; current = current.ParentId.HasValue ? document.Find(current.ParentId.Value) : null)
                        _controllable.Add(current.Id);
            }
            _refreshing = true;
            try
            {
                if (!same)
                {
                    RestoreVisibility();
                    RestoreNames();
                }
                else
                {
                    RebuildVisibility();
                    RebuildExportability();
                }
            }
            finally { _refreshing = false; }
            NotifyContextChanged();
        }
        public void RestoreVisibility()
        {
            var oldHidden = new HashSet<int>(_hidden);
            var oldExcluded = new HashSet<int>(_excluded);
            bool oldDocumentVisible = DocumentVisible;
            var state = Document != null ? _visibilityRepository?.Load(Document.AssetGuid) : null;
            _hidden.Clear();
            if (state != null) _hidden.UnionWith(state.HiddenLayerIds);
            DocumentVisible = state?.DocumentVisible ?? true;
            var export = Document != null ? _exportRepository?.LoadExport(Document.AssetGuid) : null;
            _excluded.Clear();
            if (export != null) _excluded.UnionWith(export.ExcludedLayerIds);
            RebuildExportability();
            RebuildVisibility();
            if (oldDocumentVisible != DocumentVisible || !oldHidden.SetEquals(_hidden) || !oldExcluded.SetEquals(_excluded))
                NotifyContextChanged();
        }
        public PsdNodeNameState CaptureNames() => _names;
        public string GetDocumentRootName() => _names.RootName;
        public bool SetDocumentRootName(string name)
        {
            if (Document == null || !PsdNodeNaming.IsValidRoot(name)) return false;
            return PersistNames(new PsdNodeNameState(_names.Overrides,
                string.Equals(name, PsdNodeNameState.DefaultRootName, StringComparison.Ordinal) ? null : name));
        }
        public string GetNodeName(int id)
        {
            var node = Document?.Find(id);
            return node != null ? _names.Resolve(node) : null;
        }
        public bool SetNodeNames(IReadOnlyDictionary<int, string> names)
        {
            return PsdNodeNaming.TrySet(Document, _names, names, out var candidate) && PersistNames(candidate);
        }
        public bool ResetNodeNames(IEnumerable<int> ids)
        {
            return PsdNodeNaming.TryReset(Document, _names, ids, out var candidate) && PersistNames(candidate);
        }
        public void RestoreNames()
        {
            var state = (Document != null ? _nameRepository?.Load(Document.AssetGuid) : null) ?? new PsdNodeNameState();
            if (_names.Equals(state)) return;
            _names = state;
            NotifyContextChanged();
        }
        private bool PersistNames(PsdNodeNameState candidate)
        {
            if (_names.Equals(candidate)) return false;
            // 外部存储是事务边界：保存成功前不公布任何部分名称或上下文变化。
            try { _nameRepository?.Save(Document.AssetGuid, candidate); }
            catch (Exception) { return false; }
            _names = candidate;
            NotifyContextChanged();
            return true;
        }
        public PsdNodeAnnotationState CaptureAnnotations() => _annotations;
        public string GetNodeAnnotation(int id)
            => Document?.Find(id) != null ? _annotations.Get(id) : string.Empty;
        public bool SetNodeAnnotation(int id, string text)
        {
            if (Document?.Find(id) == null || !PsdNodeAnnotationState.TryNormalize(text, out string normalized))
                return false;
            var values = _annotations.Annotations.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (normalized == null) values.Remove(id);
            else values[id] = normalized;
            var candidate = new PsdNodeAnnotationState(values);
            if (_annotations.Equals(candidate)) return false;
            // 保存成功前，内存快照、上下文版本和事件均保持原样。
            try { _annotationRepository?.Save(Document.AssetGuid, candidate); }
            catch (Exception) { return false; }
            _annotations = candidate;
            NotifyContextChanged();
            return true;
        }
        public void RestoreAnnotations()
        {
            var state = Document == null ? new PsdNodeAnnotationState() :
                (_annotationRepository != null ? _annotationRepository.Load(Document.AssetGuid) : _annotations)
                    ?? new PsdNodeAnnotationState();
            if (_annotations.Equals(state)) return;
            _annotations = state;
            NotifyContextChanged();
        }
        private void NotifyContextChanged()
        {
            if (_refreshing) return;
            ContextVersion++;
            ContextChanged?.Invoke();
        }
        public bool CanToggle(int id) => _controllable.Contains(id);
        public bool IsNodeEnabled(int id) => !_hidden.Contains(id);
        public PsdVisibilityState CaptureVisibility() => new PsdVisibilityState(DocumentVisible, _hidden);
        public bool IsEffectivelyVisible(int id) => _visible.Contains(id);
        public bool IsHiddenByAncestor(int id)
        {
            if (!DocumentVisible) return true;
            var node = Document?.Find(id);
            for (var p = node?.ParentId; p.HasValue; p = Document.Find(p.Value).ParentId)
                if (_hidden.Contains(p.Value)) return true;
            return false;
        }
        public int? GetMergedAncestor(int id)
        {
            var node = Document?.Find(id);
            for (var p = node?.ParentId; p.HasValue; p = Document.Find(p.Value).ParentId)
            {
                var ancestor = Document.Find(p.Value);
                if (ancestor.Flatten && ancestor.IsImported && !string.IsNullOrEmpty(ancestor.SpriteId)) return p;
            }
            return null;
        }
        public void SetNodeVisible(int id, bool visible) => SetNodesVisible(new[] { id }, visible);
        public void SetNodesVisible(IEnumerable<int> ids, bool visible)
        {
            bool changed = false;
            foreach (int id in (ids ?? Enumerable.Empty<int>()).Distinct().Where(CanToggle))
                changed |= visible ? _hidden.Remove(id) : _hidden.Add(id);
            if (changed) PersistVisibility();
        }

        public bool CanToggleExport(int id) => _controllable.Contains(id);
        public bool IsNodeExportEnabled(int id) => !_excluded.Contains(id);
        public bool IsEffectivelyExported(int id) => _exportable.Contains(id);
        public PsdExportState CaptureExport() => new PsdExportState(_excluded);
        public int? GetExportExcludedAncestor(int id)
        {
            var node = Document?.Find(id);
            for (var parent = node?.ParentId; parent.HasValue; parent = Document.Find(parent.Value).ParentId)
                if (_excluded.Contains(parent.Value)) return parent;
            return null;
        }
        public void SetNodeExportable(int id, bool exportable) => SetNodesExportable(new[] { id }, exportable);
        public void SetNodesExportable(IEnumerable<int> ids, bool exportable)
        {
            bool changed = false;
            foreach (int id in (ids ?? Enumerable.Empty<int>()).Distinct().Where(CanToggleExport))
                changed |= exportable ? _excluded.Remove(id) : _excluded.Add(id);
            if (!changed) return;
            _exportRepository?.SaveExport(Document.AssetGuid, CaptureExport());
            RebuildExportability();
            NotifyContextChanged();
        }
        private void RebuildExportability()
        {
            _exportable.Clear();
            var state = CaptureExport();
            foreach (int id in _controllable)
                if (state.Allows(Document, id)) _exportable.Add(id);
            ExportableDrawNodeCount = Document?.DrawNodes.Count(n => _exportable.Contains(n.Id)) ?? 0;
        }
        public void SetDocumentVisible(bool visible)
        {
            if (Document == null || DocumentVisible == visible) return;
            DocumentVisible = visible;
            PersistVisibility();
        }
        public void ResetVisibility()
        {
            if (Document == null || (DocumentVisible && _hidden.Count == 0)) return;
            DocumentVisible = true;
            _hidden.Clear();
            PersistVisibility();
        }
        private void PersistVisibility()
        {
            _visibilityRepository?.Save(Document.AssetGuid, new PsdVisibilityState(DocumentVisible, _hidden));
            RebuildVisibility();
            NotifyContextChanged();
        }
        private void RebuildVisibility()
        {
            _visible.Clear();
            foreach (int id in _controllable)
                if (IsNodeEnabled(id) && !IsHiddenByAncestor(id)) _visible.Add(id);
            _visibleDrawNodes = Document == null ? new PsdNode[0] : Document.DrawNodes.Where(n => _visible.Contains(n.Id)).ToArray();
        }
        public PixelRect GetVisibleBounds(int id)
        {
            if (!IsEffectivelyVisible(id)) return default;
            var rect = default(PixelRect);
            foreach (var node in VisibleDrawNodes)
            {
                for (PsdNode current = node; current != null; current = current.ParentId.HasValue ? Document.Find(current.ParentId.Value) : null)
                {
                    if (current.Id != id) continue;
                    rect = PixelRect.Union(rect, node.Bounds);
                    break;
                }
            }
            return rect;
        }
        public void Select(int? id) => SelectMany(id.HasValue ? new[] { id.Value } : null, id);
        public void SelectMany(IEnumerable<int> ids, int? primaryId = null)
        {
            var selected = (ids ?? Enumerable.Empty<int>()).Where(id => Document?.Find(id) != null).Distinct().ToArray();
            SelectedIds = System.Array.AsReadOnly(selected);
            SelectedId = primaryId.HasValue && selected.Contains(primaryId.Value) ? primaryId :
                selected.Length > 0 ? (int?)selected[0] : null;
        }
        public int? HitTest(float x, float y)
        {
            if (Document == null) return null;
            for (int i = VisibleDrawNodes.Count - 1; i >= 0; i--)
                if (VisibleDrawNodes[i].Bounds.Contains(x, y)) return VisibleDrawNodes[i].Id;
            return null;
        }
    }
}
