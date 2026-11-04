using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PsdTools.Constants;
using PsdTools.Utils;

namespace PsdTools.Psd
{
    /// <summary>
    /// A single tagged block (additional layer information).
    /// Data is stored as raw bytes for round-trip safety.
    /// </summary>
    public class TaggedBlock
    {
        public const string SIGNATURE_8BIM = "8BIM";
        public const string SIGNATURE_8B64 = "8B64";

        /// <summary>Block signature (8BIM or 8B64)</summary>
        public string Signature { get; set; }

        /// <summary>4-byte tag key</summary>
        public string Key { get; set; }

        /// <summary>Raw block data (preserved as-is for round-trip)</summary>
        public byte[] Data { get; set; }

        /// <summary>Whether this block uses 8-byte length (PSB large block)</summary>
        public bool UseLongLength { get; set; }

        /// <summary>Actual padding bytes read after the data (preserved for round-trip)</summary>
        public byte[] PaddingBytes { get; set; }

        /// <summary>
        /// Read a single tagged block.
        /// Keeps data as raw bytes so write produces identical output.
        /// </summary>
        public static TaggedBlock Read(BigEndianReader reader, bool isPsb, int padding)
        {
            var block = new TaggedBlock();

            block.Signature = reader.ReadSignature();
            if (block.Signature != SIGNATURE_8BIM && block.Signature != SIGNATURE_8B64)
                throw new InvalidOperationException($"Invalid tagged block signature: {block.Signature}");

            block.Key = reader.ReadSignature();

            block.UseLongLength = block.Signature == SIGNATURE_8B64 || (isPsb && IsLargeBlock(block.Key));

            long length;
            if (block.UseLongLength)
                length = reader.ReadInt64();
            else
                length = reader.ReadUInt32();

            if (length > 0)
            {
                block.Data = reader.ReadBytes((int)length);
                int padSize = (int)((padding - (length % padding)) % padding);
                if (padSize > 0)
                    block.PaddingBytes = reader.ReadBytes(padSize);
            }
            else
            {
                block.Data = Array.Empty<byte>();
            }

            return block;
        }

        /// <summary>
        /// Write a single tagged block.
        /// Writes data as-is to guarantee round-trip consistency.
        /// </summary>
        public void Write(BigEndianWriter writer, bool isPsb, int padding)
        {
            writer.WriteSignature(Signature ?? SIGNATURE_8BIM);
            writer.WriteSignature(Key);

            byte[] data = Data ?? Array.Empty<byte>();
            bool useLong = UseLongLength || (Signature == SIGNATURE_8B64) || (isPsb && IsLargeBlock(Key));

            if (useLong)
                writer.WriteInt64(data.Length);
            else
                writer.WriteUInt32((uint)data.Length);

            writer.WriteBytes(data);

            if (PaddingBytes != null && PaddingBytes.Length > 0)
            {
                writer.WriteBytes(PaddingBytes);
            }
            else
            {
                int pad = (int)((padding - (data.Length % padding)) % padding);
                writer.WriteZeros(pad);
            }
        }

        public static bool IsLargeBlock(string key)
        {
            return key == "LMsk" || key == "Lr16" || key == "Lr32" ||
                   key == "Layr" || key == "Mt16" || key == "Mt32" ||
                   key == "Mtrn" || key == "Alph" || key == "FMsk" ||
                   key == "lnk2" || key == "FEid" || key == "FXid" ||
                   key == "PxSD";
        }
    }

    /// <summary>
    /// Collection of tagged blocks (preserves insertion order, supports duplicate keys).
    /// padding=1 for layer-level blocks (no extra alignment).
    /// padding=4 for section-level blocks (aligned to 4 bytes).
    /// </summary>
    public class TaggedBlocks
    {
        private readonly List<TaggedBlock> _blockList;

        /// <summary>
        /// The padding value used when these blocks were read/written.
        /// Layer-level = 1 (spec says length is already rounded, no extra padding).
        /// Section-level = 4 (data padded to 4-byte boundary).
        /// </summary>
        private int _padding;

        public TaggedBlocks(int padding = 4)
        {
            _blockList = new List<TaggedBlock>();
            _padding = padding;
        }

        public IEnumerable<TaggedBlock> Blocks => _blockList;
        public int Count => _blockList.Count;

        public void Add(TaggedBlock block)
        {
            _blockList.Add(block);
        }

        public bool Contains(string key)
        {
            for (int i = 0; i < _blockList.Count; i++)
            {
                if (_blockList[i].Key == key)
                    return true;
            }
            return false;
        }

        public TaggedBlock Get(string key)
        {
            for (int i = 0; i < _blockList.Count; i++)
            {
                if (_blockList[i].Key == key)
                    return _blockList[i];
            }
            return null;
        }

        public byte[] GetData(string key)
        {
            var block = Get(key);
            return block?.Data;
        }

        /// <summary>
        /// Get Unicode layer name if present
        /// </summary>
        public string GetUnicodeName()
        {
            var data = GetData(Tag.UNICODE_LAYER_NAME);
            if (data == null || data.Length < 4)
                return null;

            using (var reader = new BigEndianReader(data))
            {
                return reader.ReadUnicodeString();
            }
        }

        /// <summary>
        /// Get section divider type
        /// </summary>
        public SectionDivider GetSectionDivider()
        {
            var data = GetData(Tag.SECTION_DIVIDER_SETTING);
            if (data == null)
                data = GetData(Tag.SECTION_DIVIDER_SETTING2);

            if (data == null || data.Length < 4)
                return SectionDivider.Other;

            using (var reader = new BigEndianReader(data))
            {
                return (SectionDivider)reader.ReadUInt32();
            }
        }

        /// <summary>
        /// Get section divider blend mode
        /// </summary>
        public BlendMode? GetSectionDividerBlendMode()
        {
            var data = GetData(Tag.SECTION_DIVIDER_SETTING);
            if (data == null)
                data = GetData(Tag.SECTION_DIVIDER_SETTING2);

            if (data == null || data.Length < 12)
                return null;

            using (var reader = new BigEndianReader(data))
            {
                reader.Skip(4);
                string signature = reader.ReadSignature();
                if (signature != "8BIM")
                    return null;
                string blendKey = reader.ReadSignature();
                return BlendModeHelper.FromKey(blendKey);
            }
        }

        /// <summary>
        /// Get layer ID
        /// </summary>
        public int? GetLayerId()
        {
            var data = GetData(Tag.LAYER_ID);
            if (data == null || data.Length < 4)
                return null;

            using (var reader = new BigEndianReader(data))
            {
                return reader.ReadInt32();
            }
        }

        /// <summary>
        /// Set or replace raw data for a tagged block key (updates first match, or appends).
        /// </summary>
        public void SetData(string key, byte[] data)
        {
            var existing = Get(key);
            if (existing != null)
            {
                existing.Data = data ?? Array.Empty<byte>();
                existing.PaddingBytes = null;
            }
            else
            {
                Add(new TaggedBlock
                {
                    Signature = TaggedBlock.SIGNATURE_8BIM,
                    Key = key,
                    Data = data ?? Array.Empty<byte>()
                });
            }
        }

        /// <summary>
        /// Set Unicode layer name (creates or updates the "luni" tagged block)
        /// </summary>
        public void SetUnicodeName(string name)
        {
            using (var ms = new MemoryStream())
            using (var w = new BigEndianWriter(ms))
            {
                w.WriteUnicodeString(name ?? "");
                w.Flush();
                SetData(Tag.UNICODE_LAYER_NAME, ms.ToArray());
            }
        }

        /// <summary>
        /// Write all tagged blocks in original order.
        /// </summary>
        public void Write(BigEndianWriter writer, bool isPsb)
        {
            foreach (var block in _blockList)
            {
                block.Write(writer, isPsb, _padding);
            }
        }

        /// <summary>
        /// Read tagged blocks from a length-limited section.
        /// For layer-level (inside LayerRecord extra data): padding=1.
        /// For section-level (after GlobalLayerMask): padding=4.
        /// </summary>
        public static TaggedBlocks Read(BigEndianReader reader, long length, bool isPsb, int padding = 4)
        {
            var blocks = new TaggedBlocks();
            blocks._padding = padding;
            long endPosition = reader.Position + length;

            while (reader.Position + 12 <= endPosition)
            {
                try
                {
                    var block = TaggedBlock.Read(reader, isPsb, padding);
                    blocks.Add(block);
                }
                catch
                {
                    reader.Seek(endPosition);
                    break;
                }
            }

            if (reader.Position < endPosition)
                reader.Seek(endPosition);

            return blocks;
        }
    }
}
