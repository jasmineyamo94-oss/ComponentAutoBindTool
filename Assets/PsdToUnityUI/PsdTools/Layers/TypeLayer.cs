using System;
using System.Collections.Generic;
using System.Text;
using PsdTools.Constants;
using PsdTools.Psd;
using PsdTools.Utils;

namespace PsdTools.Layers
{
    /// <summary>
    /// Text layer
    /// </summary>
    public class TypeLayer : Layer
    {
        private string _text;
        private string _fontName;
        private float _fontSize;
        private float[] _fillColor; // [a, r, g, b] 0-1
        private double[] _transform;
        private bool _parsed;

        public TypeLayer(LayerRecord record, FileHeader header) : base(record, header)
        {
        }

        public override LayerKind Kind => LayerKind.Type;

        /// <summary>Text content</summary>
        public string Text
        {
            get
            {
                EnsureParsed();
                return _text ?? "";
            }
        }

        /// <summary>Font size (approximate, may need transform adjustment)</summary>
        public float FontSize
        {
            get
            {
                EnsureParsed();
                return _fontSize;
            }
        }

        /// <summary>Transformation matrix (6 elements: xx, xy, yx, yy, tx, ty)</summary>
        public double[] Transform
        {
            get
            {
                EnsureParsed();
                return _transform;
            }
        }

        /// <summary>Font fill color [a, r, g, b] in 0-1 range, null if not found</summary>
        public float[] FillColor
        {
            get
            {
                EnsureParsed();
                return _fillColor;
            }
        }

        /// <summary>Font name from the PSD type layer (from EngineData; empty if parsing fails). Used for font mapping.</summary>
        public string PsdFontName
        {
            get
            {
                EnsureParsed();
                return _fontName ?? "";
            }
        }

        /// <summary>Effective font size (adjusted by transform)</summary>
        public float EffectiveFontSize
        {
            get
            {
                EnsureParsed();
                if (_transform != null && _transform.Length >= 4)
                {
                    double scale = Math.Abs(_transform[3]);
                    if (scale > 0)
                        return (float)(_fontSize * scale);
                }
                return _fontSize;
            }
        }

        private void EnsureParsed()
        {
            if (_parsed)
                return;

            _parsed = true;
            _fontSize = 24f; // Default
            _transform = new double[] { 1, 0, 0, 1, 0, 0 };

            // Get TypeTool tagged block
            var data = TaggedBlocks?.GetData(Tag.TYPE_TOOL_OBJECT_SETTING);
            if (data == null)
                data = TaggedBlocks?.GetData(Tag.TYPE_TOOL_INFO);

            if (data == null || data.Length < 10)
                return;

            try
            {
                ParseTypeToolData(data);
            }
            catch
            {
                // Parsing failed, use defaults
            }
        }

        private void ParseTypeToolData(byte[] data)
        {
            using (var reader = new BigEndianReader(data))
            {
                // Version (2 bytes)
                ushort version = reader.ReadUInt16();

                // Transform matrix (6 doubles = 48 bytes)
                _transform = new double[6];
                for (int i = 0; i < 6; i++)
                {
                    _transform[i] = reader.ReadDouble();
                }

                // Text version (2 bytes)
                ushort textVersion = reader.ReadUInt16();

                // Descriptor version (4 bytes)
                uint descriptorVersion = reader.ReadUInt32();

                // Text descriptor - complex parsing
                // For now, try to find text string in the data
                ParseTextFromDescriptor(reader);
            }
        }

        private void ParseTextFromDescriptor(BigEndianReader reader)
        {
            try
            {
                var data = reader.ReadToEnd();

                // 1) Most reliable: read Unicode text from Descriptor "Txt " + "TEXT" fields
                int txtStart = FindPattern(data, Encoding.ASCII.GetBytes("Txt "));
                if (txtStart >= 0)
                {
                    ExtractTextFromTxtField(data, txtStart);
                }

                // 2) From EngineData: font size, color, and other metadata
                int engineDataStart = FindPattern(data, Encoding.ASCII.GetBytes("EngineData"));
                if (engineDataStart >= 0)
                {
                    ParseEngineData(data, engineDataStart);
                }
            }
            catch
            {
                // Parsing failed
            }
        }

        private void ParseEngineData(byte[] data, int startOffset)
        {
            try
            {
                int maxLen = Math.Min(data.Length - startOffset, 16384);
                string content = Encoding.UTF8.GetString(data, startOffset, maxLen);

                // Font name must be sliced from raw bytes: EngineData is not pure UTF-8; decoding the whole chunk as UTF-8 corrupts bytes inside parens (U+FFFD).
                if (TryExtractFontNameFromEngineDataBytes(data, startOffset, out string fnBytes) && !string.IsNullOrEmpty(fnBytes))
                    _fontName = fnBytes;
                else if (TryExtractFontNameFromFontSetBytes(data, startOffset, out string fnSet) && !string.IsNullOrEmpty(fnSet))
                    _fontName = fnSet;
                else if (TryExtractFontNameFromEngineData(content, out string fnStr) && !string.IsNullOrEmpty(fnStr))
                    _fontName = fnStr;

                // Extract FontSize
                int fontSizeIdx = content.IndexOf("/FontSize ");
                if (fontSizeIdx >= 0)
                {
                    int valueStart = fontSizeIdx + 10;
                    int valueEnd = content.IndexOfAny(new[] { ' ', '\n', '\r', '/' }, valueStart);
                    if (valueEnd > valueStart)
                    {
                        string sizeStr = content.Substring(valueStart, valueEnd - valueStart);
                        if (float.TryParse(sizeStr, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float size))
                        {
                            _fontSize = size;
                        }
                    }
                }

                // Extract FillColor /Values [ a r g b ]
                int fillColorIdx = content.IndexOf("/FillColor");
                if (fillColorIdx >= 0)
                {
                    int valuesIdx = content.IndexOf("/Values", fillColorIdx);
                    if (valuesIdx >= 0 && valuesIdx < fillColorIdx + 200)
                    {
                        int bracketStart = content.IndexOf('[', valuesIdx);
                        int bracketEnd = content.IndexOf(']', bracketStart >= 0 ? bracketStart : valuesIdx);
                        if (bracketStart >= 0 && bracketEnd > bracketStart)
                        {
                            string valStr = content.Substring(bracketStart + 1, bracketEnd - bracketStart - 1).Trim();
                            string[] parts = valStr.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 4)
                            {
                                _fillColor = new float[4];
                                for (int i = 0; i < 4; i++)
                                {
                                    float.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out _fillColor[i]);
                                }
                            }
                        }
                    }
                }

                // If _text is still empty, try extracting from EngineData
                if (string.IsNullOrEmpty(_text))
                {
                    int textStart = content.IndexOf("(/");
                    if (textStart >= 0)
                    {
                        int textEnd = content.IndexOf(")", textStart + 2);
                        if (textEnd > textStart)
                        {
                            _text = content.Substring(textStart + 2, textEnd - textStart - 2);
                            _text = CleanEngineDataText(_text);
                        }
                    }
                }
            }
            catch
            {
                // Parsing failed
            }
        }

        private void ExtractTextFromTxtField(byte[] data, int startOffset)
        {
            try
            {
                // Find "Txt " immediately followed by "TEXT" (multiple "Txt " may exist; pick the correct one)
                byte[] txtPattern = Encoding.ASCII.GetBytes("Txt ");
                byte[] textMarker = Encoding.ASCII.GetBytes("TEXT");
                int offset = startOffset;

                while (offset >= 0 && offset + 12 < data.Length)
                {
                    // Check whether bytes at offset+4 are "TEXT"
                    bool isTextType = true;
                    for (int i = 0; i < 4; i++)
                    {
                        if (data[offset + 4 + i] != textMarker[i])
                        {
                            isTextType = false;
                            break;
                        }
                    }

                    if (isTextType)
                    {
                        using (var reader = new BigEndianReader(
                            new System.IO.MemoryStream(data, offset + 8, data.Length - offset - 8)))
                        {
                            uint length = reader.ReadUInt32();
                            if (length > 0 && length < 100000)
                            {
                                byte[] strBytes = reader.ReadBytes((int)(length * 2));
                                string result = Encoding.BigEndianUnicode.GetString(strBytes);
                                // Strip trailing \0
                                _text = result.TrimEnd('\0');
                                if (!string.IsNullOrEmpty(_text))
                                    return;
                            }
                        }
                    }

                    // Search for the next "Txt "
                    int next = FindPattern(data, txtPattern, offset + 1);
                    if (next <= offset) break;
                    offset = next;
                }
            }
            catch
            {
                // Parsing failed
            }
        }

        private static int FindPattern(byte[] data, byte[] pattern, int startIndex = 0)
        {
            for (int i = startIndex; i <= data.Length - pattern.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                    return i;
            }
            return -1;
        }

        private static bool TryExtractFontNameFromEngineDataBytes(byte[] data, int engineDataStart, out string fontName)
        {
            fontName = null;
            if (data == null || engineDataStart < 0 || engineDataStart >= data.Length)
                return false;

            byte[] key = Encoding.ASCII.GetBytes("/FontName");
            int idx = FindPattern(data, key, engineDataStart);
            if (idx < 0)
                return false;

            int p = idx + key.Length;
            while (p < data.Length && (data[p] == (byte)' ' || data[p] == (byte)'\t' || data[p] == (byte)'\r' || data[p] == (byte)'\n'))
                p++;
            if (p >= data.Length)
                return false;

            if (data[p] == (byte)'(')
            {
                if (!TryReadPostScriptParenStringBytes(data, p, out byte[] inner))
                    return false;
                fontName = DecodePostScriptFontNameBytes(inner);
                return !string.IsNullOrEmpty(fontName);
            }

            if (data[p] == (byte)'/')
            {
                p++;
                int end = p;
                while (end < data.Length && data[end] > 32 && data[end] != (byte)')' && data[end] != (byte)']' && data[end] != (byte)'(')
                    end++;
                if (end > p)
                {
                    fontName = Encoding.ASCII.GetString(data, p, end - p).Trim();
                    return !string.IsNullOrEmpty(fontName);
                }
            }

            return false;
        }

        private static bool TryExtractFontNameFromFontSetBytes(byte[] data, int engineDataStart, out string fontName)
        {
            fontName = null;
            if (data == null || engineDataStart < 0)
                return false;

            byte[] fontSetKey = Encoding.ASCII.GetBytes("/FontSet");
            int fsIdx = FindPattern(data, fontSetKey, engineDataStart);
            if (fsIdx < 0)
                return false;

            int searchLimit = System.Math.Min(data.Length, fsIdx + 4096);
            byte[] nameKey = Encoding.ASCII.GetBytes("/Name");
            int sub = FindPattern(data, nameKey, fsIdx);
            if (sub < 0 || sub >= searchLimit)
                return false;

            int p = sub + nameKey.Length;
            while (p < data.Length && (data[p] == (byte)' ' || data[p] == (byte)'\t' || data[p] == (byte)'\r' || data[p] == (byte)'\n'))
                p++;
            if (p >= data.Length || data[p] != (byte)'(')
                return false;

            if (!TryReadPostScriptParenStringBytes(data, p, out byte[] inner))
                return false;
            fontName = DecodePostScriptFontNameBytes(inner);
            return !string.IsNullOrEmpty(fontName);
        }

        /// <summary>Read raw bytes inside a PostScript literal string ( ... ) (handles \ escapes).</summary>
        private static bool TryReadPostScriptParenStringBytes(byte[] data, int openParenIndex, out byte[] inner)
        {
            inner = null;
            if (openParenIndex < 0 || openParenIndex >= data.Length || data[openParenIndex] != (byte)'(')
                return false;

            var list = new List<byte>(32);
            int i = openParenIndex + 1;
            while (i < data.Length)
            {
                byte b = data[i];
                if (b == (byte)')')
                    break;
                if (b == (byte)'\\' && i + 1 < data.Length)
                {
                    i++;
                    byte b2 = data[i];
                    if (b2 >= (byte)'0' && b2 <= (byte)'7')
                    {
                        int oct = b2 - '0';
                        int digits = 1;
                        while (digits < 3 && i + 1 < data.Length && data[i + 1] >= (byte)'0' && data[i + 1] <= (byte)'7')
                        {
                            i++;
                            oct = (oct << 3) + (data[i] - '0');
                            digits++;
                        }
                        list.Add((byte)(oct & 0xFF));
                    }
                    else if (b2 == (byte)'n') list.Add((byte)'\n');
                    else if (b2 == (byte)'r') list.Add((byte)'\r');
                    else if (b2 == (byte)'t') list.Add((byte)'\t');
                    else list.Add(b2);
                }
                else
                    list.Add(b);
                i++;
            }
            inner = list.ToArray();
            return true;
        }

        /// <summary>Decode parenthesized raw bytes as a font name (UTF-8 / UTF-16 / GBK / MacRoman fallbacks).</summary>
        private static string DecodePostScriptFontNameBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return "";

            bool allAscii = true;
            foreach (byte b in bytes)
            {
                if (b >= 0x80) { allAscii = false; break; }
            }
            if (allAscii)
                return Encoding.ASCII.GetString(bytes).Trim();

            // BOM
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2).Trim('\0');
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).Trim('\0');

            string utf8 = Encoding.UTF8.GetString(bytes);
            if (utf8.IndexOf('\uFFFD') < 0)
                return utf8.Trim();

            if (bytes.Length >= 2 && bytes.Length % 2 == 0)
            {
                string be = Encoding.BigEndianUnicode.GetString(bytes);
                if (be.IndexOf('\uFFFD') < 0)
                    return be.Trim('\0');
                string le = Encoding.Unicode.GetString(bytes);
                if (le.IndexOf('\uFFFD') < 0)
                    return le.Trim('\0');
            }

            try
            {
                string gbk = Encoding.GetEncoding(936).GetString(bytes);
                if (gbk.IndexOf('\uFFFD') < 0)
                    return gbk.Trim();
            }
            catch
            {
                try
                {
                    string gbk2 = Encoding.GetEncoding("GBK").GetString(bytes);
                    if (gbk2.IndexOf('\uFFFD') < 0)
                        return gbk2.Trim();
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                return Encoding.GetEncoding(10000).GetString(bytes).Trim();
            }
            catch
            {
                return Encoding.GetEncoding("iso-8859-1").GetString(bytes).Trim();
            }
        }

        private static bool TryExtractFontNameFromEngineData(string content, out string fontName)
        {
            fontName = null;
            if (string.IsNullOrEmpty(content))
                return false;

            int idx = content.IndexOf("/FontName");
            if (idx >= 0)
            {
                int p = idx + "/FontName".Length;
                while (p < content.Length && char.IsWhiteSpace(content[p])) p++;
                if (p < content.Length && content[p] == '(')
                {
                    int end = content.IndexOf(')', p + 1);
                    if (end > p)
                    {
                        fontName = content.Substring(p + 1, end - p - 1).Trim();
                        if (!string.IsNullOrEmpty(fontName))
                            return true;
                    }
                }
                else if (p < content.Length && content[p] == '/')
                {
                    p++;
                    int end = p;
                    while (end < content.Length && !char.IsWhiteSpace(content[end]) && content[end] != ')' && content[end] != ']' && content[end] != '(')
                        end++;
                    fontName = content.Substring(p, end - p).Trim();
                    if (!string.IsNullOrEmpty(fontName))
                        return true;
                }
            }

            // Some PSDs embed the font name under /FontSet
            int fsIdx = content.IndexOf("/FontSet");
            if (fsIdx >= 0)
            {
                int sub = content.IndexOf("/Name", fsIdx, System.Math.Min(2048, content.Length - fsIdx));
                if (sub >= 0)
                {
                    int p = sub + "/Name".Length;
                    while (p < content.Length && char.IsWhiteSpace(content[p])) p++;
                    if (p < content.Length && content[p] == '(')
                    {
                        int end = content.IndexOf(')', p + 1);
                        if (end > p)
                        {
                            fontName = content.Substring(p + 1, end - p - 1).Trim();
                            if (!string.IsNullOrEmpty(fontName))
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        private static string CleanEngineDataText(string text)
        {
            // Remove common escape sequences
            text = text.Replace("\\r", "\n");
            text = text.Replace("\\n", "\n");
            text = text.Replace("\\t", "\t");
            text = text.Replace("\\\\", "\\");
            return text;
        }
    }
}
