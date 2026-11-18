using System;
using System.Collections.Generic;
using System.Linq;

namespace PsdUiWorkbench.Core
{
    public static class PsdNodeNaming
    {
        public static bool IsValid(string name)
            => !string.IsNullOrWhiteSpace(name) && !name.Any(c => c == '/' || c == '\\' || char.IsControl(c));

        public static bool IsValidRoot(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 128 || name[0] < 'A' || name[0] > 'Z') return false;
            for (int i = 1; i < name.Length; i++)
            {
                char c = name[i];
                if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9')) return false;
            }
            return true;
        }

        internal static bool TrySet(PsdDocumentSnapshot document, PsdNodeNameState current,
            IReadOnlyDictionary<int, string> replacements, out PsdNodeNameState candidate)
        {
            candidate = null;
            if (document == null || replacements == null) return false;
            var values = current.Overrides.ToDictionary(p => p.Key, p => p.Value);
            foreach (var pair in replacements)
            {
                var node = document.Find(pair.Key);
                if (node == null || !IsValid(pair.Value)) return false;
                if (string.Equals(pair.Value, node.Name, StringComparison.Ordinal)) values.Remove(pair.Key);
                else values[pair.Key] = pair.Value;
            }
            candidate = new PsdNodeNameState(values, current.RootNameOverride);
            return HasNoNewCollision(document, current, candidate);
        }

        internal static bool TryReset(PsdDocumentSnapshot document, PsdNodeNameState current,
            IEnumerable<int> ids, out PsdNodeNameState candidate)
        {
            candidate = null;
            if (document == null || ids == null) return false;
            var values = current.Overrides.ToDictionary(p => p.Key, p => p.Value);
            foreach (int id in ids.Distinct())
            {
                if (document.Find(id) == null) return false;
                values.Remove(id);
            }
            candidate = new PsdNodeNameState(values, current.RootNameOverride);
            return HasNoNewCollision(document, current, candidate);
        }

        private static bool HasNoNewCollision(PsdDocumentSnapshot document, PsdNodeNameState before, PsdNodeNameState after)
        {
            // 只拒绝新增的同级重名配对，源 PSD 中已有的重名不能阻止其他节点编辑。
            // 先完成整批替换再检查，因此交换名称不会出现中间态冲突。
            foreach (var siblings in document.Nodes.GroupBy(n => n.ParentId))
            {
                foreach (var names in siblings.GroupBy(after.Resolve, StringComparer.Ordinal))
                {
                    if (names.Count() < 2) continue;
                    string previousName = before.Resolve(names.First());
                    if (names.Any(n => !string.Equals(before.Resolve(n), previousName, StringComparison.Ordinal))) return false;
                }
            }
            return true;
        }
    }
}
