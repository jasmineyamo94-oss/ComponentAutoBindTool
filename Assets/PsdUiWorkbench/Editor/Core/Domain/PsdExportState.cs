using System.Collections.Generic;
using System.Linq;

namespace PsdUiWorkbench.Core
{
    public sealed class PsdExportState
    {
        private readonly HashSet<int> _excluded;
        public IReadOnlyList<int> ExcludedLayerIds { get; }

        public PsdExportState(IEnumerable<int> excludedLayerIds = null)
        {
            _excluded = new HashSet<int>(excludedLayerIds ?? Enumerable.Empty<int>());
            ExcludedLayerIds = System.Array.AsReadOnly(_excluded.OrderBy(id => id).ToArray());
        }

        public bool Allows(PsdDocumentSnapshot document, int id)
        {
            var node = document?.Find(id);
            if (node == null) return false;
            for (var current = node; current != null; current = current.ParentId.HasValue ? document.Find(current.ParentId.Value) : null)
                if (_excluded.Contains(current.Id)) return false;
            return true;
        }
    }
}
