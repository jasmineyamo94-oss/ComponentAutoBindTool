using PsdTools.Constants;
using PsdTools.Psd;

namespace PsdTools.Layers
{
    /// <summary>
    /// Smart Object layer
    /// </summary>
    public class SmartObjectLayer : Layer
    {
        public SmartObjectLayer(LayerRecord record, FileHeader header) : base(record, header)
        {
        }

        public override LayerKind Kind => LayerKind.SmartObject;

        /// <summary>Get smart object data block</summary>
        public byte[] GetSmartObjectData()
        {
            var data = TaggedBlocks?.GetData(Tag.SMART_OBJECT_LAYER_DATA1);
            if (data == null)
                data = TaggedBlocks?.GetData(Tag.SMART_OBJECT_LAYER_DATA2);
            if (data == null)
                data = TaggedBlocks?.GetData(Tag.PLACED_LAYER1);
            if (data == null)
                data = TaggedBlocks?.GetData(Tag.PLACED_LAYER2);
            return data;
        }
    }
}
