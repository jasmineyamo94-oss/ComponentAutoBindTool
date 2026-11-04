namespace PsdTools.Constants
{
    /// <summary>
    /// Section divider types for layer groups
    /// </summary>
    public enum SectionDivider : uint
    {
        /// <summary>Any other type of layer</summary>
        Other = 0,
        
        /// <summary>Open folder (group end marker in flat list)</summary>
        OpenFolder = 1,
        
        /// <summary>Closed folder (group end marker in flat list)</summary>
        ClosedFolder = 2,
        
        /// <summary>Bounding section divider (group start marker in flat list)</summary>
        BoundingSectionDivider = 3
    }
}
