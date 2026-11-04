using System;
using PsdTools.Constants;
using PsdTools.Utils;

namespace PsdTools.Psd
{
    /// <summary>
    /// PSD File Header (26 bytes)
    /// </summary>
    public class FileHeader
    {
        public const string PSD_SIGNATURE = "8BPS";
        public const int HEADER_SIZE = 26;

        /// <summary>File signature, always "8BPS"</summary>
        public string Signature { get; private set; }

        /// <summary>Version number: 1 = PSD, 2 = PSB (large document)</summary>
        public ushort Version { get; private set; }

        /// <summary>Number of channels in the image (1-56)</summary>
        public ushort Channels { get; private set; }

        /// <summary>Height of the image in pixels (1-300000, or 1-4000000 for PSB)</summary>
        public uint Height { get; private set; }

        /// <summary>Width of the image in pixels (1-300000, or 1-4000000 for PSB)</summary>
        public uint Width { get; private set; }

        /// <summary>Bits per channel (1, 8, 16, or 32)</summary>
        public ushort Depth { get; private set; }

        /// <summary>Color mode of the image</summary>
        public ColorMode ColorMode { get; private set; }

        /// <summary>Reserved 6 bytes (spec says must be zero, but stored for round-trip safety)</summary>
        public byte[] Reserved { get; private set; }

        /// <summary>Whether this is a PSB (large document) file</summary>
        public bool IsPsb => Version == 2;

        /// <summary>
        /// Read header from binary stream
        /// </summary>
        public static FileHeader Read(BigEndianReader reader)
        {
            var header = new FileHeader();

            // Signature (4 bytes)
            header.Signature = reader.ReadSignature();
            if (header.Signature != PSD_SIGNATURE)
            {
                throw new InvalidOperationException($"Invalid PSD signature: expected '{PSD_SIGNATURE}', got '{header.Signature}'");
            }

            // Version (2 bytes)
            header.Version = reader.ReadUInt16();
            if (header.Version != 1 && header.Version != 2)
            {
                throw new InvalidOperationException($"Unsupported PSD version: {header.Version}");
            }

            // Reserved (6 bytes)
            header.Reserved = reader.ReadBytes(6);

            // Channels (2 bytes)
            header.Channels = reader.ReadUInt16();
            if (header.Channels < 1 || header.Channels > 56)
            {
                throw new InvalidOperationException($"Invalid channel count: {header.Channels}");
            }

            // Height (4 bytes)
            header.Height = reader.ReadUInt32();

            // Width (4 bytes)
            header.Width = reader.ReadUInt32();

            // Depth (2 bytes)
            header.Depth = reader.ReadUInt16();
            if (header.Depth != 1 && header.Depth != 8 && header.Depth != 16 && header.Depth != 32)
            {
                throw new InvalidOperationException($"Invalid bit depth: {header.Depth}");
            }

            // Color mode (2 bytes)
            header.ColorMode = (ColorMode)reader.ReadUInt16();

            return header;
        }

        /// <summary>
        /// Write header to binary stream
        /// </summary>
        public void Write(BigEndianWriter writer)
        {
            writer.WriteSignature(PSD_SIGNATURE);
            writer.WriteUInt16(Version);
            writer.WriteBytes(Reserved ?? new byte[6]);
            writer.WriteUInt16(Channels);
            writer.WriteUInt32(Height);
            writer.WriteUInt32(Width);
            writer.WriteUInt16(Depth);
            writer.WriteUInt16((ushort)ColorMode);
        }

        public override string ToString()
        {
            return $"PSD Header: {Width}x{Height}, {Channels} channels, {Depth}-bit, {ColorMode}, Version {Version}";
        }
    }
}
