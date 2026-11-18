using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PsdUiWorkbench.Core;

namespace PsdUiWorkbench.Infrastructure
{
    public enum PsdMetadataStatus { Success, Partial, Unsupported, Invalid }

    public sealed class PsdMetadataResult
    {
        public PsdMetadataStatus Status { get; }
        public string Diagnostic { get; }
        public IReadOnlyDictionary<int, PsdLayerKind> Layers { get; }
        public PsdMetadataResult(PsdMetadataStatus status, string diagnostic, IDictionary<int, PsdLayerKind> layers = null)
        {
            Status = status;
            Diagnostic = diagnostic;
            Layers = new System.Collections.ObjectModel.ReadOnlyDictionary<int, PsdLayerKind>(
                new Dictionary<int, PsdLayerKind>(layers ?? new Dictionary<int, PsdLayerKind>()));
        }
    }

    public sealed class PsdLayerMetadataReader
    {
        public PsdMetadataResult Read(Stream stream)
            => Read(stream, null);

        internal PsdMetadataResult Read(Stream stream, Action<int, IReadOnlyDictionary<string, byte[]>> receive)
        {
            try
            {
                if (stream == null || !stream.CanRead || !stream.CanSeek) throw new InvalidDataException("需要可读取和定位的流。");
                return new Parser(stream, receive).Read();
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is OverflowException || e is ArgumentException)
            {
                return new PsdMetadataResult(PsdMetadataStatus.Invalid, "图层类型读取失败：" + e.Message);
            }
        }

        private sealed class Parser
        {
            private readonly Stream _stream;
            private readonly Dictionary<int, PsdLayerKind> _layers = new Dictionary<int, PsdLayerKind>();
            private bool _large, _ambiguous, _parsed;
            private readonly Action<int, IReadOnlyDictionary<string, byte[]>> _receive;
            private readonly Dictionary<int, IReadOnlyDictionary<string, byte[]>> _payloads = new Dictionary<int, IReadOnlyDictionary<string, byte[]>>();
            private long _metadataBytes;
            private static readonly HashSet<string> _longKeys = new HashSet<string>
            {
                "LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD", "lnkE", "extn", "cinf"
            };
            public Parser(Stream stream, Action<int, IReadOnlyDictionary<string, byte[]>> receive) { _stream = stream; _receive = receive; }
            private void Require(long count, long end)
            {
                if (count < 0 || _stream.Position > end || count > end - _stream.Position || end > _stream.Length)
                    throw new InvalidDataException("数据块越界或文件截断。");
            }
            private ulong Number(int bytes, long end)
            {
                Require(bytes, end);
                ulong value = 0;
                for (int i = 0; i < bytes; i++)
                {
                    int b = _stream.ReadByte();
                    if (b < 0) throw new EndOfStreamException();
                    value = (value << 8) | (byte)b;
                }
                return value;
            }
            private long Length(int bytes, long end) => checked((long)Number(bytes, end));
            private string Key(long end)
            {
                var chars = new char[4];
                for (int i = 0; i < 4; i++) chars[i] = (char)Number(1, end);
                return new string(chars);
            }
            private void Skip(long count, long end)
            {
                Require(count, end);
                _stream.Seek(count, SeekOrigin.Current);
            }
            private long SizedEnd(int bytes, long end)
            {
                long length = Length(bytes, end);
                Require(length, end);
                return checked(_stream.Position + length);
            }
            public PsdMetadataResult Read()
            {
                long end = _stream.Length;
                if (Key(end) != "8BPS") throw new InvalidDataException("不是 PSD/PSB 文件。");
                int version = (int)Number(2, end);
                if (version != 1 && version != 2) throw new InvalidDataException("未知的 PSD 文件版本。");
                _large = version == 2;
                Skip(6, end);
                Number(2, end); Number(4, end); Number(4, end);
                int depth = (int)Number(2, end);
                Number(2, end);
                if (depth != 8) return new PsdMetadataResult(PsdMetadataStatus.Unsupported, "类型识别目前仅支持 8 位 PSD/PSB，当前位深：" + depth);
                for (int i = 0; i < 2; i++)
                {
                    long blockEnd = SizedEnd(4, end);
                    Skip(blockEnd - _stream.Position, end);
                }
                long maskEnd = SizedEnd(_large ? 8 : 4, end);
                if (_stream.Position < maskEnd)
                {
                    long layerEnd = SizedEnd(_large ? 8 : 4, maskEnd);
                    if (_stream.Position < layerEnd) ReadLayers(layerEnd);
                    Skip(layerEnd - _stream.Position, maskEnd);
                    if (_stream.Position < maskEnd)
                    {
                        long globalEnd = SizedEnd(4, maskEnd);
                        Skip(globalEnd - _stream.Position, maskEnd);
                    }
                    while (maskEnd - _stream.Position >= 12)
                    {
                        ReadBlock(maskEnd, true, (key, blockEnd) =>
                        {
                            if (key != "Layr") return;
                            if (_parsed) throw new InvalidDataException("存在多个图层记录区。");
                            ReadLayers(blockEnd);
                        });
                    }
                    Skip(maskEnd - _stream.Position, maskEnd);
                }
                if (!_parsed) return new PsdMetadataResult(PsdMetadataStatus.Partial, "没有可关联的源图层记录。");
                if (_ambiguous)
                    return new PsdMetadataResult(PsdMetadataStatus.Partial, "源图层 ID 缺失或重复，停止类型关联以避免官方 ID 修复后的误匹配。",
                        _layers.ToDictionary(p => p.Key, p => PsdLayerKind.Unknown));
                // 完整校验 ID 后才发布，重复 ID 或后续截断不能留下可误用的部分结果。
                if (_receive != null) foreach (var pair in _payloads) _receive(pair.Key, pair.Value);
                return new PsdMetadataResult(PsdMetadataStatus.Success, "", _layers);
            }
            private void ReadLayers(long end)
            {
                _parsed = true;
                int count = Math.Abs((int)unchecked((short)Number(2, end)));
                long pixels = 0;
                for (int i = 0; i < count; i++)
                {
                    Skip(16, end);
                    int channels = (int)Number(2, end);
                    for (int c = 0; c < channels; c++)
                    {
                        Skip(2, end);
                        pixels = checked(pixels + Length(_large ? 8 : 4, end));
                    }
                    if (Key(end) != "8BIM") throw new InvalidDataException("图层混合签名错误。");
                    Skip(8, end);
                    long extraEnd = SizedEnd(4, end);
                    for (int j = 0; j < 2; j++)
                    {
                        long blockEnd = SizedEnd(4, extraEnd);
                        Skip(blockEnd - _stream.Position, extraEnd);
                    }
                    int nameLength = (int)Number(1, extraEnd);
                    Skip(nameLength + ((4 - (nameLength + 1) % 4) % 4), extraEnd);
                    int? id = null;
                    bool text = false;
                    uint section = 0;
                    var payloads = _receive == null ? null : new Dictionary<string, byte[]>();
                    while (extraEnd - _stream.Position >= 12)
                    {
                        ReadBlock(extraEnd, false, (key, blockEnd) =>
                        {
                            if (key == "lyid")
                            {
                                if (id.HasValue) _ambiguous = true;
                                id = unchecked((int)Number(4, blockEnd));
                            }
                            if (key == "TySh" || key == "tySh") text = true;
                            if (key == "lsct" || key == "lsdk") section = (uint)Number(4, blockEnd);
                            if (payloads != null && (key == "TySh" || key == "tySh" || key == "lfx2" || key == "lfxs"))
                            {
                                long size = blockEnd - _stream.Position;
                                _metadataBytes = checked(_metadataBytes + size);
                                if (size > 16 * 1024 * 1024 || _metadataBytes > 64 * 1024 * 1024 || payloads.ContainsKey(key))
                                    throw new InvalidDataException("文本元数据过大或附加块重复。");
                                var data = new byte[checked((int)size)];
                                for (int offset = 0; offset < data.Length;)
                                {
                                    int read = _stream.Read(data, offset, data.Length - offset);
                                    if (read == 0) throw new EndOfStreamException();
                                    offset += read;
                                }
                                payloads.Add(key, data);
                            }
                        });
                    }
                    Skip(extraEnd - _stream.Position, extraEnd);
                    if (section == 3) continue;
                    if (!id.HasValue) { _ambiguous = true; continue; }
                    if (_layers.ContainsKey(id.Value)) _ambiguous = true;
                    _layers[id.Value] = section == 1 || section == 2 ? PsdLayerKind.Group : text ? PsdLayerKind.Text : PsdLayerKind.NonText;
                    if (text && payloads != null) _payloads[id.Value] = payloads;
                }
                Require(pixels, end);
                Skip(end - _stream.Position, end);
            }
            private void ReadBlock(long end, bool global, Action<string, long> read)
            {
                string signature = Key(end);
                if (signature != "8BIM" && signature != "8B64") throw new InvalidDataException("附加块签名错误。");
                string key = Key(end);
                long blockEnd = SizedEnd(_large && _longKeys.Contains(key) ? 8 : 4, end);
                long start = _stream.Position;
                read(key, blockEnd);
                Skip(blockEnd - _stream.Position, end);
                int alignment = global ? 4 : 2;
                long padding = (alignment - (blockEnd - start) % alignment) % alignment;
                Skip(padding, end);
            }
        }
    }
}
