using PsdTools.Constants;
using PsdTools.Psd;

namespace PsdTools.Layers
{
    /// <summary>
    /// Adjustment layer type
    /// </summary>
    public enum AdjustmentType
    {
        Unknown,
        SolidColor,
        GradientFill,
        PatternFill,
        BrightnessContrast,
        Levels,
        Curves,
        Exposure,
        Vibrance,
        HueSaturation,
        ColorBalance,
        BlackAndWhite,
        PhotoFilter,
        ChannelMixer,
        ColorLookup,
        Invert,
        Posterize,
        Threshold,
        GradientMap,
        SelectiveColor
    }

    /// <summary>
    /// Adjustment layer
    /// </summary>
    public class AdjustmentLayer : Layer
    {
        private AdjustmentType? _adjustmentType;

        public AdjustmentLayer(LayerRecord record, FileHeader header) : base(record, header)
        {
        }

        public override LayerKind Kind => LayerKind.Adjustment;

        /// <summary>Type of adjustment</summary>
        public AdjustmentType AdjustmentType
        {
            get
            {
                if (_adjustmentType.HasValue)
                    return _adjustmentType.Value;

                _adjustmentType = DetectAdjustmentType();
                return _adjustmentType.Value;
            }
        }

        private AdjustmentType DetectAdjustmentType()
        {
            var blocks = TaggedBlocks;
            if (blocks == null)
                return Layers.AdjustmentType.Unknown;

            if (blocks.Contains(Tag.SOLID_COLOR))
                return Layers.AdjustmentType.SolidColor;
            if (blocks.Contains(Tag.GRADIENT_FILL))
                return Layers.AdjustmentType.GradientFill;
            if (blocks.Contains(Tag.PATTERN_FILL))
                return Layers.AdjustmentType.PatternFill;
            if (blocks.Contains(Tag.BRIGHTNESS_CONTRAST))
                return Layers.AdjustmentType.BrightnessContrast;
            if (blocks.Contains(Tag.LEVELS))
                return Layers.AdjustmentType.Levels;
            if (blocks.Contains(Tag.CURVES))
                return Layers.AdjustmentType.Curves;
            if (blocks.Contains(Tag.EXPOSURE))
                return Layers.AdjustmentType.Exposure;
            if (blocks.Contains(Tag.VIBRANCE))
                return Layers.AdjustmentType.Vibrance;
            if (blocks.Contains(Tag.HUE_SATURATION))
                return Layers.AdjustmentType.HueSaturation;
            if (blocks.Contains(Tag.COLOR_BALANCE))
                return Layers.AdjustmentType.ColorBalance;
            if (blocks.Contains(Tag.BLACK_AND_WHITE))
                return Layers.AdjustmentType.BlackAndWhite;
            if (blocks.Contains(Tag.PHOTO_FILTER))
                return Layers.AdjustmentType.PhotoFilter;
            if (blocks.Contains(Tag.CHANNEL_MIXER))
                return Layers.AdjustmentType.ChannelMixer;
            if (blocks.Contains(Tag.COLOR_LOOKUP))
                return Layers.AdjustmentType.ColorLookup;
            if (blocks.Contains(Tag.INVERT))
                return Layers.AdjustmentType.Invert;
            if (blocks.Contains(Tag.POSTERIZE))
                return Layers.AdjustmentType.Posterize;
            if (blocks.Contains(Tag.THRESHOLD))
                return Layers.AdjustmentType.Threshold;
            if (blocks.Contains(Tag.GRADIENT_MAP))
                return Layers.AdjustmentType.GradientMap;
            if (blocks.Contains(Tag.SELECTIVE_COLOR))
                return Layers.AdjustmentType.SelectiveColor;

            return Layers.AdjustmentType.Unknown;
        }
    }
}
