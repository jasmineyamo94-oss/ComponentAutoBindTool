using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace PsdUiWorkbench.Tests
{
    // 只修改隔离截图实例；不修改导出默认值、源材质或字库。
    public static class PsdTextAppearanceProbe
    {
        public static string Capture(string name, float weight = 0.35f, float strokeScale = 1,
            float faceEdge = 1, float outlineEdge = 1, float offsetX = 0, float offsetY = 0, bool maskColors = false)
        {
            var copies = new Dictionary<Material, Material>();
            try
            {
                return PsdTmpCandidateExport.Capture("Assets/PsdUiWorkbench/Exports/战斗-主菜单界面_单字形共享材质.prefab",
                    PsdStrokeVisualDiagnosis.Folder + name + ".png", root =>
                    {
                        if (maskColors)
                        {
                            foreach (var obj in root.scene.GetRootGameObjects())
                                if (obj.TryGetComponent<Camera>(out var camera)) camera.backgroundColor = Color.black;
                            foreach (var image in root.GetComponentsInChildren<UnityEngine.UI.Image>(true)) image.enabled = false;
                        }
                        foreach (var text in root.GetComponentsInChildren<TextMeshProUGUI>(true))
                        {
                            if (text.name == "Placeholder") continue;
                            var original = text.fontSharedMaterial;
                            if (!copies.TryGetValue(original, out var copy))
                            {
                                copy = new Material(original);
                                copies.Add(original, copy);
                                copy.SetFloat("_WeightBold", weight);
                                copy.SetFloat("_OutlineWidth", original.GetFloat("_OutlineWidth") * strokeScale);
                                copy.SetFloat("_PsdFaceEdge", faceEdge);
                                copy.SetFloat("_PsdOutlineEdge", outlineEdge);
                                if (maskColors)
                                {
                                    copy.SetColor("_FaceColor", Color.red);
                                    copy.SetColor("_OutlineColor", Color.green);
                                }
                            }
                            text.fontSharedMaterial = copy;
                            text.rectTransform.anchoredPosition += new Vector2(offsetX, offsetY);
                            text.UpdateMeshPadding();
                            text.ForceMeshUpdate(true, true);
                        }
                    });
            }
            finally
            {
                foreach (var copy in copies.Values) Object.DestroyImmediate(copy);
            }
        }
    }
}
