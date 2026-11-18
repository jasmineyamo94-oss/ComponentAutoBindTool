using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace PsdUiWorkbench.Tests
{
    public static class TmpOutlineValidation
    {
        private const int Width = 768, Height = 256;
        [Serializable]
        public sealed class Row
        {
            public string font, shader, status, sample, sourcePath, sourceSha256, family, sourceStyle, renderMode;
            public int padding, pointSize, fontSize;
            public bool bold;
            public float requestedPx, atlasGradientTimesG, glyphScale, ratioA0, ratioA1, width, dilate;
            public float faceErrorCentered, faceErrorSimpleCompensation, faceErrorCompensated, outerPixels, faceThresholdShift;
            public float shadowErrorUncorrected, shadowErrorCorrected, ratioC0, ratioC1;
            public int faceBoundaryPixelsChanged;
            public float originShiftX, baselineShiftY, weight, characterSpacing, baselineDilate, screenScale;
            public int baselineFaceMaskPixels;
            public bool sourceFontMatches, frameClipped;
            public float sourceScaleMin, sourceScaleMax, outlineEquationError, innerEquationError;
            public float sharpness, sharpnessBaselineError, faceErrorToNeutral;
        }
        public sealed class Matrix
        {
            public string FontPath, Sample, EvidencePrefix;
            public GlyphRenderMode RenderMode = GlyphRenderMode.SDFAA;
            public int[] PointSizes = {90}, Paddings = {6, 12}, FontSizes = {16, 32, 64};
            public float[] Strokes = {1, 2, 3, 5}, Spacings = {0, 100};
            public bool[] SyntheticBold = {false, true};
            public float Sharpness;
        }
        [Serializable] private sealed class Report { public string unity; public List<Row> rows = new List<Row>(); }
        private static readonly string[] _fonts =
        {
            "Assets/AAGame/UI/Fonts/Toxia_FRE.ttf",
            "Assets/Third Party/ComponentAutoBindTool/Example/TextMesh Pro/Fonts/LiberationSans.ttf",
            "Assets/Third Party/ComponentAutoBindTool/Example/Fonts/NotoSansCJKsc-Regular.otf"
        };
        private static readonly string[] _shaders = { "TextMeshPro/Mobile/Distance Field", "TextMeshPro/Distance Field" };
        public static string Run(bool full = true, bool writeReport = true, int smokePoint = 96, int smokeSize = 96, float smokeStroke = 2, float smokeSpacing = 0, float smokeNormalWeight = 0, float smokeBaselineDilate = 0)
        {
            var report = Measure(full, writeReport, smokePoint, smokeSize, smokeStroke, smokeSpacing, smokeNormalWeight, smokeBaselineDilate, 1);
            string output = "Assets/PsdUiWorkbench/Documentation/tmp-outline-validation.json";
            if (writeReport) File.WriteAllText(output, JsonUtility.ToJson(report, true));
            var valid = report.rows.Where(r => r.status.StartsWith("可表达")).ToArray();
            return JsonUtility.ToJson(new Summary
            {
                rows = report.rows.Count, valid = valid.Length,
                worstFaceRelativeError = valid.Length == 0 ? -1 : valid.Max(r => r.faceErrorCompensated),
                worstShadowRelativeError = valid.Length == 0 ? -1 : valid.Max(r => r.shadowErrorCorrected),
                worstFaceShift = valid.Length == 0 ? -1 : valid.Max(r => r.faceThresholdShift),
                worstOuterError = valid.Length == 0 ? -1 : valid.Max(r => Mathf.Abs(r.outerPixels - r.requestedPx)), output = output
            });
        }

        public static Row Probe(int point = 96, int size = 96, float stroke = 2, float spacing = 0, float normalWeight = 0, float baseDilate = 0, float screenScale = 1)
        {
            return Measure(false, false, point, size, stroke, spacing, normalWeight, baseDilate, screenScale).rows[0];
        }

        public static List<Row> MeasureMatrix(Matrix matrix)
        {
            if (matrix == null || string.IsNullOrEmpty(matrix.FontPath) || string.IsNullOrEmpty(matrix.Sample))
                throw new ArgumentException("必须指定字体文件和测试字符。");
            return Measure(true, matrix.EvidencePrefix != null, 96, 96, 2, 0, 0, 0, 1, matrix).rows;
        }

        private static Report Measure(bool full, bool writeReport, int smokePoint, int smokeSize, float smokeStroke, float smokeSpacing, float smokeNormalWeight, float smokeBaselineDilate, float screenScale, Matrix matrix = null)
        {
            var report = new Report { unity = Application.unityVersion };
            foreach (string path in matrix != null ? new[] {matrix.FontPath} : full ? _fonts : _fonts.Take(1))
            foreach (int point in matrix?.PointSizes ?? (full ? new[] {64, 96} : new[] {smokePoint}))
            foreach (int padding in matrix?.Paddings ?? (full ? new[] {6, 12} : new[] {12}))
            {
                var mode = matrix?.RenderMode ?? GlyphRenderMode.SDFAA;
                var font = Path.IsPathRooted(path)
                    ? TMP_FontAsset.CreateFontAsset(path, 0, point, padding, mode, 1024, 1024)
                    : TMP_FontAsset.CreateFontAsset(AssetDatabase.LoadAssetAtPath<Font>(path), point, padding, mode, 1024, 1024, AtlasPopulationMode.Dynamic, false);
                if (font == null) throw new InvalidOperationException("无法创建临时字体：" + path);
                try
                {
                    string sample = matrix?.Sample ?? (path.Contains("Noto") ? "H田国" : "H8O");
                    string hash;
                    using (var sha = SHA256.Create())
                    using (var stream = File.OpenRead(path)) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (!font.TryAddCharacters(sample, out string missing)) throw new InvalidOperationException("缺少测试字符：" + missing);
                    font.atlasTexture.Apply(false, false);
                    float gradient = AtlasGradient(font);
                    foreach (string shader in full ? _shaders : _shaders.Take(1))
                    foreach (int size in matrix?.FontSizes ?? (full ? new[] {32, 64, 96} : new[] {smokeSize}))
                    foreach (bool bold in matrix?.SyntheticBold ?? (full ? new[] {false, true} : new[] {false}))
                    foreach (float spacing in matrix?.Spacings ?? (full ? new[] {0f, 40f} : new[] {smokeSpacing}))
                    {
                        using var rig = new Rig(font, shader, size, bold, sample, screenScale);
                        rig.Text.characterSpacing = spacing;
                        float baseDilate = full ? 0 : smokeBaselineDilate;
                        if (!full) rig.Material.SetFloat("_WeightNormal", smokeNormalWeight);
                        rig.Configure(0, baseDilate);
                        var neutral = rig.Render();
                        float sharpness = matrix?.Sharpness ?? 0;
                        if (sharpness != 0)
                        {
                            rig.Material.SetFloat("_Sharpness", sharpness);
                            rig.Configure(0, baseDilate);
                        }
                        var baseline = sharpness == 0 ? neutral : rig.Render();
                        float a0 = rig.Material.GetFloat("_ScaleRatioA");
                        float weight = rig.Material.GetFloat(bold ? "_WeightBold" : "_WeightNormal") / 4;
                        float scale = rig.Text.textInfo.characterInfo[0].scale;
                        float origin = rig.Text.textInfo.characterInfo[0].origin;
                        float baselineY = rig.Text.textInfo.characterInfo[0].baseLine;
                        var characters = rig.Text.textInfo.characterInfo.Take(rig.Text.textInfo.characterCount).Where(c => c.isVisible).ToArray();
                        bool sourceMatches = characters.Length > 0 && characters.All(c => c.fontAsset == font);
                        float scaleMin = characters.Min(c => c.scale), scaleMax = characters.Max(c => c.scale);
                        foreach (float pixels in matrix?.Strokes ?? (full ? new[] {1f, 3f, 5f} : new[] {smokeStroke}))
                        {
                            // 目标外扩转换为 SDF alpha 阈值移动量，阈值斜率为 1/(2G)。
                            float g = rig.Material.GetFloat("_GradientScale");
                            float q = pixels / (2 * g * scale * screenScale);
                            float maxWeight = Mathf.Max(rig.Material.GetFloat("_WeightNormal"), rig.Material.GetFloat("_WeightBold")) / 4;
                            bool solved = TrySolve(g, q, weight, maxWeight, baseDilate, a0, out float w, out float d);
                            if (solved) rig.Configure(w, d);
                            float a1 = rig.Material.GetFloat("_ScaleRatioA");
                            var row = new Row
                            {
                                font = Path.GetFileNameWithoutExtension(path), shader = shader, pointSize = point, padding = padding, sample = sample, weight = weight,
                                sourcePath = path, sourceSha256 = hash, family = font.faceInfo.familyName, sourceStyle = font.faceInfo.styleName, renderMode = mode.ToString(),
                                sourceFontMatches = sourceMatches, sourceScaleMin = scaleMin, sourceScaleMax = scaleMax,
                                sharpness = sharpness, sharpnessBaselineError = RelativeError(neutral, baseline, 0),
                                fontSize = size, bold = bold, characterSpacing = spacing, baselineDilate = baseDilate, screenScale = screenScale, requestedPx = pixels, atlasGradientTimesG = gradient,
                                glyphScale = scale, ratioA0 = a0, ratioA1 = a1, width = w, dilate = d,
                                status = solved && Mathf.Abs(w * a1 - q) < 0.0001f ? "可表达" : "需要更大Padding或采样调整"
                            };
                            if (row.status != "可表达") { report.rows.Add(row); continue; }
                            row.outlineEquationError = Mathf.Abs(w * a1 - q);
                            row.innerEquationError = Mathf.Abs((weight + d - w) * a1 - (weight + baseDilate) * a0);
                            row.originShiftX = rig.Text.textInfo.characterInfo[0].origin - origin;
                            row.baselineShiftY = rig.Text.textInfo.characterInfo[0].baseLine - baselineY;
                            var compensated = rig.Render();
                            row.baselineFaceMaskPixels = baseline.Count(c => c.r >= 128);
                            row.frameClipped = FrameClipped(baseline) || FrameClipped(compensated);
                            rig.Configure(w, baseDilate);
                            var centered = rig.Render();
                            row.faceErrorCentered = RelativeError(baseline, centered, 0);
                            row.faceErrorCompensated = RelativeError(baseline, compensated, 0);
                            row.faceErrorToNeutral = RelativeError(neutral, compensated, 0);
                            rig.Configure(w, baseDilate + w);
                            row.faceErrorSimpleCompensation = RelativeError(baseline, rig.Render(), 0);
                            row.faceThresholdShift = FaceShift(baseline, compensated);
                            row.faceBoundaryPixelsChanged = MaskDifference(baseline, compensated, 0);
                            row.outerPixels = OuterGrowth(baseline, compensated);
                            // 独立读取阴影覆盖，避免外描边遮挡投影导致误判。
                            rig.Configure(0, baseDilate);
                            rig.Shadow(.15f, -.1f, 0, .08f);
                            var shadow0 = rig.Render();
                            float c0 = rig.Material.GetFloat("_ScaleRatioC");
                            rig.Configure(w, d);
                            rig.Shadow(.15f, -.1f, 0, .08f);
                            var shadowBad = rig.Render();
                            float c1 = rig.Material.GetFloat("_ScaleRatioC");
                            row.ratioC0 = c0; row.ratioC1 = c1;
                            if (c1 < 0.00001f)
                            {
                                row.status += "；阴影距离场余量不足";
                                row.shadowErrorCorrected = -1;
                                report.rows.Add(row);
                                rig.Configure(0, baseDilate);
                                continue;
                            }
                            float ux = .15f * c0 / c1, uy = -.1f * c0 / c1;
                            float ud = ((weight + baseDilate) * a0 - (weight + d) * a1) / c1;
                            float us = .08f * c0 / c1;
                            for (int iteration = 0; iteration < 40; iteration++)
                            {
                                rig.Shadow(ux, uy, ud, us);
                                c1 = rig.Material.GetFloat("_ScaleRatioC");
                                ux = .15f * c0 / c1; uy = -.1f * c0 / c1;
                                ud = ((weight + baseDilate) * a0 - (weight + d) * a1) / c1;
                                us = .08f * c0 / c1;
                            }
                            rig.Shadow(ux, uy, ud, us);
                            var shadowFixed = rig.Render();
                            row.shadowErrorUncorrected = RelativeError(shadow0, shadowBad, 2);
                            row.shadowErrorCorrected = RelativeError(shadow0, shadowFixed, 2);
                            row.ratioC1 = rig.Material.GetFloat("_ScaleRatioC");
                            if (Mathf.Abs(ux) > 1 || Mathf.Abs(uy) > 1 || Mathf.Abs(ud) > 1 || us > 1)
                                row.status += "；阴影需更大Padding";
                            report.rows.Add(row);
                            bool evidenceCase = matrix == null
                                ? pixels == (full ? 3 : smokeStroke) && (full ? point == 96 && size == 96 : true)
                                : pixels == 2 && size == 32;
                            if (padding == 12 && !bold && spacing == 0 && evidenceCase && shader == _shaders[0] && writeReport)
                            {
                                string prefix = matrix?.EvidencePrefix ?? "outline-" + row.font;
                                Save(prefix + "-baseline.png", baseline);
                                Save(prefix + "-centered.png", centered);
                                Save(prefix + "-compensated.png", compensated);
                            }
                            rig.Configure(0, baseDilate);
                        }
                    }
                }
                finally
                {
                    foreach (var atlas in font.atlasTextures) if (atlas != null) UnityEngine.Object.DestroyImmediate(atlas);
                    UnityEngine.Object.DestroyImmediate(font.material);
                    UnityEngine.Object.DestroyImmediate(font);
                }
            }
            return report;
        }
        [Serializable] private sealed class Summary { public int rows, valid; public float worstFaceRelativeError, worstShadowRelativeError, worstFaceShift, worstOuterError; public string output; }
        // 解析求解当前标准 Shader 的 RatioA 两个分支，避免接近饱和时固定次数迭代不收敛。
        internal static bool TrySolve(float g, float q, float weight, float maxWeight, float baseDilate, float a0, out float width, out float dilate)
        {
            return PsdUiWorkbench.Infrastructure.TmpOutlineCompensation.TrySolve(g, q, weight, maxWeight, baseDilate, a0, out width, out dilate);
        }
        private static float AtlasGradient(TMP_FontAsset font)
        {
            var pixels = font.atlasTexture.GetPixels32();
            var magnitudes = new List<float>();
            int width = font.atlasTexture.width;
            foreach (var glyph in font.glyphTable)
            {
                var r = glyph.glyphRect;
                for (int y = r.y + 1; y < r.y + r.height - 1; y++)
                for (int x = r.x + 1; x < r.x + r.width - 1; x++)
                {
                    float a = pixels[y * width + x].a / 255f;
                    if (a < .4 || a > .6) continue;
                    float dx = (pixels[y * width + x + 1].a - pixels[y * width + x - 1].a) / 510f;
                    float dy = (pixels[(y + 1) * width + x].a - pixels[(y - 1) * width + x].a) / 510f;
                    magnitudes.Add(Mathf.Sqrt(dx * dx + dy * dy));
                }
            }
            magnitudes.Sort();
            return magnitudes.Count == 0 ? 0 : magnitudes[magnitudes.Count / 2] * (font.atlasPadding + 1);
        }
        private static float Channel(Color32 c, int channel) => (channel == 0 ? c.r : channel == 2 ? c.b : c.a) / 255f;
        private static float RelativeError(Color32[] baseline, Color32[] other, int channel)
        {
            double error = 0, mass = 0;
            for (int i = 0; i < baseline.Length; i++) { float a = Channel(baseline[i], channel); error += Math.Abs(a - Channel(other[i], channel)); mass += a; }
            return mass > 0 ? (float)(error / mass) : -1;
        }
        private static int MaskDifference(Color32[] a, Color32[] b, int channel)
        {
            int count = 0;
            for (int i = 0; i < a.Length; i++) if ((Channel(a[i], channel) >= .5f) != (Channel(b[i], channel) >= .5f)) count++;
            return count;
        }
        private static bool FrameClipped(Color32[] pixels)
        {
            for (int x = 0; x < Width; x++) if (pixels[x].a != 0 || pixels[(Height - 1) * Width + x].a != 0) return true;
            for (int y = 0; y < Height; y++) if (pixels[y * Width].a != 0 || pixels[y * Width + Width - 1].a != 0) return true;
            return false;
        }
        private static Vector4 Bounds(Color32[] pixels, int channel)
        {
            int left = Width, right = -1, bottom = Height, top = -1;
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++) if (Channel(pixels[y * Width + x], channel) >= .5f)
            { left = Math.Min(left, x); right = Math.Max(right, x); bottom = Math.Min(bottom, y); top = Math.Max(top, y); }
            return new Vector4(left, right, bottom, top);
        }
        private static float FaceShift(Color32[] a, Color32[] b)
        {
            var delta = Bounds(a, 0) - Bounds(b, 0);
            return Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.y), Mathf.Abs(delta.z), Mathf.Abs(delta.w));
        }
        private static float OuterGrowth(Color32[] a, Color32[] b)
        {
            var before = Bounds(a, 3); var after = Bounds(b, 3);
            return ((before.x - after.x) + (after.y - before.y) + (before.z - after.z) + (after.w - before.w)) / 4;
        }
        private static void Save(string name, Color32[] pixels)
        {
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true);
            try { texture.SetPixels32(pixels); texture.Apply(); File.WriteAllBytes("Assets/PsdUiWorkbench/Documentation/" + name, texture.EncodeToPNG()); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }
        private sealed class Rig : IDisposable
        {
            private readonly UnityEngine.SceneManagement.Scene _scene;
            private readonly GameObject _root, _cameraGo;
            private readonly Camera _camera;
            private readonly RenderTexture _target;
            private readonly Texture2D _readback;
            public TextMeshProUGUI Text { get; }
            public Material Material { get; }
            public Rig(TMP_FontAsset font, string shaderName, int fontSize, bool bold, string sample, float screenScale)
            {
                _scene = EditorSceneManager.NewPreviewScene();
                _root = new GameObject("TMP轮廓验证", typeof(RectTransform));
                _root.SetActive(false);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(_root, _scene);
                var canvas = _root.AddComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace;
                var rt = (RectTransform)_root.transform; rt.sizeDelta = new Vector2(Width, Height); rt.localScale = Vector3.one * screenScale;
                var textGo = new GameObject("文本", typeof(RectTransform)); textGo.transform.SetParent(_root.transform, false);
                Text = textGo.AddComponent<TextMeshProUGUI>();
                Material = new Material(Shader.Find(shaderName));
                Material.CopyPropertiesFromMaterial(font.material);
                Material.shader = Shader.Find(shaderName);
                Material.SetFloat("_Sharpness", 0); Material.SetFloat("_PerspectiveFilter", 0);
                Text.font = font; Text.fontSharedMaterial = Material; Text.fontSize = fontSize;
                Text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
                Text.text = sample; Text.alignment = TextAlignmentOptions.Center; Text.enableAutoSizing = false;
                Text.textWrappingMode = TextWrappingModes.NoWrap; Text.overflowMode = TextOverflowModes.Overflow;
                Text.extraPadding = true; Text.raycastTarget = false;
                Text.rectTransform.sizeDelta = new Vector2(Width - 80, Height - 80);
                _target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                _target.antiAliasing = 1; _target.Create();
                _cameraGo = new GameObject("验证相机", typeof(Camera));
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(_cameraGo, _scene);
                _camera = _cameraGo.GetComponent<Camera>(); _camera.scene = _scene;
                _camera.orthographic = true; _camera.orthographicSize = Height / 2; _camera.transform.position = new Vector3(0, 0, -10);
                _camera.clearFlags = CameraClearFlags.SolidColor; _camera.backgroundColor = Color.clear;
                _camera.targetTexture = _target; _camera.nearClipPlane = .1f; _camera.farClipPlane = 100;
                canvas.worldCamera = _camera;
                _readback = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true);
                _root.SetActive(true);
            }
            public void Configure(float width, float dilate)
            {
                Material.EnableKeyword("OUTLINE_ON"); Material.DisableKeyword("UNDERLAY_ON"); Material.DisableKeyword("UNDERLAY_INNER");
                Material.SetColor("_FaceColor", Color.red); Material.SetColor("_OutlineColor", Color.green);
                Material.SetFloat("_OutlineWidth", width); Material.SetFloat("_FaceDilate", dilate); Material.SetFloat("_OutlineSoftness", 0);
                Material.SetFloat("_UnderlayOffsetX", 0); Material.SetFloat("_UnderlayOffsetY", 0); Material.SetFloat("_UnderlayDilate", 0); Material.SetFloat("_UnderlaySoftness", 0);
                ShaderUtilities.UpdateShaderRatios(Material);
                Text.UpdateMeshPadding(); Text.ForceMeshUpdate();
            }
            public void Shadow(float x, float y, float dilate, float softness)
            {
                Material.SetColor("_FaceColor", Color.clear); Material.SetColor("_OutlineColor", Color.clear);
                Material.EnableKeyword("UNDERLAY_ON"); Material.SetColor("_UnderlayColor", Color.blue);
                Material.SetFloat("_UnderlayOffsetX", x); Material.SetFloat("_UnderlayOffsetY", y);
                Material.SetFloat("_UnderlayDilate", dilate); Material.SetFloat("_UnderlaySoftness", softness);
                ShaderUtilities.UpdateShaderRatios(Material); Text.UpdateMeshPadding(); Text.ForceMeshUpdate();
            }
            public Color32[] Render()
            {
                var previous = RenderTexture.active;
                try
                {
                    Canvas.ForceUpdateCanvases(); _camera.Render(); RenderTexture.active = _target;
                    _readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); _readback.Apply();
                    return _readback.GetPixels32();
                }
                finally { RenderTexture.active = previous; }
            }
            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(_root); UnityEngine.Object.DestroyImmediate(_cameraGo);
                _target.Release(); UnityEngine.Object.DestroyImmediate(_target); UnityEngine.Object.DestroyImmediate(_readback);
                UnityEngine.Object.DestroyImmediate(Material); EditorSceneManager.ClosePreviewScene(_scene);
            }
        }
    }
}
