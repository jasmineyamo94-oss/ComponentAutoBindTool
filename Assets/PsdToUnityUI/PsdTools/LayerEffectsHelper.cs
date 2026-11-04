using System.Collections.Generic;
using PsdTools.Constants;
using PsdTools.Layers;
using UnityEngine;

namespace PsdTools
{
    /// <summary>
    /// Shared layer-effect helpers: color overlay, gradient overlay, layer mask, solid fill.
    /// Used by PsdImage (preview compositing) and PSDAutoPrefab (export slices).
    /// </summary>
    public static class LayerEffectsHelper
    {
        // ─────────────────────── Gradient stops ───────────────────────

        public struct GradientColorStop
        {
            public float position; // 0-1
            public byte r, g, b;
        }

        // ─────────────────────── Main entry ───────────────────────

        /// <summary>
        /// Builds the layer's processed texture (color overlay, gradient overlay, layer mask); not saved to disk.
        /// <para>Preview compositing (bakeLayerOpacity = false): do not bake LayerOpacity into alpha;
        /// the caller's blend loop applies opacity once to avoid double application.</para>
        /// <para>Export slices (bakeLayerOpacity = true, same idea as PSDAutoPrefab.CreateLayerTexture):
        /// bake all opacity into alpha.</para>
        /// </summary>
        public static Texture2D CreateLayerTextureWithEffects(Layer layer, bool bakeLayerOpacity = false)
        {
            Texture2D texture = layer.Composite();
            bool hasGradient = TryGetGradientOverlay(layer, out var gradStops, out float gradAngle, out float gradOpacity);
            float layerOpacity = bakeLayerOpacity ? layer.OpacityFloat : 1f;

            if (texture == null)
            {
                if (hasGradient)
                {
                    texture = CreateGradientTexture(layer.Width, layer.Height, gradStops, gradAngle, gradOpacity * layerOpacity);
                }
                else if (TryGetLayerColor(layer, out Color32 fallbackColor, out float colorOpacity))
                {
                    texture = CreateSolidColorTexture(layer.Width, layer.Height, fallbackColor, layerOpacity * colorOpacity);
                }
                else
                {
                    return null;
                }
            }
            else
            {
                if (hasGradient)
                    ApplyGradientOverlay(texture, gradStops, gradAngle, gradOpacity, layerOpacity);
                else
                    ApplyFillColorAndOpacityInternal(texture, layer, layerOpacity);
            }

            ApplyLayerMask(texture, layer);
            return texture;
        }

        // ─────────────────────── Color extraction ───────────────────────

        public static bool HasExtractableColor(Layer layer)
        {
            var blocks = layer.TaggedBlocks;
            if (blocks == null) return false;
            return blocks.Contains(Tag.SOLID_COLOR) ||
                   blocks.Contains(Tag.VECTOR_STROKE_CONTENT_DATA) ||
                   blocks.Contains(Tag.OBJECT_BASED_EFFECTS_LAYER1) ||
                   blocks.Contains(Tag.OBJECT_BASED_EFFECTS_LAYER2);
        }

        /// <summary>Resolve layer color with priority: color overlay &gt; solid fill &gt; none.</summary>
        public static bool TryGetLayerColor(Layer layer, out Color32 color, out float colorOpacity)
        {
            if (TryGetColorOverlay(layer, out color, out colorOpacity))
                return true;

            if (TryGetSolidColor(layer, out color))
            {
                colorOpacity = 1f;
                return true;
            }

            colorOpacity = 1f;
            return false;
        }

        /// <summary>Try to read solid fill color from the layer (SoCo / vscg).</summary>
        public static bool TryGetSolidColor(Layer layer, out Color32 color)
        {
            color = default;
            var blocks = layer.TaggedBlocks;
            if (blocks == null) return false;

            var socoData = blocks.GetData(Tag.SOLID_COLOR);
            if (socoData != null)
            {
                int clrPos = FindPattern(socoData, "Clr ");
                if (clrPos >= 0 && TryReadRGBFromRawBytes(socoData, clrPos, out byte r, out byte g, out byte b))
                {
                    color = new Color32(r, g, b, 255);
                    return true;
                }
            }

            var vscgData = blocks.GetData(Tag.VECTOR_STROKE_CONTENT_DATA);
            if (vscgData != null)
            {
                int clrPos = FindPattern(vscgData, "Clr ");
                if (clrPos >= 0 && TryReadRGBFromRawBytes(vscgData, clrPos, out byte r, out byte g, out byte b))
                {
                    color = new Color32(r, g, b, 255);
                    return true;
                }
            }

            return false;
        }

        /// <summary>Try to read color overlay from layer effects (Color Overlay / SoFi).</summary>
        public static bool TryGetColorOverlay(Layer layer, out Color32 color, out float overlayOpacity)
        {
            color = default;
            overlayOpacity = 1f;
            var blocks = layer.TaggedBlocks;
            if (blocks == null) return false;

            byte[] data = blocks.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER1);
            if (data == null)
                data = blocks.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER2);
            if (data == null) return false;

            int sofiPos = FindPattern(data, "SoFi");
            if (sofiPos < 0) return false;

            int enabPos = FindPattern(data, "enab", sofiPos);
            if (enabPos >= 0 && enabPos < sofiPos + 200)
            {
                int boolPos = FindPattern(data, "bool", enabPos + 4);
                if (boolPos >= 0 && boolPos < enabPos + 20 && boolPos + 5 <= data.Length)
                {
                    if (data[boolPos + 4] == 0)
                        return false;
                }
            }

            int clrPos = FindPattern(data, "Clr ", sofiPos);
            if (clrPos < 0 || clrPos > sofiPos + 500) return false;

            if (!TryReadRGBFromRawBytes(data, clrPos, out byte rr, out byte gg, out byte bb))
                return false;

            int opctPos = FindPattern(data, "Opct", sofiPos);
            if (opctPos >= 0 && opctPos < clrPos)
            {
                int untfPos = FindPattern(data, "UntF", opctPos + 4);
                if (untfPos >= 0 && untfPos < opctPos + 20)
                {
                    int prcPos = FindPattern(data, "#Prc", untfPos + 4);
                    if (prcPos >= 0 && prcPos < untfPos + 12 && prcPos + 12 <= data.Length)
                    {
                        double pct = ReadBigEndianDouble(data, prcPos + 4);
                        overlayOpacity = Mathf.Clamp01((float)pct / 100f);
                    }
                }
            }

            color = new Color32(rr, gg, bb, 255);
            return true;
        }

        // ─────────────────────── Color / gradient application ───────────────────────

        /// <summary>Apply color to a composited texture (replace RGB, keep alpha) and layer opacity (layer.OpacityFloat).</summary>
        public static void ApplyFillColorAndOpacity(Texture2D texture, Layer layer)
            => ApplyFillColorAndOpacityInternal(texture, layer, layer.OpacityFloat);

        private static void ApplyFillColorAndOpacityInternal(Texture2D texture, Layer layer, float layerOpacity)
        {
            bool hasColor = TryGetLayerColor(layer, out Color32 fillColor, out float colorOpacity);
            float totalAlphaScale = layerOpacity * colorOpacity;
            bool needsAlphaScale = totalAlphaScale < 0.999f;

            if (!hasColor && !needsAlphaScale) return;

            Color32[] pixels = texture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                if (hasColor)
                {
                    pixels[i].r = fillColor.r;
                    pixels[i].g = fillColor.g;
                    pixels[i].b = fillColor.b;
                }
                if (needsAlphaScale)
                    pixels[i].a = (byte)Mathf.RoundToInt(pixels[i].a * totalAlphaScale);
            }
            texture.SetPixels32(pixels);
            texture.Apply();
        }

        /// <summary>Create a solid-fill texture (shape/fill layers with no pixel data).</summary>
        public static Texture2D CreateSolidColorTexture(int width, int height, Color32 color, float opacity)
        {
            byte a = (byte)Mathf.RoundToInt(color.a * opacity);
            Color32 finalColor = new Color32(color.r, color.g, color.b, a);
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = finalColor;
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        // ─────────────────────── Gradient overlay ───────────────────────

        /// <summary>Try to read gradient overlay (Gradient Overlay / GrFl) from layer effect data.</summary>
        public static bool TryGetGradientOverlay(Layer layer, out List<GradientColorStop> stops,
            out float angle, out float opacity)
        {
            stops = null;
            angle = 90f;
            opacity = 1f;
            var blocks = layer.TaggedBlocks;
            if (blocks == null) return false;

            byte[] data = blocks.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER1);
            if (data == null)
                data = blocks.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER2);
            if (data == null) return false;

            int grflPos = FindPattern(data, "GrFl");
            if (grflPos < 0) return false;

            int enabPos = FindPattern(data, "enab", grflPos);
            if (enabPos >= 0 && enabPos < grflPos + 200)
            {
                int boolPos = FindPattern(data, "bool", enabPos + 4);
                if (boolPos >= 0 && boolPos < enabPos + 20 && boolPos + 5 <= data.Length)
                {
                    if (data[boolPos + 4] == 0)
                        return false;
                }
            }

            int opctPos = FindPattern(data, "Opct", grflPos);
            if (opctPos >= 0 && opctPos < grflPos + 300)
            {
                int untfPos = FindPattern(data, "UntF", opctPos + 4);
                if (untfPos >= 0 && untfPos < opctPos + 20)
                {
                    int prcPos = FindPattern(data, "#Prc", untfPos + 4);
                    if (prcPos >= 0 && prcPos < untfPos + 12 && prcPos + 12 <= data.Length)
                    {
                        double pct = ReadBigEndianDouble(data, prcPos + 4);
                        opacity = Mathf.Clamp01((float)pct / 100f);
                    }
                }
            }

            int anglPos = FindPattern(data, "Angl", grflPos);
            if (anglPos >= 0 && anglPos < grflPos + 500)
            {
                int untfPos = FindPattern(data, "UntF", anglPos + 4);
                if (untfPos >= 0 && untfPos < anglPos + 20 && untfPos + 16 <= data.Length)
                {
                    double a = ReadBigEndianDouble(data, untfPos + 8);
                    angle = (float)a;
                }
            }

            int clrsPos = FindPattern(data, "Clrs", grflPos);
            if (clrsPos < 0) return false;

            int vlLsPos = FindPattern(data, "VlLs", clrsPos);
            if (vlLsPos < 0 || vlLsPos > clrsPos + 20) return false;

            int stopCount = ReadBigEndianInt32(data, vlLsPos + 4);
            if (stopCount < 2 || stopCount > 100) return false;

            int trnsPos = FindPattern(data, "Trns", vlLsPos);
            int colorRegionEnd = trnsPos > 0 ? trnsPos : data.Length;

            stops = new List<GradientColorStop>();
            int searchFrom = vlLsPos + 8;

            for (int i = 0; i < stopCount; i++)
            {
                int clrPos = FindPattern(data, "Clr ", searchFrom);
                if (clrPos < 0 || clrPos >= colorRegionEnd) break;

                int lctnPos = FindPattern(data, "Lctn", clrPos);
                if (lctnPos < 0 || lctnPos >= colorRegionEnd) break;

                if (!TryReadRGBFromRawBytes(data, clrPos, out byte r, out byte g, out byte b))
                    break;

                int longPos = FindPattern(data, "long", lctnPos + 4);
                if (longPos < 0 || longPos > lctnPos + 20 || longPos + 8 > data.Length)
                    break;

                int lctnValue = ReadBigEndianInt32(data, longPos + 4);
                float pos = Mathf.Clamp01(lctnValue / 4096f);

                stops.Add(new GradientColorStop { position = pos, r = r, g = g, b = b });
                searchFrom = longPos + 8;
            }

            if (stops.Count < 2) { stops = null; return false; }

            stops.Sort((a, b) => a.position.CompareTo(b.position));
            return true;
        }

        /// <summary>Interpolate color at parameter t along gradient color stops.</summary>
        public static Color32 SampleGradient(List<GradientColorStop> stops, float t)
        {
            t = Mathf.Clamp01(t);
            if (t <= stops[0].position)
                return new Color32(stops[0].r, stops[0].g, stops[0].b, 255);
            if (t >= stops[stops.Count - 1].position)
            {
                var last = stops[stops.Count - 1];
                return new Color32(last.r, last.g, last.b, 255);
            }
            for (int i = 0; i < stops.Count - 1; i++)
            {
                if (t >= stops[i].position && t <= stops[i + 1].position)
                {
                    float range = stops[i + 1].position - stops[i].position;
                    float lerp = range > 0.0001f ? (t - stops[i].position) / range : 0f;
                    byte r = (byte)Mathf.RoundToInt(Mathf.Lerp(stops[i].r, stops[i + 1].r, lerp));
                    byte g = (byte)Mathf.RoundToInt(Mathf.Lerp(stops[i].g, stops[i + 1].g, lerp));
                    byte b = (byte)Mathf.RoundToInt(Mathf.Lerp(stops[i].b, stops[i + 1].b, lerp));
                    return new Color32(r, g, b, 255);
                }
            }
            var fallback = stops[stops.Count - 1];
            return new Color32(fallback.r, fallback.g, fallback.b, 255);
        }

        /// <summary>Apply gradient overlay to a composited texture (replace RGB; scale alpha by overlay opacity).</summary>
        public static void ApplyGradientOverlay(Texture2D texture, List<GradientColorStop> stops,
            float angleDeg, float opacity, float layerOpacity)
        {
            int w = texture.width;
            int h = texture.height;
            float totalAlpha = opacity * layerOpacity;

            float rad = angleDeg * Mathf.Deg2Rad;
            float dx = Mathf.Cos(rad);
            float dy = Mathf.Sin(rad);

            Color32[] pixels = texture.GetPixels32();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float nx = (float)x / Mathf.Max(1, w - 1);
                    float ny = 1f - (float)y / Mathf.Max(1, h - 1);
                    float cx = nx - 0.5f;
                    float cy = ny - 0.5f;
                    float t = cx * dx + cy * dy + 0.5f;

                    Color32 gc = SampleGradient(stops, t);
                    int idx = y * w + x;
                    pixels[idx].r = gc.r;
                    pixels[idx].g = gc.g;
                    pixels[idx].b = gc.b;
                    if (totalAlpha < 0.999f)
                        pixels[idx].a = (byte)Mathf.RoundToInt(pixels[idx].a * totalAlpha);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
        }

        /// <summary>Create a gradient-only texture (no pixel data but gradient overlay).</summary>
        public static Texture2D CreateGradientTexture(int width, int height,
            List<GradientColorStop> stops, float angleDeg, float opacity)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];

            float rad = angleDeg * Mathf.Deg2Rad;
            float dx = Mathf.Cos(rad);
            float dy = Mathf.Sin(rad);
            byte alphaByte = (byte)Mathf.RoundToInt(Mathf.Clamp01(opacity) * 255f);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float nx = (float)x / Mathf.Max(1, width - 1);
                    float ny = 1f - (float)y / Mathf.Max(1, height - 1);
                    float cx = nx - 0.5f;
                    float cy = ny - 0.5f;
                    float t = cx * dx + cy * dy + 0.5f;

                    Color32 gc = SampleGradient(stops, t);
                    gc.a = alphaByte;
                    pixels[y * width + x] = gc;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        // ─────────────────────── Layer mask ───────────────────────

        /// <summary>Apply the layer mask to a texture (multiply alpha by mask grayscale).</summary>
        public static void ApplyLayerMask(Texture2D texture, Layer layer)
        {
            if (!layer.HasMask || layer.Mask.IsDisabled)
                return;

            byte[] maskData = layer.GetMaskChannelData();
            if (maskData == null || maskData.Length == 0)
                return;

            var mask = layer.Mask;
            int maskW = mask.Width;
            int maskH = mask.Height;
            byte defaultColor = mask.DefaultColor;

            int texW = texture.width;
            int texH = texture.height;
            Color32[] pixels = texture.GetPixels32();

            for (int psdY = 0; psdY < layer.Height; psdY++)
            {
                for (int psdX = 0; psdX < layer.Width; psdX++)
                {
                    int worldX = layer.Left + psdX;
                    int worldY = layer.Top  + psdY;

                    int mx = worldX - mask.Left;
                    int my = worldY - mask.Top;

                    byte maskValue;
                    if (mx >= 0 && mx < maskW && my >= 0 && my < maskH)
                    {
                        int maskIdx = my * maskW + mx;
                        maskValue = (maskIdx < maskData.Length) ? maskData[maskIdx] : defaultColor;
                    }
                    else
                    {
                        maskValue = defaultColor;
                    }

                    if (maskValue == 255) continue;

                    int texIdx = (texH - 1 - psdY) * texW + psdX;
                    if (texIdx < 0 || texIdx >= pixels.Length) continue;

                    pixels[texIdx].a = (byte)(pixels[texIdx].a * maskValue / 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
        }

        // ─────────────────────── Binary helpers ───────────────────────

        public static int FindPattern(byte[] data, string pattern, int startIndex = 0)
        {
            byte[] patternBytes = System.Text.Encoding.ASCII.GetBytes(pattern);
            for (int i = startIndex; i <= data.Length - patternBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < patternBytes.Length; j++)
                {
                    if (data[i + j] != patternBytes[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        public static double ReadBigEndianDouble(byte[] data, int offset)
        {
            if (offset + 8 > data.Length) return 0;
            byte[] temp = new byte[8];
            System.Array.Copy(data, offset, temp, 0, 8);
            if (System.BitConverter.IsLittleEndian)
                System.Array.Reverse(temp);
            return System.BitConverter.ToDouble(temp, 0);
        }

        public static int ReadBigEndianInt32(byte[] data, int offset)
        {
            if (offset + 4 > data.Length) return 0;
            return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }

        public static bool TryReadRGBFromRawBytes(byte[] data, int searchStart,
            out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            int rdPos = FindPattern(data, "Rd  ", searchStart);
            if (rdPos < 0) return false;
            int rdDoub = FindPattern(data, "doub", rdPos + 4);
            if (rdDoub < 0 || rdDoub > rdPos + 20) return false;
            double rdVal = ReadBigEndianDouble(data, rdDoub + 4);

            int grnPos = FindPattern(data, "Grn ", rdDoub);
            if (grnPos < 0) return false;
            int grnDoub = FindPattern(data, "doub", grnPos + 4);
            if (grnDoub < 0 || grnDoub > grnPos + 20) return false;
            double grnVal = ReadBigEndianDouble(data, grnDoub + 4);

            int blPos = FindPattern(data, "Bl  ", grnDoub);
            if (blPos < 0) return false;
            int blDoub = FindPattern(data, "doub", blPos + 4);
            if (blDoub < 0 || blDoub > blPos + 20) return false;
            double blVal = ReadBigEndianDouble(data, blDoub + 4);

            r = (byte)Mathf.Clamp(Mathf.RoundToInt((float)rdVal), 0, 255);
            g = (byte)Mathf.Clamp(Mathf.RoundToInt((float)grnVal), 0, 255);
            b = (byte)Mathf.Clamp(Mathf.RoundToInt((float)blVal), 0, 255);
            return true;
        }
    }
}
