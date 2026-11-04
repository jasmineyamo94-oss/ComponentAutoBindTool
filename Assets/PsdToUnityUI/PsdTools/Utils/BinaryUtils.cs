using System;

namespace PsdTools.Utils
{
    /// <summary>
    /// Binary utility functions
    /// </summary>
    public static class BinaryUtils
    {
        /// <summary>
        /// Calculate padded size
        /// </summary>
        public static int Pad(int size, int alignment)
        {
            if (alignment <= 1) return size;
            int remainder = size % alignment;
            return remainder == 0 ? size : size + (alignment - remainder);
        }

        /// <summary>
        /// Calculate padding bytes needed
        /// </summary>
        public static int GetPadding(int size, int alignment)
        {
            if (alignment <= 1) return 0;
            int remainder = size % alignment;
            return remainder == 0 ? 0 : alignment - remainder;
        }

        /// <summary>
        /// Swap bytes for endianness conversion (16-bit)
        /// </summary>
        public static ushort SwapBytes(ushort value)
        {
            return (ushort)((value >> 8) | (value << 8));
        }

        /// <summary>
        /// Swap bytes for endianness conversion (32-bit)
        /// </summary>
        public static uint SwapBytes(uint value)
        {
            return ((value >> 24) & 0xFF) |
                   ((value >> 8) & 0xFF00) |
                   ((value << 8) & 0xFF0000) |
                   ((value << 24) & 0xFF000000);
        }

        /// <summary>
        /// Swap bytes for endianness conversion (64-bit)
        /// </summary>
        public static ulong SwapBytes(ulong value)
        {
            return ((value >> 56) & 0xFF) |
                   ((value >> 40) & 0xFF00) |
                   ((value >> 24) & 0xFF0000) |
                   ((value >> 8) & 0xFF000000) |
                   ((value << 8) & 0xFF00000000) |
                   ((value << 24) & 0xFF0000000000) |
                   ((value << 40) & 0xFF000000000000) |
                   ((value << 56) & 0xFF00000000000000);
        }

        /// <summary>
        /// Convert big-endian bytes to ushort array
        /// </summary>
        public static ushort[] BytesToUInt16ArrayBE(byte[] data)
        {
            if (data.Length % 2 != 0)
                throw new ArgumentException("Data length must be a multiple of 2");

            ushort[] result = new ushort[data.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
            }
            return result;
        }

        /// <summary>
        /// Convert big-endian bytes to uint array
        /// </summary>
        public static uint[] BytesToUInt32ArrayBE(byte[] data)
        {
            if (data.Length % 4 != 0)
                throw new ArgumentException("Data length must be a multiple of 4");

            uint[] result = new uint[data.Length / 4];
            for (int i = 0; i < result.Length; i++)
            {
                int offset = i * 4;
                result[i] = ((uint)data[offset] << 24) |
                           ((uint)data[offset + 1] << 16) |
                           ((uint)data[offset + 2] << 8) |
                           data[offset + 3];
            }
            return result;
        }

        /// <summary>
        /// Convert ushort array to big-endian bytes
        /// </summary>
        public static byte[] UInt16ArrayToBytesBE(ushort[] data)
        {
            byte[] result = new byte[data.Length * 2];
            for (int i = 0; i < data.Length; i++)
            {
                result[i * 2] = (byte)(data[i] >> 8);
                result[i * 2 + 1] = (byte)(data[i] & 0xFF);
            }
            return result;
        }

        /// <summary>
        /// Convert uint array to big-endian bytes
        /// </summary>
        public static byte[] UInt32ArrayToBytesBE(uint[] data)
        {
            byte[] result = new byte[data.Length * 4];
            for (int i = 0; i < data.Length; i++)
            {
                int offset = i * 4;
                result[offset] = (byte)(data[i] >> 24);
                result[offset + 1] = (byte)(data[i] >> 16);
                result[offset + 2] = (byte)(data[i] >> 8);
                result[offset + 3] = (byte)(data[i] & 0xFF);
            }
            return result;
        }
    }
}
