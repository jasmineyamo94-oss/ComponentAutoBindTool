using PsdTools.Utils;

namespace PsdTools.Psd
{
    /// <summary>
    /// Color Mode Data section
    /// Contains palette data for Indexed color mode and duotone info for Duotone mode
    /// </summary>
    public class ColorModeData
    {
        /// <summary>Raw color mode data</summary>
        public byte[] Data { get; private set; }

        /// <summary>
        /// Read color mode data from binary stream
        /// </summary>
        public static ColorModeData Read(BigEndianReader reader)
        {
            var data = new ColorModeData();
            
            uint length = reader.ReadUInt32();
            
            if (length > 0)
            {
                data.Data = reader.ReadBytes((int)length);
            }
            else
            {
                data.Data = System.Array.Empty<byte>();
            }

            return data;
        }

        /// <summary>
        /// Write color mode data to binary stream
        /// </summary>
        public void Write(BigEndianWriter writer)
        {
            byte[] data = Data ?? System.Array.Empty<byte>();
            writer.WriteUInt32((uint)data.Length);
            writer.WriteBytes(data);
        }
    }
}
