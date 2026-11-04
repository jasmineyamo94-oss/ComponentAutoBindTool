using System;
using System.Collections.Generic;
using PsdTools.Constants;
using PsdTools.Psd;
using UnityEngine;

// Alias for layer effects and vector mask
using LayerEffects = PsdTools.Psd.LayerEffects;
using VectorMask = PsdTools.Psd.VectorMask;

namespace PsdTools.Layers
{
    /// <summary>
    /// Layer kind/type
    /// </summary>
    public enum LayerKind
    {
        Pixel,
        Group,
        Type,
        Shape,
        SmartObject,
        Adjustment,
        SolidColor,
        GradientFill,
        PatternFill,
        GroupEnd  // Internal marker
    }

    /// <summary>
    /// Base class for all layer types
    /// </summary>
    public class Layer
    {
        protected readonly LayerRecord _record;
        protected readonly FileHeader _header;
        protected Layer _parent;
        protected List<Layer> _children;

        public Layer(LayerRecord record, FileHeader header)
        {
            _record = record;
            _header = header;
            _children = new List<Layer>();
        }

        /// <summary>Layer name (readable and writable)</summary>
        public virtual string Name
        {
            get => _record?.GetName() ?? "";
            set
            {
                if (_record != null)
                    _record.SetName(value);
            }
        }

        /// <summary>Layer kind</summary>
        public virtual LayerKind Kind => LayerKind.Pixel;

        /// <summary>Left edge position</summary>
        public int Left => _record?.Left ?? 0;

        /// <summary>Top edge position</summary>
        public int Top => _record?.Top ?? 0;

        /// <summary>Right edge position</summary>
        public int Right => _record?.Right ?? 0;

        /// <summary>Bottom edge position</summary>
        public int Bottom => _record?.Bottom ?? 0;

        /// <summary>Layer width</summary>
        public int Width => _record?.Width ?? 0;

        /// <summary>Layer height</summary>
        public int Height => _record?.Height ?? 0;

        /// <summary>Bounding box (left, top, right, bottom)</summary>
        public (int Left, int Top, int Right, int Bottom) BBox => (Left, Top, Right, Bottom);

        /// <summary>Layer opacity (0-255)</summary>
        public byte Opacity => _record?.Opacity ?? 255;

        /// <summary>Layer opacity as float (0-1)</summary>
        public float OpacityFloat => Opacity / 255f;

        /// <summary>Whether layer is visible (readable and writable)</summary>
        public bool Visible
        {
            get => _record?.IsVisible ?? true;
            set
            {
                if (_record != null)
                    _record.SetVisible(value);
            }
        }

        /// <summary>Blend mode</summary>
        public BlendMode BlendMode => _record?.BlendMode ?? BlendMode.Normal;

        /// <summary>Clipping type</summary>
        public Clipping Clipping => _record?.Clipping ?? Clipping.Base;

        /// <summary>Whether this layer is clipped to the layer below</summary>
        public bool IsClipped => Clipping == Clipping.NonBase;

        /// <summary>Parent layer (group)</summary>
        public Layer Parent => _parent;

        /// <summary>Child layers (for groups)</summary>
        public IReadOnlyList<Layer> Children => _children;

        /// <summary>Whether this is a group layer</summary>
        public virtual bool IsGroup => false;

        /// <summary>Underlying layer record</summary>
        public LayerRecord Record => _record;

        /// <summary>Tagged blocks</summary>
        public TaggedBlocks TaggedBlocks => _record?.TaggedBlocks;

        /// <summary>Layer ID (if available)</summary>
        public int? LayerId => TaggedBlocks?.GetLayerId();

        /// <summary>Mask data</summary>
        public MaskData Mask => _record?.Mask;

        /// <summary>Whether layer has a mask</summary>
        public bool HasMask => Mask != null && Mask.Width > 0 && Mask.Height > 0;

        /// <summary>Cached layer effects</summary>
        private LayerEffects _effects;

        /// <summary>Cached vector mask</summary>
        private VectorMask _vectorMask;
        private bool _vectorMaskParsed;

        /// <summary>Layer effects</summary>
        public LayerEffects Effects
        {
            get
            {
                if (_effects != null)
                    return _effects;

                // Try lfx2 first (object-based effects)
                var data = TaggedBlocks?.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER1);
                if (data == null)
                    data = TaggedBlocks?.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER2);
                if (data == null)
                    data = TaggedBlocks?.GetData(Tag.EFFECTS_LAYER);

                if (data != null)
                {
                    _effects = LayerEffects.Parse(data);
                }

                return _effects;
            }
        }

        /// <summary>Whether layer has effects</summary>
        public bool HasEffects => Effects?.HasEffects == true;

        /// <summary>Vector mask</summary>
        public VectorMask VectorMask
        {
            get
            {
                if (_vectorMaskParsed)
                    return _vectorMask;

                _vectorMaskParsed = true;

                var data = TaggedBlocks?.GetData(Tag.VECTOR_MASK_SETTING1);
                if (data == null)
                    data = TaggedBlocks?.GetData(Tag.VECTOR_MASK_SETTING2);

                if (data != null)
                {
                    _vectorMask = VectorMask.Parse(data);
                }

                return _vectorMask;
            }
        }

        /// <summary>Whether layer has a vector mask</summary>
        public bool HasVectorMask => VectorMask != null;

        /// <summary>
        /// Set parent layer
        /// </summary>
        internal void SetParent(Layer parent)
        {
            _parent = parent;
        }

        /// <summary>
        /// Release cached effects / vectorMask parsing results to reduce strong references in the object graph.
        /// Called by <see cref="PsdImage.ReleaseAllData"/> when closing the PSD.
        /// </summary>
        internal void ClearCachedData()
        {
            _effects = null;
            _vectorMask = null;
            _vectorMaskParsed = false;
        }

        /// <summary>
        /// Add child layer
        /// </summary>
        internal void AddChild(Layer child)
        {
            _children.Add(child);
            child.SetParent(this);
        }

        /// <summary>
        /// Insert child layer at index
        /// </summary>
        internal void InsertChild(int index, Layer child)
        {
            _children.Insert(index, child);
            child.SetParent(this);
        }

        /// <summary>
        /// Get all descendant layers (recursive)
        /// </summary>
        public IEnumerable<Layer> Descendants()
        {
            foreach (var child in _children)
            {
                yield return child;
                foreach (var descendant in child.Descendants())
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// Get channel data by ID
        /// </summary>
        public byte[] GetChannelData(ChannelId channelId)
        {
            if (_record?.ChannelData == null)
                return null;

            for (int i = 0; i < _record.ChannelData.Count; i++)
            {
                if (_record.ChannelData[i].Id == channelId)
                {
                    return _record.ChannelData[i].GetData(Width, Height, _header.Depth, _header.Version);
                }
            }
            return null;
        }

        /// <summary>
        /// Get mask channel data (decompressed with mask dimensions)
        /// </summary>
        public byte[] GetMaskChannelData()
        {
            if (_record?.ChannelData == null || Mask == null || Mask.Width <= 0 || Mask.Height <= 0)
                return null;

            for (int i = 0; i < _record.ChannelData.Count; i++)
            {
                if (_record.ChannelData[i].Id == ChannelId.UserLayerMask)
                {
                    return _record.ChannelData[i].GetData(Mask.Width, Mask.Height, _header.Depth, _header.Version);
                }
            }
            return null;
        }

        /// <summary>
        /// Check if layer has pixel data
        /// </summary>
        public bool HasPixels()
        {
            if (_record?.ChannelData == null || _record.ChannelData.Count == 0)
                return false;
            if (Width <= 0 || Height <= 0)
                return false;
            return true;
        }

        /// <summary>
        /// Composite layer to Texture2D
        /// </summary>
        public virtual Texture2D Composite()
        {
            if (!HasPixels())
                return null;

            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            var pixels = new Color32[Width * Height];

            // Get channel data
            byte[] red = GetChannelData(ChannelId.Channel0);
            byte[] green = GetChannelData(ChannelId.Channel1);
            byte[] blue = GetChannelData(ChannelId.Channel2);
            byte[] alpha = GetChannelData(ChannelId.TransparencyMask);

            // Handle grayscale
            if (_header.ColorMode == ColorMode.Grayscale)
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

        public override string ToString()
        {
            return $"{Kind}: '{Name}' ({Left},{Top} - {Right},{Bottom})";
        }
    }
}
