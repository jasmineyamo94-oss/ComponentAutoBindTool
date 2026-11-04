using System;
using System.IO;
using System.IO.Compression;

namespace PsdTools.Compression
{
    /// <summary>
    /// ZIP compression decoder for PSD
    /// </summary>
    public static class ZipDecoder
    {
        /// <summary>
        /// Decode ZIP compressed data (DEFLATE)
        /// </summary>
        /// <param name="data">Compressed data</param>
        /// <param name="expectedSize">Expected decompressed size</param>
        /// <returns>Decompressed data</returns>
        public static byte[] Decode(byte[] data, int expectedSize)
        {
            if (data == null || data.Length == 0)
                return new byte[expectedSize];

            try
            {
                // PSD uses raw DEFLATE without zlib header
                // Try with DeflateStream first
                using (var input = new MemoryStream(data))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    deflate.CopyTo(output);
                    return output.ToArray();
                }
            }
            catch
            {
                // If that fails, try skipping zlib header (2 bytes)
                if (data.Length > 2)
                {
                    try
                    {
                        using (var input = new MemoryStream(data, 2, data.Length - 2))
                        using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                        using (var output = new MemoryStream())
                        {
                            deflate.CopyTo(output);
                            return output.ToArray();
                        }
                    }
                    catch
                    {
                        // Return empty if all fails
                        return new byte[expectedSize];
                    }
                }
                return new byte[expectedSize];
            }
        }

        /// <summary>
        /// Decode ZIP with prediction compressed data
        /// </summary>
        /// <param name="data">Compressed data</param>
        /// <param name="expectedSize">Expected decompressed size</param>
        /// <param name="width">Row width in bytes</param>
        /// <param name="depth">Bit depth</param>
        /// <returns>Decompressed data</returns>
        public static byte[] DecodeWithPrediction(byte[] data, int expectedSize, int width, int depth)
        {
            // First, decode the ZIP data
            byte[] decompressed = Decode(data, expectedSize);
            
            if (decompressed.Length == 0)
                return decompressed;

            // Then apply reverse prediction
            return ReversePrediction(decompressed, width, depth);
        }

        /// <summary>
        /// Reverse the horizontal prediction filter
        /// </summary>
        private static byte[] ReversePrediction(byte[] data, int rowWidth, int depth)
        {
            byte[] result = new byte[data.Length];
            
            int bytesPerPixel = depth / 8;
            if (bytesPerPixel < 1) bytesPerPixel = 1;
            
            int rowCount = data.Length / rowWidth;
            if (rowCount == 0) rowCount = 1;

            for (int row = 0; row < rowCount; row++)
            {
                int rowStart = row * rowWidth;
                int rowEnd = Math.Min(rowStart + rowWidth, data.Length);
                
                // First pixel(s) are stored directly
                for (int i = 0; i < bytesPerPixel && rowStart + i < rowEnd; i++)
                {
                    result[rowStart + i] = data[rowStart + i];
                }
                
                // Subsequent pixels are differences from previous
                for (int i = rowStart + bytesPerPixel; i < rowEnd; i++)
                {
                    result[i] = (byte)(data[i] + result[i - bytesPerPixel]);
                }
            }

            return result;
        }

        /// <summary>
        /// Encode data using ZIP compression
        /// </summary>
        public static byte[] Encode(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
        }

        /// <summary>
        /// Encode data using ZIP with prediction compression
        /// </summary>
        public static byte[] EncodeWithPrediction(byte[] data, int rowWidth, int depth)
        {
            // Apply prediction filter
            byte[] predicted = ApplyPrediction(data, rowWidth, depth);
            
            // Then compress
            return Encode(predicted);
        }

        /// <summary>
        /// Apply horizontal prediction filter
        /// </summary>
        private static byte[] ApplyPrediction(byte[] data, int rowWidth, int depth)
        {
            byte[] result = new byte[data.Length];
            
            int bytesPerPixel = depth / 8;
            if (bytesPerPixel < 1) bytesPerPixel = 1;
            
            int rowCount = data.Length / rowWidth;
            if (rowCount == 0) rowCount = 1;

            for (int row = 0; row < rowCount; row++)
            {
                int rowStart = row * rowWidth;
                int rowEnd = Math.Min(rowStart + rowWidth, data.Length);
                
                // First pixel(s) are stored directly
                for (int i = 0; i < bytesPerPixel && rowStart + i < rowEnd; i++)
                {
                    result[rowStart + i] = data[rowStart + i];
                }
                
                // Subsequent pixels are differences from previous
                for (int i = rowStart + bytesPerPixel; i < rowEnd; i++)
                {
                    result[i] = (byte)(data[i] - data[i - bytesPerPixel]);
                }
            }

            return result;
        }
    }
}
