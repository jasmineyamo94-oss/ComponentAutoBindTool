using System;
using System.Collections.Generic;
using System.Linq;

namespace PsdUiWorkbench.Core
{
    public static class PsdPrefabPlanner
    {
        public static PsdPrefabPlan Build(PsdDocumentSnapshot document, PsdVisibilityState visibility,
            PsdExportState exportState = null, PsdNodeNameState names = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (visibility == null) throw new ArgumentNullException(nameof(visibility));
            if (document.DrawNodes.Count == 0) throw new InvalidOperationException("当前文档没有可导出的 Sprite。");
            exportState ??= new PsdExportState();
            var drawNodes = document.DrawNodes.Where(n => exportState.Allows(document, n.Id)).ToArray();
            if (drawNodes.Length == 0) throw new InvalidOperationException("所有可导出内容均已被排除，请先恢复节点导出。");
            var images = new HashSet<int>(drawNodes.Select(n => n.Id));
            var included = new HashSet<int>(images);
            var bounds = drawNodes.ToDictionary(n => n.Id, n => n.Bounds);
            foreach (var image in drawNodes)
            {
                for (var parent = image.ParentId; parent.HasValue; parent = document.Find(parent.Value).ParentId)
                {
                    included.Add(parent.Value);
                    if (images.Contains(parent.Value)) continue;
                    bounds.TryGetValue(parent.Value, out var existing);
                    bounds[parent.Value] = PixelRect.Union(existing, image.Bounds);
                }
            }
            var hidden = new HashSet<int>(visibility.HiddenLayerIds);
            var children = document.Nodes.Reverse().Where(n => included.Contains(n.Id)).ToLookup(n => n.ParentId);
            var nodes = new List<PsdPrefabNode>();
            void BuildChildren(int? parent, PixelRect parentBounds)
            {
                int sibling = 0;
                foreach (var node in children[parent])
                {
                    var rect = bounds[node.Id];
                    nodes.Add(new PsdPrefabNode(node, rect, parentBounds, sibling++, images.Contains(node.Id), !hidden.Contains(node.Id), names));
                    BuildChildren(node.Id, rect);
                }
            }
            BuildChildren(null, new PixelRect(0, 0, document.Width, document.Height));
            if (!nodes.Where(n => n.IsImage).Select(n => n.Id).SequenceEqual(drawNodes.Select(n => n.Id)))
                throw new InvalidOperationException("源层级无法同时表达当前 Sprite 绘制顺序，已停止导出。");
            var skipped = new List<PsdPrefabSkippedNode>();
            var excluded = new HashSet<int>(exportState.ExcludedLayerIds);
            foreach (var node in document.Nodes.Where(n => !included.Contains(n.Id)))
            {
                if (!exportState.Allows(document, node.Id))
                {
                    skipped.Add(new PsdPrefabSkippedNode(node, excluded.Contains(node.Id) ? "用户设为不导出" : "受上级分组排除", true, names));
                    continue;
                }
                bool merged = false;
                for (var parent = node.ParentId; parent.HasValue; parent = document.Find(parent.Value).ParentId)
                {
                    var ancestor = document.Find(parent.Value);
                    if (ancestor.Flatten && images.Contains(ancestor.Id)) { merged = true; break; }
                }
                skipped.Add(new PsdPrefabSkippedNode(node, merged ? "已合并到组，无独立 Sprite" :
                    node.IsGroup ? "没有可绘制后代的分组" : "未导入或没有可用 Sprite 矩形", names: names));
            }
            return new PsdPrefabPlan(document, visibility.DocumentVisible, nodes, skipped, names);
        }
    }
}
