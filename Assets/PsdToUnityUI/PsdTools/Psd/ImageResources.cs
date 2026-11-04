using System;
using System.Collections.Generic;
using System.IO;
using PsdTools.Utils;

namespace PsdTools.Psd
{
    /// <summary>
    /// Image resource block.
    /// Stored as a raw byte blob for perfect round-trip. Fields parsed for read-only API access.
    /// </summary>
    public class ImageResource
    {
        public const string RESOURCE_SIGNATURE = "8BIM";

        /// <summary>Resource ID (parsed from raw bytes)</summary>
        public ushort Id { get; set; }

        /// <summary>Resource data (parsed from raw bytes for API access)</summary>
        public byte[] Data { get; set; }

        /// <summary>Complete raw bytes of this resource (signature through data+padding)</summary>
        public byte[] RawBytes { get; set; }

        /// <summary>
        /// Read a single image resource from binary stream.
        /// The entire resource is captured as raw bytes for write-back.
        /// </summary>
        public static ImageResource Read(BigEndianReader reader)
        {
            var resource = new ImageResource();
            long startPos = reader.Position;

            string signature = reader.ReadSignature();
            if (signature != RESOURCE_SIGNATURE)
                throw new InvalidOperationException($"Invalid resource signature: {signature}");

            resource.Id = reader.ReadUInt16();
            reader.ReadPascalString(2); // name — skip, preserved in raw bytes

            uint dataLength = reader.ReadUInt32();
            if (dataLength > 0)
            {
                resource.Data = reader.ReadBytes((int)dataLength);
                if (dataLength % 2 != 0)
                    reader.Skip(1);
            }
            else
            {
                resource.Data = Array.Empty<byte>();
            }

            long endPos = reader.Position;
            reader.Seek(startPos);
            resource.RawBytes = reader.ReadBytes((int)(endPos - startPos));

            return resource;
        }

        /// <summary>
        /// Write resource by dumping the raw bytes — guarantees perfect round-trip.
        /// </summary>
        public void Write(BigEndianWriter writer)
        {
            writer.WriteBytes(RawBytes);
        }
    }

    /// <summary>
    /// Image Resources section.
    /// Uses an ordered list to preserve insertion order for round-trip consistency.
    /// </summary>
    public class ImageResources
    {
        private readonly List<ImageResource> _resourceList;
        private byte[] _trailingBytes;

        public ImageResources()
        {
            _resourceList = new List<ImageResource>();
        }

        /// <summary>All image resources in file order</summary>
        public IReadOnlyList<ImageResource> Resources => _resourceList;

        /// <summary>
        /// Read image resources section from binary stream
        /// </summary>
        public static ImageResources Read(BigEndianReader reader)
        {
            var resources = new ImageResources();

            uint sectionLength = reader.ReadUInt32();
            long endPosition = reader.Position + sectionLength;

            while (reader.Position < endPosition)
            {
                long beforeRead = reader.Position;
                try
                {
                    var resource = ImageResource.Read(reader);
                    resources._resourceList.Add(resource);
                }
                catch
                {
                    // Capture any remaining bytes as trailing data
                    long remaining = endPosition - beforeRead;
                    if (remaining > 0)
                    {
                        reader.Seek(beforeRead);
                        resources._trailingBytes = reader.ReadBytes((int)remaining);
                    }
                    break;
                }
            }

            // Capture any trailing bytes after all valid resources
            if (reader.Position < endPosition)
            {
                long remaining = endPosition - reader.Position;
                resources._trailingBytes = reader.ReadBytes((int)remaining);
            }

            return resources;
        }

        /// <summary>
        /// Get a resource by ID (first match)
        /// </summary>
        public ImageResource GetResource(ushort id)
        {
            for (int i = 0; i < _resourceList.Count; i++)
            {
                if (_resourceList[i].Id == id)
                    return _resourceList[i];
            }
            return null;
        }

        /// <summary>
        /// Check if a resource exists
        /// </summary>
        public bool HasResource(ushort id)
        {
            return GetResource(id) != null;
        }

        /// <summary>
        /// Write image resources section to binary stream.
        /// Each resource writes its raw bytes for perfect round-trip.
        /// </summary>
        public void Write(BigEndianWriter writer)
        {
            using (var ms = new MemoryStream())
            using (var inner = new BigEndianWriter(ms))
            {
                foreach (var resource in _resourceList)
                {
                    resource.Write(inner);
                }
                if (_trailingBytes != null && _trailingBytes.Length > 0)
                    inner.WriteBytes(_trailingBytes);

                inner.Flush();
                byte[] data = ms.ToArray();
                writer.WriteUInt32((uint)data.Length);
                writer.WriteBytes(data);
            }
        }
    }
}
