using System;
using System.Collections.Generic;

namespace PsdTools.Constants
{
    /// <summary>
    /// Layer blend modes (4-byte signature)
    /// </summary>
    public enum BlendMode
    {
        PassThrough,
        Normal,
        Dissolve,
        Darken,
        Multiply,
        ColorBurn,
        LinearBurn,
        DarkerColor,
        Lighten,
        Screen,
        ColorDodge,
        LinearDodge,
        LighterColor,
        Overlay,
        SoftLight,
        HardLight,
        VividLight,
        LinearLight,
        PinLight,
        HardMix,
        Difference,
        Exclusion,
        Subtract,
        Divide,
        Hue,
        Saturation,
        Color,
        Luminosity
    }

    public static class BlendModeHelper
    {
        private static readonly Dictionary<string, BlendMode> _keyToMode = new Dictionary<string, BlendMode>
        {
            { "pass", BlendMode.PassThrough },
            { "norm", BlendMode.Normal },
            { "diss", BlendMode.Dissolve },
            { "dark", BlendMode.Darken },
            { "mul ", BlendMode.Multiply },
            { "idiv", BlendMode.ColorBurn },
            { "lbrn", BlendMode.LinearBurn },
            { "dkCl", BlendMode.DarkerColor },
            { "lite", BlendMode.Lighten },
            { "scrn", BlendMode.Screen },
            { "div ", BlendMode.ColorDodge },
            { "lddg", BlendMode.LinearDodge },
            { "lgCl", BlendMode.LighterColor },
            { "over", BlendMode.Overlay },
            { "sLit", BlendMode.SoftLight },
            { "hLit", BlendMode.HardLight },
            { "vLit", BlendMode.VividLight },
            { "lLit", BlendMode.LinearLight },
            { "pLit", BlendMode.PinLight },
            { "hMix", BlendMode.HardMix },
            { "diff", BlendMode.Difference },
            { "smud", BlendMode.Exclusion },
            { "fsub", BlendMode.Subtract },
            { "fdiv", BlendMode.Divide },
            { "hue ", BlendMode.Hue },
            { "sat ", BlendMode.Saturation },
            { "colr", BlendMode.Color },
            { "lum ", BlendMode.Luminosity }
        };

        private static readonly Dictionary<BlendMode, string> _modeToKey = new Dictionary<BlendMode, string>();

        static BlendModeHelper()
        {
            foreach (var kvp in _keyToMode)
            {
                _modeToKey[kvp.Value] = kvp.Key;
            }
        }

        /// <summary>
        /// Parse blend mode from 4-byte signature
        /// </summary>
        public static BlendMode FromKey(string key)
        {
            if (_keyToMode.TryGetValue(key, out var mode))
                return mode;
            return BlendMode.Normal;
        }

        /// <summary>
        /// Get 4-byte signature for blend mode
        /// </summary>
        public static string ToKey(this BlendMode mode)
        {
            if (_modeToKey.TryGetValue(mode, out var key))
                return key;
            return "norm";
        }
    }
}
