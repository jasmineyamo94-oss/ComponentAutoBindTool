using System;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEngine;

namespace PsdUiWorkbench.Infrastructure
{
    public sealed class PsdLayerMetadataAsset : ScriptableObject
    {
        public const int CurrentVersion = 1;
        [Serializable]
        private struct Entry
        {
            public int Id;
            public PsdLayerKind Kind;
        }
        [SerializeField] private int _version;
        [SerializeField] private PsdMetadataStatus _status;
        [SerializeField] private string _diagnostic;
        [SerializeField] private Entry[] _layers;
        public int Version => _version;
        public PsdMetadataResult Read()
        {
            if (_version != CurrentVersion) return new PsdMetadataResult(PsdMetadataStatus.Unsupported, "类型元数据版本不兼容，请重新导入。");
            if (!Enum.IsDefined(typeof(PsdMetadataStatus), _status)) return new PsdMetadataResult(PsdMetadataStatus.Invalid, "类型元数据状态无效。");
            if (_status == PsdMetadataStatus.Invalid || _status == PsdMetadataStatus.Unsupported) return new PsdMetadataResult(_status, _diagnostic);
            if (_layers == null || _layers.GroupBy(e => e.Id).Any(g => g.Count() != 1) || _layers.Any(e => !Enum.IsDefined(typeof(PsdLayerKind), e.Kind)))
                return new PsdMetadataResult(PsdMetadataStatus.Invalid, "类型元数据条目无效。");
            return new PsdMetadataResult(_status, _diagnostic, _layers.ToDictionary(e => e.Id, e => e.Kind));
        }
        internal void Initialize(PsdMetadataResult result)
        {
            _version = CurrentVersion;
            _status = result.Status;
            _diagnostic = result.Diagnostic;
            _layers = result.Layers.Select(p => new Entry { Id = p.Key, Kind = p.Value }).ToArray();
        }
    }
}
