using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PsdUiWorkbench.Infrastructure
{
    // 仅解析有界的文本描述符；不接触像素、官方内部解析器或 Unity 对象。
    internal sealed class PsdTextDescriptor
    {
        private readonly byte[] _data;
        private int _position, _nodes;
        internal PsdTextDescriptor(byte[] data) { _data = data; }
        internal int Remaining => _data.Length - _position;
        internal byte[] Bytes(int count)
        {
            if (count < 0 || count > Remaining) throw new InvalidDataException("文本描述符截断或越界。");
            var value = new byte[count]; Array.Copy(_data, _position, value, 0, count); _position += count; return value;
        }
        internal ulong Number(int size)
        {
            ulong value = 0;
            foreach (byte b in Bytes(size)) value = (value << 8) | b;
            return value;
        }
        internal double Double()
        {
            var bytes = Bytes(8); if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            double value = BitConverter.ToDouble(bytes, 0);
            if (!double.IsFinite(value)) throw new InvalidDataException("文本包含非有限数值。");
            return value;
        }
        internal string Key() => Encoding.GetEncoding(28591).GetString(Bytes(4));
        private int Count()
        {
            uint count = (uint)Number(4);
            if (count > 100000) throw new InvalidDataException("文本描述符条目过多。");
            return (int)count;
        }
        private string Unicode() => new UnicodeEncoding(true, false, true).GetString(Bytes(checked(Count() * 2)));
        private string Identifier()
        {
            int size = Count(); return Encoding.GetEncoding(28591).GetString(Bytes(size == 0 ? 4 : size));
        }
        internal Dictionary<string, object> Read(int depth = 0)
        {
            Limit(depth); Unicode(); Identifier(); int count = Count();
            var result = new Dictionary<string, object>();
            for (int i = 0; i < count; i++)
            {
                string key = Identifier();
                if (result.ContainsKey(key)) throw new InvalidDataException("文本描述符重复键：" + key);
                result.Add(key, Value(Key(), depth + 1, key));
            }
            return result;
        }
        private void Limit(int depth)
        {
            if (depth > 64 || ++_nodes > 100000) throw new InvalidDataException("文本描述符复杂度超限。");
        }
        private object Value(string kind, int depth, string key = "")
        {
            Limit(depth);
            switch (kind)
            {
                case "Objc": case "GlbO": return Read(depth);
                case "VlLs":
                    int count = Count(); var list = new List<object>();
                    for (int i = 0; i < count; i++) list.Add(Value(Key(), depth + 1));
                    return list;
                case "TEXT": return Unicode();
                case "enum": Identifier(); return new Dictionary<string, object> { { "value", Identifier() } };
                case "type": case "GlbC": Unicode(); return Identifier();
                case "UntF": case "UnFl":
                    string unit = Key(); double value;
                    if (kind == "UntF") value = Double();
                    else
                    {
                        var bytes = Bytes(4); if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
                        value = BitConverter.ToSingle(bytes, 0);
                        if (!double.IsFinite(value)) throw new InvalidDataException("单位数值无效。");
                    }
                    return new Dictionary<string, object> { { "unit", unit }, { "value", value } };
                case "doub": return Double();
                case "long": return (double)unchecked((int)Number(4));
                case "comp": return (double)unchecked((long)Number(8));
                case "bool": return Number(1) != 0;
                case "tdta": case "alis": case "Pth ":
                    long size = checked((long)Number(4));
                    if (size > 16 * 1024 * 1024) throw new InvalidDataException("文本原始负载过大。");
                    var raw = Bytes((int)size);
                    return key == "EngineData" ? new EngineReader(raw).Read() : (object)raw;
                default: throw new InvalidDataException("尚不支持的文本描述符类型：" + kind);
            }
        }

        private sealed class EngineReader
        {
            private readonly byte[] _data;
            private int _position, _nodes;
            internal EngineReader(byte[] data) { _data = data; }
            private bool Starts(string token)
            {
                if (_data.Length - _position < token.Length) return false;
                for (int i = 0; i < token.Length; i++) if (_data[_position + i] != token[i]) return false;
                return true;
            }
            private void Space()
            {
                while (_position < _data.Length && " \t\r\n\0".IndexOf((char)_data[_position]) >= 0) _position++;
            }
            internal object Read()
            {
                object result = Value(0); Space();
                if (_position != _data.Length) throw new InvalidDataException("EngineData 尾部未识别。");
                return result;
            }
            private object Value(int depth)
            {
                if (depth > 64 || ++_nodes > 100000) throw new InvalidDataException("EngineData 复杂度超限。");
                Space();
                if (Starts("<<"))
                {
                    _position += 2; var dict = new Dictionary<string, object>();
                    while (true)
                    {
                        Space(); if (Starts(">>")) { _position += 2; return dict; }
                        string key = Token();
                        if (!key.StartsWith("/") || dict.ContainsKey(key.Substring(1))) throw new InvalidDataException("EngineData 字典键重复或无效。");
                        dict.Add(key.Substring(1), Value(depth + 1));
                    }
                }
                if (Starts("["))
                {
                    _position++; var list = new List<object>();
                    while (true) { Space(); if (Starts("]")) { _position++; return list; } list.Add(Value(depth + 1)); }
                }
                if (Starts("(")) return String();
                string token = Token();
                if (token == "true" || token == "false") return token == "true";
                if (token == "null") return null;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)) return value;
                if (token.StartsWith("/")) return token.Substring(1);
                throw new InvalidDataException("EngineData 未识别的值。");
            }
            private string Token()
            {
                Space(); int start = _position;
                while (_position < _data.Length && " \t\r\n\0[]<>()".IndexOf((char)_data[_position]) < 0) _position++;
                if (start == _position) throw new InvalidDataException("EngineData 截断或分隔符无效。");
                return Encoding.ASCII.GetString(_data, start, _position - start);
            }
            private string String()
            {
                _position++; int nesting = 1; var bytes = new List<byte>();
                while (_position < _data.Length)
                {
                    byte b = _data[_position++];
                    if (b == 92)
                    {
                        if (_position == _data.Length) break;
                        b = _data[_position++];
                        if (b >= 48 && b <= 55)
                        {
                            int code = b - 48;
                            for (int i = 0; i < 2 && _position < _data.Length && _data[_position] >= 48 && _data[_position] <= 55; i++) code = code * 8 + _data[_position++] - 48;
                            bytes.Add((byte)code);
                        }
                        else if (b == 10 || b == 13) { if (b == 13 && Starts("\n")) _position++; }
                        else bytes.Add(b == 'n' ? (byte)10 : b == 'r' ? (byte)13 : b == 't' ? (byte)9 : b == 'b' ? (byte)8 : b == 'f' ? (byte)12 : b);
                    }
                    else if (b == 40) { nesting++; bytes.Add(b); }
                    else if (b == 41)
                    {
                        if (--nesting == 0)
                        {
                            var data = bytes.ToArray();
                            return data.Length >= 2 && data[0] == 254 && data[1] == 255
                                ? new UnicodeEncoding(true, false, true).GetString(data, 2, data.Length - 2)
                                : Encoding.GetEncoding(28591).GetString(data);
                        }
                        bytes.Add(b);
                    }
                    else bytes.Add(b);
                }
                throw new InvalidDataException("EngineData 字符串截断。");
            }
        }
    }
}
