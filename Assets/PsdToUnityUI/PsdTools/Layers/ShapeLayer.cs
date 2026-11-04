using PsdTools.Constants;
using PsdTools.Psd;

namespace PsdTools.Layers
{
    /// <summary>
    /// Shape (vector) layer
    /// </summary>
    public class ShapeLayer : Layer
    {
        public ShapeLayer(LayerRecord record, FileHeader header) : base(record, header)
        {
        }

        public override LayerKind Kind => LayerKind.Shape;

        /// <summary>Whether layer has vector mask data</summary>
        public bool HasVectorMask
        {
            get
            {
                return TaggedBlocks?.Contains(Tag.VECTOR_MASK_SETTING1) == true ||
                       TaggedBlocks?.Contains(Tag.VECTOR_MASK_SETTING2) == true;
            }
        }

        /// <summary>Whether layer has stroke data</summary>
        public bool HasStroke => TaggedBlocks?.Contains(Tag.VECTOR_STROKE_DATA) == true;

        /// <summary>Whether layer has origination data (live shapes)</summary>
        public bool HasOrigination => TaggedBlocks?.Contains(Tag.VECTOR_ORIGINATION_DATA) == true;
    }
}
