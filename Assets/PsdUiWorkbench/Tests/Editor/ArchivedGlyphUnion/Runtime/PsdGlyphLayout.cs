using TMPro;
using UnityEngine;

namespace PsdUiWorkbench.Runtime
{
    // 固定缓冲区由组件持有；只消费 TMP 已完成的几何，不读取 PSD 或字体像素。
    internal static class PsdGlyphLayout
    {
        public const int Capacity = 32;

        public static string Fill(TextMeshProUGUI text, TMP_TextInfo info, Color[] data, Rect[] rectangles, out int count)
        {
            count = 0;
            if (text.horizontalMapping != TextureMappingOptions.Paragraph || text.verticalMapping != TextureMappingOptions.Paragraph)
                return "邻字合成需要 Paragraph UV。";
            if (info.lineCount > 1 || text.geometrySortingOrder != VertexSortingOrder.Normal)
                return "多行或反向几何排序使用单字形描边。";
            if (info.spriteCount > 0 || text.enableVertexGradient)
                return "Sprite 或顶点渐变使用单字形描边。";
            TMP_CharacterInfo first = default;
            for (int i = 0; i < info.characterCount; i++)
            {
                var c = info.characterInfo[i];
                if (!c.isVisible) continue;
                if (count == Capacity) return "超过 32 个可见字形，使用单字形描边。";
                if (c.materialReferenceIndex != 0 || c.elementType != TMP_TextElementType.Character)
                    return "多材质或回退图集使用单字形描边。";
                if ((c.style & (FontStyles.Italic | FontStyles.Underline | FontStyles.Strikethrough | FontStyles.Highlight)) != 0)
                    return "斜体或文字装饰使用单字形描边。";
                if (count == 0) first = c;
                else if (c.fontAsset != first.fontAsset || c.style != first.style || Mathf.Abs(c.scale - first.scale) > 0.0001f || !c.color.Equals(first.color))
                    return "混合字重、字号、字体或颜色使用单字形描边。";
                var mesh = info.meshInfo[0];
                int k = c.vertexIndex;
                var bl = mesh.vertices[k];
                var tl = mesh.vertices[k + 1];
                var tr = mesh.vertices[k + 2];
                var br = mesh.vertices[k + 3];
                var p0 = mesh.uvs2[k];
                var p1 = mesh.uvs2[k + 2];
                if (!Finite(bl.x) || !Finite(bl.y) || !Finite(tr.x) || !Finite(tr.y) ||
                    tr.x <= bl.x || tr.y <= bl.y || Mathf.Abs(tl.x - bl.x) > 0.0001f || Mathf.Abs(tl.y - tr.y) > 0.0001f ||
                    Mathf.Abs(br.x - tr.x) > 0.0001f || Mathf.Abs(br.y - bl.y) > 0.0001f ||
                    !Finite(p0.x) || !Finite(p0.y) || !Finite(p1.x) || !Finite(p1.y) || p1.x <= p0.x || p1.y <= p0.y)
                    return "字形网格或 UV 不适用，使用单字形描边。";
                for (int v = 0; v < 4; v++)
                    if (!mesh.colors32[k + v].Equals(mesh.colors32[first.vertexIndex]))
                        return "字形顶点颜色不同，使用单字形描边。";
                rectangles[count] = Rect.MinMaxRect(bl.x, bl.y, tr.x, tr.y);
                for (int j = 0; j < count - 1; j++)
                    if (rectangles[j].Overlaps(rectangles[count]))
                        return "非邻接字形网格交叠，使用单字形描边。";
                var uv0 = mesh.uvs0[k];
                var uv1 = mesh.uvs0[k + 2];
                data[count] = new Color(p0.x, p0.y, p1.x, p1.y);
                data[Capacity + count] = new Color(uv0.x, uv0.y, uv1.x, uv1.y);
                count++;
            }
            return null;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
