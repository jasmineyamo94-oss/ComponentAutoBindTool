using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using PsdUiWorkbench.Infrastructure;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

namespace PsdUiWorkbench.Tests
{
    public static class PsdTextReferenceValidation
    {
        private const string Folder = "Assets/PsdUiWorkbench/Documentation/PsdTextAudit/";
        private const string SourcePath = "Assets/Psd/战斗-主菜单界面.psd";
        private const string FontPath = "Assets/AAGame/UI/Fonts/Toxia_FRE.ttf";
        private const int Width = 768, Height = 256;
        public const float PlaceholderSize = 36;

        [Serializable]
        public sealed class Inventory
        {
            public string sha256, metaSha256;
            public Job[] jobs;
        }

        [Serializable]
        public sealed class Job
        {
            public int id, justification;
            public string text;
            public bool eligibleForToxiaProbe, fauxBold;
            public float fontSizeUi, tracking, originX, originY, strokePixels;
            public float[] color, strokeColor;
            public Segment[] segments;
        }

        [Serializable] public sealed class Segment { public int start, length; public string font; }
        [Serializable] public sealed class PlaceholderSpan
        {
            public string text, actualFont;
            public bool placeholder, bold, outline, shadow;
            public float fontSize;
        }

        [Serializable]
        public sealed class Measurement
        {
            public float weightBold, boldSpacing, alphaRelativeError, alphaIntersectionOverUnion;
            public float widthDifference, heightDifference, leftDifference, topDifference;
            public bool kerning;
            public float[] characterOrigins;
        }

        [Serializable]
        public sealed class LayerResult
        {
            public int id;
            public string text, status, spriteId;
            public long spriteLocalFileId;
            public float fontSizeUi, tracking, baselineX, baselineY, outlineWidth, faceDilate;
            public Measurement defaultTmp, bestFill;
            public bool measured;
            public bool sourceImported;
            public bool placeholder;
            public PlaceholderSpan[] placeholderSpans;
            public List<Measurement> measurements = new List<Measurement>();
        }

        [Serializable]
        public sealed class Report
        {
            public string unity, sourceSha256, sourceMetaSha256, fontSha256;
            public string referenceScope = "官方 Sprite 仅作为填充与布局基准，不含 Photoshop 图层效果；描边图是候选结果，不是原稿验收。";
            public List<LayerResult> layers = new List<LayerResult>();
            public bool sourceUnchanged, metaUnchanged, fontUnchanged, temporaryStateRestored;
            public float referenceCoverageProbeAlpha;
            public string referenceShader = "Sprites/Default，仅验证用预乘 Alpha 材质，不修改源资源或正式 UI 材质";
            public string defaultFontPath, defaultFontSha256;
            public bool defaultFontUnchanged;
        }

        public static string Run()
        {
            var report = Measure(true);
            return $"文本 {report.layers.Count}，填充探针 {report.layers.Count(l => l.measured)}，固定占位 {report.layers.Count(l => l.placeholder)}；源文件保持={report.sourceUnchanged && report.metaUnchanged && report.fontUnchanged && report.defaultFontUnchanged}；临时状态恢复={report.temporaryStateRestored}";
        }

        public static Report Measure(bool writeEvidence)
        {
            var input = JsonUtility.FromJson<Inventory>(File.ReadAllText(Folder + "inventory.json"));
            if (Hash(SourcePath) != input.sha256 || Hash(SourcePath + ".meta") != input.metaSha256)
                throw new InvalidOperationException("PSD 或 .meta 已变化，请先重新执行源文件审计。");
            var source = new UnityPsdDocumentSource();
            var load = source.Load(AssetDatabase.AssetPathToGUID(SourcePath));
            if (load.Document == null) throw new InvalidOperationException(load.Diagnostic);
            int scenes = EditorSceneManager.previewSceneCount;
            var activeScene = EditorSceneManager.GetActiveScene();
            var activeTarget = RenderTexture.active;
            var report = new Report
            {
                unity = Application.unityVersion, sourceSha256 = input.sha256,
                sourceMetaSha256 = input.metaSha256, fontSha256 = Hash(FontPath)
            };
            var defaultFont = TMP_Settings.defaultFontAsset;
            if (defaultFont == null) throw new InvalidOperationException("项目没有配置默认 TMP 字体。");
            report.defaultFontPath = AssetDatabase.GetAssetPath(defaultFont);
            report.defaultFontSha256 = Hash(report.defaultFontPath);
            var font = TMP_FontAsset.CreateFontAsset(AssetDatabase.LoadAssetAtPath<Font>(FontPath), 96, 24, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, false);
            try
            {
                string toXiaCharacters = string.Concat(input.jobs.SelectMany(job => job.segments.Where(segment => segment.font == "TOXIA")
                    .Select(segment => job.text.Substring(segment.start, Math.Max(0, Math.Min(segment.length, job.text.Length - segment.start))))));
                if (!font.TryAddCharacters(toXiaCharacters, out string missing))
                    throw new InvalidOperationException("TOXIA 缺少字符：" + missing);
                font.atlasTexture.Apply(false, false);
                using var rig = new Rig(font);
                report.referenceCoverageProbeAlpha = rig.ProbeCoverage();
                if (Mathf.Abs(report.referenceCoverageProbeAlpha - 128f / 255) > 0.006f)
                    throw new InvalidOperationException("参考材质的 Alpha 覆盖校验失败。");
                foreach (var job in input.jobs)
                {
                    var node = load.Document.Find(job.id);
                    var row = new LayerResult { id = job.id, text = job.text, fontSizeUi = job.fontSizeUi, tracking = job.tracking };
                    report.layers.Add(row);
                    if (node == null) { row.status = "源 ID 无法关联"; continue; }
                    row.sourceImported = node.IsImported;
                    if (!job.eligibleForToxiaProbe)
                    {
                        row.placeholder = true; row.status = "缺字体段使用默认字体固定占位，不做原稿匹配";
                        rig.Image.enabled = false; rig.Text.enabled = false;
                        var pixels = rig.RenderPlaceholder(job, defaultFont, out row.placeholderSpans);
                        if (writeEvidence) Save(job.id + "-placeholder.png", pixels);
                        continue;
                    }
                    var sprite = source.Resolve(node.SpriteId);
                    if (sprite == null) { row.status = "没有独立导入 Sprite"; continue; }
                    row.spriteId = node.SpriteId;
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string _, out row.spriteLocalFileId);
                    rig.Image.sprite = sprite;
                    rig.Image.rectTransform.sizeDelta = new Vector2(node.Bounds.Width, node.Bounds.Height);
                    rig.Image.rectTransform.anchoredPosition = new Vector2(node.Bounds.X + node.Bounds.Width / 2 - Mathf.Floor(job.originX), Mathf.Floor(job.originY) - node.Bounds.Y - node.Bounds.Height / 2);
                    rig.Image.enabled = true; rig.Text.enabled = false;
                    var reference = rig.Render();
                    rig.Image.enabled = false; rig.Text.enabled = true;
                    row.measured = true;
                    Color32[] bestPixels = null;
                    foreach (float spacing in new[] {7f, 0f, 2.5f})
                    foreach (float weight in new[] {0.75f, 0f, 0.25f, 0.5f, 1f})
                    foreach (bool kerning in new[] {true, false})
                    {
                        rig.Configure(job, weight, spacing, kerning);
                        var pixels = rig.Render();
                        var measurement = Compare(reference, pixels, weight, spacing);
                        measurement.kerning = kerning;
                        measurement.characterOrigins = rig.Text.textInfo.characterInfo.Take(rig.Text.textInfo.characterCount).Select(c => c.origin).ToArray();
                        row.measurements.Add(measurement);
                        if (spacing == 7 && weight == 0.75f && kerning) row.defaultTmp = measurement;
                        if (row.bestFill == null || measurement.alphaRelativeError < row.bestFill.alphaRelativeError)
                        {
                            row.bestFill = measurement;
                            bestPixels = pixels;
                        }
                    }
                    rig.Configure(job, row.bestFill.weightBold, row.bestFill.boldSpacing, row.bestFill.kerning);
                    row.baselineX = rig.Text.rectTransform.anchoredPosition.x;
                    row.baselineY = rig.Text.rectTransform.anchoredPosition.y + rig.Text.textInfo.characterInfo[0].baseLine;
                    float g = rig.Material.GetFloat("_GradientScale"), a0 = rig.Material.GetFloat("_ScaleRatioA");
                    float weightValue = row.bestFill.weightBold / 4;
                    bool supported = TmpOutlineValidation.TrySolve(g, job.strokePixels / (2 * g * rig.Text.textInfo.characterInfo[0].scale), weightValue, weightValue, 0, a0, out row.outlineWidth, out row.faceDilate);
                    row.status = supported ? "已比较填充；描边为待验收候选" : "填充已比较；描边距离场余量不足";
                    if (writeEvidence && (job.id == 1091 || job.id == 979 || job.id == 1057))
                    {
                        Save(job.id + "-official-fill.png", reference);
                        Save(job.id + "-tmp-fill.png", bestPixels);
                        if (supported)
                        {
                            rig.Material.SetFloat("_OutlineWidth", row.outlineWidth);
                            rig.Material.SetFloat("_FaceDilate", row.faceDilate);
                            rig.Material.SetColor("_OutlineColor", new Color(job.strokeColor[0], job.strokeColor[1], job.strokeColor[2], job.strokeColor[3]));
                            rig.Material.EnableKeyword("OUTLINE_ON");
                            rig.Update(); Save(job.id + "-tmp-outline-candidate.png", rig.Render());
                        }
                    }
                }
            }
            finally { if (font != null) UnityEngine.Object.DestroyImmediate(font); }
            report.sourceUnchanged = Hash(SourcePath) == report.sourceSha256;
            report.metaUnchanged = Hash(SourcePath + ".meta") == report.sourceMetaSha256;
            report.fontUnchanged = Hash(FontPath) == report.fontSha256;
            report.defaultFontUnchanged = Hash(report.defaultFontPath) == report.defaultFontSha256;
            report.temporaryStateRestored = scenes == EditorSceneManager.previewSceneCount && activeScene == EditorSceneManager.GetActiveScene() && activeTarget == RenderTexture.active;
            if (writeEvidence) File.WriteAllText(Folder + "unity-reference.json", JsonUtility.ToJson(report, true));
            return report;
        }

        private static string Hash(string path)
        {
            using var sha = SHA256.Create(); using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static Measurement Compare(Color32[] reference, Color32[] candidate, float weight, float spacing)
        {
            double mass = 0, error = 0;
            int intersection = 0, union = 0;
            for (int i = 0; i < reference.Length; i++)
            {
                mass += reference[i].a; error += Math.Abs(reference[i].a - candidate[i].a);
                bool a = reference[i].a >= 128, b = candidate[i].a >= 128;
                if (a || b) union++; if (a && b) intersection++;
            }
            var r = Bounds(reference); var c = Bounds(candidate);
            return new Measurement
            {
                weightBold = weight, boldSpacing = spacing, alphaRelativeError = (float)(error / mass),
                alphaIntersectionOverUnion = union == 0 ? 0 : (float)intersection / union,
                widthDifference = c.width - r.width, heightDifference = c.height - r.height,
                leftDifference = c.x - r.x, topDifference = c.y - r.y
            };
        }

        private static Rect Bounds(Color32[] pixels)
        {
            int left = Width, right = -1, bottom = Height, top = -1;
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                if (pixels[y * Width + x].a >= 128) { left = Math.Min(left, x); right = Math.Max(right, x); bottom = Math.Min(bottom, y); top = Math.Max(top, y); }
            return new Rect(left, Height - 1 - top, right - left + 1, top - bottom + 1);
        }

        private static void Save(string name, Color32[] pixels)
        {
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true);
            try { texture.SetPixels32(pixels); texture.Apply(); File.WriteAllBytes(Folder + name, texture.EncodeToPNG()); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        private sealed class Rig : IDisposable
        {
            private readonly UnityEngine.SceneManagement.Scene _scene;
            private readonly GameObject _root, _cameraObject;
            private readonly Camera _camera;
            private readonly RenderTexture _target;
            private readonly Texture2D _readback;
            private readonly TMP_FontAsset _font;
            private readonly Material _referenceMaterial;
            public TextMeshProUGUI Text { get; }
            public Image Image { get; }
            public Material Material { get; }
            public Rig(TMP_FontAsset font)
            {
                _font = font;
                _scene = EditorSceneManager.NewPreviewScene();
                _root = new GameObject("PSD文字对照", typeof(RectTransform)); _root.SetActive(false);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(_root, _scene);
                var canvas = _root.AddComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace;
                ((RectTransform)_root.transform).sizeDelta = new Vector2(Width, Height);
                var imageObject = new GameObject("官方填充", typeof(RectTransform)); imageObject.transform.SetParent(_root.transform, false);
                Image = imageObject.AddComponent<Image>(); Image.raycastTarget = false; Image.useSpriteMesh = false;
                _referenceMaterial = new Material(Shader.Find("Sprites/Default")); Image.material = _referenceMaterial;
                var textObject = new GameObject("TMP候选", typeof(RectTransform)); textObject.transform.SetParent(_root.transform, false);
                Text = textObject.AddComponent<TextMeshProUGUI>();
                Material = new Material(font.material); Material.SetFloat("_Sharpness", 0); Material.SetFloat("_PerspectiveFilter", 0);
                Text.font = font; Text.fontSharedMaterial = Material; Text.enableAutoSizing = false; Text.extraPadding = true; Text.raycastTarget = false;
                Text.textWrappingMode = TextWrappingModes.NoWrap; Text.overflowMode = TextOverflowModes.Overflow;
                Text.rectTransform.sizeDelta = new Vector2(Width - 40, Height - 40);
                _target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear); _target.Create();
                _cameraObject = new GameObject("PSD文字验证相机", typeof(Camera));
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(_cameraObject, _scene);
                _camera = _cameraObject.GetComponent<Camera>(); _camera.scene = _scene; _camera.orthographic = true; _camera.orthographicSize = Height / 2;
                _camera.transform.position = new Vector3(0, 0, -10); _camera.clearFlags = CameraClearFlags.SolidColor; _camera.backgroundColor = Color.clear;
                _camera.targetTexture = _target; canvas.worldCamera = _camera;
                _readback = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true); _root.SetActive(true);
            }
            public void Configure(Job job, float weight, float spacing, bool kerning)
            {
                _font.boldSpacing = spacing;
                Text.text = job.text; Text.fontSize = job.fontSizeUi; Text.characterSpacing = job.tracking / 10;
                Text.fontStyle = job.fauxBold ? FontStyles.Bold : FontStyles.Normal;
                Text.fontFeatures = kerning ? new List<OTL_FeatureTag> { OTL_FeatureTag.kern } : new List<OTL_FeatureTag>();
                Text.alignment = job.justification == 2 ? TextAlignmentOptions.Baseline : TextAlignmentOptions.BaselineLeft;
                Text.rectTransform.anchoredPosition = Vector2.zero;
                Material.SetFloat("_WeightBold", weight); Material.SetFloat("_WeightNormal", 0);
                Material.SetFloat("_OutlineWidth", 0); Material.SetFloat("_FaceDilate", 0); Material.SetFloat("_OutlineSoftness", 0);
                Material.SetColor("_FaceColor", new Color(job.color[0], job.color[1], job.color[2], job.color[3]));
                Material.DisableKeyword("OUTLINE_ON"); Material.DisableKeyword("UNDERLAY_ON");
                Update();
                var first = Text.textInfo.characterInfo[0];
                Text.rectTransform.anchoredPosition = new Vector2((job.justification == 0 ? -first.origin : 0) + job.originX - Mathf.Floor(job.originX), -first.baseLine - job.originY + Mathf.Floor(job.originY));
                Text.ForceMeshUpdate();
            }
            public void Update() { ShaderUtilities.UpdateShaderRatios(Material); Text.UpdateMeshPadding(); Text.ForceMeshUpdate(); }
            public float ProbeCoverage()
            {
                var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                Sprite sprite = null;
                try
                {
                    texture.SetPixel(0, 0, new Color32(255, 255, 255, 128)); texture.Apply();
                    sprite = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1);
                    Image.sprite = sprite; Image.rectTransform.sizeDelta = new Vector2(8, 8); Image.rectTransform.anchoredPosition = Vector2.zero;
                    Image.enabled = true; Text.enabled = false;
                    return Render()[(Height / 2) * Width + Width / 2].a / 255f;
                }
                finally { UnityEngine.Object.DestroyImmediate(sprite); UnityEngine.Object.DestroyImmediate(texture); }
            }
            public Color32[] RenderPlaceholder(Job job, TMP_FontAsset defaultFont, out PlaceholderSpan[] proof)
            {
                Configure(job, 0.5f, 2.5f, false); Text.enabled = false;
                var group = new GameObject("固定占位段", typeof(RectTransform)); group.transform.SetParent(_root.transform, false);
                var plain = new Material(defaultFont.material);
                foreach (string keyword in new[] {"OUTLINE_ON", "UNDERLAY_ON", "UNDERLAY_INNER", "GLOW_ON"}) plain.DisableKeyword(keyword);
                foreach (string property in new[] {"_OutlineWidth", "_FaceDilate", "_OutlineSoftness", "_UnderlayDilate", "_UnderlaySoftness", "_WeightNormal", "_WeightBold"})
                    if (plain.HasProperty(property)) plain.SetFloat(property, 0);
                plain.SetColor("_FaceColor", Color.white);
                var spans = new List<PlaceholderSpan>();
                var nodes = new List<TextMeshProUGUI>();
                float advance = 0;
                try
                {
                    foreach (var segment in job.segments)
                    {
                        int count = Math.Min(segment.length, job.text.Length - segment.start);
                        if (count <= 0) continue;
                        bool placeholder = segment.font != "TOXIA";
                        var go = new GameObject("文字段", typeof(RectTransform)); go.transform.SetParent(group.transform, false);
                        var text = go.AddComponent<TextMeshProUGUI>(); nodes.Add(text);
                        text.font = placeholder ? defaultFont : _font; text.fontSharedMaterial = placeholder ? plain : Material;
                        text.text = job.text.Substring(segment.start, count); text.richText = false;
                        text.fontSize = placeholder ? PlaceholderSize : job.fontSizeUi;
                        text.fontStyle = placeholder ? FontStyles.Normal : FontStyles.Bold;
                        text.characterSpacing = placeholder ? 0 : job.tracking / 10;
                        text.fontFeatures = new List<OTL_FeatureTag>();
                        text.enableAutoSizing = false; text.textWrappingMode = TextWrappingModes.NoWrap; text.overflowMode = TextOverflowModes.Overflow;
                        text.alignment = TextAlignmentOptions.BaselineLeft; text.raycastTarget = false;
                        text.rectTransform.sizeDelta = new Vector2(Width - 40, Height - 40);
                        text.ForceMeshUpdate();
                        var first = text.textInfo.characterInfo[0]; var last = text.textInfo.characterInfo[text.textInfo.characterCount - 1];
                        float width = last.xAdvance - first.origin;
                        text.rectTransform.anchoredPosition = new Vector2(advance - first.origin, -first.baseLine);
                        advance += width;
                        spans.Add(new PlaceholderSpan
                        {
                            text = text.text, actualFont = text.font.faceInfo.familyName, placeholder = placeholder, fontSize = text.fontSize,
                            bold = (text.fontStyle & FontStyles.Bold) != 0,
                            outline = text.fontSharedMaterial.GetFloat("_OutlineWidth") != 0,
                            shadow = text.fontSharedMaterial.IsKeywordEnabled("UNDERLAY_ON") || text.fontSharedMaterial.IsKeywordEnabled("UNDERLAY_INNER")
                        });
                    }
                    group.transform.localPosition = new Vector3((job.justification == 2 ? -advance / 2 : 0) + job.originX - Mathf.Floor(job.originX), -job.originY + Mathf.Floor(job.originY));
                    var available = nodes.FirstOrDefault(text => text.font == _font);
                    if (available != null)
                    {
                        float g = Material.GetFloat("_GradientScale"), a0 = Material.GetFloat("_ScaleRatioA");
                        if (TmpOutlineValidation.TrySolve(g, job.strokePixels / (2 * g * available.textInfo.characterInfo[0].scale), 0.125f, 0.125f, 0, a0, out float width, out float dilate))
                        {
                            Material.SetFloat("_OutlineWidth", width); Material.SetFloat("_FaceDilate", dilate);
                            Material.SetColor("_OutlineColor", new Color(job.strokeColor[0], job.strokeColor[1], job.strokeColor[2], job.strokeColor[3]));
                            Material.EnableKeyword("OUTLINE_ON"); ShaderUtilities.UpdateShaderRatios(Material);
                        }
                    }
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        nodes[i].UpdateMeshPadding(); nodes[i].ForceMeshUpdate();
                        spans[i].outline = nodes[i].fontSharedMaterial.GetFloat("_OutlineWidth") != 0;
                    }
                    proof = spans.ToArray();
                    return Render();
                }
                finally { UnityEngine.Object.DestroyImmediate(group); UnityEngine.Object.DestroyImmediate(plain); }
            }
            public Color32[] Render()
            {
                var previous = RenderTexture.active;
                try { Canvas.ForceUpdateCanvases(); _camera.Render(); RenderTexture.active = _target; _readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); _readback.Apply(); return _readback.GetPixels32(); }
                finally { RenderTexture.active = previous; }
            }
            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(_root); UnityEngine.Object.DestroyImmediate(_cameraObject); UnityEngine.Object.DestroyImmediate(Material);
                UnityEngine.Object.DestroyImmediate(_referenceMaterial);
                _target.Release(); UnityEngine.Object.DestroyImmediate(_target); UnityEngine.Object.DestroyImmediate(_readback); EditorSceneManager.ClosePreviewScene(_scene);
            }
        }
    }
}
