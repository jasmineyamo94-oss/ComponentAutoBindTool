using System;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.Core;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;

namespace PsdUiWorkbench.Infrastructure
{
    public static class PsdTmpTextBuilder
    {
        // 当前真实 PSD 对照得到的候选起点，不宣称是 Photoshop 伪粗体的通用参数。
        public const float CandidateBoldWeight = 0.425f;
        public const float CandidateBoldAdvance = 2.5f;

        public static IReadOnlyList<TextMeshProUGUI> Build(RectTransform root, PsdTextLayoutPlan plan,
            Func<PsdTextRunPlan, TMP_FontAsset> resolveFont, Func<PsdTextRunPlan, TMP_FontAsset, float, Material> resolveMaterial,
            bool matchSourceAdvance = false, bool useComponentFillRgb = false)
        {
            if (root == null || plan == null || resolveFont == null || resolveMaterial == null) throw new ArgumentNullException();
            if (plan.Runs.Count != 1 || plan.Runs[0].IsPlaceholder)
                throw new InvalidOperationException("每个文本层必须使用一个完整文本段，不允许占位或拆分 TMP。");
            var texts = new List<TextMeshProUGUI>();
            var measurementMaterials = new List<Material>();
            float advance = 0;
            try
            {
                foreach (var run in plan.Runs)
                {
                    var font = resolveFont(run);
                    if (font == null) throw new InvalidOperationException("没有可用的 TMP 字体。");
                    if (!font.HasCharacters(run.Text, out uint[] missing, false, false))
                        throw new InvalidOperationException("候选字库缺少本次导出所需字形：" + string.Join(",", missing.Select(c => c.ToString("X"))));
                    var go = new GameObject(run.IsPlaceholder ? "Placeholder" : "Text", typeof(RectTransform));
                    go.SetActive(false);
                    go.transform.SetParent(root, false);
                    var text = go.AddComponent<TextMeshProUGUI>(); texts.Add(text);
                    var measurementMaterial = new Material(font.material); measurementMaterials.Add(measurementMaterial);
                    // 先给同图集的独立材质，再赋字体；字体 setter 会立即读取并初始化材质。
                    text.fontSharedMaterial = measurementMaterial; text.font = font; text.text = run.Text;
                    text.richText = false; text.fontSize = run.Style.FontSize; text.fontStyle = run.Style.FauxBold ? FontStyles.Bold : FontStyles.Normal;
                    // 补偿共享字库自身的 boldSpacing，不修改 Font Asset。
                    text.characterSpacing = run.Style.Tracking / 10 + (run.Style.FauxBold ? CandidateBoldAdvance - font.boldSpacing : 0);
                    text.fontFeatures = run.Style.UseKerning ? new List<OTL_FeatureTag> { OTL_FeatureTag.kern } : new List<OTL_FeatureTag>();
                    text.enableAutoSizing = false; text.textWrappingMode = TextWrappingModes.NoWrap; text.overflowMode = TextOverflowModes.Overflow;
                    text.alignment = TextAlignmentOptions.BaselineLeft; text.extraPadding = true; text.raycastTarget = false;
                    text.color = Color.white;
                    var rect = text.rectTransform;
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.sizeDelta = root.sizeDelta; rect.localScale = Vector3.one; rect.localRotation = Quaternion.identity;
                    go.SetActive(true);
                    text.ForceMeshUpdate(true, true);
                    if (text.textInfo.characterCount == 0) throw new InvalidOperationException("TMP 没有生成文字段。");
                    var characters = text.textInfo.characterInfo.Take(text.textInfo.characterCount).ToArray();
                    var visible = characters.Where(c => c.isVisible).ToArray();
                    float scale = (visible.Length > 0 ? visible[0] : characters[0]).scale;
                    if (scale <= 0 || characters.Any(c => c.fontAsset != font) || visible.Any(c => Mathf.Abs(c.scale - scale) > 0.00001f))
                        throw new InvalidOperationException("候选段存在回退字体或不同字形缩放，不能共用同一描边材质。");
                    text.fontSharedMaterial = resolveMaterial(run, font, scale);
                    if (text.fontSharedMaterial == null) throw new InvalidOperationException("文字材质无法解析。");
                    if (useComponentFillRgb)
                        text.color = new Color(run.Style.Color.R, run.Style.Color.G, run.Style.Color.B, 1);
                    text.UpdateMeshPadding(); text.ForceMeshUpdate(true, true);
                    var first = text.textInfo.characterInfo[0]; var last = text.textInfo.characterInfo[text.textInfo.characterCount - 1];
                    rect.anchoredPosition = new Vector2(advance - first.origin, -first.baseLine);
                    advance += last.xAdvance - first.origin;
                }
                // AutoKerning 不是 TMP kern 的等价模式；只比较两个离散排版方案，
                // 以源排版框宽度校验，不修改 Tracking、字号、字重或字形缩放。
                if (plan.ReferenceWidth.HasValue && plan.Runs.All(r => !r.IsPlaceholder) && plan.Runs.Any(r => r.Style.UseKerning))
                {
                    float reference = plan.ReferenceWidth.Value;
                    float unkerned = 0;
                    foreach (var text in texts)
                    {
                        text.fontFeatures = new List<OTL_FeatureTag>(); text.ForceMeshUpdate(true, true);
                        var info = text.textInfo;
                        unkerned += info.characterInfo[info.characterCount - 1].xAdvance - info.characterInfo[0].origin;
                    }
                    bool useUnkerned = Mathf.Abs(unkerned - reference) + Mathf.Max(0.5f, reference * 0.005f) < Mathf.Abs(advance - reference);
                    advance = 0;
                    for (int i = 0; i < texts.Count; i++)
                    {
                        var text = texts[i];
                        if (!useUnkerned && plan.Runs[i].Style.UseKerning)
                        {
                            text.fontFeatures = new List<OTL_FeatureTag> { OTL_FeatureTag.kern };
                            text.ForceMeshUpdate(true, true);
                        }
                        var first = text.textInfo.characterInfo[0];
                        var last = text.textInfo.characterInfo[text.textInfo.characterCount - 1];
                        text.rectTransform.anchoredPosition = new Vector2(advance - first.origin, -first.baseLine);
                        advance += last.xAdvance - first.origin;
                    }
                }
                if (matchSourceAdvance && plan.ReferenceWidth.HasValue && plan.Runs.Count == 1 &&
                    !plan.Runs[0].IsPlaceholder && plan.Runs[0].Style.FauxBold &&
                    PsdTextAdvanceCorrection.TryGetSpacingDelta(plan.ReferenceWidth.Value, advance, texts[0].fontSize,
                        texts[0].textInfo.characterCount, out float spacingDelta))
                {
                    var text = texts[0];
                    text.characterSpacing += spacingDelta;
                    text.ForceMeshUpdate(true, true);
                    var first = text.textInfo.characterInfo[0];
                    var last = text.textInfo.characterInfo[text.textInfo.characterCount - 1];
                    text.rectTransform.anchoredPosition = new Vector2(-first.origin, -first.baseLine);
                    advance = last.xAdvance - first.origin;
                }
                var result = texts[0];
                // 使用 TMP 原生居中，运行时改字后也能围绕同一源基线锚点重新排版。
                result.alignment = plan.Alignment == PsdTextAlignment.Center
                    ? TextAlignmentOptions.Baseline : TextAlignmentOptions.BaselineLeft;
                result.ForceMeshUpdate(true, true);
                var origin = result.textInfo.characterInfo[0];
                result.rectTransform.anchoredPosition = new Vector2(
                    plan.Alignment == PsdTextAlignment.Center ? plan.BaselineX : plan.BaselineX - origin.origin,
                    plan.BaselineY - origin.baseLine);
                result.ForceMeshUpdate(true, true);
                return texts.AsReadOnly();
            }
            catch
            {
                foreach (var text in texts) if (text != null) UnityEngine.Object.DestroyImmediate(text.gameObject);
                throw;
            }
            finally
            {
                foreach (var material in measurementMaterials) UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
