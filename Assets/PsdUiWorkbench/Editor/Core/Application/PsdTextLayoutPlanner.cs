using System;
using System.Collections.Generic;
using System.Linq;

namespace PsdUiWorkbench.Core
{
    public static class PsdTextLayoutPlanner
    {
        public static PsdTextLayoutPlan Build(PsdTextSource source, PixelRect rootBounds, ISet<string> availableFonts)
        {
            if (!TryBuild(source, rootBounds, availableFonts, out var plan, out string reason))
                throw new InvalidOperationException(reason);
            return plan;
        }

        public static bool TryBuild(PsdTextSource source, PixelRect rootBounds, ISet<string> availableFonts,
            out PsdTextLayoutPlan plan, out string reason)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (availableFonts == null) throw new ArgumentNullException(nameof(availableFonts));
            if (!rootBounds.IsValid || !float.IsFinite(rootBounds.X) || !float.IsFinite(rootBounds.Y) || !float.IsFinite(rootBounds.Width) || !float.IsFinite(rootBounds.Height))
                throw new ArgumentException("文本容器矩形无效。");
            plan = null;
            var style = source.Spans[0].Style;
            if (source.Spans.Any(span => !SameStyle(style, span.Style)))
            {
                reason = "同层存在字体之外的样式差异，当前单 TMP 导出不支持。";
                return false;
            }
            // 优先保留正文占比最大的可用字体，同数量时按名称排序，不依赖资源搜索顺序。
            string font = source.Spans.Where(span => availableFonts.Contains(span.Style.FontKey))
                .GroupBy(span => span.Style.FontKey)
                .OrderByDescending(group => group.Sum(span => span.Length))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key).FirstOrDefault();
            if (string.IsNullOrEmpty(font))
            {
                reason = "整层没有可用的源字体，跳过且不生成占位文字。";
                return false;
            }
            bool substituted = source.Spans.Any(span => span.Style.FontKey != font);
            var normalized = new PsdTextStyle(font, style.FontSize, style.FauxBold, style.Tracking,
                style.Color, style.StrokePixels, style.StrokeColor, style.UseKerning);
            plan = new PsdTextLayoutPlan(source, rootBounds,
                new[] { new PsdTextRunPlan(source.Text, normalized, false) }, substituted);
            reason = "";
            return true;
        }

        private static bool SameStyle(PsdTextStyle a, PsdTextStyle b) =>
            a.FontSize == b.FontSize && a.FauxBold == b.FauxBold && a.Tracking == b.Tracking &&
            a.UseKerning == b.UseKerning && a.Color.Equals(b.Color) &&
            a.StrokePixels == b.StrokePixels && a.StrokeColor.Equals(b.StrokeColor);
    }
}
