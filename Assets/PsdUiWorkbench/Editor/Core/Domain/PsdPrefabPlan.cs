using System;
using System.Collections.Generic;
using System.Linq;

namespace PsdUiWorkbench.Core
{
    public sealed class PsdPrefabNode
    {
        public int Id { get; }
        public int? ParentId { get; }
        public string Name { get; }
        public PixelRect Bounds { get; }
        public float LocalX { get; }
        public float LocalY { get; }
        public int SiblingIndex { get; }
        public string SpriteId { get; }
        public bool ActiveSelf { get; }
        public bool IsImage => !string.IsNullOrEmpty(SpriteId);
        internal PsdPrefabNode(PsdNode node, PixelRect bounds, PixelRect parentBounds, int siblingIndex, bool image, bool active,
            PsdNodeNameState names = null)
        {
            Id = node.Id;
            ParentId = node.ParentId;
            Name = names?.Resolve(node) ?? node.Name;
            Bounds = bounds;
            LocalX = bounds.X + bounds.Width / 2 - parentBounds.X - parentBounds.Width / 2;
            LocalY = parentBounds.Y + parentBounds.Height / 2 - bounds.Y - bounds.Height / 2;
            SiblingIndex = siblingIndex;
            SpriteId = image ? node.SpriteId : "";
            ActiveSelf = active;
        }
    }

    public sealed class PsdPrefabSkippedNode
    {
        public int Id { get; }
        public string Name { get; }
        public string Reason { get; }
        public bool IsUserExcluded { get; }
        internal PsdPrefabSkippedNode(PsdNode node, string reason, bool userExcluded = false, PsdNodeNameState names = null)
        {
            Id = node.Id; Name = names?.Resolve(node) ?? node.Name; Reason = reason; IsUserExcluded = userExcluded;
        }
    }

    public sealed class PsdPrefabPlan
    {
        public string AssetGuid { get; }
        public string RootName { get; }
        public int Width { get; }
        public int Height { get; }
        public bool ContentActive { get; }
        public IReadOnlyList<PsdPrefabNode> Nodes { get; }
        public IReadOnlyList<PsdPrefabSkippedNode> Skipped { get; }
        public int ImageCount { get; }
        public int ExcludedNodeCount { get; }
        internal PsdPrefabPlan(PsdDocumentSnapshot document, bool active, List<PsdPrefabNode> nodes, List<PsdPrefabSkippedNode> skipped,
            PsdNodeNameState names)
        {
            AssetGuid = document.AssetGuid; Width = document.Width; Height = document.Height;
            RootName = names?.RootName ?? PsdNodeNameState.DefaultRootName;
            ContentActive = active;
            Nodes = Array.AsReadOnly(nodes.ToArray());
            Skipped = Array.AsReadOnly(skipped.ToArray());
            ImageCount = nodes.Count(n => n.IsImage);
            ExcludedNodeCount = skipped.Count(n => n.IsUserExcluded);
        }
    }
}
