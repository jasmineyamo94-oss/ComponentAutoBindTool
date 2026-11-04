namespace PsdTools.Constants
{
    /// <summary>
    /// Tagged block keys (4-byte signatures)
    /// </summary>
    public static class Tag
    {
        // Layer info
        public const string UNICODE_LAYER_NAME = "luni";
        public const string LAYER_ID = "lyid";
        public const string LAYER_NAME_SOURCE = "lnsr";
        
        // Section divider (groups)
        public const string SECTION_DIVIDER_SETTING = "lsct";
        public const string SECTION_DIVIDER_SETTING2 = "lsdk";
        
        // Text
        public const string TYPE_TOOL_INFO = "tySh";
        public const string TYPE_TOOL_OBJECT_SETTING = "TySh";
        
        // Shape/vector
        public const string VECTOR_MASK_SETTING1 = "vmsk";
        public const string VECTOR_MASK_SETTING2 = "vsms";
        public const string VECTOR_STROKE_DATA = "vstk";
        public const string VECTOR_STROKE_CONTENT_DATA = "vscg";
        public const string VECTOR_ORIGINATION_DATA = "vogk";
        
        // Smart object
        public const string SMART_OBJECT_LAYER_DATA1 = "SoLd";
        public const string SMART_OBJECT_LAYER_DATA2 = "SoLE";
        public const string PLACED_LAYER1 = "PlLd";
        public const string PLACED_LAYER2 = "plLd";
        
        // Effects
        public const string OBJECT_BASED_EFFECTS_LAYER1 = "lfx2";
        public const string OBJECT_BASED_EFFECTS_LAYER2 = "lmfx";
        public const string EFFECTS_LAYER = "lrFX";
        
        // Mask
        public const string USER_MASK = "LMsk";
        public const string FILTER_MASK = "FMsk";
        
        // Adjustments
        public const string SOLID_COLOR = "SoCo";
        public const string GRADIENT_FILL = "GdFl";
        public const string PATTERN_FILL = "PtFl";
        public const string BRIGHTNESS_CONTRAST = "brit";
        public const string LEVELS = "levl";
        public const string CURVES = "curv";
        public const string EXPOSURE = "expA";
        public const string VIBRANCE = "vibA";
        public const string HUE_SATURATION = "hue2";
        public const string COLOR_BALANCE = "blnc";
        public const string BLACK_AND_WHITE = "blwh";
        public const string PHOTO_FILTER = "phfl";
        public const string CHANNEL_MIXER = "mixr";
        public const string COLOR_LOOKUP = "clrL";
        public const string INVERT = "nvrt";
        public const string POSTERIZE = "post";
        public const string THRESHOLD = "thrs";
        public const string GRADIENT_MAP = "grdm";
        public const string SELECTIVE_COLOR = "selc";
        
        // Metadata
        public const string METADATA_SETTING = "shmd";
        public const string CONTENT_GENERATOR_EXTRA_DATA = "CgEd";
        
        // Layer properties
        public const string BLEND_CLIPPING_ELEMENTS = "clbl";
        public const string BLEND_INTERIOR_ELEMENTS = "infx";
        public const string KNOCKOUT_SETTING = "knko";
        public const string PROTECTED_SETTING = "lspf";
        public const string SHEET_COLOR_SETTING = "lclr";
        public const string REFERENCE_POINT = "fxrp";
        
        // Animation
        public const string ANIMATION_EFFECTS = "anFX";
        public const string TIMELINE = "tmln";
        
        // Patterns
        public const string PATTERNS1 = "Patt";
        public const string PATTERNS2 = "Pat2";
        public const string PATTERNS3 = "Pat3";
        
        // Artboard
        public const string ARTBOARD_DATA1 = "artb";
        public const string ARTBOARD_DATA2 = "artd";
        public const string ARTBOARD_DATA3 = "abdd";
        
        // Linked layers
        public const string LINKED_LAYER1 = "lnkD";
        public const string LINKED_LAYER2 = "lnk2";
        public const string LINKED_LAYER3 = "lnk3";
        public const string LINKED_LAYER_EXTERNAL = "lnkE";
        
        // Pixel source data (for 16-bit and 32-bit)
        public const string PIXEL_SOURCE_DATA1 = "PxSc";
        public const string PIXEL_SOURCE_DATA2 = "PxSD";
        
        // Annotations
        public const string ANNOTATIONS = "Anno";
        
        // Filter effects
        public const string FILTER_EFFECTS1 = "FXid";
        public const string FILTER_EFFECTS2 = "FEid";
        
        // Compositor used
        public const string COMPOSITOR_USED = "cinf";
        
        // Using aligned rendering
        public const string USING_ALIGNED_RENDERING = "sn2P";
        
        // Transparency shapes layer
        public const string TRANSPARENCY_SHAPES_LAYER = "tsly";
        
        // Layer mask as global mask
        public const string LAYER_MASK_AS_GLOBAL_MASK = "lmgm";
        
        // Vector mask as global mask
        public const string VECTOR_MASK_AS_GLOBAL_MASK = "vmgm";
        
        // Fill opacity
        public const string FILL_OPACITY = "iOpa";
        
        // Gradient fill
        public const string GRADIENT_FILL_SETTING = "GrFl";
        
        // Pattern data
        public const string PATTERN_DATA = "shpa";
        
        // Saving merged transparency
        public const string SAVING_MERGED_TRANSPARENCY = "Mtrn";
        public const string SAVING_MERGED_TRANSPARENCY16 = "Mt16";
        public const string SAVING_MERGED_TRANSPARENCY32 = "Mt32";
    }
}
