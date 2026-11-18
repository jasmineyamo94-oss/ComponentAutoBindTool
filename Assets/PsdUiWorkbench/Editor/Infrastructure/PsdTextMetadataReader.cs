using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using PsdUiWorkbench.Core;

namespace PsdUiWorkbench.Infrastructure
{
    public sealed class PsdTextMetadataResult
    {
        public PsdMetadataStatus Status { get; }
        public string Diagnostic { get; }
        public IReadOnlyDictionary<int, PsdTextSource> Sources { get; }
        public IReadOnlyDictionary<int, string> RejectedLayers { get; }
        public PsdTextMetadataResult(PsdMetadataStatus status, string diagnostic, IDictionary<int, PsdTextSource> sources = null, IDictionary<int, string> rejectedLayers = null)
        {
            Status = status; Diagnostic = diagnostic ?? "";
            Sources = new ReadOnlyDictionary<int, PsdTextSource>(new Dictionary<int, PsdTextSource>(sources ?? new Dictionary<int, PsdTextSource>()));
            RejectedLayers = new ReadOnlyDictionary<int, string>(new Dictionary<int, string>(rejectedLayers ?? new Dictionary<int, string>()));
        }
    }

    public sealed class PsdTextMetadataReader
    {
        public PsdTextMetadataResult Read(Stream stream)
        {
            try
            {
                if (stream == null || !stream.CanRead || !stream.CanSeek) throw new InvalidDataException("需要可读取和定位的源流。");
                stream.Position = 0;
                CheckResolution(stream);
                stream.Position = 0;
                var payloads = new Dictionary<int, IReadOnlyDictionary<string, byte[]>>();
                var layers = new PsdLayerMetadataReader().Read(stream, (id, blocks) => payloads.Add(id, blocks));
                if (layers.Status != PsdMetadataStatus.Success)
                    return new PsdTextMetadataResult(layers.Status == PsdMetadataStatus.Partial ? PsdMetadataStatus.Invalid : layers.Status,
                        "文本图层关联不可用，停止文本导出：" + layers.Diagnostic);
                var sources = new Dictionary<int, PsdTextSource>(); var diagnostics = new List<string>();
                var rejectedLayers = new Dictionary<int, string>();
                foreach (var pair in payloads)
                {
                    try { sources.Add(pair.Key, ConvertLayer(pair.Key, pair.Value)); }
                    catch (Exception e) when (Expected(e))
                    {
                        rejectedLayers.Add(pair.Key, e.Message);
                        diagnostics.Add($"跳过文本层 {pair.Key}：{e.Message}");
                    }
                }
                return new PsdTextMetadataResult(diagnostics.Count == 0 ? PsdMetadataStatus.Success : PsdMetadataStatus.Partial,
                    string.Join("\n", diagnostics), sources, rejectedLayers);
            }
            catch (Exception e) when (Expected(e)) { return new PsdTextMetadataResult(PsdMetadataStatus.Invalid, "文本元数据读取失败：" + e.Message); }
        }
        private static bool Expected(Exception e) => e is IOException || e is InvalidDataException || e is ArgumentException || e is OverflowException || e is KeyNotFoundException || e is InvalidCastException;
        private static Dictionary<string, object> Dict(object value) => value as Dictionary<string, object> ?? throw new InvalidDataException("缺少文本字典。");
        private static List<object> List(object value) => value as List<object> ?? throw new InvalidDataException("缺少文本数组。");
        private static double Num(object value) => value is double number && double.IsFinite(number) ? number : throw new InvalidDataException("文本数值无效。");
        private static string Text(object value) => value as string ?? throw new InvalidDataException("缺少文本字符串。");
        private static double N(Dictionary<string, object> dict, string key, double fallback = 0) => dict.TryGetValue(key, out var value) ? Num(value) : fallback;
        private static bool B(Dictionary<string, object> dict, string key, bool fallback = false) => dict.TryGetValue(key, out var value) ? value is bool b ? b : throw new InvalidDataException("文本布尔值无效。") : fallback;
        private static double Unit(Dictionary<string, object> dict, string key, string unit)
        {
            var value = Dict(dict[key]);
            if (!Equals(value["unit"], unit)) throw new InvalidDataException("不支持的文本参数单位：" + key);
            return Num(value["value"]);
        }
        private static int Index(object value)
        {
            double number = Num(value);
            if (number < 0 || number > 100000 || number != Math.Floor(number)) throw new InvalidDataException("文本索引或段长无效。");
            return (int)number;
        }
        private static PsdTextSource ConvertLayer(int id, IReadOnlyDictionary<string, byte[]> blocks)
        {
            if (!blocks.TryGetValue("TySh", out var payload) || blocks.ContainsKey("tySh")) throw new InvalidDataException("旧版 tySh 样式暂不支持。");
            var reader = new PsdTextDescriptor(payload);
            if (reader.Number(2) != 1) throw new InvalidDataException("未知 TySh 版本。");
            var transform = Enumerable.Range(0, 6).Select(_ => reader.Double()).ToArray();
            if (transform[0] <= 0 || Math.Abs(transform[0] - transform[3]) > 0.00001 || Math.Abs(transform[1]) > 0.00001 || Math.Abs(transform[2]) > 0.00001)
                throw new InvalidDataException("目前支持正向均匀缩放的水平点文本。");
            if (reader.Number(2) != 50 || reader.Number(4) != 16) throw new InvalidDataException("未知文本描述符版本。");
            var descriptor = reader.Read();
            if (reader.Number(2) != 1 || reader.Number(4) != 16) throw new InvalidDataException("未知文本变形版本。");
            var warp = reader.Read(); reader.Bytes(16);
            if (reader.Remaining > 3 || reader.Bytes(reader.Remaining).Any(b => b != 0) || !Equals(Dict(warp["warpStyle"])["value"], "warpNone"))
                throw new InvalidDataException("不支持的文本变形或尾部数据。");
            var data = Dict(descriptor["EngineData"]); var engine = Dict(data["EngineDict"]); var resources = Dict(data["ResourceDict"]);
            var shapes = Dict(Dict(engine["Rendered"])["Shapes"]);
            var children = List(shapes["Children"]);
            if (N(shapes, "WritingDirection") != 0 || children.Count != 1 || N(Dict(Dict(Dict(Dict(children[0])["Cookie"])["Photoshop"])), "ShapeType") != 0)
                throw new InvalidDataException("目前仅支持水平点文本。");
            string raw = Text(Dict(engine["Editor"])["Text"]);
            string text = raw.EndsWith("\r", StringComparison.Ordinal) ? raw.Substring(0, raw.Length - 1) : raw;
            if (Text(descriptor["Txt "]).TrimEnd('\0') != text) throw new InvalidDataException("文本内容与 EngineData 不一致。");
            var paragraphs = Dict(engine["ParagraphRun"]); var runs = List(paragraphs["RunArray"]);
            if (runs.Count != 1) throw new InvalidDataException("目前仅支持单行文本。");
            var paragraph = Dict(Dict(runs[0])["ParagraphSheet"]); var properties = Dict(paragraph["Properties"]);
            int alignment = (int)N(properties, "Justification");
            if (alignment != 0 && alignment != 2) throw new InvalidDataException("目前仅支持左对齐或居中。");
            foreach (string key in new[] { "FirstLineIndent", "StartIndent", "EndIndent" })
                if (Math.Abs(N(properties, key)) > 0.00001) throw new InvalidDataException("尚不支持段落缩进。");
            var defaults = new Dictionary<string, object>();
            if (resources.TryGetValue("StyleSheetSet", out var sheets))
            {
                int index = (int)N(paragraph, "DefaultStyleSheet"); var set = List(sheets);
                if (index < 0 || index >= set.Count) throw new InvalidDataException("默认样式索引越界。");
                defaults = new Dictionary<string, object>(Dict(Dict(set[index])["StyleSheetData"]));
            }
            var styleRun = Dict(engine["StyleRun"]);
            if (styleRun.TryGetValue("DefaultRunData", out var defaultRun))
                foreach (var pair in Dict(Dict(Dict(defaultRun)["StyleSheet"])["StyleSheetData"])) defaults[pair.Key] = pair.Value;
            var lengths = List(styleRun["RunLengthArray"]); var styles = List(styleRun["RunArray"]); var fonts = List(resources["FontSet"]);
            if (lengths.Count != styles.Count || lengths.Sum(n => (long)Index(n)) != raw.Length) throw new InvalidDataException("UTF-16 样式段长度不一致。");
            var stroke = ReadStroke(blocks); var spans = new List<PsdTextSourceSpan>(); int start = 0;
            for (int i = 0; i < lengths.Count; i++)
            {
                int length = Index(lengths[i]); var style = new Dictionary<string, object>(defaults);
                foreach (var pair in Dict(Dict(Dict(styles[i])["StyleSheet"])["StyleSheetData"])) style[pair.Key] = pair.Value;
                int used = Math.Min(length, text.Length - start);
                if (used > 0)
                {
                    int fontIndex = Index(style["Font"]);
                    if (fontIndex >= fonts.Count) throw new InvalidDataException("字体索引越界。");
                    foreach (string key in new[] { "FauxItalic", "Underline", "Strikethrough", "StrokeFlag" })
                        if (B(style, key)) throw new InvalidDataException("暂不支持文字样式：" + key);
                    foreach (string key in new[] { "HorizontalScale", "VerticalScale" })
                        if (Math.Abs(N(style, key, 1) - 1) > 0.00001) throw new InvalidDataException("暂不支持非标准字形缩放。");
                    foreach (string key in new[] { "BaselineShift", "FontCaps", "FontBaseline", "Kerning", "Tsume" })
                        if (Math.Abs(N(style, key)) > 0.00001) throw new InvalidDataException("暂不支持文字参数：" + key);
                    var fill = Dict(style["FillColor"]); var rgba = List(fill["Values"]);
                    if (N(fill, "Type") != 1 || rgba.Count != 4 || !B(style, "FillFlag", true)) throw new InvalidDataException("暂不支持非 RGB 填充。");
                    var color = new PsdTextColor((float)Num(rgba[1]), (float)Num(rgba[2]), (float)Num(rgba[3]), (float)Num(rgba[0]));
                    var sourceStyle = new PsdTextStyle(Text(Dict(fonts[fontIndex])["Name"]).TrimEnd('\0'),
                        (float)(N(style, "FontSize") * transform[3]), B(style, "FauxBold"), (float)N(style, "Tracking"), color, stroke.Item1, stroke.Item2, B(style, "AutoKerning"));
                    spans.Add(new PsdTextSourceSpan(start, used, sourceStyle));
                }
                start = checked(start + length);
            }
            var bounds = Dict(descriptor["bounds"]);
            float referenceWidth = (float)((Unit(bounds, "Rght", "#Pnt") - Unit(bounds, "Left", "#Pnt")) * transform[0]);
            return new PsdTextSource(id, text, (float)transform[4], (float)transform[5], alignment == 2 ? PsdTextAlignment.Center : PsdTextAlignment.Left, spans, referenceWidth);
        }
        private static Tuple<float, PsdTextColor> ReadStroke(IReadOnlyDictionary<string, byte[]> blocks)
        {
            if (blocks.ContainsKey("lfx2") && blocks.ContainsKey("lfxs")) throw new InvalidDataException("多个效果描述符存在歧义。");
            if (!blocks.TryGetValue("lfx2", out var bytes) && !blocks.TryGetValue("lfxs", out bytes)) return Tuple.Create(0f, PsdTextColor.White);
            var reader = new PsdTextDescriptor(bytes);
            if (reader.Number(4) != 0 || reader.Number(4) != 16) throw new InvalidDataException("未知图层效果版本。");
            var effects = reader.Read(); var active = new List<KeyValuePair<string, Dictionary<string, object>>>();
            if (B(effects, "masterFXSwitch"))
                foreach (var pair in effects)
                    foreach (var item in pair.Value is List<object> list ? list : new List<object> { pair.Value })
                        if (item is Dictionary<string, object> effect && B(effect, "enab")) active.Add(new KeyValuePair<string, Dictionary<string, object>>(pair.Key, effect));
            if (active.Count == 0) return Tuple.Create(0f, PsdTextColor.White);
            if (active.Count != 1 || active[0].Key != "FrFX") throw new InvalidDataException("当前仅支持单色外描边，跳过包含其他活动效果的文本层。");
            var stroke = active[0].Value;
            if (!Equals(Dict(stroke["Styl"])["value"], "OutF") || !Equals(Dict(stroke["PntT"])["value"], "SClr") || !Equals(Dict(stroke["Md  "])["value"], "Nrml"))
                throw new InvalidDataException("当前仅支持正常混合的单色外描边。");
            if (effects.ContainsKey("Scl ") && Math.Abs(Unit(effects, "Scl ", "#Prc") - 100) > 0.00001) throw new InvalidDataException("效果缩放尚未验证。");
            var color = Dict(stroke["Clr "]);
            return Tuple.Create((float)Unit(stroke, "Sz  ", "#Pxl"), new PsdTextColor((float)(N(color, "Rd  ") / 255), (float)(N(color, "Grn ") / 255),
                (float)(N(color, "Bl  ") / 255), (float)(Unit(stroke, "Opct", "#Prc") / 100)));
        }
        private static void CheckResolution(Stream stream)
        {
            // 点数换算暂限定已验证的 72 DPI；缺少资源时使用 PSD 默认 72 DPI。
            ulong Number(int count)
            {
                ulong value = 0;
                for (int i = 0; i < count; i++) { int b = stream.ReadByte(); if (b < 0) throw new EndOfStreamException(); value = (value << 8) | (byte)b; }
                return value;
            }
            void Skip(long size, long end)
            {
                if (size < 0 || size > end - stream.Position || end > stream.Length) throw new InvalidDataException("图像资源越界。");
                stream.Seek(size, SeekOrigin.Current);
            }
            Skip(26, stream.Length); long colorSize = checked((long)Number(4)); Skip(colorSize, stream.Length);
            long size = checked((long)Number(4)); long end = checked(stream.Position + size);
            if (end > stream.Length) throw new InvalidDataException("资源区截断。");
            while (stream.Position < end)
            {
                if (Number(4) != 0x3842494D) throw new InvalidDataException("图像资源签名错误。");
                int id = (int)Number(2); long name = (long)Number(1); Skip(name + (name + 1) % 2, end);
                long length = checked((long)Number(4)), start = stream.Position;
                if (length > end - start) throw new InvalidDataException("图像资源负载越界。");
                if (id == 1005)
                {
                    if (length != 16) throw new InvalidDataException("分辨率资源长度无效。");
                    double horizontal = Number(4) / 65536d; Number(2); Number(2);
                    double vertical = Number(4) / 65536d; Number(2); Number(2);
                    if (horizontal != 72 || vertical != 72) throw new InvalidDataException("文本导出当前仅验证 72 DPI，其他分辨率保留 Image。");
                }
                Skip(length - (stream.Position - start) + length % 2, end);
            }
        }
    }
}
