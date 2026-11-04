namespace PsdTools.Constants
{
    /// <summary>
    /// Layer clipping type
    /// </summary>
    public enum Clipping : byte
    {
        /// <summary>Base layer</summary>
        Base = 0,
        
        /// <summary>Non-base (clipped to layer below)</summary>
        NonBase = 1
    }
}
