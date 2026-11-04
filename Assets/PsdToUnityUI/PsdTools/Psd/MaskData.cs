using System;
using PsdTools.Utils;

namespace PsdTools.Psd
{
    /// <summary>
    /// Mask flags
    /// </summary>
    [Flags]
    public enum MaskFlags : byte
    {
        None = 0,
        PositionRelativeToLayer = 1,
        MaskDisabled = 2,
        InvertMaskOnBlend = 4,
        MaskFromRenderingData = 8,
        ParametersApplied = 16
    }

    /// <summary>
    /// Layer mask data.
    /// Stores the raw bytes read from the file so that Write produces identical output.
    /// Parsed fields are provided as read-only convenience accessors.
    /// </summary>
    public class MaskData
    {
        /// <summary>Raw mask data bytes (everything inside the length-prefixed block)</summary>
        public byte[] RawData { get; set; }

        /// <summary>Top coordinate of mask</summary>
        public int Top { get; private set; }

        /// <summary>Left coordinate of mask</summary>
        public int Left { get; private set; }

        /// <summary>Bottom coordinate of mask</summary>
        public int Bottom { get; private set; }

        /// <summary>Right coordinate of mask</summary>
        public int Right { get; private set; }

        /// <summary>Default color (0 or 255)</summary>
        public byte DefaultColor { get; private set; }

        /// <summary>Mask flags</summary>
        public MaskFlags Flags { get; private set; }

        /// <summary>Mask width</summary>
        public int Width => Right - Left;

        /// <summary>Mask height</summary>
        public int Height => Bottom - Top;

        /// <summary>Whether mask is disabled</summary>
        public bool IsDisabled => (Flags & MaskFlags.MaskDisabled) != 0;

        /// <summary>Whether position is relative to layer</summary>
        public bool IsPositionRelativeToLayer => (Flags & MaskFlags.PositionRelativeToLayer) != 0;

        /// <summary>
        /// Read mask data from binary stream.
        /// The entire block is stored as raw bytes for round-trip safety.
        /// </summary>
        public static MaskData Read(BigEndianReader reader)
        {
            uint size = reader.ReadUInt32();
            if (size == 0)
                return null;

            var mask = new MaskData();
            mask.RawData = reader.ReadBytes((int)size);

            // Parse convenience fields from the raw data
            if (mask.RawData.Length >= 18)
            {
                using (var r = new BigEndianReader(mask.RawData))
                {
                    mask.Top = r.ReadInt32();
                    mask.Left = r.ReadInt32();
                    mask.Bottom = r.ReadInt32();
                    mask.Right = r.ReadInt32();
                    mask.DefaultColor = r.ReadByte();
                    mask.Flags = (MaskFlags)r.ReadByte();
                }
            }

            return mask;
        }

        /// <summary>
        /// Write mask data to binary stream.
        /// Writes the raw bytes exactly as read, guaranteeing round-trip consistency.
        /// </summary>
        public void Write(BigEndianWriter writer)
        {
            byte[] data = RawData ?? Array.Empty<byte>();
            writer.WriteUInt32((uint)data.Length);
            writer.WriteBytes(data);
        }
    }
}
