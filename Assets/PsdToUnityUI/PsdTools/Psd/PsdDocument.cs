using System;
using System.IO;
using PsdTools.Utils;

namespace PsdTools.Psd
{
    /// <summary>
    /// Low-level PSD document structure
    /// Represents the complete binary structure of a PSD file
    /// </summary>
    public class PsdDocument
    {
        /// <summary>File header</summary>
        public FileHeader Header { get; private set; }

        /// <summary>Color mode data</summary>
        public ColorModeData ColorModeData { get; private set; }

        /// <summary>Image resources</summary>
        public ImageResources ImageResources { get; private set; }

        /// <summary>Layer and mask information</summary>
        public LayerAndMaskInfo LayerAndMaskInfo { get; private set; }

        /// <summary>Merged image data</summary>
        public ImageData ImageData { get; private set; }

        /// <summary>
        /// Read a PSD document from a file
        /// </summary>
        public static PsdDocument Open(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                return Read(stream);
            }
        }

        /// <summary>
        /// Read a PSD document from a stream
        /// </summary>
        public static PsdDocument Read(Stream stream)
        {
            using (var reader = new BigEndianReader(stream))
            {
                return Read(reader);
            }
        }

        /// <summary>
        /// Read a PSD document from a binary reader
        /// </summary>
        public static PsdDocument Read(BigEndianReader reader)
        {
            var doc = new PsdDocument();

            doc.Header = FileHeader.Read(reader);
            doc.ColorModeData = ColorModeData.Read(reader);
            doc.ImageResources = ImageResources.Read(reader);
            doc.LayerAndMaskInfo = LayerAndMaskInfo.Read(reader, doc.Header);
            doc.ImageData = ImageData.Read(reader, doc.Header);

            return doc;
        }

        /// <summary>
        /// Write the PSD document to a file.
        /// Uses temp file + atomic replacement to avoid corruption from concurrent access.
        /// </summary>
        public void Save(string path)
        {
            string tempPath = path + ".tmp";
            try
            {
                using (var stream = File.Create(tempPath))
                {
                    Write(stream);
                }

                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tempPath, path);
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                throw;
            }
        }

        /// <summary>
        /// Write the PSD document to a stream
        /// </summary>
        public void Write(Stream stream)
        {
            using (var writer = new BigEndianWriter(stream))
            {
                Header.Write(writer);
                ColorModeData.Write(writer);
                ImageResources.Write(writer);
                LayerAndMaskInfo.Write(writer, Header);
                ImageData.Write(writer);
                writer.Flush();
            }
        }

        public override string ToString()
        {
            return $"PSD Document: {Header.Width}x{Header.Height}, {LayerAndMaskInfo?.LayerCount ?? 0} layers";
        }
    }
}
