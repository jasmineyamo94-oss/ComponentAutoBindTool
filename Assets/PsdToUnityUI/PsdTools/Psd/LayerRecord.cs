using System;
using System.Collections.Generic;
using System.IO;
using PsdTools.Constants;
using PsdTools.Utils;

namespace PsdTools.Psd
{
    /// <summary>
    /// Layer flags
    /// </summary>
    [Flags]
    public enum LayerFlags : byte
    {
        None = 0,
        TransparencyProtected = 1,
        Hidden = 2,
        Obsolete = 4,
        PhotoshopV5 = 8,
        PixelDataIrrelevant = 16
    }

    /// <summary>
    /// Layer record containing layer metadata.
    /// Blending ranges are stored as raw bytes for round-trip safety.
    /// </summary>
    public class LayerRecord
    {
        public int Top { get; set; }
        public int Left { get; set; }
        public int Bottom { get; set; }
        public int Right { get; set; }

        public List<ChannelInfo> ChannelInfos { get; set; }

        public string BlendModeSignature { get; set; }
        /// <summary>Raw 4-byte blend mode key, preserved for round-trip</summary>
        public string BlendModeKeyRaw { get; set; }
        public BlendMode BlendMode { get; set; }
        public byte Opacity { get; set; }
        public Clipping Clipping { get; set; }
        public LayerFlags Flags { get; set; }

        /// <summary>Undocumented byte between flags and extra data length (filler)</summary>
        public byte Filler { get; set; }

        public MaskData Mask { get; set; }

        /// <summary>Blending ranges stored as raw bytes (length-prefixed block)</summary>
        public byte[] BlendingRangesRaw { get; set; }

        /// <summary>Layer name (from Pascal string)</summary>
        public string Name { get; set; }

        /// <summary>Raw Pascal string bytes (length + data + padding) for round-trip safety</summary>
        public byte[] NamePascalRaw { get; set; }

        /// <summary>Whether the Pascal name was changed by the user</summary>
        private bool _nameDirty;

        public TaggedBlocks TaggedBlocks { get; set; }

        public List<ChannelData> ChannelData { get; set; }

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        /// <summary>Whether layer is visible (Hidden flag NOT set)</summary>
        public bool IsVisible => (Flags & LayerFlags.Hidden) == 0;

        /// <summary>Whether layer is a group (based on section divider)</summary>
        public bool IsGroup => TaggedBlocks?.GetSectionDivider() == SectionDivider.BoundingSectionDivider;

        /// <summary>Whether this is a group end marker</summary>
        public bool IsGroupEnd
        {
            get
            {
                var divider = TaggedBlocks?.GetSectionDivider() ?? SectionDivider.Other;
                return divider == SectionDivider.OpenFolder || divider == SectionDivider.ClosedFolder;
            }
        }

        /// <summary>
        /// Get Unicode name if available, otherwise Pascal name
        /// </summary>
        public string GetName()
        {
            return TaggedBlocks?.GetUnicodeName() ?? Name;
        }

        /// <summary>
        /// Read a layer record
        /// </summary>
        public static LayerRecord Read(BigEndianReader reader, bool isPsb)
        {
            var record = new LayerRecord();

            record.Top = reader.ReadInt32();
            record.Left = reader.ReadInt32();
            record.Bottom = reader.ReadInt32();
            record.Right = reader.ReadInt32();

            ushort channelCount = reader.ReadUInt16();
            record.ChannelInfos = new List<ChannelInfo>(channelCount);
            for (int i = 0; i < channelCount; i++)
                record.ChannelInfos.Add(ChannelInfo.Read(reader, isPsb));

            record.BlendModeSignature = reader.ReadSignature();
            if (record.BlendModeSignature != "8BIM")
                throw new InvalidOperationException($"Invalid blend mode signature: {record.BlendModeSignature}");

            record.BlendModeKeyRaw = reader.ReadSignature();
            record.BlendMode = BlendModeHelper.FromKey(record.BlendModeKeyRaw);

            record.Opacity = reader.ReadByte();
            record.Clipping = (Clipping)reader.ReadByte();
            record.Flags = (LayerFlags)reader.ReadByte();
            record.Filler = reader.ReadByte();

            uint extraDataLength = reader.ReadUInt32();
            long extraDataEnd = reader.Position + extraDataLength;

            if (extraDataLength > 0)
            {
                record.Mask = MaskData.Read(reader);

                // Blending ranges: read as raw length-prefixed block
                uint blendRangesLength = reader.ReadUInt32();
                if (blendRangesLength > 0)
                    record.BlendingRangesRaw = reader.ReadBytes((int)blendRangesLength);
                else
                    record.BlendingRangesRaw = Array.Empty<byte>();

                long pascalStart = reader.Position;
                record.Name = reader.ReadPascalString(4);
                long pascalEnd = reader.Position;
                reader.Seek(pascalStart);
                record.NamePascalRaw = reader.ReadBytes((int)(pascalEnd - pascalStart));

                long remainingLength = extraDataEnd - reader.Position;
                if (remainingLength > 0)
                    record.TaggedBlocks = TaggedBlocks.Read(reader, remainingLength, isPsb, 1);
                else
                    record.TaggedBlocks = new TaggedBlocks(1);
            }
            else
            {
                record.Name = "";
                record.TaggedBlocks = new TaggedBlocks(1);
            }

            reader.Seek(extraDataEnd);
            return record;
        }

        /// <summary>
        /// Set layer visibility
        /// </summary>
        public void SetVisible(bool visible)
        {
            if (visible)
                Flags &= ~LayerFlags.Hidden;
            else
                Flags |= LayerFlags.Hidden;
        }

        /// <summary>
        /// Set layer name (updates both Pascal name and Unicode tagged block)
        /// </summary>
        public void SetName(string name)
        {
            Name = name ?? "";
            _nameDirty = true;
            if (TaggedBlocks == null)
                TaggedBlocks = new TaggedBlocks(1);
            TaggedBlocks.SetUnicodeName(Name);
        }

        /// <summary>
        /// Update channel info lengths to match actual channel data sizes before writing
        /// </summary>
        public void UpdateChannelLengths()
        {
            if (ChannelInfos == null || ChannelData == null)
                return;
            for (int i = 0; i < ChannelInfos.Count && i < ChannelData.Count; i++)
                ChannelInfos[i].Length = ChannelData[i].GetWriteLength();
        }

        /// <summary>
        /// Write a layer record to binary stream
        /// </summary>
        public void Write(BigEndianWriter writer, bool isPsb)
        {
            writer.WriteInt32(Top);
            writer.WriteInt32(Left);
            writer.WriteInt32(Bottom);
            writer.WriteInt32(Right);

            ushort channelCount = (ushort)(ChannelInfos?.Count ?? 0);
            writer.WriteUInt16(channelCount);
            if (ChannelInfos != null)
            {
                foreach (var ci in ChannelInfos)
                    ci.Write(writer, isPsb);
            }

            writer.WriteSignature(BlendModeSignature ?? "8BIM");
            writer.WriteSignature(BlendModeKeyRaw ?? BlendModeHelper.ToKey(BlendMode));
            writer.WriteByte(Opacity);
            writer.WriteByte((byte)Clipping);
            writer.WriteByte((byte)Flags);
            writer.WriteByte(Filler);

            byte[] extraData = WriteExtraData(isPsb);
            writer.WriteUInt32((uint)extraData.Length);
            writer.WriteBytes(extraData);
        }

        private byte[] WriteExtraData(bool isPsb)
        {
            using (var ms = new MemoryStream())
            using (var w = new BigEndianWriter(ms))
            {
                if (Mask != null)
                    Mask.Write(w);
                else
                    w.WriteUInt32(0);

                byte[] blendRanges = BlendingRangesRaw ?? Array.Empty<byte>();
                w.WriteUInt32((uint)blendRanges.Length);
                w.WriteBytes(blendRanges);

                if (!_nameDirty && NamePascalRaw != null && NamePascalRaw.Length > 0)
                    w.WriteBytes(NamePascalRaw);
                else
                    w.WritePascalString(Name ?? "", 4);

                if (TaggedBlocks != null && TaggedBlocks.Count > 0)
                    TaggedBlocks.Write(w, isPsb);

                // Pad extra data to 2-byte boundary (matches psd-tools behavior)
                if (ms.Position % 2 != 0)
                    w.WriteByte(0);

                w.Flush();
                return ms.ToArray();
            }
        }

        public override string ToString()
        {
            return $"Layer '{GetName()}': {Left},{Top} - {Right},{Bottom} ({Width}x{Height})";
        }
    }
}
