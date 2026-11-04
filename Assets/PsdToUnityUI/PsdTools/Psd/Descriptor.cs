using System;
using System.Collections.Generic;
using System.Text;
using PsdTools.Utils;

namespace PsdTools.Psd
{
    /// <summary>
    /// Adobe Descriptor type
    /// </summary>
    public enum DescriptorType
    {
        Reference,
        Descriptor,
        List,
        Double,
        UnitFloat,
        String,
        Enumerated,
        Integer,
        LargeInteger,
        Boolean,
        GlobalObject,
        Class,
        Alias,
        RawData,
        ObjectArray,
        ObjectArrayReference,
        Name,
        Identifier,
        Index,
        Offset,
        Property
    }

    /// <summary>
    /// Unit float types
    /// </summary>
    public enum UnitFloatType
    {
        Points,      // #Pnt
        Millimeters, // #Mlm
        Angle,       // #Ang
        Density,     // #Rsl
        Distance,    // #Rlt
        None,        // #Nne
        Percent,     // #Prc
        Pixels       // #Pxl
    }

    /// <summary>
    /// Descriptor value (can hold various types)
    /// </summary>
    public class DescriptorValue
    {
        public DescriptorType Type { get; set; }
        public object Value { get; set; }

        public double AsDouble()
        {
            if (Value is double d) return d;
            if (Value is int i) return i;
            if (Value is long l) return l;
            if (Value is UnitFloat uf) return uf.Value;
            return 0;
        }

        public int AsInt()
        {
            if (Value is int i) return i;
            if (Value is long l) return (int)l;
            if (Value is double d) return (int)d;
            return 0;
        }

        public bool AsBool()
        {
            if (Value is bool b) return b;
            return false;
        }

        public string AsString()
        {
            if (Value is string s) return s;
            return Value?.ToString() ?? "";
        }

        public Descriptor AsDescriptor()
        {
            return Value as Descriptor;
        }

        public List<DescriptorValue> AsList()
        {
            return Value as List<DescriptorValue>;
        }
    }

    /// <summary>
    /// Unit float value
    /// </summary>
    public struct UnitFloat
    {
        public UnitFloatType Unit;
        public double Value;
    }

    /// <summary>
    /// Enumerated value
    /// </summary>
    public struct EnumeratedValue
    {
        public string Type;
        public string Value;
    }

    /// <summary>
    /// Adobe Descriptor (nested key-value structure)
    /// </summary>
    public class Descriptor
    {
        private readonly Dictionary<string, DescriptorValue> _items;

        /// <summary>Class ID</summary>
        public string ClassId { get; set; }

        /// <summary>Class name</summary>
        public string ClassName { get; set; }

        public Descriptor()
        {
            _items = new Dictionary<string, DescriptorValue>();
        }

        /// <summary>Get value by key</summary>
        public DescriptorValue this[string key]
        {
            get
            {
                _items.TryGetValue(key, out var value);
                return value;
            }
            set => _items[key] = value;
        }

        /// <summary>Check if key exists</summary>
        public bool Contains(string key) => _items.ContainsKey(key);

        /// <summary>Get all keys</summary>
        public IEnumerable<string> Keys => _items.Keys;

        /// <summary>Number of items</summary>
        public int Count => _items.Count;

        /// <summary>
        /// Parse descriptor from binary data
        /// </summary>
        public static Descriptor Parse(byte[] data)
        {
            if (data == null || data.Length < 8)
                return null;

            try
            {
                using (var reader = new BigEndianReader(data))
                {
                    return ReadDescriptor(reader);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Read descriptor from reader
        /// </summary>
        public static Descriptor ReadDescriptor(BigEndianReader reader)
        {
            var desc = new Descriptor();

            // Class name (Unicode string)
            desc.ClassName = reader.ReadUnicodeString();

            // Class ID
            desc.ClassId = ReadKey(reader);

            // Item count
            uint count = reader.ReadUInt32();

            for (uint i = 0; i < count && reader.Remaining > 0; i++)
            {
                // Key
                string key = ReadKey(reader);

                // Value
                var value = ReadValue(reader);
                if (value != null)
                {
                    desc[key] = value;
                }
            }

            return desc;
        }

        private static string ReadKey(BigEndianReader reader)
        {
            uint length = reader.ReadUInt32();
            if (length == 0)
            {
                // 4-byte key
                return reader.ReadSignature();
            }
            else
            {
                // Variable length key
                byte[] bytes = reader.ReadBytes((int)length);
                return Encoding.ASCII.GetString(bytes);
            }
        }

        private static DescriptorValue ReadValue(BigEndianReader reader)
        {
            if (reader.Remaining < 4)
                return null;

            string typeCode = reader.ReadSignature();
            var value = new DescriptorValue();

            switch (typeCode)
            {
                case "obj ": // Reference
                    value.Type = DescriptorType.Reference;
                    value.Value = ReadReference(reader);
                    break;

                case "Objc": // Descriptor
                case "GlbO": // GlobalObject
                    value.Type = typeCode == "Objc" ? DescriptorType.Descriptor : DescriptorType.GlobalObject;
                    value.Value = ReadDescriptor(reader);
                    break;

                case "VlLs": // List
                    value.Type = DescriptorType.List;
                    value.Value = ReadList(reader);
                    break;

                case "doub": // Double
                    value.Type = DescriptorType.Double;
                    value.Value = reader.ReadDouble();
                    break;

                case "UntF": // UnitFloat
                    value.Type = DescriptorType.UnitFloat;
                    value.Value = ReadUnitFloat(reader);
                    break;

                case "TEXT": // String
                    value.Type = DescriptorType.String;
                    value.Value = reader.ReadUnicodeString();
                    break;

                case "enum": // Enumerated
                    value.Type = DescriptorType.Enumerated;
                    value.Value = ReadEnumerated(reader);
                    break;

                case "long": // Integer
                    value.Type = DescriptorType.Integer;
                    value.Value = reader.ReadInt32();
                    break;

                case "comp": // LargeInteger
                    value.Type = DescriptorType.LargeInteger;
                    value.Value = reader.ReadInt64();
                    break;

                case "bool": // Boolean
                    value.Type = DescriptorType.Boolean;
                    value.Value = reader.ReadByte() != 0;
                    break;

                case "type": // Class
                case "GlbC":
                    value.Type = DescriptorType.Class;
                    reader.ReadUnicodeString(); // Class name
                    ReadKey(reader); // Class ID
                    break;

                case "alis": // Alias
                    value.Type = DescriptorType.Alias;
                    uint length = reader.ReadUInt32();
                    value.Value = reader.ReadBytes((int)length);
                    break;

                case "tdta": // RawData
                    value.Type = DescriptorType.RawData;
                    uint dataLength = reader.ReadUInt32();
                    value.Value = reader.ReadBytes((int)dataLength);
                    break;

                case "ObAr": // ObjectArray
                    value.Type = DescriptorType.ObjectArray;
                    value.Value = ReadObjectArray(reader);
                    break;

                default:
                    // Unknown type, try to skip
                    return null;
            }

            return value;
        }

        private static object ReadReference(BigEndianReader reader)
        {
            var items = new List<object>();
            uint count = reader.ReadUInt32();

            for (uint i = 0; i < count && reader.Remaining > 0; i++)
            {
                string type = reader.ReadSignature();
                switch (type)
                {
                    case "prop": // Property
                        reader.ReadUnicodeString();
                        ReadKey(reader);
                        ReadKey(reader);
                        break;
                    case "Clss": // Class
                        reader.ReadUnicodeString();
                        ReadKey(reader);
                        break;
                    case "Enmr": // EnumeratedReference
                        reader.ReadUnicodeString();
                        ReadKey(reader);
                        ReadKey(reader);
                        ReadKey(reader);
                        break;
                    case "rele": // Offset
                        reader.ReadUnicodeString();
                        ReadKey(reader);
                        reader.ReadUInt32();
                        break;
                    case "Idnt": // Identifier
                        reader.ReadUInt32();
                        break;
                    case "indx": // Index
                        reader.ReadUInt32();
                        break;
                    case "name": // Name
                        reader.ReadUnicodeString();
                        break;
                }
            }

            return items;
        }

        private static List<DescriptorValue> ReadList(BigEndianReader reader)
        {
            var list = new List<DescriptorValue>();
            uint count = reader.ReadUInt32();

            for (uint i = 0; i < count && reader.Remaining > 0; i++)
            {
                var value = ReadValue(reader);
                if (value != null)
                {
                    list.Add(value);
                }
            }

            return list;
        }

        private static UnitFloat ReadUnitFloat(BigEndianReader reader)
        {
            string unit = reader.ReadSignature();
            double value = reader.ReadDouble();

            UnitFloatType unitType = UnitFloatType.None;
            switch (unit)
            {
                case "#Pnt": unitType = UnitFloatType.Points; break;
                case "#Mlm": unitType = UnitFloatType.Millimeters; break;
                case "#Ang": unitType = UnitFloatType.Angle; break;
                case "#Rsl": unitType = UnitFloatType.Density; break;
                case "#Rlt": unitType = UnitFloatType.Distance; break;
                case "#Nne": unitType = UnitFloatType.None; break;
                case "#Prc": unitType = UnitFloatType.Percent; break;
                case "#Pxl": unitType = UnitFloatType.Pixels; break;
            }

            return new UnitFloat { Unit = unitType, Value = value };
        }

        private static EnumeratedValue ReadEnumerated(BigEndianReader reader)
        {
            string type = ReadKey(reader);
            string value = ReadKey(reader);
            return new EnumeratedValue { Type = type, Value = value };
        }

        private static object ReadObjectArray(BigEndianReader reader)
        {
            // Object array format
            uint count = reader.ReadUInt32();
            reader.ReadUnicodeString(); // Class name
            ReadKey(reader); // Class ID
            uint itemCount = reader.ReadUInt32();

            var items = new List<Descriptor>();
            for (uint i = 0; i < itemCount && reader.Remaining > 0; i++)
            {
                var desc = new Descriptor();
                for (uint j = 0; j < count; j++)
                {
                    string key = ReadKey(reader);
                    var value = ReadValue(reader);
                    if (value != null)
                    {
                        desc[key] = value;
                    }
                }
                items.Add(desc);
            }

            return items;
        }
    }
}
