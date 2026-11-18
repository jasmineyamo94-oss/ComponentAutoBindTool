using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEngine;

namespace PsdUiWorkbench.AiNaming
{
    public enum PsdAiProvider
    {
        [InspectorName("DeepSeek")] DeepSeek = 0,
        [InspectorName("智谱")] Zhipu = 1
    }

    public enum PsdAiReasoningEffort
    {
        [InspectorName("关闭")] None = 0,
        [InspectorName("低")] Low = 1,
        [InspectorName("高")] High = 2,
        [InspectorName("最高")] Max = 3
    }

    public sealed class PsdAiNamingException : InvalidOperationException
    {
        public PsdAiNamingException(string message) : base(message) { }
    }

    // 在启动时复制设置，禁止将凭据、窗口或 Unity 图形对象放入请求快照。
    public sealed class PsdAiRequestSettings
    {
        public string Model { get; }
        public PsdAiProvider Provider { get; }
        public PsdAiReasoningEffort ReasoningEffort { get; }
        public int TimeoutSeconds { get; }
        public string Rules { get; }
        public PsdAiRequestSettings(string model, PsdAiReasoningEffort effort, int timeoutSeconds, string rules)
            : this(PsdAiProvider.DeepSeek, model, effort, timeoutSeconds, rules) { }
        public PsdAiRequestSettings(PsdAiProvider provider, string model, PsdAiReasoningEffort effort, int timeoutSeconds, string rules)
        {
            Provider = provider; Model = model?.Trim() ?? ""; ReasoningEffort = effort;
            TimeoutSeconds = Math.Max(10, Math.Min(600, timeoutSeconds)); Rules = rules ?? "";
        }
    }

    public sealed class PsdAiImage
    {
        public string Label { get; }
        public string Base64Png { get; }
        public PsdAiImage(string label, byte[] png)
        {
            Label = label ?? "";
            Base64Png = Convert.ToBase64String(png ?? throw new ArgumentNullException(nameof(png)));
        }
    }

    public sealed class PsdAiNamingSnapshot
    {
        public string ContextJson { get; }
        public IReadOnlyList<string> NodeKeys { get; }
        public IReadOnlyList<PsdAiImage> Images { get; }
        public PsdAiNamingSnapshot(string contextJson, IEnumerable<string> nodeKeys, IEnumerable<PsdAiImage> images)
        {
            ContextJson = contextJson ?? "{}";
            NodeKeys = Array.AsReadOnly(nodeKeys.ToArray()); Images = Array.AsReadOnly(images.ToArray());
        }
    }

    public sealed class PsdAiSuggestion
    {
        public string NodeKey { get; }
        public string SuggestedName { get; }
        public string Reason { get; }
        public bool NeedsReview { get; }
        public PsdAiSuggestion(string nodeKey, string suggestedName, string reason, bool needsReview)
        {
            NodeKey = nodeKey; SuggestedName = suggestedName; Reason = reason; NeedsReview = needsReview;
        }
    }

    public interface IPsdAiNamingClient : IDisposable
    {
        IEnumerator Analyze(PsdAiNamingSnapshot snapshot, PsdAiRequestSettings settings, Action<IReadOnlyList<PsdAiSuggestion>> completed);
    }

    // 可选预检只暴露是否可用，不将凭据放入配置或请求快照；普通测试替身无需实现。
    public interface IPsdAiNamingPreflight
    {
        bool HasCredentials { get; }
    }

    public interface IPsdAiProviderPreflight : IPsdAiNamingPreflight
    {
        bool HasCredentialsFor(PsdAiProvider provider);
    }

    public sealed class PsdAiPreviewResult
    {
        public IReadOnlyList<PsdAiImage> Images { get; }
        public IReadOnlyCollection<int> PreviewIds { get; }
        public PsdAiPreviewResult(IEnumerable<PsdAiImage> images, IEnumerable<int> previewIds)
        {
            Images = Array.AsReadOnly(images.ToArray()); PreviewIds = Array.AsReadOnly(previewIds.Distinct().ToArray());
        }
    }

    // 提供器仅在启动时接收图形依赖，返回值为纯数据，不保留 Sprite。
    public interface IPsdAiPreviewProvider
    {
        PsdAiPreviewResult Capture(PsdDocumentSnapshot document, IReadOnlyList<int> targets, PsdVisibilityState visibility,
            Func<string, Sprite> resolveSprite);
    }

    // 每次 MoveNext 最多生成一张图片；Dispose 必须释放当前图形资源。
    public interface IPsdAiIncrementalPreviewProvider : IPsdAiPreviewProvider
    {
        IEnumerator CaptureIncrementally(PsdDocumentSnapshot document, IReadOnlyList<int> targets,
            PsdVisibilityState visibility, Func<string, Sprite> resolveSprite, Action<PsdAiPreviewResult> completed);
    }
    // 真实预览按有效导出范围过滤图片；普通替身仍可使用基础接口。
    public interface IPsdAiExportAwarePreviewProvider : IPsdAiIncrementalPreviewProvider
    {
        IEnumerator CaptureIncrementally(PsdDocumentSnapshot document, IReadOnlyList<int> targets,
            PsdVisibilityState visibility, PsdExportState export, Func<string, Sprite> resolveSprite,
            Action<PsdAiPreviewResult> completed);
    }


    public sealed class PsdAiNamingRow
    {
        private bool _available;
        public int LayerId { get; }
        public string OriginalName { get; }
        public string CurrentName { get; }
        public string Path { get; }
        public string SuggestedName { get; set; }
        public string Reason { get; internal set; }
        public bool NeedsReview { get; internal set; }
        public bool Selected { get; set; }
        public bool CanApply => _available && PsdAiProtocol.IsValidName(SuggestedName);
        public bool HasPreview { get; }
        internal PsdAiNamingRow(int id, string original, string current, string path, bool hasPreview)
        {
            LayerId = id; OriginalName = original; CurrentName = current; Path = path; HasPreview = hasPreview;
            SuggestedName = ""; Reason = "等待命名建议。"; NeedsReview = !hasPreview;
        }
        internal void Complete(PsdAiSuggestion suggestion)
        {
            _available = suggestion != null;
            SuggestedName = suggestion?.SuggestedName ?? "";
            Reason = suggestion?.Reason ?? "模型未返回该节点的建议。";
            NeedsReview = !HasPreview || suggestion == null || suggestion.NeedsReview;
        }
        internal void Invalidate() { _available = false; Selected = false; }
    }
}
