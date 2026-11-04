using System;
using System.IO;
using System.Text;

namespace PsdTools.Utils
{
    /// <summary>
    /// Binary reader for big-endian data (PSD format)
    /// </summary>
    public class BigEndianReader : IDisposable
    {
        private readonly BinaryReader _reader;
        private readonly Stream _stream;
        private bool _disposed;

        public BigEndianReader(Stream stream)
        {
            _stream = stream;
            _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        }

        public BigEndianReader(byte[] data) : this(new MemoryStream(data))
        {
        }

        public Stream BaseStream => _stream;
        public long Position => _stream.Position;
        public long Length => _stream.Length;
        public long Remaining => _stream.Length - _stream.Position;

        public void Seek(long offset, SeekOrigin origin = SeekOrigin.Begin)
        {
            _stream.Seek(offset, origin);
        }

        public void Skip(long count)
        {
            _stream.Seek(count, SeekOrigin.Current);
        }

        public byte ReadByte()
        {
            return _reader.ReadByte();
        }

        public sbyte ReadSByte()
        {
            return _reader.ReadSByte();
        }

        public byte[] ReadBytes(int count)
        {
            return _reader.ReadBytes(count);
        }

        public short ReadInt16()
        {
            byte[] bytes = _reader.ReadBytes(2);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToInt16(bytes, 0);
        }

        public ushort ReadUInt16()
        {
            byte[] bytes = _reader.ReadBytes(2);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToUInt16(bytes, 0);
        }

        public int ReadInt32()
        {
            byte[] bytes = _reader.ReadBytes(4);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }

        public uint ReadUInt32()
        {
            byte[] bytes = _reader.ReadBytes(4);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToUInt32(bytes, 0);
        }

        public long ReadInt64()
        {
            byte[] bytes = _reader.ReadBytes(8);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToInt64(bytes, 0);
        }

        public ulong ReadUInt64()
        {
            byte[] bytes = _reader.ReadBytes(8);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToUInt64(bytes, 0);
        }

        public double ReadDouble()
        {
            byte[] bytes = _reader.ReadBytes(8);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToDouble(bytes, 0);
        }

        public float ReadSingle()
        {
            byte[] bytes = _reader.ReadBytes(4);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToSingle(bytes, 0);
        }

        /// <summary>
        /// Read a 4-byte signature string (ASCII)
        /// </summary>
        public string ReadSignature()
        {
            byte[] bytes = _reader.ReadBytes(4);
            return Encoding.ASCII.GetString(bytes);
        }

        /// <summary>
        /// Read a Pascal string (1-byte length prefix)
        /// </summary>
        /// <param name="padding">Pad to this alignment (default 2 bytes)</param>
        public string ReadPascalString(int padding = 2)
        {
            long startPos = _stream.Position;
            int length = _reader.ReadByte();
            
            string result;
            if (length > 0)
            {
                byte[] bytes = _reader.ReadBytes(length);
                // MacRoman encoding fallback to Latin1
                result = Encoding.GetEncoding("iso-8859-1").GetString(bytes);
            }
            else
            {
                result = string.Empty;
            }

            // Pad to alignment
            if (padding > 1)
            {
                long bytesRead = _stream.Position - startPos;
                int paddingNeeded = (int)((padding - (bytesRead % padding)) % padding);
                if (paddingNeeded > 0)
                    Skip(paddingNeeded);
            }

            return result;
        }

        /// <summary>
        /// Read a Unicode string (4-byte length prefix, UTF-16 BE)
        /// </summary>
        /// <param name="padding">Pad to this alignment (default 1 byte = no padding)</param>
        public string ReadUnicodeString(int padding = 1)
        {
            long startPos = _stream.Position;
            uint length = ReadUInt32(); // Number of characters (not bytes)
            
            string result;
            if (length > 0)
            {
                byte[] bytes = _reader.ReadBytes((int)(length * 2));
                result = Encoding.BigEndianUnicode.GetString(bytes);
                // Remove null terminator if present
                result = result.TrimEnd('\0');
            }
            else
            {
                result = string.Empty;
            }

            // Pad to alignment
            if (padding > 1)
            {
                long bytesRead = _stream.Position - startPos;
                int paddingNeeded = (int)((padding - (bytesRead % padding)) % padding);
                if (paddingNeeded > 0)
                    Skip(paddingNeeded);
            }

            return result;
        }

        /// <summary>
        /// Read a length-prefixed block of data
        /// </summary>
        /// <param name="use64Bit">Use 64-bit length for PSB format</param>
        /// <param name="padding">Pad to this alignment</param>
        public byte[] ReadLengthBlock(bool use64Bit = false, int padding = 1)
        {
            long startPos = _stream.Position;
            long length = use64Bit ? ReadInt64() : ReadUInt32();
            
            byte[] data;
            if (length > 0)
            {
                data = _reader.ReadBytes((int)length);
            }
            else
            {
                data = Array.Empty<byte>();
            }

            // Pad to alignment
            if (padding > 1)
            {
                long bytesRead = _stream.Position - startPos;
                int paddingNeeded = (int)((padding - (bytesRead % padding)) % padding);
                if (paddingNeeded > 0)
                    Skip(paddingNeeded);
            }

            return data;
        }

        /// <summary>
        /// Read remaining bytes in the stream
        /// </summary>
        public byte[] ReadToEnd()
        {
            int remaining = (int)(_stream.Length - _stream.Position);
            return _reader.ReadBytes(remaining);
        }

        /// <summary>
        /// Read a fixed-point number (16.16 format)
        /// </summary>
        public double ReadFixedPoint()
        {
            int value = ReadInt32();
            return value / 65536.0;
        }

        /// <summary>
        /// Read a 32-bit fixed point stored as two 16-bit values
        /// </summary>
        public double ReadFixedPoint32Bit()
        {
            ushort integer = ReadUInt16();
            ushort fraction = ReadUInt16();
            return integer + (fraction / 65536.0);
        }

        /// <summary>
        /// Skip padding to align to specified boundary
        /// </summary>
        public void SkipPadding(long startPosition, int alignment)
        {
            long bytesRead = _stream.Position - startPosition;
            int paddingNeeded = (int)((alignment - (bytesRead % alignment)) % alignment);
            if (paddingNeeded > 0)
                Skip(paddingNeeded);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _reader.Dispose();
                _stream.Dispose();
                _disposed = true;
            }
        }
    }
}
