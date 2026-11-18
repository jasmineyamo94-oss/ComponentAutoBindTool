using System;
using System.Collections.Generic;
using System.Linq;

namespace PsdUiWorkbench.Core
{
    public enum PsdLayerKind { Unknown, Group, Text, NonText }
    public readonly struct PixelRect
    {
        public readonly float X, Y, Width, Height;
        public PixelRect(float x, float y, float width, float height)
        {
            X = x; Y = y; Width = width; Height = height;
        }
        public bool IsValid => Width > 0 && Height > 0;
        public bool Contains(float x, float y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
        public static PixelRect FromBottomLeft(float x, float y, float width, float height, float canvasHeight)
            => new PixelRect(x, canvasHeight - y - height, width, height);
        public static PixelRect Union(PixelRect a, PixelRect b)
        {
            if (!a.IsValid) return b;
            if (!b.IsValid) return a;
            float x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            return new PixelRect(x, y, Math.Max(a.X + a.Width, b.X + b.Width) - x,
                Math.Max(a.Y + a.Height, b.Y + b.Height) - y);
        }
    }

    public sealed class PsdNode
    {
        public int Id { get; }
        public int? ParentId { get; }
        public string Name { get; }
        public bool IsGroup { get; }
        public bool IsImported { get; }
        public bool IsVisible { get; }
        public bool Flatten { get; }
        public string SpriteId { get; }
        public PixelRect Bounds { get; }
        public int Order { get; }
        public PsdLayerKind SourceKind { get; }
        public PsdNode(int id, int? parentId, string name, bool group, bool imported, bool visible,
            bool flatten, string spriteId, PixelRect bounds, int order, PsdLayerKind sourceKind = PsdLayerKind.Unknown)
        {
            Id = id; ParentId = parentId; Name = name; IsGroup = group; IsImported = imported;
            IsVisible = visible; Flatten = flatten; SpriteId = spriteId ?? ""; Bounds = bounds; Order = order;
            SourceKind = group ? PsdLayerKind.Group : sourceKind;
        }
    }

    public sealed class PsdDocumentSnapshot
    {
        private readonly Dictionary<int, PsdNode> _byId;
        public string AssetGuid { get; }
        public int Width { get; }
        public int Height { get; }
        public IReadOnlyList<PsdNode> Nodes { get; }
        public IReadOnlyList<PsdNode> DrawNodes { get; }
        public PsdDocumentSnapshot(string guid, int width, int height, IEnumerable<PsdNode> nodes)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("画布尺寸无效。");
            AssetGuid = guid; Width = width; Height = height;
            var list = nodes.OrderBy(n => n.Order).ToArray();
            _byId = list.ToDictionary(n => n.Id);
            foreach (var n in list)
            {
                var seen = new HashSet<int> { n.Id };
                var parent = n.ParentId;
                while (parent.HasValue)
                {
                    if (!_byId.TryGetValue(parent.Value, out var p) || !seen.Add(p.Id))
                        throw new ArgumentException("图层父子关系无效。");
                    parent = p.ParentId;
                }
            }
            Nodes = Array.AsReadOnly(list);
            DrawNodes = Array.AsReadOnly(list.Where(IsDrawable).Reverse().ToArray());
        }
        public PsdNode Find(int id) => _byId.TryGetValue(id, out var n) ? n : null;
        private bool IsDrawable(PsdNode n)
        {
            if (!n.IsImported || !n.Bounds.IsValid || string.IsNullOrEmpty(n.SpriteId)) return false;
            for (var p = n.ParentId; p.HasValue; p = Find(p.Value).ParentId)
            {
                var ancestor = Find(p.Value);
                if (ancestor.Flatten && ancestor.IsImported && !string.IsNullOrEmpty(ancestor.SpriteId)) return false;
            }
            return true;
        }
        public PixelRect GetBounds(int id)
        {
            var node = Find(id);
            if (node == null) return default;
            if (DrawNodes.Any(n => n.Id == id)) return node.Bounds;
            var rect = default(PixelRect);
            foreach (var n in DrawNodes)
            {
                for (var p = n.ParentId; p.HasValue; p = Find(p.Value).ParentId)
                    if (p.Value == id) { rect = PixelRect.Union(rect, n.Bounds); break; }
            }
            return rect;
        }
    }
}
