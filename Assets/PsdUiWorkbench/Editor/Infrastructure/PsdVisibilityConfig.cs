using System.Collections.Generic;
using PsdUiWorkbench.Core;
using UnityEngine;

namespace PsdUiWorkbench.Infrastructure
{
    public sealed class PsdVisibilityConfig : ScriptableObject
    {
        [SerializeField] private string _assetGuid;
        [SerializeField] private bool _documentVisible = true;
        [SerializeField] private List<int> _hiddenLayerIds = new List<int>();
        [SerializeField] private List<int> _excludedExportLayerIds = new List<int>();

        public string AssetGuid => _assetGuid;
        public PsdVisibilityState Read() => new PsdVisibilityState(_documentVisible, _hiddenLayerIds);
        public PsdExportState ReadExport() => new PsdExportState(_excludedExportLayerIds);

        internal void Write(string assetGuid, PsdVisibilityState state)
        {
            _assetGuid = assetGuid;
            _documentVisible = state.DocumentVisible;
            _hiddenLayerIds = new List<int>(state.HiddenLayerIds);
        }

        internal void WriteExport(string assetGuid, PsdExportState state)
        {
            _assetGuid = assetGuid;
            _excludedExportLayerIds = new List<int>(state.ExcludedLayerIds);
        }
    }
}
