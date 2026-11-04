using System;

namespace PsdTools.Compression
{
    /// <summary>
    /// RLE (Run-Length Encoding) decoder using Apple PackBits algorithm
    /// </summary>
    public static class RleDecoder
    {
        /// <summary>
        /// Decode RLE compressed data (PackBits algorithm)
        /// </summary>
        /// <param name="data">Compressed data</param>
        /// <param name="expectedSize">Expected decompressed size</param>
        /// <returns>Decompressed data</returns>
        public static byte[] Decode(byte[] data, int expectedSize)
        {
            if (data == null || data.Length == 0)
                return new byte[expectedSize];

            byte[] result = new byte[expectedSize];
            int srcPos = 0;
            int dstPos = 0;

            while (srcPos < data.Length && dstPos < expectedSize)
            {
                sbyte header = (sbyte)data[srcPos++];

                if (header >= 0)
                {
                    // 0 to 127: copy next (header + 1) literal bytes
                    int count = header + 1;
                    
                    // Bounds check
                    if (srcPos + count > data.Length)
                        count = data.Length - srcPos;
                    if (dstPos + count > expectedSize)
                        count = expectedSize - dstPos;
                    
                    Buffer.BlockCopy(data, srcPos, result, dstPos, count);
                    srcPos += count;
                    dstPos += count;
                }
                else if (header > -128)
                {
                    // -1 to -127: repeat next byte (1 - header) times
                    int count = 1 - header;
                    
                    if (srcPos >= data.Length)
                        break;
                    
                    byte value = data[srcPos++];
                    
                    // Bounds check
                    if (dstPos + count > expectedSize)
                        count = expectedSize - dstPos;
                    
                    for (int i = 0; i < count; i++)
                    {
                        result[dstPos++] = value;
                    }
                }
                // header == -128 (0x80): no-op, skip
            }

            return result;
        }

        /// <summary>
        /// Encode data using RLE (PackBits algorithm)
        /// </summary>
        /// <param name="data">Raw data to compress</param>
        /// <returns>Compressed data</returns>
        public static byte[] Encode(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            // Worst case: all literal bytes = 1 header + data for every 128 bytes
            // Plus a bit of extra for safety
            byte[] buffer = new byte[data.Length * 2 + 1];
            int srcPos = 0;
            int dstPos = 0;

            while (srcPos < data.Length)
            {
                // Find run of identical bytes
                int runStart = srcPos;
                byte runValue = data[srcPos];
                int runLength = 1;
                
                while (srcPos + runLength < data.Length && 
                       data[srcPos + runLength] == runValue && 
                       runLength < 128)
                {
                    runLength++;
                }

                if (runLength >= 3)
                {
                    // Worth encoding as a run
                    buffer[dstPos++] = (byte)(1 - runLength); // -2 to -127
                    buffer[dstPos++] = runValue;
                    srcPos += runLength;
                }
                else
                {
                    // Find sequence of non-repeating bytes
                    int literalStart = srcPos;
                    int literalLength = 0;
                    
                    while (srcPos < data.Length && literalLength < 128)
                    {
                        // Check if we're at the start of a run
                        int lookAhead = 1;
                        while (srcPos + lookAhead < data.Length && 
                               data[srcPos + lookAhead] == data[srcPos] && 
                               lookAhead < 3)
                        {
                            lookAhead++;
                        }
                        
                        if (lookAhead >= 3)
                            break; // Start a new run
                        
                        srcPos++;
                        literalLength++;
                    }
                    
                    if (literalLength > 0)
                    {
                        buffer[dstPos++] = (byte)(literalLength - 1); // 0 to 127
                        Buffer.BlockCopy(data, literalStart, buffer, dstPos, literalLength);
                        dstPos += literalLength;
                    }
                }
            }

            // Copy to correctly sized array
            byte[] result = new byte[dstPos];
            Buffer.BlockCopy(buffer, 0, result, 0, dstPos);
            return result;
        }
    }
}
