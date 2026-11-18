using System;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEngine;

namespace PsdUiWorkbench.Infrastructure
{
    public sealed class PsdNodeNameConfig : ScriptableObject
    {
        [Serializable]
        private struct Entry
        {
            public int Id;
            public string Name;
        }

        [SerializeField] private string _assetGuid;
        [SerializeField] private string _rootName;
        [SerializeField] private List<Entry> _overrides = new List<Entry>();
        public string AssetGuid => _assetGuid;

        public PsdNodeNameState Read()
        {
            var names = new Dictionary<int, string>();
            foreach (var entry in _overrides)
            {
                if (!PsdNodeNaming.IsValid(entry.Name) || names.ContainsKey(entry.Id))
                    throw new InvalidOperationException("命名配置包含无效名称或重复节点 ID。");
                names.Add(entry.Id, entry.Name);
            }
            if (!string.IsNullOrEmpty(_rootName) && !PsdNodeNaming.IsValidRoot(_rootName))
                throw new InvalidOperationException("文档根名称无效。");
            return new PsdNodeNameState(names, _rootName);
        }

        internal void Write(string assetGuid, PsdNodeNameState state)
        {
            _assetGuid = assetGuid;
            _rootName = state.RootNameOverride;
            _overrides = state.Overrides.OrderBy(p => p.Key).Select(p => new Entry { Id = p.Key, Name = p.Value }).ToList();
        }
    }
}
