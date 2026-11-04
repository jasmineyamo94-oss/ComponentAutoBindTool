using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

namespace PsdTools
{
    [AddComponentMenu("UI/Effects/TextGradient")]
    public class TextGradient : BaseMeshEffect
    {
        public Color topLeftColor = Color.white;
        public Color topRightColor = Color.white;
        public Color bottomLeftColor = Color.black;
        public Color bottomRightColor = Color.black;

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive()) return;

            List<UIVertex> vertexList = new List<UIVertex>();
            vh.GetUIVertexStream(vertexList);

            int count = vertexList.Count;
            // Each character consists of 6 vertices (two triangles, sharing two vertices, but 6 in the VertexStream)
            // Order is usually: TopLeft, TopRight, BottomRight, BottomRight, BottomLeft, TopLeft
            for (int i = 0; i < count; i++)
            {
                UIVertex v = vertexList[i];

                // In the VertexStream, every 6 vertices represent one character
                int index = i % 6;

                switch (index)
                {
                    case 0: case 5: // Top Left
                        v.color = topLeftColor;
                        break;
                    case 1: // Top Right
                        v.color = topRightColor;
                        break;
                    case 2: case 3: // Bottom Right
                        v.color = bottomRightColor;
                        break;
                    case 4: // Bottom Left
                        v.color = bottomLeftColor;
                        break;
                }

                vertexList[i] = v;
            }

            vh.Clear();
            vh.AddUIVertexTriangleStream(vertexList);
        }
    }
} // namespace PsdTools
