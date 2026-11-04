namespace PsdTools.Constants
{
    /// <summary>
    /// Channel ID values in PSD
    /// </summary>
    public enum ChannelId : short
    {
        /// <summary>First color channel (Red in RGB, Cyan in CMYK)</summary>
        Channel0 = 0,
        
        /// <summary>Second color channel (Green in RGB, Magenta in CMYK)</summary>
        Channel1 = 1,
        
        /// <summary>Third color channel (Blue in RGB, Yellow in CMYK)</summary>
        Channel2 = 2,
        
        /// <summary>Fourth color channel (Black in CMYK)</summary>
        Channel3 = 3,
        
        /// <summary>Fifth color channel</summary>
        Channel4 = 4,
        
        /// <summary>Sixth color channel</summary>
        Channel5 = 5,
        
        /// <summary>Seventh color channel</summary>
        Channel6 = 6,
        
        /// <summary>Eighth color channel</summary>
        Channel7 = 7,
        
        /// <summary>Ninth color channel</summary>
        Channel8 = 8,
        
        /// <summary>Tenth color channel</summary>
        Channel9 = 9,
        
        /// <summary>Transparency mask (alpha channel)</summary>
        TransparencyMask = -1,
        
        /// <summary>User supplied layer mask</summary>
        UserLayerMask = -2,
        
        /// <summary>Real user layer mask (composite of vector and pixel masks)</summary>
        RealUserLayerMask = -3
    }
}
