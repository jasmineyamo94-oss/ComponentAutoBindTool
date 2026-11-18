using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PsdUiWorkbench.Core;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace PsdUiWorkbench.Infrastructure
{
    public sealed class PsdTmpPrefabExportResult
    {
        public string AssetPath { get; }
        public int TextLayers { get; }
        public int TextComponents { get; }
        public int PlaceholderRuns { get; }
        public int ImageCount { get; }
        public int KerningAdjustedLayers { get; }
        public int AdvanceCorrectedLayers { get; }
        public int FontSubstitutedLayers { get; }
        public IReadOnlyDictionary<int, string> SkippedTextLayers { get; }
        internal PsdTmpPrefabExportResult(string path, int layers, int components, int placeholders, int images,
            int kerningAdjustedLayers, int advanceCorrectedLayers, int fontSubstitutedLayers, IDictionary<int, string> skipped)
        {
            AssetPath = path; TextLayers = layers; TextComponents = components; PlaceholderRuns = placeholders; ImageCount = images;
            KerningAdjustedLayers = kerningAdjustedLayers;
            AdvanceCorrectedLayers = advanceCorrectedLayers;
            FontSubstitutedLayers = fontSubstitutedLayers;
            SkippedTextLayers = new ReadOnlyDictionary<int, string>(new Dictionary<int, string>(skipped));
        }
    }

    public static class PsdTmpPrefabExporter
    {
        public const float ThicknessReuseThreshold = 0.005f;
        public const float MaxOutlinePixelError = 0.1f;

        public static PsdTmpPrefabExportResult Export(PsdPrefabPlan plan, IReadOnlyDictionary<int, PsdTextSource> textSources,
            IReadOnlyDictionary<string, TMP_FontAsset> fonts, TMP_FontAsset defaultFont, string assetPath, Func<string, Sprite> resolveSprite,
            PsdTmpRenderingMode renderingMode = PsdTmpRenderingMode.OuterOutline,
            IReadOnlyDictionary<int, string> rejectedLayers = null)
        {
            if (plan == null || textSources == null || fonts == null) throw new ArgumentNullException();
            if (!Enum.IsDefined(typeof(PsdTmpRenderingMode), renderingMode)) throw new ArgumentOutOfRangeException(nameof(renderingMode));
            if (fonts.Values.Any(f => f == null || !EditorUtility.IsPersistent(f)))
                throw new InvalidOperationException("保存 Prefab 前必须提供持久化的 TMP 字库。");
            if (renderingMode == PsdTmpRenderingMode.OuterOutline &&
                fonts.Values.Any(f => (f.normalStyle != 0 || f.boldStyle != 0) && f.atlasPopulationMode != AtlasPopulationMode.Static))
                throw new InvalidOperationException("外描边度量副本仅支持静态字库，不能共享动态字库的可写图集。");
            var available = new HashSet<string>(fonts.Keys);
            var imageIds = new HashSet<int>(plan.Nodes.Where(n => n.IsImage).Select(n => n.Id));
            var skipped = rejectedLayers?.Where(pair => imageIds.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value) ?? new Dictionary<int, string>();
            var textPlans = new Dictionary<int, PsdTextLayoutPlan>();
            foreach (var node in plan.Nodes.Where(n => n.IsImage && textSources.ContainsKey(n.Id) && !skipped.ContainsKey(n.Id)))
            {
                var source = textSources[node.Id];
                if (source.LayerId != node.Id) throw new InvalidOperationException("文本源图层 ID 与导出节点不一致。");
                if (!PsdTextLayoutPlanner.TryBuild(source, node.Bounds, available, out var textPlan, out string reason))
                {
                    skipped[node.Id] = reason;
                    continue;
                }
                var run = textPlan.Runs[0];
                if (!fonts[run.Style.FontKey].HasCharacters(run.Text, out uint[] missing, false, false))
                {
                    skipped[node.Id] = "选定字库缺字：" + string.Join(",", missing.Select(c => c.ToString("X")));
                    continue;
                }
                textPlans.Add(node.Id, textPlan);
            }
            string output = null, resources = null;
            GameObject root = null;
            bool complete = false, resourcesCreated = false;
            try
            {
                output = PsdPrefabExporter.Export(plan, assetPath, resolveSprite).AssetPath;
                resources = Path.ChangeExtension(output, null) + " Materials";
                if (textPlans.Count > 0)
                {
                    if (AssetDatabase.IsValidFolder(resources) || File.Exists(resources)) throw new IOException("候选材质目录已存在。");
                    string parent = Path.GetDirectoryName(resources).Replace('\\', '/');
                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(resources)))) throw new IOException("候选材质目录创建失败。");
                    resourcesCreated = true;
                }
                var renderFonts = new Dictionary<string, TMP_FontAsset>();
                var metricCopies = new Dictionary<TMP_FontAsset, TMP_FontAsset>();
                foreach (string key in textPlans.Values.Select(p => p.Runs[0].Style.FontKey).Distinct())
                {
                    var font = fonts[key];
                    if (renderingMode == PsdTmpRenderingMode.OuterOutline && (font.normalStyle != 0 || font.boldStyle != 0))
                    {
                        if (!metricCopies.TryGetValue(font, out var copy))
                        {
                            // 仅复制度量资产并共享原图集；绝不修改调用方已有字库或材质。
                            copy = UnityEngine.Object.Instantiate(font);
                            try
                            {
                                copy.name = font.name + " Outline Metrics";
                                copy.normalStyle = 0;
                                copy.boldStyle = 0;
                                AssetDatabase.CreateAsset(copy, resources + "/Font Metrics " + (metricCopies.Count + 1) + ".asset");
                                metricCopies.Add(font, copy);
                            }
                            catch
                            {
                                if (!EditorUtility.IsPersistent(copy))
                                {
                                    copy.material = null;
                                    copy.atlasTextures = Array.Empty<Texture2D>();
                                    UnityEngine.Object.DestroyImmediate(copy);
                                }
                                throw;
                            }
                        }
                        font = copy;
                    }
                    renderFonts.Add(key, font);
                }
                var materials = new MaterialStore(resources, renderingMode);
                root = PrefabUtility.LoadPrefabContents(output);
                root.SetActive(false);
                var canvas = root.GetComponent<Canvas>();
                var scaler = root.GetComponent<UnityEngine.UI.CanvasScaler>();
                canvas.enabled = false; canvas.scaleFactor = 1; scaler.enabled = false;
                var rootRect = root.GetComponent<RectTransform>();
                rootRect.localScale = Vector3.one; rootRect.sizeDelta = new Vector2(plan.Width, plan.Height);
                var content = root.transform.GetChild(0);
                // 先按稳定计划映射完整层级，再添加文字子节点，避免重名和子节点索引变化。
                var transforms = new Dictionary<int, RectTransform>();
                foreach (var node in plan.Nodes)
                {
                    Transform p = node.ParentId.HasValue ? transforms[node.ParentId.Value] : content;
                    transforms.Add(node.Id, (RectTransform)p.GetChild(node.SiblingIndex));
                }
                // 保留层级容器以维持其他节点坐标和显隐，但被拒绝文本不输出任何可视内容。
                foreach (int id in skipped.Keys)
                {
                    var rect = transforms[id];
                    UnityEngine.Object.DestroyImmediate(rect.GetComponent<UnityEngine.UI.Image>());
                    UnityEngine.Object.DestroyImmediate(rect.GetComponent<CanvasRenderer>());
                }
                // TMP 排版需要可用 Canvas；隔离场景中关闭 Canvas 驱动，再临时激活已有节点。
                var activation = root.GetComponentsInChildren<Transform>(true).ToDictionary(t => t.gameObject, t => t.gameObject.activeSelf);
                foreach (var go in activation.Keys) go.SetActive(true);
                int components = 0, placeholders = 0, kerningAdjusted = 0, advanceCorrected = 0;
                foreach (var pair in textPlans)
                {
                    var rect = transforms[pair.Key];
                    var image = rect.GetComponent<UnityEngine.UI.Image>();
                    if (image == null) throw new InvalidOperationException("目标图层不是原始 Image 节点。");
                    UnityEngine.Object.DestroyImmediate(image);
                    UnityEngine.Object.DestroyImmediate(rect.GetComponent<CanvasRenderer>());
                    var built = PsdTmpTextBuilder.Build(rect, pair.Value,
                        run => renderFonts[run.Style.FontKey], materials.Resolve,
                        renderingMode != PsdTmpRenderingMode.Standard && renderingMode != PsdTmpRenderingMode.OuterOutline,
                        useComponentFillRgb: renderingMode == PsdTmpRenderingMode.OuterOutline);
                    components += built.Count; placeholders += pair.Value.Runs.Count(r => r.IsPlaceholder);
                    if (pair.Value.Runs.Any(r => r.Style.UseKerning) && built.All(t => !t.fontFeatures.Contains(UnityEngine.TextCore.OTL_FeatureTag.kern))) kerningAdjusted++;
                    if (pair.Value.Runs.Count == 1 && !pair.Value.Runs[0].IsPlaceholder && pair.Value.Runs[0].Style.FauxBold)
                    {
                        float baselineSpacing = pair.Value.Runs[0].Style.Tracking / 10 + PsdTmpTextBuilder.CandidateBoldAdvance - built[0].font.boldSpacing;
                        if (Mathf.Abs(built[0].characterSpacing - baselineSpacing) > 0.0001f) advanceCorrected++;
                    }
                }
                root.SetActive(false);
                foreach (var pair in activation.Where(p => p.Key != root)) pair.Key.SetActive(pair.Value);
                canvas.enabled = true; scaler.enabled = true;
                rootRect.localScale = Vector3.one;
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, output, out bool saved);
                if (!saved || prefab == null) throw new IOException("TMP 候选 Prefab 保存失败。");
                prefab.SetActive(true);
                PrefabUtility.SavePrefabAsset(prefab, out bool activated);
                if (!activated) throw new IOException("TMP 候选 Prefab 激活状态保存失败。");
                complete = true;
                return new PsdTmpPrefabExportResult(output, textPlans.Count, components, placeholders,
                    plan.ImageCount - textPlans.Count - skipped.Count, kerningAdjusted, advanceCorrected,
                    textPlans.Values.Count(p => p.FontSubstituted), skipped);
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
                if (!complete)
                {
                    if (output != null) AssetDatabase.DeleteAsset(output);
                    if (resourcesCreated) AssetDatabase.DeleteAsset(resources);
                }
            }
        }


        private sealed class MaterialStore
        {
            private readonly string _folder;
            private readonly PsdTmpRenderingMode _renderingMode;
            private readonly Dictionary<string, List<Material>> _materials = new Dictionary<string, List<Material>>();
            private int _materialCount;
            public MaterialStore(string folder, PsdTmpRenderingMode renderingMode) { _folder = folder; _renderingMode = renderingMode; }
            public Material Resolve(PsdTextRunPlan run, TMP_FontAsset font, float scale)
            {
                var style = run.Style;
                var material = new Material(font.material);
                try
                {
                    if (material.shader.name != "TextMeshPro/Mobile/Distance Field" && material.shader.name != "TextMeshPro/Distance Field" &&
                        material.shader.name != PsdTmpOuterStroke.UserShaderName)
                        throw new InvalidOperationException("候选导出仅支持已验证的 SDF Shader。");
                    foreach (string keyword in new[] {"OUTLINE_ON", "UNDERLAY_ON", "UNDERLAY_INNER", "GLOW_ON", "BEVEL_ON", "RATIOS_OFF"}) material.DisableKeyword(keyword);
                    foreach (string name in new[] {"_OutlineWidth", "_FaceDilate", "_OutlineSoftness", "_UnderlayDilate", "_UnderlaySoftness", "_Sharpness"})
                        if (material.HasProperty(name)) material.SetFloat(name, 0);
                    bool glyphLocalBold = !run.IsPlaceholder && PsdTmpGlyphLocalBold.IsEnabled(_renderingMode, style.FontKey);
                    float normal = run.IsPlaceholder ? 0 : font.normalStyle;
                    float bold = run.IsPlaceholder || glyphLocalBold ? 0 : _renderingMode != PsdTmpRenderingMode.Standard
                        ? PsdTmpOuterStroke.DefaultBoldWeight : PsdTmpTextBuilder.CandidateBoldWeight;
                    material.SetFloat("_WeightNormal", normal); material.SetFloat("_WeightBold", bold);
                    // 填充 RGB 交给 TMP 顶点色；PSD 填充 Alpha 仍独立于整段淡出和描边 Alpha。
                    material.SetColor("_FaceColor", _renderingMode == PsdTmpRenderingMode.OuterOutline
                        ? new Color(1, 1, 1, style.Color.A) : ColorOf(style.Color));
                    ShaderUtilities.UpdateShaderRatios(material);
                    if (_renderingMode != PsdTmpRenderingMode.Standard && !run.IsPlaceholder)
                    {
                        if (_renderingMode == PsdTmpRenderingMode.OuterOutline)
                            PsdTmpOuterStroke.ConfigureOuterOutline(material, style.StrokePixels, scale, bold);
                        else
                            PsdTmpOuterStroke.Configure(material, style.StrokePixels, scale, bold);
                        if (glyphLocalBold) PsdTmpGlyphLocalBold.Configure(material, font);
                        material.SetColor("_OutlineColor", ColorOf(style.StrokeColor));
                    }
                    else if (style.StrokePixels > 0)
                    {
                        float g = material.GetFloat("_GradientScale"), a0 = material.GetFloat("_ScaleRatioA");
                        float weight = (style.FauxBold ? bold : normal) / 4;
                        if (!TmpOutlineCompensation.TrySolve(g, style.StrokePixels / (2 * g * scale), weight, Mathf.Max(normal, bold) / 4, 0, a0, out float width, out float dilate))
                            throw new InvalidOperationException("当前字库距离场不足以表达外描边。");
                        material.SetFloat("_OutlineWidth", width); material.SetFloat("_FaceDilate", dilate);
                        material.SetColor("_OutlineColor", ColorOf(style.StrokeColor)); material.EnableKeyword("OUTLINE_ON");
                        ShaderUtilities.UpdateShaderRatios(material);
                        if (Mathf.Abs(width * material.GetFloat("_ScaleRatioA") - style.StrokePixels / (2 * g * scale)) > 0.0001f)
                            throw new InvalidOperationException("最终材质 RatioA 与外描边方程不一致。");
                    }
                    // 其他属性和关键字严格一致，仅正式外描边模式允许有限的 Thickness 差值。
                    material.name = "PSD TMP Style";
                    bool approximate = _renderingMode == PsdTmpRenderingMode.OuterOutline;
                    float thickness = material.GetFloat("_OutlineWidth");
                    if (approximate) material.SetFloat("_OutlineWidth", 0);
                    string key = font.GetInstanceID() + "|" + EditorJsonUtility.ToJson(material);
                    if (approximate) material.SetFloat("_OutlineWidth", thickness);
                    if (_materials.TryGetValue(key, out var candidates))
                    {
                        Material nearest = null;
                        float nearestDifference = float.PositiveInfinity;
                        foreach (var cached in candidates)
                        {
                            float difference = Mathf.Abs(cached.GetFloat("_OutlineWidth") - thickness);
                            bool reusable = difference == 0 || (approximate &&
                                difference < ThicknessReuseThreshold &&
                                difference * (material.GetFloat("_GradientScale") - 1) * scale <= MaxOutlinePixelError);
                            if (reusable && difference < nearestDifference)
                            {
                                nearest = cached;
                                nearestDifference = difference;
                            }
                        }
                        if (nearest != null)
                        {
                            UnityEngine.Object.DestroyImmediate(material);
                            return nearest;
                        }
                    }
                    // TMP 的预设检索先按字体名称首词搜索材质，再核对图集引用。
                    string prefix = new string(font.name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
                    material.name = prefix + " - Text Style " + (_materialCount + 1).ToString("D2");
                    string path = AssetDatabase.GenerateUniqueAssetPath(_folder + "/" + material.name + ".mat");
                    AssetDatabase.CreateAsset(material, path);
                    AssetDatabase.SaveAssetIfDirty(material);
                    if (candidates == null) _materials.Add(key, candidates = new List<Material>());
                    // 保留首次生成的代表值，不平均、不因后续请求而扩大同一簇的范围。
                    candidates.Add(material);
                    _materialCount++;
                    return material;
                }
                catch { if (!EditorUtility.IsPersistent(material)) UnityEngine.Object.DestroyImmediate(material); throw; }
            }
            private static Color ColorOf(PsdTextColor c) => new Color(c.R, c.G, c.B, c.A);
        }
    }
}
