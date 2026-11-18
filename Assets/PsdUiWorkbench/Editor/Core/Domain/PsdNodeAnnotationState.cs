using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace PsdUiWorkbench.Core
{
    // 批注只关联稳定图层 ID；暂时从 PSD 消失的节点仍保留记录。
    public sealed class PsdNodeAnnotationState : IEquatable<PsdNodeAnnotationState>
    {
        public const int MaxLength = 2048;
        public IReadOnlyDictionary<int, string> Annotations { get; }

        public PsdNodeAnnotationState(IReadOnlyDictionary<int, string> annotations = null)
        {
            var copy = new Dictionary<int, string>();
            if (annotations != null)
                foreach (var pair in annotations)
                {
                    if (!TryNormalize(pair.Value, out string text))
                        throw new ArgumentException("节点批注不得超过 2048 个 UTF-16 字符。", nameof(annotations));
                    if (text != null) copy.Add(pair.Key, text);
                }
            Annotations = new ReadOnlyDictionary<int, string>(copy);
        }

        public string Get(int id) => Annotations.TryGetValue(id, out string text) ? text : string.Empty;

        public static bool TryNormalize(string text, out string normalized)
        {
            // 纯空白始终表示删除；非空描述原样保留换行、缩进和前后空格。
            normalized = string.IsNullOrWhiteSpace(text) ? null : text;
            return normalized == null || normalized.Length <= MaxLength;
        }

        public bool Equals(PsdNodeAnnotationState other)
        {
            if (other == null || Annotations.Count != other.Annotations.Count) return false;
            foreach (var pair in Annotations)
                if (!other.Annotations.TryGetValue(pair.Key, out string text) ||
                    !string.Equals(pair.Value, text, StringComparison.Ordinal)) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is PsdNodeAnnotationState other && Equals(other);

        public override int GetHashCode()
        {
            int hash = 0;
            foreach (var pair in Annotations)
                hash ^= unchecked(pair.Key * 397) ^ StringComparer.Ordinal.GetHashCode(pair.Value);
            return hash;
        }
    }
}
