using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;

namespace PsdUiWorkbench.Tests
{
    // 纯填充诊断只修改隔离实例；默认仅改变 Shader 字重，保留真实导出的排版。
    public static class PsdTextFillProbe
    {
        public const string PrefabPath = "Assets/PsdUiWorkbench/Exports/战斗-主菜单界面_单字形共享材质.prefab";
        private const string SourcePath = "Assets/Psd/战斗-主菜单界面.psd";
        private const string FontPath = "Assets/AAGame/UI/Fonts/Toxia_FRE.ttf";

        [Serializable]
        public sealed class Glyph
        {
            public int index, unicode;
            public uint glyphIndex;
            public bool visible;
            public Vector2 originPsd, advancePsd, quadBottomLeftPsd, quadTopRightPsd;
            public float scale, metricWidth, metricHeight, metricAdvance;
        }

        [Serializable]
        public sealed class Layout
        {
            public string text, fontStyle;
            public float fontSize, characterSpacing, wordSpacing, lineSpacing, paragraphSpacing, characterHorizontalScale;
            public Vector2 anchoredPosition;
            public string[] fontFeatures;
            public Glyph[] glyphs;
        }

        [Serializable]
        public sealed class Report
        {
            public string sourcePath, sourceGuid, prefabPath, png, mode, note;
            public int layerId, width, height;
            public bool whiteCoverage, normalStyle, layoutPreserved;
            public float requestedWeight, sourceTracking;
            public bool sourceFauxBold;
            public string sourceFontKey, fontAssetPath, fontGuid, fontSourcePath, fontSourceGuid, fontSourceIdentityMethod, sourceMaterialJson, probeMaterialJson;
            public long fontLocalId;
            public bool generatedFont;
            public int sourceSamplingPoint, sourcePadding, generatedSamplingPoint, generatedPadding;
            public string sourceRenderMode, generatedRenderMode;
            public float sourceMeshBoldStyle, actualMeshBoldStyle;
            public bool meshBoldStyleOverridden;
            public bool directionalBoldEnabled;
            public float directionalBoldX, directionalBoldY;
            public float directionalBias = 1;
            public bool effectsProbe, maskColors, outlineEnabled;
            public bool glyphSpaceExpansion;
            public bool spacingProbe, characterSpacingOverridden;
            public string requestedKerning;
            public Vector2 alignmentOffsetPsd, alignmentOffsetAnchored;
            public Layout before, unaligned, after;
        }

        public static string Capture(string name, int layerId = 1058, float weight = 0.35f,
            bool whiteCoverage = true, bool normalStyle = false)
        {
            return CaptureInternal(name, layerId, weight, whiteCoverage, normalStyle, null);
        }

        public static string CaptureSpacing(string name, int layerId = 1057, float? character = null,
            float word = 0, bool? kerning = null, bool whiteCoverage = true)
        {
            if ((character.HasValue && !float.IsFinite(character.Value)) || !float.IsFinite(word))
                throw new ArgumentException("Spacing 探针参数必须是有限值。");
            return CaptureInternal(name, layerId, 0, whiteCoverage, true, null,
                new SpacingOptions { Character = character, Word = word, Kerning = kerning });
        }

        private sealed class SpacingOptions
        {
            public float? Character;
            public float Word;
            public bool? Kerning;
        }

        // 回到真实粗体排版，保留正式描边参数。方向量仅供参考分辨率验证，不写入生产材质。
        public static string CaptureEffects(string name, int layerId, bool directional,
            bool maskColors = false, float directionAt48 = 1.5f, bool outline = true, float bias = 1, bool glyphSpace = false)
        {
            if (!float.IsFinite(directionAt48) || directionAt48 < 0 || directionAt48 > 3)
                throw new ArgumentException("方向性验证参数必须在 0～3 之间。");
            if (!float.IsFinite(bias) || bias < 0 || bias > 1)
                throw new ArgumentException("方向性左右分配必须在 0～1 之间。");
            return CaptureInternal(name, layerId, directional ? 0 : PsdTmpOuterStroke.DefaultBoldWeight,
                false, false, directional ? new GeneratedFontOptions
                {
                    Point = 96, Padding = 24, Mode = GlyphRenderMode.SDFAA, MeshBoldStyle = 0,
                    DirectionalBoldX = directionAt48, DirectionalBoldY = 0
                } : null, null, new EffectsOptions { MaskColors = maskColors, Outline = outline, Bias = bias, GlyphSpace = glyphSpace });
        }

        private sealed class EffectsOptions
        {
            public bool MaskColors, Outline;
            public float Bias;
            public bool GlyphSpace;
        }

        public static string CaptureWithFont(string name, int layerId, int point, int padding,
            GlyphRenderMode renderMode, float weight = 0.35f, bool whiteCoverage = true, bool normalStyle = false,
            float? meshBoldStyle = null, float? directionalBoldX = null, float? directionalBoldY = null)
        {
            if (point < 8 || point > 512 || padding < 1 || padding > 128)
                throw new ArgumentException("临时字库采样点与 padding 超出隔离验证范围。");
            if (renderMode != GlyphRenderMode.SDFAA && renderMode != GlyphRenderMode.SDFAA_HINTED)
                throw new ArgumentException("临时字体探针仅验证 SDFAA 与 SDFAA_HINTED。");
            if (meshBoldStyle.HasValue && (!float.IsFinite(meshBoldStyle.Value) || meshBoldStyle.Value < 0 || meshBoldStyle.Value > 1))
                throw new ArgumentException("临时字库的网格粗体样式必须在 0～1 之间。");
            if (directionalBoldX.HasValue != directionalBoldY.HasValue)
                throw new ArgumentException("方向性增粗探针必须同时提供 X 和 Y 参数。");
            if (directionalBoldX.HasValue &&
                (!float.IsFinite(directionalBoldX.Value) || !float.IsFinite(directionalBoldY.Value) ||
                 directionalBoldX.Value < 0 || directionalBoldX.Value > 3 || directionalBoldY.Value < 0 || directionalBoldY.Value > 3))
                throw new ArgumentException("方向性增粗探针参数必须为 0～3 的有限值。");
            if (directionalBoldX.HasValue && (meshBoldStyle != 0 || weight != 0))
                throw new ArgumentException("方向性增粗探针要求 meshBoldStyle=0 且 Shader 字重=0，避免重复增粗。");
            return CaptureInternal(name, layerId, weight, whiteCoverage, normalStyle,
                new GeneratedFontOptions
                {
                    Point = point, Padding = padding, Mode = renderMode, MeshBoldStyle = meshBoldStyle,
                    DirectionalBoldX = directionalBoldX, DirectionalBoldY = directionalBoldY
                });
        }

        private sealed class GeneratedFontOptions
        {
            public int Point, Padding;
            public GlyphRenderMode Mode;
            public float? MeshBoldStyle;
            public float? DirectionalBoldX, DirectionalBoldY;
        }

        private static string CaptureInternal(string name, int layerId, float weight,
            bool whiteCoverage, bool normalStyle, GeneratedFontOptions generated, SpacingOptions spacing = null, EffectsOptions effects = null)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.IndexOfAny(new[] {'/', '\\', ':'}) >= 0)
                throw new ArgumentException("探针名称必须是不含目录的文件名。");
            if (!float.IsFinite(weight) || weight < 0 || weight > 1) throw new ArgumentOutOfRangeException(nameof(weight));
            string png = PsdStrokeVisualDiagnosis.Folder + name + ".png";
            string json = Path.ChangeExtension(png, ".json");
            if (File.Exists(png) || File.Exists(json)) throw new IOException("拒绝覆盖已有填充探针证据。");
            var source = new UnityPsdDocumentSource();
            string guid = AssetDatabase.AssetPathToGUID(SourcePath);
            var loaded = source.Load(guid);
            if (loaded.Document == null) throw new InvalidOperationException(loaded.Diagnostic);
            var plan = PsdPrefabPlanner.Build(loaded.Document, new PsdVisibilityState());
            var metadata = AssetDatabase.LoadAllAssetsAtPath(SourcePath).OfType<PsdTextMetadataAsset>().Single().Read();
            if (!metadata.Sources.TryGetValue(layerId, out var textSource) || textSource.Spans.Any(s => s.Style.FontKey != "TOXIA"))
                throw new InvalidOperationException("此隔离探针仅接受已有真实 TOXIA 字库的纯文字层，不使用缺字体占位。");
            var report = new Report
            {
                sourcePath = SourcePath, sourceGuid = guid, prefabPath = PrefabPath, png = png,
                layerId = layerId, width = plan.Width, height = plan.Height, requestedWeight = weight,
                whiteCoverage = whiteCoverage, normalStyle = normalStyle,
                mode = normalStyle ? "真正 FontStyles.Normal；记录其排版变化，不与同布局实验混用" : "固定原字体样式与排版，仅控制 Shader 字重",
                note = "黑底；关闭所有图像、其他文字、描边及阴影。像素值用于覆盖率诊断，不是视觉还原率。",
                sourceFontKey = textSource.Spans[0].Style.FontKey, sourceTracking = textSource.Spans[0].Style.Tracking,
                sourceFauxBold = textSource.Spans[0].Style.FauxBold
            };
            if (generated != null)
            {
                report.generatedFont = true;
                report.mode = "隔离生成字体；仅整体对齐首个可见字符 origin/baseLine，后续 advance 差异单独记录";
                report.generatedSamplingPoint = generated.Point;
                report.generatedPadding = generated.Padding;
                report.generatedRenderMode = generated.Mode.ToString();
                report.directionalBoldEnabled = generated.DirectionalBoldX.HasValue;
                report.directionalBoldX = generated.DirectionalBoldX ?? 0;
                report.directionalBoldY = generated.DirectionalBoldY ?? 0;
            }
            if (spacing != null)
            {
                report.spacingProbe = true;
                report.characterSpacingOverridden = spacing.Character.HasValue;
                report.requestedKerning = spacing.Kerning.HasValue ? (spacing.Kerning.Value ? "启用 kern" : "关闭 kern") : "保留原 kern 设置";
                report.mode = "普通字重 Spacing 探针：默认 Character=源 Tracking/10、Word/Line/Paragraph=0、Horizontal Scale=1；不进行位置补偿";
            }
            if (effects != null)
            {
                report.effectsProbe = true;
                report.maskColors = effects.MaskColors;
                report.outlineEnabled = effects.Outline;
                report.directionalBias = effects.Bias;
                report.glyphSpaceExpansion = effects.GlyphSpace;
                report.mode = "真实粗体排版与原描边：固定所有字符 origin/advance，不平移、不拉伸；方向量按字号/48缩放";
                report.note = effects.MaskColors ? "黑底；红=字面覆盖，绿=外描边覆盖；只在参考分辨率验证。" : "统一灰底；保留原字面色及外描边色；只在参考分辨率验证。";
            }
            Material copy = null;
            TMP_FontAsset temporaryFont = null;
            try
            {
                PsdTmpCandidateExport.Capture(PrefabPath, png, root =>
                {
                    var content = (RectTransform)root.transform.GetChild(0);
                    var targets = MapPlan(root, plan);
                    if (!targets.TryGetValue(layerId, out var target)) throw new InvalidOperationException("目标图层没有导出节点。");
                    var texts = target.GetComponentsInChildren<TextMeshProUGUI>(true);
                    if (texts.Length != 1 || texts[0].text != textSource.Text)
                        throw new InvalidOperationException("目标文字段与源 ID 不符；无法安全复用当前 Prefab。");
                    var text = texts[0];
                    report.fontSourcePath = ResolveSourceFont(text.font, out report.fontSourceGuid, out report.fontSourceIdentityMethod);
                    if (report.fontSourceGuid != AssetDatabase.AssetPathToGUID(FontPath))
                        throw new InvalidOperationException("目标 TMP 字库并非预期的 TOXIA 源文件。");
                    foreach (var obj in root.scene.GetRootGameObjects())
                        if (obj.TryGetComponent<Camera>(out var camera))
                            camera.backgroundColor = effects != null && !effects.MaskColors ? new Color(31 / 255f, 31 / 255f, 31 / 255f, 1) : Color.black;
                    foreach (var image in root.GetComponentsInChildren<UnityEngine.UI.Image>(true)) image.enabled = false;
                    foreach (var other in root.GetComponentsInChildren<TextMeshProUGUI>(true))
                    {
                        other.enabled = other == text;
                        // 截图宿主随后会对全部 TMP 强制更新，禁用对象可防止非目标文字重新提交网格。
                        if (other != text) other.gameObject.SetActive(false);
                    }
                    for (Transform current = text.transform; current != null; current = current.parent) current.gameObject.SetActive(true);
                    Canvas.ForceUpdateCanvases();
                    text.ForceMeshUpdate(true, true);
                    report.before = ReadLayout(text, content, plan);
                    report.sourceSamplingPoint = Mathf.RoundToInt(text.font.faceInfo.pointSize);
                    report.sourcePadding = text.font.atlasPadding;
                    report.sourceRenderMode = text.font.atlasRenderMode.ToString();
                    report.sourceMeshBoldStyle = text.font.boldStyle;
                    report.actualMeshBoldStyle = text.font.boldStyle;
                    report.fontAssetPath = AssetDatabase.GetAssetPath(text.font);
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(text.font, out report.fontGuid, out report.fontLocalId))
                        throw new InvalidOperationException("目标字体没有持久化资产身份。");
                    var original = text.fontSharedMaterial;
                    report.sourceMaterialJson = EditorJsonUtility.ToJson(original);
                    copy = new Material(original);
                    if (generated != null && generated.DirectionalBoldX.HasValue)
                    {
                        var shader = Shader.Find("TextMeshPro/Mobile/Distance Field - PSD Outer Stroke - Directional Bold Probe");
                        if (shader == null) throw new InvalidOperationException("方向性增粗实验 Shader 尚未导入。");
                        copy.shader = shader;
                        if (!copy.HasProperty("_PsdProbeBoldX") || !copy.HasProperty("_PsdProbeBoldY"))
                            throw new InvalidOperationException("方向性增粗实验 Shader 缺少所需参数。");
                        float directionScale = effects == null ? 1 : (effects.GlyphSpace ? generated.Point : text.fontSize) / 48f;
                        report.directionalBoldX = generated.DirectionalBoldX.Value * directionScale;
                        report.directionalBoldY = generated.DirectionalBoldY.Value * directionScale;
                        copy.SetFloat("_PsdProbeBoldX", report.directionalBoldX);
                        copy.SetFloat("_PsdProbeBoldY", report.directionalBoldY);
                        copy.SetFloat("_PsdProbeBoldBias", effects == null ? 1 : effects.Bias);
                        copy.SetFloat("_PsdProbeGlyphSpace", effects != null && effects.GlyphSpace ? 1 : 0);
                    }
                    if (generated != null)
                    {
                        var previousFont = text.font;
                        temporaryFont = TMP_FontAsset.CreateFontAsset(AssetDatabase.LoadAssetAtPath<Font>(report.fontSourcePath),
                            generated.Point, generated.Padding, generated.Mode, 2048, 2048, AtlasPopulationMode.Dynamic, false);
                        if (temporaryFont == null) throw new InvalidOperationException("临时字体生成失败。");
                        temporaryFont.normalStyle = previousFont.normalStyle;
                        temporaryFont.boldStyle = previousFont.boldStyle;
                        // 单独控制 TMP 网格生成使用的粗体 padding，不改 FontStyles.Bold 或 boldSpacing。
                        if (generated.MeshBoldStyle.HasValue) temporaryFont.boldStyle = generated.MeshBoldStyle.Value;
                        report.actualMeshBoldStyle = temporaryFont.boldStyle;
                        report.meshBoldStyleOverridden = generated.MeshBoldStyle.HasValue;
                        temporaryFont.normalSpacingOffset = previousFont.normalSpacingOffset;
                        temporaryFont.boldSpacing = previousFont.boldSpacing;
                        if (!temporaryFont.TryAddCharacters(text.text, out string missing, true))
                            throw new InvalidOperationException("临时同源字体缺少字符：" + missing);
                        temporaryFont.atlasTexture.Apply(false, false);
                        temporaryFont.atlasPopulationMode = AtlasPopulationMode.Static;
                        copy.SetTexture("_MainTex", temporaryFont.atlasTexture);
                        foreach (string property in new[] {"_TextureWidth", "_TextureHeight", "_GradientScale"})
                            copy.SetFloat(property, temporaryFont.material.GetFloat(property));
                        // 字体与材质都只属于隔离实例，不改原 Font Asset 或源字体文件。
                        text.fontSharedMaterial = copy;
                        text.font = temporaryFont;
                    }
                    if (effects == null)
                    {
                        foreach (string keyword in new[] {"OUTLINE_ON", "UNDERLAY_ON", "UNDERLAY_INNER", "GLOW_ON", "BEVEL_ON"}) copy.DisableKeyword(keyword);
                        foreach (string property in new[] {"_OutlineWidth", "_OutlineSoftness", "_UnderlayDilate", "_UnderlaySoftness", "_FaceDilate", "_WeightNormal"})
                            if (copy.HasProperty(property)) copy.SetFloat(property, 0);
                    }
                    else
                    {
                        if (copy.IsKeywordEnabled("UNDERLAY_ON") || copy.IsKeywordEnabled("UNDERLAY_INNER"))
                            throw new InvalidOperationException("真实样本探针暂不验证阴影，不能将投影混入外描边结果。");
                        if (!effects.Outline)
                        {
                            copy.DisableKeyword("OUTLINE_ON");
                            copy.SetFloat("_OutlineWidth", 0);
                        }
                        if (effects.MaskColors)
                        {
                            copy.SetColor("_FaceColor", new Color(1, 0, 0, original.GetColor("_FaceColor").a));
                            copy.SetColor("_OutlineColor", new Color(0, 1, 0, original.GetColor("_OutlineColor").a));
                            text.color = Color.white;
                        }
                    }
                    copy.SetFloat("_WeightBold", weight);
                    if (whiteCoverage)
                    {
                        copy.SetColor("_FaceColor", Color.white);
                        text.color = Color.white;
                    }
                    text.fontSharedMaterial = copy;
                    if (normalStyle) text.fontStyle &= ~FontStyles.Bold;
                    if (spacing != null)
                    {
                        text.fontStyle = FontStyles.Normal;
                        text.characterSpacing = spacing.Character ?? textSource.Spans[0].Style.Tracking / 10;
                        text.wordSpacing = spacing.Word;
                        text.lineSpacing = 0;
                        text.paragraphSpacing = 0;
                        text.characterHorizontalScale = 1;
                        text.enableAutoSizing = false;
                        if (spacing.Kerning.HasValue)
                        {
                            var features = text.fontFeatures.ToList();
                            features.RemoveAll(f => f == OTL_FeatureTag.kern);
                            if (spacing.Kerning.Value) features.Add(OTL_FeatureTag.kern);
                            text.fontFeatures = features;
                        }
                    }
                    text.UpdateMeshPadding();
                    text.ForceMeshUpdate(true, true);
                    Canvas.ForceUpdateCanvases();
                    if (generated != null && effects == null)
                    {
                        report.unaligned = ReadLayout(text, content, plan);
                        var firstBefore = report.before.glyphs.First(g => g.visible);
                        var firstAfter = report.unaligned.glyphs.First(g => g.visible);
                        if (firstBefore.index != firstAfter.index || firstBefore.unicode != firstAfter.unicode)
                            throw new InvalidOperationException("临时字体首个可见字符发生变化，不能安全对齐。");
                        report.alignmentOffsetPsd = firstBefore.originPsd - firstAfter.originPsd;
                        var worldDelta = content.TransformVector(new Vector3(report.alignmentOffsetPsd.x, -report.alignmentOffsetPsd.y, 0));
                        report.alignmentOffsetAnchored = text.transform.parent.InverseTransformVector(worldDelta);
                        text.rectTransform.anchoredPosition += report.alignmentOffsetAnchored;
                        text.ForceMeshUpdate(true, true);
                        Canvas.ForceUpdateCanvases();
                    }
                    report.after = ReadLayout(text, content, plan);
                    if (spacing != null)
                    {
                        // 只检查而不自动挪动文字，以免隐藏 Spacing 或字体样式造成的实际排版差异。
                        var firstBefore = report.before.glyphs.First(g => g.visible);
                        var firstAfter = report.after.glyphs.First(g => g.visible);
                        if (firstBefore.index != firstAfter.index || firstBefore.unicode != firstAfter.unicode ||
                            (firstBefore.originPsd - firstAfter.originPsd).sqrMagnitude > 0.000001f)
                            throw new InvalidOperationException("Spacing 探针改变了首字符原点或基线；未做自动位置补偿，停止输出。");
                    }
                    report.layoutPreserved = SameLayout(report.before, report.after);
                    if (effects != null && !report.layoutPreserved)
                        throw new InvalidOperationException("效果探针改变了字符原点或 advance；未对齐结果，拒绝保存。");
                    if (generated == null && !normalStyle && !report.layoutPreserved)
                        throw new InvalidOperationException("Shader 字重探针意外改变字符原点或 advance，不能作为固定排版对照。");
                    report.probeMaterialJson = EditorJsonUtility.ToJson(copy);
                });
                File.WriteAllText(json, JsonUtility.ToJson(report, true));
                return json;
            }
            finally
            {
                if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
                // TMP_FontAsset.OnDestroy 会清理该非持久化字库自身的材质和图集。
                if (temporaryFont != null) UnityEngine.Object.DestroyImmediate(temporaryFont);
            }
        }

        private static string ResolveSourceFont(TMP_FontAsset font, out string sourceGuid, out string method)
        {
            if (font.sourceFontFile != null)
            {
                string path = AssetDatabase.GetAssetPath(font.sourceFontFile);
                sourceGuid = AssetDatabase.AssetPathToGUID(path);
                method = "TMP_FontAsset.sourceFontFile 持久化引用";
                if (string.IsNullOrEmpty(sourceGuid)) throw new InvalidOperationException("TMP 源字体引用没有持久化 GUID。");
                return path;
            }
            // Static 字库会移除运行时 Font 引用，但 Editor 保存源字体 GUID；只读该身份，不按名称猜测。
            using var serialized = new SerializedObject(font);
            var property = serialized.FindProperty("m_SourceFontFileGUID");
            if (property == null || property.propertyType != SerializedPropertyType.String || string.IsNullOrEmpty(property.stringValue))
                throw new InvalidOperationException("Static TMP 字库缺少 Editor 源字体 GUID，不能验证字体身份。");
            sourceGuid = property.stringValue;
            method = "TMP_FontAsset 序列化 m_SourceFontFileGUID（Static 字库）";
            string sourcePath = AssetDatabase.GUIDToAssetPath(sourceGuid);
            if (string.IsNullOrEmpty(sourcePath) || AssetDatabase.LoadAssetAtPath<Font>(sourcePath) == null)
                throw new InvalidOperationException("TMP 字库保存的源字体 GUID 无法解析到项目 Font。");
            return sourcePath;
        }

        private static Dictionary<int, RectTransform> MapPlan(GameObject root, PsdPrefabPlan plan)
        {
            if (root.transform.childCount != 1) throw new InvalidOperationException("Prefab 不符合固定 Content 结构。");
            var content = root.transform.GetChild(0);
            var result = new Dictionary<int, RectTransform>();
            foreach (var node in plan.Nodes)
            {
                Transform parent = node.ParentId.HasValue ? result[node.ParentId.Value] : content;
                if (node.SiblingIndex >= parent.childCount) throw new InvalidOperationException("Prefab 层级与源导出计划不一致。");
                var rect = parent.GetChild(node.SiblingIndex) as RectTransform;
                // 名称和矩形只用于拒绝过期或手改资产；身份映射始终来自计划 ID 与 sibling 路径。
                if (rect == null || rect.name != node.Name ||
                    (rect.anchoredPosition - new Vector2(node.LocalX, node.LocalY)).sqrMagnitude > 0.0001f ||
                    (rect.sizeDelta - new Vector2(node.Bounds.Width, node.Bounds.Height)).sqrMagnitude > 0.0001f)
                    throw new InvalidOperationException("Prefab 名称或矩形与当前计划不符，请先重新生成隔离验证资产。");
                result.Add(node.Id, rect);
            }
            return result;
        }

        private static Layout ReadLayout(TextMeshProUGUI text, RectTransform content, PsdPrefabPlan plan)
        {
            Vector2 Psd(Vector3 local)
            {
                var p = content.InverseTransformPoint(text.transform.TransformPoint(local));
                return new Vector2(p.x + plan.Width * 0.5f, plan.Height * 0.5f - p.y);
            }
            return new Layout
            {
                text = text.text, fontStyle = text.fontStyle.ToString(), fontSize = text.fontSize,
                characterSpacing = text.characterSpacing, anchoredPosition = text.rectTransform.anchoredPosition,
                wordSpacing = text.wordSpacing, lineSpacing = text.lineSpacing, paragraphSpacing = text.paragraphSpacing,
                characterHorizontalScale = text.characterHorizontalScale,
                fontFeatures = text.fontFeatures.Select(f => f.ToString()).ToArray(),
                glyphs = text.textInfo.characterInfo.Take(text.textInfo.characterCount).Select(c => new Glyph
                {
                    index = c.index, unicode = c.character, glyphIndex = c.textElement.glyphIndex, visible = c.isVisible,
                    originPsd = Psd(new Vector3(c.origin, c.baseLine, 0)), advancePsd = Psd(new Vector3(c.xAdvance, c.baseLine, 0)),
                    quadBottomLeftPsd = Psd(c.bottomLeft), quadTopRightPsd = Psd(c.topRight), scale = c.scale,
                    metricWidth = c.textElement.glyph.metrics.width, metricHeight = c.textElement.glyph.metrics.height,
                    metricAdvance = c.textElement.glyph.metrics.horizontalAdvance
                }).ToArray()
            };
        }

        private static bool SameLayout(Layout before, Layout after)
        {
            return before.text == after.text && before.fontSize == after.fontSize &&
                before.characterSpacing == after.characterSpacing && before.glyphs.Length == after.glyphs.Length &&
                before.glyphs.Zip(after.glyphs, (a, b) => a.unicode == b.unicode &&
                    (a.originPsd - b.originPsd).sqrMagnitude < 0.000001f &&
                    (a.advancePsd - b.advancePsd).sqrMagnitude < 0.000001f).All(same => same);
        }
    }
}
