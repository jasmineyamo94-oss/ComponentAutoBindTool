using System.Collections.Generic;
using System.Linq;

namespace PsdUiWorkbench.Core
{
    public sealed class PsdVisibilityState
    {
        public bool DocumentVisible { get; }
        public IReadOnlyList<int> HiddenLayerIds { get; }

        public PsdVisibilityState(bool documentVisible = true, IEnumerable<int> hiddenLayerIds = null)
        {
            DocumentVisible = documentVisible;
            HiddenLayerIds = System.Array.AsReadOnly((hiddenLayerIds ?? Enumerable.Empty<int>()).Distinct().OrderBy(id => id).ToArray());
        }
    }
}
