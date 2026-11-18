using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace PsdUiWorkbench.Tests
{
    public static class PsdGlyphSpaceProbe
    {
        public static string Capture(string name, float scale, float angle, bool glyphSpace, string replacement = "Hell 60", bool production = false, bool bold = true, bool legacyCalibration = false)
        {
            if (Path.GetFileName(name) != name || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                throw new ArgumentException("探针名称不能包含路径。");
            string path = PsdStrokeVisualDiagnosis.Folder + name + ".png";
            if (File.Exists(path)) throw new IOException("拒绝覆盖原始探针。");
            TMP_FontAsset font = null;
            Material material = null;
            try
            {
                font = TMP_FontAsset.CreateFontAsset(AssetDatabase.LoadAssetAtPath<Font>("Assets/AAGame/UI/Fonts/Toxia_FRE.ttf"),
                    96, 24, GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, false);
                font.boldStyle = 0;
                if (!font.TryAddCharacters(new string(Enumerable.Range(32, 95).Select(i => (char)i).ToArray()), out string missing))
                    throw new InvalidOperationException("夹具缺字：" + missing);
                font.atlasTexture.Apply(false, false);
                font.atlasPopulationMode = AtlasPopulationMode.Static;
                material = new Material(font.material);
                Infrastructure.PsdTmpOuterStroke.Configure(material, 3, 0.5f, 0);
                material.shader = Shader.Find("TextMeshPro/Mobile/Distance Field - PSD Outer Stroke - Directional Bold Probe");
                material.SetFloat("_PsdProbeGlyphSpace", glyphSpace ? 1 : 0);
                material.SetFloat("_PsdProbeBoldX", glyphSpace ? 3 : 1.5f * scale);
                material.SetFloat("_PsdProbeBoldY", 0);
                material.SetFloat("_PsdProbeBoldBias", 0.5f);
                if (production)
                {
                    Infrastructure.PsdTmpGlyphLocalBold.Configure(material, font);
                    if (legacyCalibration)
                    {
                        material.SetFloat("_PsdBoldSampling", 0);
                        material.SetFloat("_PsdBoldAtlasWidth", 3);
                        material.SetFloat("_PsdBoldVerticalRadius", 0);
                    }
                }
                else if (!bold) material.SetFloat("_PsdProbeBoldX", 0);
                material.SetColor("_FaceColor", Color.red);
                material.SetColor("_OutlineColor", Color.green);
                return PsdTmpCandidateExport.Capture(PsdTextFillProbe.PrefabPath, path, root =>
                {
                    foreach (var image in root.GetComponentsInChildren<UnityEngine.UI.Image>(true)) image.enabled = false;
                    foreach (var old in root.GetComponentsInChildren<TextMeshProUGUI>(true)) old.gameObject.SetActive(false);
                    foreach (var obj in root.scene.GetRootGameObjects())
                        if (obj.TryGetComponent<Camera>(out var camera)) camera.backgroundColor = Color.black;
                    var go = new GameObject("字形坐标验证", typeof(RectTransform));
                    go.transform.SetParent(root.transform, false);
                    var text = go.AddComponent<TextMeshProUGUI>();
                    text.fontSharedMaterial = material;
                    text.font = font;
                    text.fontSharedMaterial = material;
                    text.fontSize = 48;
                    text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
                    text.richText = false;
                    text.enableAutoSizing = false;
                    text.textWrappingMode = TextWrappingModes.NoWrap;
                    text.alignment = TextAlignmentOptions.Center;
                    text.extraPadding = true;
                    text.color = Color.white;
                    var rect = text.rectTransform;
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.sizeDelta = new Vector2(400, 100);
                    rect.anchoredPosition = Vector2.zero;
                    rect.localScale = Vector3.one * scale;
                    rect.localRotation = Quaternion.Euler(0, 0, angle);
                    text.text = "Original";
                    text.ForceMeshUpdate(true, true);
                    // 在同一组件和同一材质上改字，不通过 Workbench 组件同步数据。
                    text.text = replacement;
                    text.ForceMeshUpdate(true, true);
                    if (text.textInfo.characterCount != replacement.Length || text.fontSharedMaterial != material)
                        throw new InvalidOperationException("普通 TMP 改字后字符数或共享材质异常。");
                });
            }
            finally
            {
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (font != null) UnityEngine.Object.DestroyImmediate(font);
            }
        }
    }
}
