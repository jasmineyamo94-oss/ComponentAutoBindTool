using System;
using System.IO;
using System.Text;

namespace PsdTools.Utils
{
    /// <summary>
    /// Binary writer for big-endian data (PSD format)
    /// </summary>
    public class BigEndianWriter : IDisposable
    {
        private readonly BinaryWriter _writer;
        private readonly Stream _stream;
        private bool _disposed;

        public BigEndianWriter(Stream stream)
        {
            _stream = stream;
            _writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        }

        public Stream BaseStream => _stream;
        public long Position => _stream.Position;

        public void Seek(long offset, SeekOrigin origin = SeekOrigin.Begin)
        {
            _stream.Seek(offset, origin);
        }

        public void WriteByte(byte value)
        {
            _writer.Write(value);
        }

        public void WriteBytes(byte[] data)
        {
            if (data != null && data.Length > 0)
                _writer.Write(data);
        }

        public void WriteInt16(short value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _writer.Write(bytes);
        }

        public void WriteUInt16(ushort value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _writer.Write(bytes);
        }

        public void WriteInt32(int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _writer.Write(bytes);
        }

        public void WriteUInt32(uint value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _writer.Write(bytes);
        }

        public void WriteInt64(long value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _writer.Write(bytes);
        }

        public void WriteUInt64(ulong value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _writer.Write(bytes);
        }

        public void WriteDouble(double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _writer.Write(bytes);
        }

        /// <summary>
        /// Write a 4-byte ASCII signature
        /// </summary>
        public void WriteSignature(string signature)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(signature);
            if (bytes.Length != 4)
                throw new ArgumentException("Signature must be exactly 4 characters");
            _writer.Write(bytes);
        }

        /// <summary>
        /// Write a Pascal string (1-byte length prefix) with padding
        /// </summary>
        public void WritePascalString(string value, int padding = 2)
        {
            long startPos = _stream.Position;
            byte[] data;
            try
            {
                data = Encoding.GetEncoding("iso-8859-1").GetBytes(value ?? "");
            }
            catch
            {
                data = new byte[0];
            }

            byte length = (byte)Math.Min(data.Length, 255);
            _writer.Write(length);
            if (length > 0)
                _writer.Write(data, 0, length);

            if (padding > 1)
            {
                long bytesWritten = _stream.Position - startPos;
                int paddingNeeded = (int)((padding - (bytesWritten % padding)) % padding);
                for (int i = 0; i < paddingNeeded; i++)
                    _writer.Write((byte)0);
            }
        }

        /// <summary>
        /// Write a Unicode string (4-byte char count prefix, UTF-16 BE)
        /// </summary>
        public void WriteUnicodeString(string value, int padding = 1)
        {
            long startPos = _stream.Position;
            value = value ?? "";

            WriteUInt32((uint)value.Length);
            byte[] bytes = Encoding.BigEndianUnicode.GetBytes(value);
            _writer.Write(bytes);

            if (padding > 1)
            {
                long bytesWritten = _stream.Position - startPos;
                int paddingNeeded = (int)((padding - (bytesWritten % padding)) % padding);
                for (int i = 0; i < paddingNeeded; i++)
                    _writer.Write((byte)0);
            }
        }

        /// <summary>
        /// Write padding zeros to align to specified boundary
        /// </summary>
        public int WritePadding(long size, int divisor)
        {
            if (divisor <= 1) return 0;
            int remainder = (int)(size % divisor);
            if (remainder == 0) return 0;
            int paddingNeeded = divisor - remainder;
            for (int i = 0; i < paddingNeeded; i++)
                _writer.Write((byte)0);
            return paddingNeeded;
        }

        /// <summary>
        /// Write zeros
        /// </summary>
        public void WriteZeros(int count)
        {
            for (int i = 0; i < count; i++)
                _writer.Write((byte)0);
        }

        public void Flush()
        {
            _writer.Flush();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _writer.Flush();
                _writer.Dispose();
                _disposed = true;
            }
        }
    }
}
