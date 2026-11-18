using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEditor;
using UnityEngine;

namespace PsdUiWorkbench.AiNaming
{
    public sealed class PsdAiNamingController : IDisposable
    {
        private readonly PsdSession _session;
        private readonly Func<string, Sprite> _resolveSprite;
        private readonly IPsdAiNamingClient _client;
        private readonly IPsdAiPreviewProvider _previewProvider;
        private readonly List<PsdAiNamingRow> _rows = new List<PsdAiNamingRow>();
        private readonly IReadOnlyList<PsdAiNamingRow> _readOnlyRows;
        private PsdAiNamingSettings _ownedSettings;
        private IEnumerator _operation;
        private IReadOnlyDictionary<int, string> _applyingNames;
        private PsdDocumentSnapshot _applyingDocument;
        private long _version;
        private int _generation;
        private bool _disposed, _advancing, _completed, _clientReleased;
        public event Action Changed;
        public IReadOnlyList<PsdAiNamingRow> Rows => _readOnlyRows;
        public string Status { get; private set; } = "选择节点后开始分析。";
        public string Error { get; private set; } = "";
        public bool IsBusy { get; private set; }
        public bool IsStale { get; private set; }
        public PsdAiNamingSettings Settings { get; private set; }

        public PsdAiNamingController(PsdSession session, Func<string, Sprite> resolveSprite,
            PsdAiNamingSettings settings = null, IPsdAiNamingClient client = null, IPsdAiPreviewProvider previewProvider = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _resolveSprite = resolveSprite ?? (_ => null);
            _client = client ?? new PsdAiNamingClient();
            _previewProvider = previewProvider ?? new PsdAiPreviewProvider();
            _readOnlyRows = _rows.AsReadOnly();
            SetSettings(settings);
            _session.ContextChanged += Invalidate;
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            EditorApplication.quitting += Dispose;
        }
        public void SetSettings(PsdAiNamingSettings settings)
        {
            if (_disposed) return;
            Settings = settings != null ? settings : PsdAiNamingSettings.LoadDefault();
            if (Settings == null)
            {
                if (_ownedSettings == null)
                {
                    _ownedSettings = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
                    _ownedSettings.hideFlags = HideFlags.HideAndDontSave;
                }
                Settings = _ownedSettings;
            }
            Changed?.Invoke();
        }
        public void Start(IReadOnlyList<int> selectedIds, bool includeDescendants)
        {
            if (_disposed) return;
            if (IsBusy)
            {
                Error = "已有命名任务正在进行，请等待完成或显式取消；不会重复发送请求。";
                Changed?.Invoke();
                return;
            }
            StopOperation();
            _rows.Clear(); Error = ""; IsStale = false; _completed = false;
            try
            {
                var document = _session.Document;
                var export = _session.CaptureExport();
                var targets = SelectTargets(document, selectedIds, includeDescendants, export);
                if (targets.Count == 0)
                {
                    Fail("所选范围没有可导出节点，不会发送 AI 请求。");
                    return;
                }
                var settings = Settings.Snapshot();
                string validation = PsdAiModelCatalog.Validate(settings.Provider, settings.Model, settings.ReasoningEffort);
                if (validation != null) { Fail(validation); return; }
                // 真实客户端在任何 Sprite 解析或 GPU 操作之前检查本机凭据；普通替身不需要凭据。
                bool missingCredentials = _client is IPsdAiProviderPreflight providerPreflight
                    ? !providerPreflight.HasCredentialsFor(settings.Provider)
                    : _client is IPsdAiNamingPreflight preflight && !preflight.HasCredentials;
                if (missingCredentials)
                {
                    Fail("请先配置" + PsdAiModelCatalog.ProviderName(settings.Provider) + "本机 API Key 或 " +
                        PsdAiCredentials.EnvironmentVariable(settings.Provider) + "，再生成预览。");
                    return;
                }
                _version = _session.ContextVersion;
                var names = _session.CaptureNames();
                var visibility = _session.CaptureVisibility();
                var annotations = _session.CaptureAnnotations();
                // 导出状态与目标选择必须来自同一快照。
                int generation = _generation;
                _clientReleased = false;
                IsBusy = true;
                Status = "正在按帧准备命名预览……";
                if (_previewProvider is IPsdAiIncrementalPreviewProvider incremental)
                {
                    _operation = PrepareAndAnalyze(incremental, document, targets, names, visibility, export,
                        annotations, settings, generation);
                }
                else
                {
                    // 保留无图形同步替身边界；真实 GPU 提供器只走上面的按帧路径。
                    var previews = _previewProvider.Capture(document, targets.Select(n => n.Id).ToArray(), visibility, _resolveSprite);
                    if (!IsCurrent(generation)) return;
                    var operation = CreateAnalysis(document, targets, names, visibility, export, annotations,
                        settings, previews, generation);
                    if (!IsCurrent(generation))
                    {
                        (operation as IDisposable)?.Dispose();
                        return;
                    }
                    _operation = operation ?? throw new InvalidOperationException();
                }
                EditorApplication.update += Advance;
            }
            catch (PsdAiNamingException)
            {
                // 不显示可注入依赖的异常文本，避免凭据或响应原文泄漏。
                Fail("无法准备命名请求：选择必须为真实节点且不超过 60 个，正文不得超过 16 MiB。");
            }
            catch (Exception) { Fail("无法准备命名请求，请检查文档、预览和命名设置。"); }
            Changed?.Invoke();
        }

        private bool IsCurrent(int generation) => !_disposed && IsBusy && !IsStale &&
            generation == _generation && _session.ContextVersion == _version;

        private IEnumerator PrepareAndAnalyze(IPsdAiIncrementalPreviewProvider provider, PsdDocumentSnapshot document,
            IReadOnlyList<PsdNode> targets, PsdNodeNameState names, PsdVisibilityState visibility, PsdExportState export,
            PsdNodeAnnotationState annotations, PsdAiRequestSettings settings, int generation)
        {
            PsdAiPreviewResult previews = null;
            var ids = targets.Select(n => n.Id).ToArray();
            var capture = provider is IPsdAiExportAwarePreviewProvider aware
                ? aware.CaptureIncrementally(document, ids, visibility, export, _resolveSprite,
                    result => { if (IsCurrent(generation)) previews = result; })
                : provider.CaptureIncrementally(document, ids, visibility, _resolveSprite,
                    result => { if (IsCurrent(generation)) previews = result; });
            if (capture == null) throw new InvalidOperationException();
            try
            {
                while (capture.MoveNext()) yield return null;
            }
            finally { (capture as IDisposable)?.Dispose(); }
            if (!IsCurrent(generation)) yield break;
            if (previews == null) throw new InvalidOperationException();
            var analysis = CreateAnalysis(document, targets, names, visibility, export, annotations, settings, previews, generation);
            if (analysis == null) throw new InvalidOperationException();
            try
            {
                while (IsCurrent(generation) && analysis.MoveNext()) yield return null;
            }
            finally { (analysis as IDisposable)?.Dispose(); }
        }

        private IEnumerator CreateAnalysis(PsdDocumentSnapshot document, IReadOnlyList<PsdNode> targets,
            PsdNodeNameState names, PsdVisibilityState visibility, PsdExportState export, PsdNodeAnnotationState annotations,
            PsdAiRequestSettings settings, PsdAiPreviewResult previews, int generation)
        {
            var hasPreview = new HashSet<int>(previews.PreviewIds);
            foreach (var node in targets)
                _rows.Add(new PsdAiNamingRow(node.Id, node.Name, names.Resolve(node), PathFor(document, node, names), hasPreview.Contains(node.Id)));
            var snapshot = PsdAiContextBuilder.Build(document, names, visibility, export, annotations, targets, hasPreview, previews.Images);
            // 在创建请求之前检查 UTF8 字节限制，不依赖具体 client 是否执行检查。
            PsdAiProtocol.BuildBody(snapshot, settings);
            Status = "正在分析命名……";
            return _client.Analyze(snapshot, settings, suggestions => Complete(generation, suggestions));
        }
        private static IReadOnlyList<PsdNode> SelectTargets(PsdDocumentSnapshot document, IReadOnlyList<int> ids,
            bool descendants, PsdExportState export)
        {
            if (document == null || ids == null || ids.Count == 0) throw new PsdAiNamingException("请先选择真实节点。");
            var selected = new HashSet<int>();
            foreach (int id in ids)
            {
                if (document.Find(id) == null) throw new PsdAiNamingException("不能分析虚拟根或不存在的节点。");
                selected.Add(id);
            }
            var result = new List<PsdNode>();
            foreach (var node in document.Nodes)
            {
                bool included = selected.Contains(node.Id);
                if (!included && descendants)
                    for (var parent = node.ParentId; parent.HasValue; parent = document.Find(parent.Value).ParentId)
                        if (selected.Contains(parent.Value)) { included = true; break; }
                if (included && export.Allows(document, node.Id)) result.Add(node);
                if (result.Count > 60) throw new PsdAiNamingException("最多分析 60 个节点。");
            }
            return result.AsReadOnly();
        }
        internal static string PathFor(PsdDocumentSnapshot document, PsdNode node, PsdNodeNameState names)
        {
            var parts = new List<string>();
            for (var current = node; current != null; current = current.ParentId.HasValue ? document.Find(current.ParentId.Value) : null)
                parts.Add(names.Resolve(current));
            parts.Reverse(); return string.Join("/", parts);
        }
        private void Advance()
        {
            if (!IsBusy || _disposed) return;
            if (_session.ContextVersion != _version) { Invalidate(); return; }
            try
            {
                _advancing = true;
                bool running = _operation.MoveNext();
                _advancing = false;
                if (_completed)
                {
                    StopOperation();
                    Changed?.Invoke();
                }
                else if (!running) Fail("命名服务未返回完整结果。");
            }
            catch (Exception)
            {
                _advancing = false;
                Fail("命名请求失败、超时或响应格式无效。请检查凭据、模型和网络后手动重试。");
            }
        }
        private void Complete(int generation, IReadOnlyList<PsdAiSuggestion> suggestions)
        {
            if (_disposed || !IsBusy || _completed || generation != _generation || IsStale || _session.ContextVersion != _version) return;
            try
            {
                var known = new HashSet<string>(_rows.Select(r => Key(r.LayerId)), StringComparer.Ordinal);
                var byKey = new Dictionary<string, PsdAiSuggestion>(StringComparer.Ordinal);
                if (suggestions == null) throw new FormatException();
                foreach (var item in suggestions)
                {
                    if (item == null || !known.Contains(item.NodeKey) || byKey.ContainsKey(item.NodeKey) || string.IsNullOrWhiteSpace(item.Reason))
                        throw new FormatException();
                    byKey.Add(item.NodeKey, item);
                }
                foreach (var row in _rows) row.Complete(byKey.TryGetValue(Key(row.LayerId), out var item) ? item : null);
                AddCrossBranchWarnings();
                _completed = true;
                Error = "";
                Status = "分析完成，请复核并手动勾选要应用的名称。";
                // 帧推进中只记录完成，由 Advance 释放请求并解除 busy 后再通知界面。
                if (!_advancing)
                {
                    StopOperation();
                    Changed?.Invoke();
                }
            }
            catch (Exception) { Fail("命名结果包含未知、重复节点或缺失字段。"); }
        }
        private void AddCrossBranchWarnings()
        {
            var proposed = _rows.Where(r => r.CanApply).ToDictionary(r => r.LayerId, r => r.SuggestedName);
            foreach (var row in _rows.Where(r => r.CanApply))
            {
                var node = _session.Document.Find(row.LayerId);
                if (_session.Document.Nodes.Any(n => n.Id != row.LayerId && n.ParentId != node.ParentId &&
                    string.Equals(proposed.TryGetValue(n.Id, out var name) ? name : _session.GetNodeName(n.Id), row.SuggestedName, StringComparison.Ordinal)))
                {
                    row.NeedsReview = true;
                    if (!row.Reason.Contains("跨分支存在同名"))
                        row.Reason += " 跨分支存在同名，请复核用途。";
                }
            }
        }
        internal static string Key(int id) => "n" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public void StartWholeDocument()
        {
            var ids = _session.Document?.Nodes.Select(n => n.Id).ToArray();
            Start(ids, false);
        }

        public void Discard()
        {
            if (_disposed || IsBusy || _rows.Count == 0) return;
            StopOperation();
            _rows.Clear();
            IsStale = false;
            _completed = false;
            Error = "";
            Status = "选择节点后开始分析。";
            Changed?.Invoke();
        }

        public void SetSelection(bool selected)
        {
            if (_disposed) return;
            foreach (var row in _rows) row.Selected = selected && row.CanApply && !IsStale && !IsBusy;
            Changed?.Invoke();
        }
        public bool ApplySelected()
        {
            if (_applyingNames != null) return false;
            if (_disposed || IsBusy || IsStale || _session.ContextVersion != _version)
            {
                if (!_disposed && _session.ContextVersion != _version) Invalidate();
                return false;
            }
            var selected = _rows.Where(r => r.Selected).ToArray();
            if (selected.Length == 0) { Error = "请先勾选要应用的建议。"; Changed?.Invoke(); return false; }
            if (selected.Any(r => !r.CanApply || _session.Document?.Find(r.LayerId) == null))
            {
                Error = "所选建议缺失或名称不符合无组件前缀的英文 PascalCase。"; Changed?.Invoke(); return false;
            }
            AddCrossBranchWarnings();
            var batch = selected.ToDictionary(r => r.LayerId, r => r.SuggestedName);
            bool applied;
            _applyingNames = batch;
            _applyingDocument = _session.Document;
            try { applied = _session.SetNodeNames(batch); }
            catch (Exception) { applied = false; }
            finally
            {
                _applyingNames = null;
                _applyingDocument = null;
            }
            if (!applied) Error = "整批应用失败：名称未变化、存在同级重名冲突或配置保存失败，未应用部分建议。";
            else
            {
                _rows.Clear();
                IsStale = false;
                Error = "";
                Status = "所选名称已保存。";
                _version = _session.ContextVersion;
                _completed = false;
            }
            Changed?.Invoke(); return applied;
        }
        public void Cancel()
        {
            if (_disposed) return;
            StopOperation();
            foreach (var row in _rows) row.Invalidate();
            Status = "已取消命名分析。"; Error = "";
            Changed?.Invoke();
        }
        public void Invalidate()
        {
            if (_disposed) return;
            // 只延后本次命名保存的同步通知；版本、文档或名称不匹配的外部变更仍正常失效。
            if (_applyingNames != null && ReferenceEquals(_session.Document, _applyingDocument) &&
                _session.ContextVersion == _version + 1 &&
                _applyingNames.All(pair => string.Equals(_session.GetNodeName(pair.Key), pair.Value, StringComparison.Ordinal)))
                return;
            bool hadAnalysis = IsBusy || _rows.Count > 0;
            StopOperation();
            foreach (var row in _rows) row.Invalidate();
            IsStale = hadAnalysis;
            Error = "";
            Status = hadAnalysis ? "文档上下文已变化，请重新分析。" : "选择节点后开始分析。";
            Changed?.Invoke();
        }
        private void Fail(string error)
        {
            StopOperation();
            foreach (var row in _rows) row.Invalidate();
            Error = error; Status = "命名分析未完成。"; Changed?.Invoke();
        }
        private void StopOperation()
        {
            _generation++;
            EditorApplication.update -= Advance;
            var operation = _operation; _operation = null;
            bool active = IsBusy; IsBusy = false;
            try { (operation as IDisposable)?.Dispose(); }
            catch (Exception) { }
            if (active) ReleaseClient();
        }
        private void ReleaseClient()
        {
            if (_clientReleased) return;
            _clientReleased = true;
            try { _client.Dispose(); }
            catch (Exception) { }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopOperation();
            ReleaseClient();
            foreach (var row in _rows) row.Invalidate();
            _session.ContextChanged -= Invalidate;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            EditorApplication.quitting -= Dispose;
            if (_ownedSettings != null) UnityEngine.Object.DestroyImmediate(_ownedSettings);
            _ownedSettings = null;
            Changed = null;
        }
    }
}
