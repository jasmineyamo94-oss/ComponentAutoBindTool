using System;
using System.Collections.Generic;
using System.IO;
using PsdTools.Utils;

namespace PsdTools.Psd
{
    /// <summary>
    /// Global layer mask info — stored as raw bytes for round-trip safety.
    /// </summary>
    public class GlobalLayerMaskInfo
    {
        /// <summary>Raw data of the global layer mask info block (inside the length prefix)</summary>
        public byte[] RawData { get; set; }

        public static GlobalLayerMaskInfo Read(BigEndianReader reader)
        {
            uint length = reader.ReadUInt32();
            if (length == 0)
                return null;

            var info = new GlobalLayerMaskInfo();
            info.RawData = reader.ReadBytes((int)length);
            return info;
        }

        public void Write(BigEndianWriter writer)
        {
            byte[] data = RawData ?? Array.Empty<byte>();
            writer.WriteUInt32((uint)data.Length);
            writer.WriteBytes(data);
        }
    }

    /// <summary>
    /// Layer and mask information section
    /// </summary>
    public class LayerAndMaskInfo
    {
        /// <summary>List of layer records</summary>
        public List<LayerRecord> LayerRecords { get; set; }

        /// <summary>Global layer mask info</summary>
        public GlobalLayerMaskInfo GlobalMask { get; set; }

        /// <summary>Additional tagged blocks at the section level</summary>
        public TaggedBlocks TaggedBlocks { get; set; }

        /// <summary>Number of layers (absolute value)</summary>
        public int LayerCount { get; set; }

        /// <summary>Whether first alpha channel contains transparency data</summary>
        public bool HasMergedAlpha { get; set; }

        /// <summary>Whether the global mask section was present in the original file</summary>
        private bool _globalMaskWasPresent;

        public LayerAndMaskInfo()
        {
            LayerRecords = new List<LayerRecord>();
            TaggedBlocks = new TaggedBlocks();
        }

        /// <summary>
        /// Read layer and mask information
        /// </summary>
        public static LayerAndMaskInfo Read(BigEndianReader reader, FileHeader header)
        {
            var info = new LayerAndMaskInfo();
            bool isPsb = header.IsPsb;

            long sectionLength = isPsb ? reader.ReadInt64() : reader.ReadUInt32();
            if (sectionLength == 0)
                return info;

            long sectionEnd = reader.Position + sectionLength;

            ReadLayerInfo(reader, info, header, sectionEnd);

            if (reader.Position < sectionEnd)
            {
                info._globalMaskWasPresent = true;
                info.GlobalMask = GlobalLayerMaskInfo.Read(reader);
            }

            if (reader.Position < sectionEnd)
            {
                long remaining = sectionEnd - reader.Position;
                info.TaggedBlocks = TaggedBlocks.Read(reader, remaining, isPsb);
            }

            reader.Seek(sectionEnd);
            return info;
        }

        private static void ReadLayerInfo(BigEndianReader reader, LayerAndMaskInfo info, FileHeader header, long sectionEnd)
        {
            bool isPsb = header.IsPsb;

            long layerInfoLength = isPsb ? reader.ReadInt64() : reader.ReadUInt32();
            if (layerInfoLength == 0)
                return;

            long layerInfoEnd = reader.Position + layerInfoLength;
            if (layerInfoEnd > sectionEnd)
                layerInfoEnd = sectionEnd;

            short layerCount = reader.ReadInt16();
            info.HasMergedAlpha = layerCount < 0;
            info.LayerCount = Math.Abs(layerCount);

            if (info.LayerCount == 0)
            {
                reader.Seek(layerInfoEnd);
                return;
            }

            info.LayerRecords = new List<LayerRecord>(info.LayerCount);
            for (int i = 0; i < info.LayerCount; i++)
            {
                var record = LayerRecord.Read(reader, isPsb);
                info.LayerRecords.Add(record);
            }

            foreach (var record in info.LayerRecords)
            {
                record.ChannelData = new List<ChannelData>(record.ChannelInfos.Count);
                foreach (var channelInfo in record.ChannelInfos)
                {
                    var channelData = ChannelData.Read(reader, channelInfo,
                        record.Width, record.Height, header.Depth, header.Version);
                    record.ChannelData.Add(channelData);
                }
            }

            // Internal padding (to 2 or 4 bytes) is included in layerInfoLength;
            // seek to end to skip it.
            if (reader.Position < layerInfoEnd)
                reader.Seek(layerInfoEnd);
        }

        /// <summary>
        /// Write layer and mask information section
        /// </summary>
        public void Write(BigEndianWriter writer, FileHeader header)
        {
            bool isPsb = header.IsPsb;
            byte[] body = WriteBody(isPsb);

            if (isPsb)
                writer.WriteInt64(body.Length);
            else
                writer.WriteUInt32((uint)body.Length);

            writer.WriteBytes(body);
        }

        private byte[] WriteBody(bool isPsb)
        {
            using (var ms = new MemoryStream())
            using (var w = new BigEndianWriter(ms))
            {
                WriteLayerInfo(w, isPsb);

                if (GlobalMask != null)
                    GlobalMask.Write(w);
                else if (_globalMaskWasPresent)
                    w.WriteUInt32(0);

                if (TaggedBlocks != null && TaggedBlocks.Count > 0)
                    TaggedBlocks.Write(w, isPsb);

                w.Flush();
                return ms.ToArray();
            }
        }

        private void WriteLayerInfo(BigEndianWriter writer, bool isPsb)
        {
            if (LayerRecords == null || LayerRecords.Count == 0)
            {
                if (isPsb)
                    writer.WriteInt64(0);
                else
                    writer.WriteUInt32(0);
                return;
            }

            foreach (var record in LayerRecords)
                record.UpdateChannelLengths();

            byte[] layerInfoBody;
            using (var ms = new MemoryStream())
            using (var w = new BigEndianWriter(ms))
            {
                short count = (short)(HasMergedAlpha ? -LayerCount : LayerCount);
                w.WriteInt16(count);

                foreach (var record in LayerRecords)
                    record.Write(w, isPsb);

                foreach (var record in LayerRecords)
                {
                    if (record.ChannelData != null)
                    {
                        foreach (var cd in record.ChannelData)
                            cd.Write(w);
                    }
                }

                // Pad to 4-byte boundary INSIDE the length block
                // (matches psd-tools: write_padding(fp, written, padding=4))
                w.Flush();
                long bodySize = ms.Position;
                int padNeeded = (int)((4 - (bodySize % 4)) % 4);
                for (int i = 0; i < padNeeded; i++)
                    w.WriteByte(0);

                w.Flush();
                layerInfoBody = ms.ToArray();
            }

            if (isPsb)
                writer.WriteInt64(layerInfoBody.Length);
            else
                writer.WriteUInt32((uint)layerInfoBody.Length);

            writer.WriteBytes(layerInfoBody);
        }
    }
}
