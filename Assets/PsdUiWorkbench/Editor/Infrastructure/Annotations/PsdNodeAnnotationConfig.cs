using System;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEngine;

namespace PsdUiWorkbench.Infrastructure
{
    public sealed class PsdNodeAnnotationConfig : ScriptableObject
    {
        [Serializable]
        private struct Entry
        {
            public int Id;
            public string Text;
        }

        [SerializeField] private string _assetGuid;
        [SerializeField] private List<Entry> _annotations = new List<Entry>();
        public string AssetGuid => _assetGuid;

        public PsdNodeAnnotationState Read()
        {
            var annotations = new Dictionary<int, string>();
            foreach (var entry in _annotations)
            {
                if (annotations.ContainsKey(entry.Id) || !PsdNodeAnnotationState.TryNormalize(entry.Text, out _))
                    throw new InvalidOperationException("批注配置包含超长批注或重复节点 ID。");
                annotations.Add(entry.Id, entry.Text);
            }
            return new PsdNodeAnnotationState(annotations);
        }

        internal void Write(string assetGuid, PsdNodeAnnotationState state)
        {
            _assetGuid = assetGuid;
            _annotations = state.Annotations.OrderBy(pair => pair.Key)
                .Select(pair => new Entry { Id = pair.Key, Text = pair.Value }).ToList();
        }
    }
}
