using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;

namespace PsdUiWorkbench.Infrastructure
{
    public sealed class PsdAssetChanges : AssetPostprocessor
    {
        public static event Action<string[]> Changed;
        private static bool _queued;
        private static readonly HashSet<string> Pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var path in imported.Concat(deleted).Concat(moved).Concat(movedFrom).Where(IsPsd)) Pending.Add(path);
            if (_queued || Pending.Count == 0) return;
            _queued = true;
            EditorApplication.delayCall += () =>
            {
                _queued = false;
                var paths = Pending.ToArray(); Pending.Clear(); Changed?.Invoke(paths);
            };
        }
        private static bool IsPsd(string path) => path.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".psb", StringComparison.OrdinalIgnoreCase);
    }
}
