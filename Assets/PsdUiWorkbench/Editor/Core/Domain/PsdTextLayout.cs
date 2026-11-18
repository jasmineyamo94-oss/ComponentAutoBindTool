using System;
using System.Collections.Generic;
using System.Linq;

namespace PsdUiWorkbench.Core
{
    public enum PsdTextAlignment { Left, Center }

    public readonly struct PsdTextColor
    {
        public readonly float R, G, B, A;
        public PsdTextColor(float r, float g, float b, float a)
        {
            if (!Valid(r) || !Valid(g) || !Valid(b) || !Valid(a)) throw new ArgumentException("文字颜色必须在 0～1 之间。");
            R = r; G = g; B = b; A = a;
        }
        private static bool Valid(float value) => !float.IsNaN(value) && value >= 0 && value <= 1;
        public static PsdTextColor White => new PsdTextColor(1, 1, 1, 1);
    }

    public sealed class PsdTextStyle
    {
        public string FontKey { get; }
        public float FontSize { get; }
        public bool FauxBold { get; }
        public float Tracking { get; }
        public bool UseKerning { get; }
        public PsdTextColor Color { get; }
        public float StrokePixels { get; }
        public PsdTextColor StrokeColor { get; }
        public PsdTextStyle(string fontKey, float fontSize, bool fauxBold, float tracking, PsdTextColor color, float strokePixels, PsdTextColor strokeColor, bool useKerning = false)
        {
            if (!float.IsFinite(fontSize) || fontSize <= 0 || !float.IsFinite(tracking) || !float.IsFinite(strokePixels) || strokePixels < 0)
                throw new ArgumentException("文字字号、字距或描边尺寸无效。");
            FontKey = fontKey ?? ""; FontSize = fontSize; FauxBold = fauxBold; Tracking = tracking;
            UseKerning = useKerning;
            Color = color; StrokePixels = strokePixels; StrokeColor = strokeColor;
        }
    }

    public sealed class PsdTextSourceSpan
    {
        public int Start { get; }
        public int Length { get; }
        public PsdTextStyle Style { get; }
        public PsdTextSourceSpan(int start, int length, PsdTextStyle style)
        {
            if (start < 0 || length <= 0) throw new ArgumentException("文字段范围无效。");
            Start = start; Length = length; Style = style ?? throw new ArgumentNullException(nameof(style));
        }
    }

    public sealed class PsdTextSource
    {
        public int LayerId { get; }
        public string Text { get; }
        public float BaselineX { get; }
        public float BaselineY { get; }
        public PsdTextAlignment Alignment { get; }
        public float? ReferenceWidth { get; }
        public IReadOnlyList<PsdTextSourceSpan> Spans { get; }
        public PsdTextSource(int layerId, string text, float baselineX, float baselineY, PsdTextAlignment alignment, IEnumerable<PsdTextSourceSpan> spans, float? referenceWidth = null)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOfAny(new[] {'\r', '\n'}) >= 0)
                throw new ArgumentException("当前布局模块仅支持非空的单行点文本。");
            if (!float.IsFinite(baselineX) || !float.IsFinite(baselineY) || !Enum.IsDefined(typeof(PsdTextAlignment), alignment))
                throw new ArgumentException("文字基线或对齐方式无效。");
            if (referenceWidth.HasValue && (!float.IsFinite(referenceWidth.Value) || referenceWidth.Value <= 0))
                throw new ArgumentException("源排版框宽度无效。");
            var list = (spans ?? throw new ArgumentNullException(nameof(spans))).ToArray();
            int end = 0;
            foreach (var span in list)
            {
                if (span == null || span.Start != end || span.Length > text.Length - end) throw new ArgumentException("文字段必须连续且完整覆盖文本。");
                end += span.Length;
                if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
                    throw new ArgumentException("文字段不能拆开 UTF-16 代理对。");
            }
            if (end != text.Length) throw new ArgumentException("文字段没有完整覆盖文本。");
            LayerId = layerId; Text = text; BaselineX = baselineX; BaselineY = baselineY; Alignment = alignment;
            ReferenceWidth = referenceWidth;
            Spans = Array.AsReadOnly(list);
        }
    }

    public sealed class PsdTextRunPlan
    {
        public string Text { get; }
        public PsdTextStyle Style { get; }
        public bool IsPlaceholder { get; }
        internal PsdTextRunPlan(string text, PsdTextStyle style, bool placeholder)
        {
            Text = text; Style = style; IsPlaceholder = placeholder;
        }
    }

    public sealed class PsdTextLayoutPlan
    {
        public int LayerId { get; }
        public float BaselineX { get; }
        public float BaselineY { get; }
        public PsdTextAlignment Alignment { get; }
        public float? ReferenceWidth { get; }
        public bool FontSubstituted { get; }
        public IReadOnlyList<PsdTextRunPlan> Runs { get; }
        internal PsdTextLayoutPlan(PsdTextSource source, PixelRect root, PsdTextRunPlan[] runs, bool fontSubstituted = false)
        {
            LayerId = source.LayerId; Alignment = source.Alignment;
            FontSubstituted = fontSubstituted;
            ReferenceWidth = fontSubstituted ? null : source.ReferenceWidth;
            BaselineX = source.BaselineX - root.X - root.Width / 2;
            BaselineY = root.Y + root.Height / 2 - source.BaselineY;
            Runs = Array.AsReadOnly(runs);
        }
    }
}
