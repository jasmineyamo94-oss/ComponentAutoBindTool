namespace PsdTools.Constants
{
    /// <summary>
    /// Image data compression types
    /// </summary>
    public enum CompressionType : ushort
    {
        /// <summary>Raw image data</summary>
        Raw = 0,
        
        /// <summary>RLE compressed (PackBits)</summary>
        Rle = 1,
        
        /// <summary>ZIP without prediction</summary>
        Zip = 2,
        
        /// <summary>ZIP with prediction</summary>
        ZipWithPrediction = 3
    }
}
