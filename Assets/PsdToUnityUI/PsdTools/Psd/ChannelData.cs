using System;
using PsdTools.Constants;
using PsdTools.Compression;
using PsdTools.Utils;

namespace PsdTools.Psd
{
    /// <summary>
    /// Channel information (metadata)
    /// </summary>
    public class ChannelInfo
    {
        /// <summary>Channel ID</summary>
        public ChannelId Id { get; set; }

        /// <summary>Length of channel data in bytes</summary>
        public long Length { get; set; }

        /// <summary>
        /// Read channel info
        /// </summary>
        public static ChannelInfo Read(BigEndianReader reader, bool isPsb)
        {
            var info = new ChannelInfo();
            info.Id = (ChannelId)reader.ReadInt16();
            info.Length = isPsb ? reader.ReadInt64() : reader.ReadUInt32();
            return info;
        }

        /// <summary>
        /// Write channel info
        /// </summary>
        public void Write(BigEndianWriter writer, bool isPsb)
        {
            writer.WriteInt16((short)Id);
            if (isPsb)
                writer.WriteInt64(Length);
            else
                writer.WriteUInt32((uint)Length);
        }
    }

    /// <summary>
    /// Channel image data (compressed pixel data)
    /// </summary>
    public class ChannelData
    {
        /// <summary>Channel ID</summary>
        public ChannelId Id { get; set; }

        /// <summary>Compression type</summary>
        public CompressionType Compression { get; set; }

        /// <summary>Raw compressed data</summary>
        public byte[] RawData { get; set; }

        /// <summary>Decompressed data (cached)</summary>
        private byte[] _decompressedData;

        /// <summary>
        /// Read channel data
        /// </summary>
        public static ChannelData Read(BigEndianReader reader, ChannelInfo info, int width, int height, int depth, int version)
        {
            var channel = new ChannelData();
            channel.Id = info.Id;

            if (info.Length < 2)
            {
                channel.Compression = CompressionType.Raw;
                channel.RawData = Array.Empty<byte>();
                return channel;
            }

            // Compression type (2 bytes)
            channel.Compression = (CompressionType)reader.ReadUInt16();

            // Compressed data
            int dataLength = (int)(info.Length - 2);
            if (dataLength > 0)
            {
                channel.RawData = reader.ReadBytes(dataLength);
            }
            else
            {
                channel.RawData = Array.Empty<byte>();
            }

            return channel;
        }

        /// <summary>
        /// Get decompressed channel data
        /// </summary>
        public byte[] GetData(int width, int height, int depth, int version)
        {
            if (_decompressedData != null)
                return _decompressedData;

            if (RawData == null || RawData.Length == 0)
            {
                int expectedSize = width * height * (depth / 8);
                _decompressedData = new byte[expectedSize];
                return _decompressedData;
            }

            _decompressedData = CompressionDecoder.Decompress(
                RawData, Compression, width, height, depth, version);

            return _decompressedData;
        }

        /// <summary>
        /// Write channel data (compression type + raw compressed data)
        /// </summary>
        public void Write(BigEndianWriter writer)
        {
            writer.WriteUInt16((ushort)Compression);
            writer.WriteBytes(RawData ?? Array.Empty<byte>());
        }

        /// <summary>
        /// Get the total byte size this channel data will occupy when written
        /// </summary>
        public long GetWriteLength()
        {
            return 2 + (RawData?.Length ?? 0);
        }

        /// <summary>
        /// Clear cached decompressed data
        /// </summary>
        public void ClearCache()
        {
            _decompressedData = null;
        }
    }
}
