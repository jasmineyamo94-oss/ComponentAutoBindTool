using PsdTools.Psd;

namespace PsdTools.Layers
{
    /// <summary>
    /// Pixel (raster) layer
    /// </summary>
    public class PixelLayer : Layer
    {
        public PixelLayer(LayerRecord record, FileHeader header) : base(record, header)
        {
        }

        public override LayerKind Kind => LayerKind.Pixel;
    }
}
