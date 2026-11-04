using System;
using System.Collections.Generic;
using PsdTools.Constants;
using PsdTools.Utils;
using UnityEngine;

namespace PsdTools.Psd
{
    /// <summary>
    /// Effect type
    /// </summary>
    public enum EffectType
    {
        Unknown,
        DropShadow,
        InnerShadow,
        OuterGlow,
        InnerGlow,
        Bevel,
        Satin,
        ColorOverlay,
        GradientOverlay,
        PatternOverlay,
        Stroke
    }

    /// <summary>
    /// Base class for layer effects
    /// </summary>
    public abstract class LayerEffect
    {
        /// <summary>Effect type</summary>
        public abstract EffectType Type { get; }

        /// <summary>Whether effect is enabled</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Effect opacity (0-255)</summary>
        public byte Opacity { get; set; } = 255;

        /// <summary>Blend mode</summary>
        public BlendMode BlendMode { get; set; } = BlendMode.Normal;
    }

    /// <summary>
    /// Drop shadow effect
    /// </summary>
    public class DropShadowEffect : LayerEffect
    {
        public override EffectType Type => EffectType.DropShadow;

        /// <summary>Shadow color</summary>
        public Color Color { get; set; } = Color.black;

        /// <summary>Blur radius in pixels</summary>
        public float Blur { get; set; }

        /// <summary>Shadow distance in pixels</summary>
        public float Distance { get; set; }

        /// <summary>Shadow angle in degrees</summary>
        public float Angle { get; set; }

        /// <summary>Spread (choke) percentage</summary>
        public float Spread { get; set; }

        /// <summary>Use global light</summary>
        public bool UseGlobalLight { get; set; }
    }

    /// <summary>
    /// Inner shadow effect
    /// </summary>
    public class InnerShadowEffect : LayerEffect
    {
        public override EffectType Type => EffectType.InnerShadow;

        public Color Color { get; set; } = Color.black;
        public float Blur { get; set; }
        public float Distance { get; set; }
        public float Angle { get; set; }
        public float Choke { get; set; }
        public bool UseGlobalLight { get; set; }
    }

    /// <summary>
    /// Outer glow effect
    /// </summary>
    public class OuterGlowEffect : LayerEffect
    {
        public override EffectType Type => EffectType.OuterGlow;

        public Color Color { get; set; } = Color.white;
        public float Blur { get; set; }
        public float Spread { get; set; }
    }

    /// <summary>
    /// Inner glow effect
    /// </summary>
    public class InnerGlowEffect : LayerEffect
    {
        public override EffectType Type => EffectType.InnerGlow;

        public Color Color { get; set; } = Color.white;
        public float Blur { get; set; }
        public float Choke { get; set; }
    }

    /// <summary>
    /// Stroke effect
    /// </summary>
    public class StrokeEffect : LayerEffect
    {
        public override EffectType Type => EffectType.Stroke;

        /// <summary>Stroke color</summary>
        public Color Color { get; set; } = Color.black;

        /// <summary>Stroke width in pixels</summary>
        public float Size { get; set; }

        /// <summary>Stroke position (0=outside, 1=inside, 2=center)</summary>
        public int Position { get; set; }
    }

    /// <summary>
    /// Color overlay effect
    /// </summary>
    public class ColorOverlayEffect : LayerEffect
    {
        public override EffectType Type => EffectType.ColorOverlay;

        public Color Color { get; set; } = Color.white;
    }

    /// <summary>
    /// Collection of layer effects
    /// </summary>
    public class LayerEffects
    {
        private readonly List<LayerEffect> _effects;

        public LayerEffects()
        {
            _effects = new List<LayerEffect>();
        }

        /// <summary>All effects</summary>
        public IReadOnlyList<LayerEffect> Effects => _effects;

        /// <summary>Whether layer has effects</summary>
        public bool HasEffects => _effects.Count > 0;

        /// <summary>Master effects enabled</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Effects scale</summary>
        public float Scale { get; set; } = 100f;

        /// <summary>Add an effect</summary>
        public void Add(LayerEffect effect)
        {
            _effects.Add(effect);
        }

        /// <summary>Get effect by type</summary>
        public T GetEffect<T>() where T : LayerEffect
        {
            foreach (var effect in _effects)
            {
                if (effect is T typed)
                    return typed;
            }
            return null;
        }

        /// <summary>
        /// Parse effects from tagged block data
        /// </summary>
        public static LayerEffects Parse(byte[] data)
        {
            if (data == null || data.Length < 8)
                return null;

            var effects = new LayerEffects();

            try
            {
                using (var reader = new BigEndianReader(data))
                {
                    // Version
                    ushort version = reader.ReadUInt16();

                    // Descriptor version
                    uint descVersion = reader.ReadUInt32();

                    // Parse descriptor data
                    // This is a simplified parser - full descriptor parsing is complex
                    ParseEffectsDescriptor(reader, effects);
                }
            }
            catch
            {
                // Parsing failed
            }

            return effects;
        }

        private static void ParseEffectsDescriptor(BigEndianReader reader, LayerEffects effects)
        {
            // Adobe descriptor format parsing
            // For now, implement a basic parser for common effects

            // Try to find known effect patterns in the data
            var remaining = reader.ReadToEnd();
            reader.Seek(0, System.IO.SeekOrigin.Begin);

            // Look for drop shadow signature
            if (ContainsPattern(remaining, "DrSh"))
            {
                effects.Add(new DropShadowEffect());
            }

            if (ContainsPattern(remaining, "IrSh"))
            {
                effects.Add(new InnerShadowEffect());
            }

            if (ContainsPattern(remaining, "OrGl"))
            {
                effects.Add(new OuterGlowEffect());
            }

            if (ContainsPattern(remaining, "IrGl"))
            {
                effects.Add(new InnerGlowEffect());
            }

            if (ContainsPattern(remaining, "FrFX"))
            {
                effects.Add(new StrokeEffect());
            }

            if (ContainsPattern(remaining, "SoFi"))
            {
                effects.Add(new ColorOverlayEffect());
            }
        }

        private static bool ContainsPattern(byte[] data, string pattern)
        {
            var patternBytes = System.Text.Encoding.ASCII.GetBytes(pattern);
            for (int i = 0; i <= data.Length - patternBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < patternBytes.Length; j++)
                {
                    if (data[i + j] != patternBytes[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return true;
            }
            return false;
        }
    }
}
