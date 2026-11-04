using System;
using System.Collections.Generic;
using PsdTools.Constants;
using PsdTools.Compression;
using PsdTools.Utils;

namespace PsdTools.Psd
{
    /// <summary>
    /// Merged (composite) image data
    /// </summary>
    public class ImageData
    {
        /// <summary>Compression type</summary>
        public CompressionType Compression { get; private set; }

        /// <summary>Raw compressed data</summary>
        public byte[] RawData { get; set; }

        /// <summary>Cached decompressed channel data</summary>
        private List<byte[]> _channelData;

        /// <summary>
        /// Read image data from stream
        /// </summary>
        public static ImageData Read(BigEndianReader reader, FileHeader header)
        {
            var data = new ImageData();

            // Check if there's any remaining data
            if (reader.Remaining <= 0)
            {
                data.Compression = CompressionType.Raw;
                data.RawData = Array.Empty<byte>();
                return data;
            }

            // Compression type (2 bytes)
            data.Compression = (CompressionType)reader.ReadUInt16();

            // Remaining data
            data.RawData = reader.ReadToEnd();

            return data;
        }

        /// <summary>
        /// Get decompressed data for all channels
        /// </summary>
        public List<byte[]> GetChannelData(FileHeader header)
        {
            if (_channelData != null)
                return _channelData;

            _channelData = new List<byte[]>();

            if (RawData == null || RawData.Length == 0)
            {
                // Create empty channels
                int channelSize = (int)(header.Width * header.Height * (header.Depth / 8));
                for (int i = 0; i < header.Channels; i++)
                {
                    _channelData.Add(new byte[channelSize]);
                }
                return _channelData;
            }

            int width = (int)header.Width;
            int height = (int)header.Height;
            int depth = header.Depth;
            int version = header.Version;
            int channels = header.Channels;

            switch (Compression)
            {
                case CompressionType.Raw:
                    DecodeRaw(header);
                    break;

                case CompressionType.Rle:
                    DecodeRle(header);
                    break;

                case CompressionType.Zip:
                    DecodeZip(header, false);
                    break;

                case CompressionType.ZipWithPrediction:
                    DecodeZip(header, true);
                    break;
            }

            return _channelData;
        }

        private void DecodeRaw(FileHeader header)
        {
            int channelSize = (int)(header.Width * header.Height * (header.Depth / 8));
            int offset = 0;

            for (int i = 0; i < header.Channels; i++)
            {
                byte[] channel = new byte[channelSize];
                int copyLength = Math.Min(channelSize, RawData.Length - offset);
                if (copyLength > 0)
                {
                    Buffer.BlockCopy(RawData, offset, channel, 0, copyLength);
                }
                _channelData.Add(channel);
                offset += channelSize;
            }
        }

        private void DecodeRle(FileHeader header)
        {
            int width = (int)header.Width;
            int height = (int)header.Height;
            int channels = header.Channels;
            int depth = header.Depth;
            int version = header.Version;

            // Row length header size
            int rowLengthSize = version == 2 ? 4 : 2;
            int totalRows = height * channels;
            int headerSize = totalRows * rowLengthSize;

            if (RawData.Length < headerSize)
            {
                // Not enough data
                int channelSize = width * height * (depth / 8);
                for (int i = 0; i < channels; i++)
                {
                    _channelData.Add(new byte[channelSize]);
                }
                return;
            }

            // Read all row lengths
            int[] rowLengths = new int[totalRows];
            int offset = 0;
            for (int i = 0; i < totalRows; i++)
            {
                if (version == 2)
                {
                    rowLengths[i] = (RawData[offset] << 24) | (RawData[offset + 1] << 16) |
                                   (RawData[offset + 2] << 8) | RawData[offset + 3];
                    offset += 4;
                }
                else
                {
                    rowLengths[i] = (RawData[offset] << 8) | RawData[offset + 1];
                    offset += 2;
                }
            }

            // Decompress each channel
            int rowBytes = width * (depth / 8);
            int channelSize2 = rowBytes * height;
            int dataOffset = headerSize;

            for (int c = 0; c < channels; c++)
            {
                byte[] channel = new byte[channelSize2];
                int channelOffset = 0;

                for (int row = 0; row < height; row++)
                {
                    int rowIndex = c * height + row;
                    int rowLength = rowLengths[rowIndex];

                    if (dataOffset + rowLength > RawData.Length)
                        break;

                    byte[] rowData = new byte[rowLength];
                    Buffer.BlockCopy(RawData, dataOffset, rowData, 0, rowLength);

                    byte[] decompressed = RleDecoder.Decode(rowData, rowBytes);
                    int copyLength = Math.Min(rowBytes, channelSize2 - channelOffset);
                    if (copyLength > 0)
                    {
                        Buffer.BlockCopy(decompressed, 0, channel, channelOffset, copyLength);
                    }

                    dataOffset += rowLength;
                    channelOffset += rowBytes;
                }

                _channelData.Add(channel);
            }
        }

        private void DecodeZip(FileHeader header, bool withPrediction)
        {
            int width = (int)header.Width;
            int height = (int)header.Height;
            int channels = header.Channels;
            int depth = header.Depth;
            int rowBytes = width * (depth / 8);
            int channelSize = rowBytes * height;

            // For merged image, all channels are compressed together
            byte[] decompressed;
            if (withPrediction)
            {
                decompressed = ZipDecoder.DecodeWithPrediction(RawData, channelSize * channels, rowBytes, depth);
            }
            else
            {
                decompressed = ZipDecoder.Decode(RawData, channelSize * channels);
            }

            // Split into channels
            int offset = 0;
            for (int c = 0; c < channels; c++)
            {
                byte[] channel = new byte[channelSize];
                int copyLength = Math.Min(channelSize, decompressed.Length - offset);
                if (copyLength > 0)
                {
                    Buffer.BlockCopy(decompressed, offset, channel, 0, copyLength);
                }
                _channelData.Add(channel);
                offset += channelSize;
            }
        }

        /// <summary>
        /// Write image data to binary stream
        /// </summary>
        public void Write(BigEndianWriter writer)
        {
            writer.WriteUInt16((ushort)Compression);
            writer.WriteBytes(RawData ?? Array.Empty<byte>());
        }

        /// <summary>
        /// Clear cached data
        /// </summary>
        public void ClearCache()
        {
            _channelData = null;
        }
    }
}
