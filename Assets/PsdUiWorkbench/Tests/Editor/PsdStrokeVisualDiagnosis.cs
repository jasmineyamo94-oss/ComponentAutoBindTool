using System;
using System.Collections.Generic;
using PsdUiWorkbench.Infrastructure;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;
using System.Linq;
using UnityEditor;
using PsdUiWorkbench.Core;
using UnityEngine.TextCore.LowLevel;

namespace PsdUiWorkbench.Tests
{
    // 仅修改隔离截图实例，已有 Prefab、字库和材质保持不变。
    public static class PsdStrokeVisualDiagnosis
    {
        public const string Folder = "Assets/PsdUiWorkbench/Documentation/PsdTextAudit/StrokeDiagnosis/";
        public static string CaptureOuter(float weight, float faceEdge, float outlineEdge, string filename, float? strokeOverride = null, bool maskColors = false, bool clipIsolatedGlyph = false)
        {
            var materials = new Dictionary<Material, Material>();
            try
            {
                return PsdTmpCandidateExport.Capture("Assets/PsdUiWorkbench/Exports/战斗-主菜单界面_TMP校正 1.prefab", Folder + filename + ".png", root =>
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
                        if (clipIsolatedGlyph && text.text == "0")
                        {
                            var parent = (RectTransform)text.transform.parent;
                            var mask = parent.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
                            mask.padding = new Vector4(0, 0, parent.rect.width * 0.5f, 0);
                        }
                        text.ForceMeshUpdate(true, true);
                        float scale = text.textInfo.characterInfo.First(c => c.isVisible).scale;
                        var original = text.fontSharedMaterial;
                        if (!materials.TryGetValue(original, out var material))
                        {
                            materials.Add(original, material = new Material(original));
                            float pixels = original.GetFloat("_OutlineWidth") * original.GetFloat("_ScaleRatioA") * 2 * original.GetFloat("_GradientScale") * scale;
                            PsdTmpOuterStroke.Configure(material, strokeOverride ?? pixels, scale, weight, faceEdge, outlineEdge);
                            if (maskColors) { material.SetColor("_FaceColor", Color.red); material.SetColor("_OutlineColor", Color.green); }
                        }
                        text.fontSharedMaterial = material; text.UpdateMeshPadding(); text.ForceMeshUpdate(true, true);
                    }
                });
            }
            finally { foreach (var material in materials.Values) UnityEngine.Object.DestroyImmediate(material); }
        }
        public static string CaptureResolution(int point, int padding, float weight, GlyphRenderMode renderMode = GlyphRenderMode.SDFAA, bool outerStroke = false)
        {
            string folder = "Assets/PsdUiWorkbench/Tests/Resolution_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/PsdUiWorkbench/Tests", System.IO.Path.GetFileName(folder));
            TMP_FontAsset font = null;
            try
            {
                const string sourcePath = "Assets/Psd/战斗-主菜单界面.psd";
                var source = new UnityPsdDocumentSource();
                var document = source.Load(AssetDatabase.AssetPathToGUID(sourcePath)).Document;
                var texts = AssetDatabase.LoadAllAssetsAtPath(sourcePath).OfType<PsdTextMetadataAsset>().Single().Read().Sources;
                string required = string.Concat(texts.Values.SelectMany(t => t.Spans.Where(s => s.Style.FontKey == "TOXIA").Select(s => t.Text.Substring(s.Start, s.Length))));
                font = TMP_FontAsset.CreateFontAsset(AssetDatabase.LoadAssetAtPath<Font>("Assets/AAGame/UI/Fonts/Toxia_FRE.ttf"), point, padding,
                    renderMode, 4096, 4096, AtlasPopulationMode.Dynamic, false);
                if (!font.TryAddCharacters(required, out string missing, true)) throw new InvalidOperationException(missing);
                font.atlasTexture.Apply(false, false); font.atlasPopulationMode = AtlasPopulationMode.Static;
                AssetDatabase.CreateAsset(font, folder + "/Font.asset"); AssetDatabase.AddObjectToAsset(font.material, font);
                foreach (var atlas in font.atlasTextures) AssetDatabase.AddObjectToAsset(atlas, font);
                EditorUtility.SetDirty(font); AssetDatabase.SaveAssetIfDirty(font);
                var result = PsdTmpPrefabExporter.Export(PsdPrefabPlanner.Build(document, new PsdVisibilityState()), texts,
                    new Dictionary<string, TMP_FontAsset> { {"TOXIA", font} }, TMP_Settings.defaultFontAsset, folder + "/test.prefab", source.Resolve,
                    outerStroke ? PsdTmpRenderingMode.OuterStrokeExperimental : PsdTmpRenderingMode.Standard);
                return CaptureWeight(result.AssetPath, weight, "resolution-" + point + "-" + renderMode + (outerStroke ? "-outer" : "-standard") + "-weight-" + weight.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            }
            finally
            {
                if (font != null && !EditorUtility.IsPersistent(font))
                {
                    UnityEngine.Object.DestroyImmediate(font.material);
                    foreach (var atlas in font.atlasTextures) UnityEngine.Object.DestroyImmediate(atlas);
                    UnityEngine.Object.DestroyImmediate(font);
                }
                AssetDatabase.DeleteAsset(folder);
            }
        }
        public static string CaptureWeight(string prefab, float weight, string filename)
        {
            var materials = new Dictionary<Material, Material>();
            try
            {
                return PsdTmpCandidateExport.Capture(prefab, Folder + filename + ".png", root =>
                {
                    foreach (var text in root.GetComponentsInChildren<TextMeshProUGUI>(true))
                    {
                        if (text.name == "Placeholder") continue;
                        var source = text.fontSharedMaterial;
                        if (!materials.TryGetValue(source, out var copy)) materials.Add(source, copy = new Material(source));
                        text.fontSharedMaterial = copy; copy.SetFloat("_WeightBold", weight);
                        ShaderUtilities.UpdateShaderRatios(copy); text.UpdateMeshPadding(); text.ForceMeshUpdate(true, true);
                    }
                });
            }
            finally { foreach (var material in materials.Values) UnityEngine.Object.DestroyImmediate(material); }
        }
        public static string Run(string prefab = "Assets/PsdUiWorkbench/Exports/战斗-主菜单界面_TMP 1.prefab")
        {
            foreach (string variant in new[] {"exported", "no-kern", "no-bold", "outline-dilate-zero", "fill-only", "fill-no-bold", "no-kern-no-bold"})
            {
                var materials = new Dictionary<Material, Material>();
                try
                {
                    PsdTmpCandidateExport.Capture(prefab, Folder + variant + ".png", root =>
                    {
                        foreach (var text in root.GetComponentsInChildren<TextMeshProUGUI>(true))
                        {
                            if (text.name == "Placeholder") continue;
                            var source = text.fontSharedMaterial;
                            if (!materials.TryGetValue(source, out var copy)) materials.Add(source, copy = new Material(source));
                            text.fontSharedMaterial = copy;
                            if (variant.Contains("no-kern")) text.fontFeatures = new List<OTL_FeatureTag>();
                            if (variant.Contains("no-bold")) copy.SetFloat("_WeightBold", 0);
                            if (variant.StartsWith("fill"))
                            {
                                copy.SetFloat("_OutlineWidth", 0); copy.SetFloat("_FaceDilate", 0); copy.DisableKeyword("OUTLINE_ON");
                            }
                            if (variant == "outline-dilate-zero") copy.SetFloat("_FaceDilate", 0);
                            ShaderUtilities.UpdateShaderRatios(copy); text.UpdateMeshPadding(); text.ForceMeshUpdate(true, true);
                        }
                    });
                }
                finally { foreach (var material in materials.Values) UnityEngine.Object.DestroyImmediate(material); }
            }
            return Folder;
        }
    }
}
