using System;
using System.Linq;
using PsdTools.Constants;
using PsdTools.Psd;
using UnityEngine;

namespace PsdTools.Layers
{
    /// <summary>
    /// Group layer (folder)
    /// </summary>
    public class Group : Layer
    {
        private readonly bool _isOpen;

        public Group(LayerRecord record, FileHeader header, bool isOpen = true) : base(record, header)
        {
            _isOpen = isOpen;
        }

        /// <summary>Create a root group (no record)</summary>
        public Group(FileHeader header) : base(null, header)
        {
            _isOpen = true;
        }

        public override LayerKind Kind => LayerKind.Group;

        public override bool IsGroup => true;

        /// <summary>Whether group is expanded in Photoshop</summary>
        public bool IsOpen => _isOpen;

        /// <summary>Group blend mode (may be PassThrough)</summary>
        public new BlendMode BlendMode
        {
            get
            {
                var dividerBlendMode = _record?.TaggedBlocks?.GetSectionDividerBlendMode();
                return dividerBlendMode ?? base.BlendMode;
            }
        }

        /// <summary>
        /// Group bounding box (computed from children)
        /// </summary>
        public new (int Left, int Top, int Right, int Bottom) BBox
        {
            get
            {
                if (_children.Count == 0)
                    return (0, 0, 0, 0);

                int left = int.MaxValue;
                int top = int.MaxValue;
                int right = int.MinValue;
                int bottom = int.MinValue;

                foreach (var child in _children)
                {
                    var bbox = child.IsGroup ? ((Group)child).BBox : child.BBox;
                    if (bbox.Left < left) left = bbox.Left;
                    if (bbox.Top < top) top = bbox.Top;
                    if (bbox.Right > right) right = bbox.Right;
                    if (bbox.Bottom > bottom) bottom = bbox.Bottom;
                }

                return (left, top, right, bottom);
            }
        }

        /// <summary>
        /// Composite all visible children in this group
        /// </summary>
        public override Texture2D Composite()
        {
            // For groups, we need to composite all visible children
            // This is a simplified implementation
            var bbox = BBox;
            int width = bbox.Right - bbox.Left;
            int height = bbox.Bottom - bbox.Top;

            if (width <= 0 || height <= 0)
                return null;

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];

            // Initialize with transparent
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(0, 0, 0, 0);
            }

            // Composite children bottom to top (children[0]=bottom, children[count-1]=top)
            for (int i = 0; i < _children.Count; i++)
            {
                var child = _children[i];
                if (!child.Visible)
                    continue;

                var childTexture = child.Composite();
                if (childTexture == null)
                    continue;

                // Calculate position within group
                int offsetX = child.Left - bbox.Left;
                int offsetY = child.Top - bbox.Top;

                var childPixels = childTexture.GetPixels32();

                // Blend child onto result
                for (int cy = 0; cy < child.Height; cy++)
                {
                    for (int cx = 0; cx < child.Width; cx++)
                    {
                        int dx = offsetX + cx;
                        int dy = offsetY + cy;

                        if (dx < 0 || dx >= width || dy < 0 || dy >= height)
                            continue;

                        int srcIdx = (child.Height - 1 - cy) * child.Width + cx;
                        int dstIdx = (height - 1 - dy) * width + dx;

                        if (srcIdx >= 0 && srcIdx < childPixels.Length &&
                            dstIdx >= 0 && dstIdx < pixels.Length)
                        {
                            var src = childPixels[srcIdx];
                            var dst = pixels[dstIdx];

                            // Simple alpha blending
                            float srcAlpha = src.a / 255f * child.OpacityFloat;
                            float dstAlpha = dst.a / 255f;
                            float outAlpha = srcAlpha + dstAlpha * (1 - srcAlpha);

                            if (outAlpha > 0)
                            {
                                pixels[dstIdx] = new Color32(
                                    (byte)((src.r * srcAlpha + dst.r * dstAlpha * (1 - srcAlpha)) / outAlpha),
                                    (byte)((src.g * srcAlpha + dst.g * dstAlpha * (1 - srcAlpha)) / outAlpha),
                                    (byte)((src.b * srcAlpha + dst.b * dstAlpha * (1 - srcAlpha)) / outAlpha),
                                    (byte)(outAlpha * 255)
                                );
                            }
                        }
                    }
                }

                UnityEngine.Object.DestroyImmediate(childTexture);
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }
    }
}
