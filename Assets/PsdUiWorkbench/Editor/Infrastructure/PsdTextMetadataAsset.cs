using System;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEngine;

namespace PsdUiWorkbench.Infrastructure
{
    public sealed class PsdTextMetadataAsset : ScriptableObject
    {
        public const int CurrentVersion = 4;
        [Serializable] private sealed class Span
        {
            public int Start, Length;
            public string Font;
            public float Size, Tracking, Stroke;
            public bool Bold, UseKerning;
            public Color Fill, Outline;
        }
        [Serializable] private sealed class Entry
        {
            public int Id;
            public string Text;
            public float X, Y, ReferenceWidth;
            public PsdTextAlignment Alignment;
            public Span[] Spans;
        }
        [Serializable] private sealed class RejectedEntry
        {
            public int Id;
            public string Reason;
        }
        [SerializeField] private int _version;
        [SerializeField] private PsdMetadataStatus _status;
        [SerializeField] private string _diagnostic;
        [SerializeField] private Entry[] _entries;
        [SerializeField] private RejectedEntry[] _rejectedLayers;
        public int Version => _version;
        public PsdTextMetadataResult Read()
        {
            if (_version != CurrentVersion) return new PsdTextMetadataResult(PsdMetadataStatus.Unsupported, "文本缓存版本不兼容，请重新导入 PSD。");
            if (!Enum.IsDefined(typeof(PsdMetadataStatus), _status)) return new PsdTextMetadataResult(PsdMetadataStatus.Invalid, "文本缓存状态无效。");
            if (_status == PsdMetadataStatus.Invalid || _status == PsdMetadataStatus.Unsupported) return new PsdTextMetadataResult(_status, _diagnostic);
            try
            {
                if (_entries == null || _entries.Any(e => e == null || e.Spans == null || e.Spans.Any(s => s == null)) ||
                    _rejectedLayers == null || _rejectedLayers.Any(e => e == null || string.IsNullOrWhiteSpace(e.Reason))) throw new ArgumentException();
                var sources = _entries.ToDictionary(e => e.Id, e => new PsdTextSource(e.Id, e.Text, e.X, e.Y, e.Alignment,
                    e.Spans.Select(s => new PsdTextSourceSpan(s.Start, s.Length, new PsdTextStyle(s.Font, s.Size, s.Bold, s.Tracking, FromColor(s.Fill), s.Stroke, FromColor(s.Outline), s.UseKerning))),
                    e.ReferenceWidth > 0 ? (float?)e.ReferenceWidth : null));
                var rejectedLayers = _rejectedLayers.ToDictionary(e => e.Id, e => e.Reason);
                // 部分成功必须能准确指出拒绝层，否则不能安全过滤原始 Image。
                if (rejectedLayers.Keys.Any(sources.ContainsKey) ||
                    ((_status == PsdMetadataStatus.Partial) != (rejectedLayers.Count > 0))) throw new ArgumentException();
                return new PsdTextMetadataResult(_status, _diagnostic, sources, rejectedLayers);
            }
            catch (ArgumentException) { return new PsdTextMetadataResult(PsdMetadataStatus.Invalid, "文本缓存条目无效，请重新导入 PSD。"); }
        }
        internal void Initialize(PsdTextMetadataResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            _version = CurrentVersion; _status = result.Status; _diagnostic = result.Diagnostic;
            _entries = result.Sources.Values.Select(e => new Entry
            {
                Id = e.LayerId, Text = e.Text, X = e.BaselineX, Y = e.BaselineY, Alignment = e.Alignment,
                ReferenceWidth = e.ReferenceWidth ?? 0,
                Spans = e.Spans.Select(s => new Span { Start = s.Start, Length = s.Length, Font = s.Style.FontKey, Size = s.Style.FontSize,
                    Bold = s.Style.FauxBold, UseKerning = s.Style.UseKerning, Tracking = s.Style.Tracking, Fill = ToColor(s.Style.Color), Stroke = s.Style.StrokePixels, Outline = ToColor(s.Style.StrokeColor) }).ToArray()
            }).ToArray();
            _rejectedLayers = result.RejectedLayers.Select(e => new RejectedEntry { Id = e.Key, Reason = e.Value }).ToArray();
        }
        private static Color ToColor(PsdTextColor c) => new Color(c.R, c.G, c.B, c.A);
        private static PsdTextColor FromColor(Color c) => new PsdTextColor(c.r, c.g, c.b, c.a);
    }
}
