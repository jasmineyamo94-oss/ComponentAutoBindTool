namespace PsdTools.Constants
{
    /// <summary>
    /// PSD color mode values
    /// </summary>
    public enum ColorMode : ushort
    {
        Bitmap = 0,
        Grayscale = 1,
        Indexed = 2,
        RGB = 3,
        CMYK = 4,
        Multichannel = 7,
        Duotone = 8,
        Lab = 9
    }

    public static class ColorModeExtensions
    {
        /// <summary>
        /// Get the number of channels for a color mode
        /// </summary>
        public static int GetChannelCount(this ColorMode mode)
        {
            switch (mode)
            {
                case ColorMode.Bitmap:
                case ColorMode.Grayscale:
                case ColorMode.Indexed:
                case ColorMode.Duotone:
                    return 1;
                case ColorMode.RGB:
                case ColorMode.Lab:
                    return 3;
                case ColorMode.CMYK:
                    return 4;
                case ColorMode.Multichannel:
                    return 0; // Variable
                default:
                    return 0;
            }
        }
    }
}
