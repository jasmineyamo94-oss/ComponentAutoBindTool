using System;
using PsdTools.Constants;

namespace PsdTools.Compression
{
    /// <summary>
    /// Unified compression decoder for PSD image data
    /// </summary>
    public static class CompressionDecoder
    {
        /// <summary>
        /// Decompress image data based on compression type
        /// </summary>
        /// <param name="data">Compressed data</param>
        /// <param name="compression">Compression type</param>
        /// <param name="width">Image width in pixels</param>
        /// <param name="height">Image height in pixels</param>
        /// <param name="depth">Bit depth (8, 16, or 32)</param>
        /// <param name="version">PSD version (1 or 2)</param>
        /// <returns>Decompressed data</returns>
        public static byte[] Decompress(byte[] data, CompressionType compression, int width, int height, int depth, int version)
        {
            int rowBytes = width * (depth / 8);
            int expectedSize = rowBytes * height;

            switch (compression)
            {
                case CompressionType.Raw:
                    return DecompressRaw(data, expectedSize);

                case CompressionType.Rle:
                    return DecompressRle(data, width, height, depth, version);

                case CompressionType.Zip:
                    return ZipDecoder.Decode(data, expectedSize);

                case CompressionType.ZipWithPrediction:
                    return ZipDecoder.DecodeWithPrediction(data, expectedSize, rowBytes, depth);

                default:
                    throw new NotSupportedException($"Compression type {compression} is not supported");
            }
        }

        /// <summary>
        /// Decompress raw (uncompressed) data
        /// </summary>
        private static byte[] DecompressRaw(byte[] data, int expectedSize)
        {
            if (data.Length >= expectedSize)
            {
                byte[] result = new byte[expectedSize];
                Buffer.BlockCopy(data, 0, result, 0, expectedSize);
                return result;
            }
            
            // Pad with zeros if data is shorter
            byte[] padded = new byte[expectedSize];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            return padded;
        }

        /// <summary>
        /// Decompress RLE data with row length headers
        /// </summary>
        private static byte[] DecompressRle(byte[] data, int width, int height, int depth, int version)
        {
            int rowBytes = width * (depth / 8);
            int expectedSize = rowBytes * height;
            
            // Row length header size: 2 bytes for PSD, 4 bytes for PSB
            int rowLengthSize = version == 2 ? 4 : 2;
            int headerSize = height * rowLengthSize;
            
            if (data.Length < headerSize)
            {
                return new byte[expectedSize];
            }

            // Read row lengths
            int[] rowLengths = new int[height];
            int offset = 0;
            
            for (int i = 0; i < height; i++)
            {
                if (version == 2)
                {
                    rowLengths[i] = (data[offset] << 24) | (data[offset + 1] << 16) | 
                                   (data[offset + 2] << 8) | data[offset + 3];
                    offset += 4;
                }
                else
                {
                    rowLengths[i] = (data[offset] << 8) | data[offset + 1];
                    offset += 2;
                }
            }

            // Decompress each row
            byte[] result = new byte[expectedSize];
            int dataOffset = headerSize;
            int resultOffset = 0;

            for (int row = 0; row < height; row++)
            {
                int rowLength = rowLengths[row];
                
                if (dataOffset + rowLength > data.Length)
                {
                    // Not enough data, stop
                    break;
                }

                // Extract row data
                byte[] rowData = new byte[rowLength];
                Buffer.BlockCopy(data, dataOffset, rowData, 0, rowLength);
                
                // Decompress row
                byte[] decompressedRow = RleDecoder.Decode(rowData, rowBytes);
                
                // Copy to result
                int copyLength = Math.Min(rowBytes, expectedSize - resultOffset);
                if (copyLength > 0)
                {
                    Buffer.BlockCopy(decompressedRow, 0, result, resultOffset, copyLength);
                }
                
                dataOffset += rowLength;
                resultOffset += rowBytes;
            }

            return result;
        }

        /// <summary>
        /// Decompress channel data (single channel from layer)
        /// </summary>
        public static byte[] DecompressChannel(byte[] data, CompressionType compression, int width, int height, int depth, int version)
        {
            return Decompress(data, compression, width, height, depth, version);
        }
    }
}
