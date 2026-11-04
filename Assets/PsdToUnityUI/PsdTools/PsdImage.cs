using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using PsdTools.Constants;
using PsdTools.Layers;
using PsdTools.Psd;
using UnityEngine;

namespace PsdTools
{
    /// <summary>
    /// High-level PSD image API
    /// </summary>
    public class PsdImage : IEnumerable<Layer>
    {
        private readonly PsdDocument _document;
        private readonly Group _root;
        private readonly List<Layer> _flatLayers;

        private PsdImage(PsdDocument document)
        {
            _document = document;
            _root = new Group(document.Header);
            _flatLayers = new List<Layer>();
            
            BuildLayerTree();
        }

        #region Properties

        /// <summary>Image width in pixels</summary>
        public int Width => (int)_document.Header.Width;

        /// <summary>Image height in pixels</summary>
        public int Height => (int)_document.Header.Height;

        /// <summary>Image size (width, height)</summary>
        public (int Width, int Height) Size => (Width, Height);

        /// <summary>Color mode</summary>
        public ColorMode ColorMode => _document.Header.ColorMode;

        /// <summary>Number of channels</summary>
        public int Channels => _document.Header.Channels;

        /// <summary>Bit depth (1, 8, 16, or 32)</summary>
        public int Depth => _document.Header.Depth;

        /// <summary>PSD version (1 = PSD, 2 = PSB)</summary>
        public int Version => _document.Header.Version;

        /// <summary>Whether this is a PSB (large document) file</summary>
        public bool IsPsb => _document.Header.IsPsb;

        /// <summary>Number of layers</summary>
        public int LayerCount => _flatLayers.Count;

        /// <summary>Underlying PSD document</summary>
        public PsdDocument Document => _document;

        /// <summary>Root layer group</summary>
        public Group Root => _root;

        /// <summary>Top-level children</summary>
        public IReadOnlyList<Layer> Children => _root.Children;

        #endregion

        #region Static Methods

        /// <summary>
        /// Open a PSD file from path
        /// </summary>
        public static PsdImage Open(string path)
        {
            var document = PsdDocument.Open(path);
            return new PsdImage(document);
        }

        /// <summary>
        /// Open a PSD from a stream
        /// </summary>
        public static PsdImage Open(Stream stream)
        {
            var document = PsdDocument.Read(stream);
            return new PsdImage(document);
        }

        /// <summary>
        /// Open a PSD from byte array
        /// </summary>
        public static PsdImage Open(byte[] data)
        {
            using (var stream = new MemoryStream(data))
            {
                return Open(stream);
            }
        }

        /// <summary>
        /// Save the PSD file to the specified path.
        /// Uses temp file + atomic replacement to avoid corruption.
        /// </summary>
        public void Save(string path)
        {
            _document.Save(path);
        }

        /// <summary>
        /// Write the PSD to a stream.
        /// </summary>
        public void Save(Stream stream)
        {
            _document.Write(stream);
        }

        #endregion

        #region Layer Tree

        private void BuildLayerTree()
        {
            var records = _document.LayerAndMaskInfo?.LayerRecords;
            if (records == null || records.Count == 0)
                return;

            // PSD layers are stored from bottom to top.
            // Group boundaries in file order (iterating bottom → top):
            //   1. BoundingSectionDivider — bottom boundary of the group (unnamed end marker)
            //   2. Child layers...
            //   3. OpenFolder/ClosedFolder — top boundary of the group (has the group name)

            var groupStack = new Stack<Group>();
            groupStack.Push(_root);

            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                var divider = record.TaggedBlocks?.GetSectionDivider() ?? SectionDivider.Other;

                switch (divider)
                {
                    case SectionDivider.BoundingSectionDivider:
                        // Bottom boundary of a group — push a temporary placeholder
                        var tempGroup = new Group(_document.Header);
                        groupStack.Push(tempGroup);
                        break;

                    case SectionDivider.OpenFolder:
                    case SectionDivider.ClosedFolder:
                        // Top boundary of a group — pop placeholder, create real group
                        if (groupStack.Count > 1)
                        {
                            var temp = groupStack.Pop();
                            var group = new Group(record, _document.Header, divider == SectionDivider.OpenFolder);
                            var tempChildren = new System.Collections.Generic.List<Layer>(temp.Children);
                            foreach (var child in tempChildren)
                            {
                                group.AddChild(child);
                            }
                            groupStack.Peek().AddChild(group);
                            _flatLayers.Add(group);
                        }
                        break;

                    default:
                        var layer = CreateLayer(record);
                        groupStack.Peek().AddChild(layer);
                        _flatLayers.Add(layer);
                        break;
                }
            }
        }

        private Layer CreateLayer(LayerRecord record)
        {
            var blocks = record.TaggedBlocks;

            // Check for type layer
            if (blocks?.Contains(Tag.TYPE_TOOL_OBJECT_SETTING) == true ||
                blocks?.Contains(Tag.TYPE_TOOL_INFO) == true)
            {
                return new TypeLayer(record, _document.Header);
            }

            // Check for smart object
            if (blocks?.Contains(Tag.SMART_OBJECT_LAYER_DATA1) == true ||
                blocks?.Contains(Tag.SMART_OBJECT_LAYER_DATA2) == true ||
                blocks?.Contains(Tag.PLACED_LAYER1) == true ||
                blocks?.Contains(Tag.PLACED_LAYER2) == true)
            {
                return new SmartObjectLayer(record, _document.Header);
            }

            // Check for shape layer
            if (blocks?.Contains(Tag.VECTOR_MASK_SETTING1) == true ||
                blocks?.Contains(Tag.VECTOR_MASK_SETTING2) == true ||
                blocks?.Contains(Tag.VECTOR_ORIGINATION_DATA) == true)
            {
                return new ShapeLayer(record, _document.Header);
            }

            // Check for adjustment layer
            if (IsAdjustmentLayer(blocks))
            {
                return new AdjustmentLayer(record, _document.Header);
            }

            // Default to pixel layer
            return new PixelLayer(record, _document.Header);
        }

        private static bool IsAdjustmentLayer(TaggedBlocks blocks)
        {
            if (blocks == null)
                return false;

            return blocks.Contains(Tag.SOLID_COLOR) ||
                   blocks.Contains(Tag.GRADIENT_FILL) ||
                   blocks.Contains(Tag.PATTERN_FILL) ||
                   blocks.Contains(Tag.BRIGHTNESS_CONTRAST) ||
                   blocks.Contains(Tag.LEVELS) ||
                   blocks.Contains(Tag.CURVES) ||
                   blocks.Contains(Tag.EXPOSURE) ||
                   blocks.Contains(Tag.VIBRANCE) ||
                   blocks.Contains(Tag.HUE_SATURATION) ||
                   blocks.Contains(Tag.COLOR_BALANCE) ||
                   blocks.Contains(Tag.BLACK_AND_WHITE) ||
                   blocks.Contains(Tag.PHOTO_FILTER) ||
                   blocks.Contains(Tag.CHANNEL_MIXER) ||
                   blocks.Contains(Tag.COLOR_LOOKUP) ||
                   blocks.Contains(Tag.INVERT) ||
                   blocks.Contains(Tag.POSTERIZE) ||
                   blocks.Contains(Tag.THRESHOLD) ||
                   blocks.Contains(Tag.GRADIENT_MAP) ||
                   blocks.Contains(Tag.SELECTIVE_COLOR);
        }

        #endregion

        #region Enumeration

        /// <summary>
        /// Get all layers (flat list)
        /// </summary>
        public IEnumerator<Layer> GetEnumerator()
        {
            return _flatLayers.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// Get all descendant layers recursively
        /// </summary>
        public IEnumerable<Layer> Descendants()
        {
            return _root.Descendants();
        }

        /// <summary>
        /// Find layers by name
        /// </summary>
        public IEnumerable<Layer> FindByName(string name, bool exactMatch = true)
        {
            foreach (var layer in Descendants())
            {
                if (exactMatch)
                {
                    if (layer.Name == name)
                        yield return layer;
                }
                else
                {
                    if (layer.Name.Contains(name))
                        yield return layer;
                }
            }
        }

        /// <summary>
        /// Find first layer by name
        /// </summary>
        public Layer Find(string name, bool exactMatch = true)
        {
            foreach (var layer in FindByName(name, exactMatch))
            {
                return layer;
            }
            return null;
        }

        /// <summary>
        /// Get layer by index
        /// </summary>
        public Layer this[int index] => _flatLayers[index];

        #endregion

        #region Composite

        /// <summary>
        /// Composite the entire document to a Texture2D
        /// </summary>
        public Texture2D Composite()
        {
            // Try to use the merged image data first
            var imageData = _document.ImageData;
            if (imageData != null && imageData.RawData != null && imageData.RawData.Length > 0)
            {
                return CompositeFromMergedImage();
            }

            // Fall back to compositing layers
            return CompositeFromLayers();
        }

        private Texture2D CompositeFromMergedImage()
        {
            var channelData = _document.ImageData.GetChannelData(_document.Header);
            if (channelData == null || channelData.Count == 0)
                return null;

            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            var pixels = new Color32[Width * Height];

            byte[] red = channelData.Count > 0 ? channelData[0] : null;
            byte[] green = channelData.Count > 1 ? channelData[1] : null;
            byte[] blue = channelData.Count > 2 ? channelData[2] : null;
            byte[] alpha = channelData.Count > 3 ? channelData[3] : null;

            // Handle grayscale
            if (ColorMode == ColorMode.Grayscale)
            {
                green = red;
                blue = red;
            }

            int pixelCount = Width * Height;
            for (int i = 0; i < pixelCount; i++)
            {
                byte r = red != null && i < red.Length ? red[i] : (byte)0;
                byte g = green != null && i < green.Length ? green[i] : (byte)0;
                byte b = blue != null && i < blue.Length ? blue[i] : (byte)0;
                byte a = alpha != null && i < alpha.Length ? alpha[i] : (byte)255;

                pixels[i] = new Color32(r, g, b, a);
            }

            // Flip vertically (PSD is top-down, Unity is bottom-up)
            Color32[] flipped = new Color32[pixelCount];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    flipped[(Height - 1 - y) * Width + x] = pixels[y * Width + x];
                }
            }

            texture.SetPixels32(flipped);
            texture.Apply();
            return texture;
        }

        private Texture2D CompositeFromLayers()
        {
            return _root.Composite();
        }

        /// <summary>
        /// Composite by reading each layer's pixel data at runtime, fully respecting the current
        /// in-memory Visible state of every layer. Unlike <see cref="Composite"/>, this method
        /// never falls back to the pre-baked merged image stored in the PSD file, so toggling
        /// layer visibility is immediately reflected in the result.
        /// </summary>
        public Texture2D CompositeFromLayersOnly()
        {
            var pixels = new Color32[Width * Height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(0, 0, 0, 0);

            BlitGroup(_root, pixels, Width, Height);

            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        /// <summary>
        /// Same clipping-mask rules as <see cref="CompositeFromLayersOnly"/>: if <paramref name="layer"/> belongs to a clipping stack
        /// (as base or clipped layer), returns a texture merged from the base plus all consecutive clipped layers below it; otherwise returns
        /// a standalone preview texture for the layer or group (same single-layer draw path as the canvas, including effects).
        /// Caller must dispose the return value with <see cref="UnityEngine.Object.DestroyImmediate"/>.
        /// </summary>
        public Texture2D CreateLayerPreviewTexture(Layer layer)
        {
            if (layer == null) return null;

            Layer baseLayer;
            if (layer.IsClipped)
            {
                baseLayer = GetClippingBaseSibling(layer);
                if (baseLayer == null)
                    return CreateStandaloneLayerPreviewTexture(layer);
            }
            else
                baseLayer = layer;

            var clips = new List<Layer>(8);
            CollectClippedSiblingsAfterBase(baseLayer, clips);

            if (clips.Count > 0)
            {
                Texture2D merged = BuildMergedClippingGroupTexture(baseLayer, clips, logMerge: false);
                if (merged != null)
                    return merged;
            }

            return CreateStandaloneLayerPreviewTexture(baseLayer);
        }

        /// <summary>
        /// Runs <see cref="BlitGroup"/> on the full document canvas (group clipping matches document composite), then crops to the group bounds.
        /// <see cref="Group.Composite"/> only alpha-blends children and does not merge clipping stacks, so preview/clipping merge must use this method.
        /// </summary>
        private Texture2D RasterizeGroupSubtreeWithClipping(Group group)
        {
            if (group == null) return null;
            var bbox = group.BBox;
            int bw = bbox.Right - bbox.Left;
            int bh = bbox.Bottom - bbox.Top;
            if (bw <= 0 || bh <= 0) return null;

            var pixels = new Color32[Width * Height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(0, 0, 0, 0);

            BlitGroup(group, pixels, Width, Height);

            var tex = new Texture2D(bw, bh, TextureFormat.RGBA32, false);
            var cropped = new Color32[bw * bh];
            for (int dy = 0; dy < bh; dy++)
            {
                for (int lx = 0; lx < bw; lx++)
                {
                    int psdX = bbox.Left + lx;
                    int psdY = bbox.Top + dy;
                    if (psdX < 0 || psdX >= Width || psdY < 0 || psdY >= Height)
                        continue;
                    int srcIdx = (Height - 1 - psdY) * Width + psdX;
                    int dstIdx = (bh - 1 - dy) * bw + lx;
                    cropped[dstIdx] = pixels[srcIdx];
                }
            }

            tex.SetPixels32(cropped);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Public wrapper for <see cref="RasterizeGroupSubtreeWithClipping"/>: composites a group using
        /// <see cref="BlitGroup"/> (which correctly merges clipping stacks inside the group), then crops to
        /// the group bounds.  Use this instead of <see cref="Group.Composite"/> when the group may contain
        /// clipping layers that must be merged with their base layers.
        /// Caller must dispose the return value with <see cref="UnityEngine.Object.DestroyImmediate"/>.
        /// </summary>
        public Texture2D CompositeGroupWithClipping(Group group)
        {
            return RasterizeGroupSubtreeWithClipping(group);
        }

        /// <summary>Clipped layer: walk up the parent's child list to the nearest non-clipped sibling as base (matches editor tree navigation).</summary>
        private static Layer GetClippingBaseSibling(Layer clipped)
        {
            if (clipped == null || !clipped.IsClipped) return null;
            Layer parent = clipped.Parent;
            if (parent == null) return null;
            var siblings = parent.Children;
            int idx = -1;
            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i] == clipped)
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0) return null;
            for (int j = idx - 1; j >= 0; j--)
            {
                if (!siblings[j].IsClipped)
                    return siblings[j];
            }
            return null;
        }

        /// <summary>All clipped layers after the base layer until a non-clipped sibling (same as the pending list in <see cref="BlitGroup"/>).</summary>
        private static void CollectClippedSiblingsAfterBase(Layer baseLayer, List<Layer> outList)
        {
            outList.Clear();
            if (baseLayer?.Parent == null) return;
            var siblings = baseLayer.Parent.Children;
            int idx = -1;
            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i] == baseLayer)
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0) return;
            for (int k = idx + 1; k < siblings.Count; k++)
            {
                if (!siblings[k].IsClipped) break;
                outList.Add(siblings[k]);
            }
        }

        private Texture2D CreateStandaloneLayerPreviewTexture(Layer layer)
        {
            if (layer == null) return null;
            if (layer.IsGroup)
                return RasterizeGroupSubtreeWithClipping((Group)layer);
            if (!layer.HasPixels() && !LayerEffectsHelper.HasExtractableColor(layer))
                return null;
            return LayerEffectsHelper.CreateLayerTextureWithEffects(layer);
        }

        /// <summary>
        /// Merges the base layer and clipped layers per document composite into one texture (size = base bounds); do not call when there are no clipped layers.
        /// </summary>
        private Texture2D BuildMergedClippingGroupTexture(Layer baseLayer, List<Layer> clippedLayers, bool logMerge)
        {
            Texture2D baseTex;
            int baseLeft, baseTop, baseW, baseH;

            if (baseLayer.IsGroup)
            {
                var bbox = ((Group)baseLayer).BBox;
                baseLeft = bbox.Left;
                baseTop = bbox.Top;
                baseW = bbox.Right - bbox.Left;
                baseH = bbox.Bottom - bbox.Top;
                if (baseW <= 0 || baseH <= 0) return null;
                baseTex = RasterizeGroupSubtreeWithClipping((Group)baseLayer);
            }
            else
            {
                baseLeft = baseLayer.Left;
                baseTop = baseLayer.Top;
                baseW = baseLayer.Width;
                baseH = baseLayer.Height;
                if (baseW <= 0 || baseH <= 0) return null;
                if (!baseLayer.HasPixels() && !LayerEffectsHelper.HasExtractableColor(baseLayer)) return null;
                baseTex = LayerEffectsHelper.CreateLayerTextureWithEffects(baseLayer);
            }

            if (baseTex == null) return null;

            Color32[] basePixels = baseTex.GetPixels32();
            byte[] origAlpha = new byte[basePixels.Length];
            for (int k = 0; k < basePixels.Length; k++)
                origAlpha[k] = basePixels[k].a;

            if (logMerge)
            {
                var clippedNames = new System.Text.StringBuilder();
                foreach (var cl in clippedLayers)
                    clippedNames.Append(cl.Name).Append(", ");
                string clippedNamesStr = clippedNames.Length > 2
                    ? clippedNames.ToString(0, clippedNames.Length - 2) : "";
                Debug.Log($"[Clipping mask merge] Base layer: {baseLayer.Name}  Clipped layers: {clippedNamesStr}");
            }

            foreach (var clipLayer in clippedLayers)
            {
                if (!clipLayer.Visible) continue;

                Texture2D clipTex;
                int clipLeft, clipTop, clipW, clipH;

                if (clipLayer.IsGroup)
                {
                    var cbbox = ((Group)clipLayer).BBox;
                    clipLeft = cbbox.Left;
                    clipTop = cbbox.Top;
                    clipW = cbbox.Right - cbbox.Left;
                    clipH = cbbox.Bottom - cbbox.Top;
                    if (clipW <= 0 || clipH <= 0) continue;
                    clipTex = RasterizeGroupSubtreeWithClipping((Group)clipLayer);
                }
                else
                {
                    clipLeft = clipLayer.Left;
                    clipTop = clipLayer.Top;
                    clipW = clipLayer.Width;
                    clipH = clipLayer.Height;
                    if (clipW <= 0 || clipH <= 0) continue;
                    clipTex = LayerEffectsHelper.CreateLayerTextureWithEffects(clipLayer);
                }

                if (clipTex == null) continue;

                float clipOpacity = clipLayer.OpacityFloat;
                Color32[] clipPixels = clipTex.GetPixels32();

                for (int psdY = 0; psdY < clipH; psdY++)
                {
                    for (int psdX = 0; psdX < clipW; psdX++)
                    {
                        int worldX = clipLeft + psdX;
                        int worldY = clipTop + psdY;
                        int bx = worldX - baseLeft;
                        int by = worldY - baseTop;

                        if (bx < 0 || bx >= baseW || by < 0 || by >= baseH) continue;

                        int baseIdx = (baseH - 1 - by) * baseW + bx;
                        int clipIdx = (clipH - 1 - psdY) * clipW + psdX;

                        if (baseIdx < 0 || baseIdx >= basePixels.Length) continue;
                        if (clipIdx < 0 || clipIdx >= clipPixels.Length) continue;

                        byte maskA = origAlpha[baseIdx];
                        if (maskA == 0) continue;

                        Color32 src = clipPixels[clipIdx];
                        float srcA = (src.a / 255f) * (maskA / 255f) * clipOpacity;
                        if (srcA < 0.001f) continue;

                        Color32 dst = basePixels[baseIdx];
                        float dstA = dst.a / 255f;
                        float outA = srcA + dstA * (1f - srcA);

                        if (outA > 0f)
                        {
                            basePixels[baseIdx] = new Color32(
                                (byte)((src.r * srcA + dst.r * dstA * (1f - srcA)) / outA),
                                (byte)((src.g * srcA + dst.g * dstA * (1f - srcA)) / outA),
                                (byte)((src.b * srcA + dst.b * dstA * (1f - srcA)) / outA),
                                (byte)(outA * 255f)
                            );
                        }
                    }
                }

                UnityEngine.Object.DestroyImmediate(clipTex);
            }

            var result = new Texture2D(baseW, baseH, TextureFormat.RGBA32, false);
            result.SetPixels32(basePixels);
            result.Apply();
            UnityEngine.Object.DestroyImmediate(baseTex);
            return result;
        }

        private static void BlitPixelRectOntoCanvas(Color32[] srcPixels, int baseLeft, int baseTop, int baseW, int baseH,
            float opacity, Color32[] canvas, int canvasW, int canvasH)
        {
            for (int by2 = 0; by2 < baseH; by2++)
            {
                for (int bx2 = 0; bx2 < baseW; bx2++)
                {
                    int canvasX = baseLeft + bx2;
                    int canvasY = baseTop + by2;
                    if (canvasX < 0 || canvasX >= canvasW || canvasY < 0 || canvasY >= canvasH) continue;

                    int srcIdx = (baseH - 1 - by2) * baseW + bx2;
                    int dstIdx = (canvasH - 1 - canvasY) * canvasW + canvasX;

                    if (srcIdx < 0 || srcIdx >= srcPixels.Length) continue;
                    if (dstIdx < 0 || dstIdx >= canvas.Length) continue;

                    Color32 src = srcPixels[srcIdx];
                    Color32 dst = canvas[dstIdx];
                    float srcA = src.a / 255f * opacity;
                    float dstA = dst.a / 255f;
                    float outA = srcA + dstA * (1f - srcA);

                    if (outA > 0f)
                    {
                        canvas[dstIdx] = new Color32(
                            (byte)((src.r * srcA + dst.r * dstA * (1f - srcA)) / outA),
                            (byte)((src.g * srcA + dst.g * dstA * (1f - srcA)) / outA),
                            (byte)((src.b * srcA + dst.b * dstA * (1f - srcA)) / outA),
                            (byte)(outA * 255f)
                        );
                    }
                }
            }
        }

        /// <summary>
        /// Iterate children bottom-to-top, group consecutive clipped layers with their base,
        /// then composite each group onto the canvas. Clipped layers are merged directly onto
        /// their base layer using the base alpha as a mask instead of being rendered independently.
        /// </summary>
        private void BlitGroup(Layer group, Color32[] canvas, int canvasW, int canvasH)
        {
            var children = group.Children;

            Layer currentBase = null;
            var pendingClipped = new List<Layer>();

            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];

                if (!child.Visible)
                    continue;

                if (child.IsClipped)
                {
                    // Clipped (NonBase) layer — belongs to the current base
                    if (currentBase != null)
                        pendingClipped.Add(child);
                    // Orphan clipped layer without a base: skip
                }
                else
                {
                    // New Base layer — finish the previous base+clipped group first
                    if (currentBase != null)
                    {
                        BlitBaseWithClipped(currentBase, pendingClipped, canvas, canvasW, canvasH);
                        pendingClipped.Clear();
                    }
                    currentBase = child;
                }
            }

            // Finalize the last base
            if (currentBase != null)
                BlitBaseWithClipped(currentBase, pendingClipped, canvas, canvasW, canvasH);
        }

        /// <summary>
        /// Composite a base layer together with its clipped layers and blit the result onto the canvas.
        /// When clippedLayers is empty the base is blitted normally.
        /// </summary>
        private void BlitBaseWithClipped(Layer baseLayer, List<Layer> clippedLayers,
                                          Color32[] canvas, int canvasW, int canvasH)
        {
            if (clippedLayers.Count == 0)
            {
                BlitSingleLayerOntoCanvas(baseLayer, canvas, canvasW, canvasH);
                return;
            }

            Texture2D merged = BuildMergedClippingGroupTexture(baseLayer, clippedLayers, logMerge: true);
            if (merged == null) return;

            int baseLeft, baseTop, baseW, baseH;
            if (baseLayer.IsGroup)
            {
                var bbox = ((Group)baseLayer).BBox;
                baseLeft = bbox.Left;
                baseTop = bbox.Top;
                baseW = bbox.Right - bbox.Left;
                baseH = bbox.Bottom - bbox.Top;
            }
            else
            {
                baseLeft = baseLayer.Left;
                baseTop = baseLayer.Top;
                baseW = baseLayer.Width;
                baseH = baseLayer.Height;
            }

            Color32[] mergedPixels = merged.GetPixels32();
            BlitPixelRectOntoCanvas(mergedPixels, baseLeft, baseTop, baseW, baseH, baseLayer.OpacityFloat, canvas, canvasW, canvasH);
            UnityEngine.Object.DestroyImmediate(merged);
        }

        /// <summary>
        /// Blit a single non-clipped layer (or group) onto the canvas without any clipping logic.
        /// Effects (color overlay, gradient overlay, layer mask) are applied via LayerEffectsHelper.
        /// </summary>
        private void BlitSingleLayerOntoCanvas(Layer layer, Color32[] canvas, int canvasW, int canvasH)
        {
            if (layer.IsGroup)
            {
                BlitGroup(layer, canvas, canvasW, canvasH);
                return;
            }

            if (!layer.HasPixels() && !LayerEffectsHelper.HasExtractableColor(layer)) return;

            var layerTex = LayerEffectsHelper.CreateLayerTextureWithEffects(layer);
            if (layerTex == null) return;

            var    layerPixels = layerTex.GetPixels32();
            float  opacity     = layer.OpacityFloat;

            for (int cy = 0; cy < layer.Height; cy++)
            {
                for (int cx = 0; cx < layer.Width; cx++)
                {
                    int canvasX = layer.Left + cx;
                    int canvasY = layer.Top  + cy;
                    if (canvasX < 0 || canvasX >= canvasW || canvasY < 0 || canvasY >= canvasH) continue;

                    // childTex is already vertically flipped (Unity bottom-up):
                    // row 0 of childTex = bottom row of the layer in PSD space.
                    int srcIdx = (layer.Height - 1 - cy) * layer.Width + cx;
                    int dstIdx = (canvasH - 1 - canvasY) * canvasW + canvasX;

                    if (srcIdx < 0 || srcIdx >= layerPixels.Length) continue;
                    if (dstIdx < 0 || dstIdx >= canvas.Length)      continue;

                    var   src  = layerPixels[srcIdx];
                    var   dst  = canvas[dstIdx];
                    float srcA = src.a / 255f * opacity;
                    float dstA = dst.a / 255f;
                    float outA = srcA + dstA * (1f - srcA);

                    if (outA > 0f)
                    {
                        canvas[dstIdx] = new Color32(
                            (byte)((src.r * srcA + dst.r * dstA * (1f - srcA)) / outA),
                            (byte)((src.g * srcA + dst.g * dstA * (1f - srcA)) / outA),
                            (byte)((src.b * srcA + dst.b * dstA * (1f - srcA)) / outA),
                            (byte)(outA * 255f)
                        );
                    }
                }
            }

            UnityEngine.Object.DestroyImmediate(layerTex);
        }

        #endregion

        /// <summary>
        /// Clears decompression caches for all layers and the merged image.
        /// Call immediately after texture synthesis to release dozens to hundreds of MBs of decompressed pixel data.
        /// Data will be re-decompressed on demand when next needed.
        /// </summary>
        public void ClearDecompressedCaches()
        {
            _document.ImageData?.ClearCache();

            var records = _document.LayerAndMaskInfo?.LayerRecords;
            if (records == null) return;
            foreach (var record in records)
            {
                if (record.ChannelData == null) continue;
                foreach (var ch in record.ChannelData)
                    ch.ClearCache();
            }
        }

        /// <summary>
        /// Explicitly releases all heavy byte[] data (compressed pixels, TaggedBlocks, MaskData, etc.) within the PSD document.
        /// Detaches these large memory blocks from the PsdDocument object graph to ensure that even if the PsdImage itself is 
        /// pinned by the Mono Boehm conservative GC (due to stale stack values resembling pointers), the byte arrays can still 
        /// be collected independently.
        /// After calling this, the instance can no longer be used for synthesis or saving.
        /// </summary>
        public void ReleaseAllData()
        {
            ClearDecompressedCaches();

            // Release merged image compressed data
            if (_document.ImageData != null)
                _document.ImageData.RawData = null;

            // Release channel compressed data, masks, and TaggedBlocks for each layer
            var records = _document.LayerAndMaskInfo?.LayerRecords;
            if (records != null)
            {
                foreach (var record in records)
                {
                    if (record.ChannelData != null)
                    {
                        foreach (var ch in record.ChannelData)
                        {
                            ch.RawData = null;
                            ch.ClearCache();
                        }
                        record.ChannelData = null;
                    }

                    if (record.Mask != null)
                        record.Mask.RawData = null;

                    if (record.TaggedBlocks != null)
                    {
                        foreach (var tb in record.TaggedBlocks.Blocks)
                        {
                            tb.Data = null;
                            tb.PaddingBytes = null;
                        }
                    }

                    record.BlendingRangesRaw = null;
                    record.NamePascalRaw = null;
                }
            }

            // Release global mask
            if (_document.LayerAndMaskInfo?.GlobalMask != null)
                _document.LayerAndMaskInfo.GlobalMask.RawData = null;

            // Release section-level TaggedBlocks
            if (_document.LayerAndMaskInfo?.TaggedBlocks != null)
            {
                foreach (var tb in _document.LayerAndMaskInfo.TaggedBlocks.Blocks)
                {
                    tb.Data = null;
                    tb.PaddingBytes = null;
                }
            }

            // Release RawBytes and Data for each resource in ImageResources
            if (_document.ImageResources != null)
            {
                foreach (var res in _document.ImageResources.Resources)
                {
                    res.RawBytes = null;
                    res.Data = null;
                }
            }

            // Release cache in the layer tree
            ClearLayerCachesRecursive(_root);

            _flatLayers.Clear();
        }

        private static void ClearLayerCachesRecursive(Layer layer)
        {
            if (layer == null) return;
            layer.ClearCachedData();
            foreach (var child in layer.Children)
                ClearLayerCachesRecursive(child);
        }

        public override string ToString()
        {
            return $"PSD: {Width}x{Height}, {LayerCount} layers, {ColorMode}, {Depth}-bit";
        }
    }

    /// <summary>
    /// Internal class for group end markers
    /// </summary>
    internal class GroupEndLayer : Layer
    {
        private readonly bool _isOpen;

        public GroupEndLayer(LayerRecord record, FileHeader header, bool isOpen) : base(record, header)
        {
            _isOpen = isOpen;
        }

        public override LayerKind Kind => LayerKind.GroupEnd;

        public bool IsOpen => _isOpen;
    }
}
