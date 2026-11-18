using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace PsdUiWorkbench.Core
{
    // 名称覆盖属于工作台配置，不修改 PSD 源节点；消失的稳定 ID 也保留在快照中。
    public sealed class PsdNodeNameState : IEquatable<PsdNodeNameState>
    {
        public const string DefaultRootName = "UIRoot";
        public string RootNameOverride { get; }
        public string RootName => RootNameOverride ?? DefaultRootName;
        public IReadOnlyDictionary<int, string> Overrides { get; }

        public PsdNodeNameState(IReadOnlyDictionary<int, string> overrides = null, string rootNameOverride = null)
        {
            RootNameOverride = string.IsNullOrEmpty(rootNameOverride) ? null : rootNameOverride;
            var copy = new Dictionary<int, string>();
            if (overrides != null)
                foreach (var pair in overrides) copy.Add(pair.Key, pair.Value);
            Overrides = new ReadOnlyDictionary<int, string>(copy);
        }

        public string Resolve(PsdNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            return Overrides.TryGetValue(node.Id, out string name) ? name : node.Name;
        }

        public bool Equals(PsdNodeNameState other)
        {
            if (other == null || !string.Equals(RootName, other.RootName, StringComparison.Ordinal) ||
                Overrides.Count != other.Overrides.Count) return false;
            foreach (var pair in Overrides)
                if (!other.Overrides.TryGetValue(pair.Key, out string value) ||
                    !string.Equals(pair.Value, value, StringComparison.Ordinal)) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is PsdNodeNameState other && Equals(other);

        public override int GetHashCode()
        {
            int hash = StringComparer.Ordinal.GetHashCode(RootName);
            foreach (var pair in Overrides)
                hash ^= unchecked(pair.Key * 397) ^ (pair.Value == null ? 0 : StringComparer.Ordinal.GetHashCode(pair.Value));
            return hash;
        }
    }
}
