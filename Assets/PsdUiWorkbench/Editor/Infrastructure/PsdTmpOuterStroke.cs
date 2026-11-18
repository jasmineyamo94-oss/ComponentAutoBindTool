using System;
using TMPro;
using UnityEngine;

namespace PsdUiWorkbench.Infrastructure
{
    public enum PsdTmpRenderingMode
    {
        Standard = 0,
        OuterStrokeExperimental = 1,
        // 2 是已退役的邻字合成模式，保持无效，不能复用。
        GlyphLocalBoldExperimental = 3,
        OuterOutline = 4
    }
    // 外描边适配入口；字形局部补偿是单独的可选模式。
    public static class PsdTmpOuterStroke
    {
        public const string ShaderName = "TextMeshPro/Mobile/Distance Field - PSD Outer Stroke";
        public const string UserShaderName = "TextMeshPro/Mobile/Distance Field (Outer Outline)";
        // 当前 TOXIA 与屏幕导数抗锯齿的联合验证起点，不作为所有字体的通式。
        public const float DefaultBoldWeight = 0.35f;
        public static void Configure(Material material, float pixels, float glyphScale, float faceWeight, float faceEdge = 1, float outlineEdge = 1)
        {
            if (material == null || !float.IsFinite(pixels) || pixels < 0 || !float.IsFinite(glyphScale) || glyphScale <= 0 ||
                !float.IsFinite(faceWeight) || faceWeight < 0 || faceWeight > 1 || !float.IsFinite(faceEdge) || !float.IsFinite(outlineEdge) || faceEdge < 0.25f || faceEdge > 2 || outlineEdge < 0.25f || outlineEdge > 2)
                throw new ArgumentException("外描边实验参数无效。");
            var shader = Shader.Find(ShaderName);
            if (shader == null) throw new InvalidOperationException("未找到工作台外描边 Shader。");
            float g = material.GetFloat("_GradientScale");
            if (!float.IsFinite(g) || g <= 1) throw new InvalidOperationException("距离场 Gradient Scale 无效。");
            float ratio = (g - 1) / g;
            // 完整宽度同时供 TMP 的 GetPadding 计算，不能只预留半边描边的网格。
            float width = pixels / (g * glyphScale * ratio);
            if (width > 1 || Mathf.Max(material.GetFloat("_WeightNormal"), faceWeight) / 4 + width > 1)
                throw new InvalidOperationException("当前字库余量不足以表达外描边。");
            material.shader = shader;
            material.SetFloat("_WeightBold", faceWeight); material.SetFloat("_FaceDilate", 0);
            material.SetFloat("_OutlineWidth", width); material.SetFloat("_OutlineSoftness", 0);
            material.SetFloat("_PsdWidthConvention", 1);
            material.SetFloat("_PsdFaceEdge", faceEdge); material.SetFloat("_PsdOutlineEdge", outlineEdge);
            material.SetFloat("_PsdDerivativeAA", 1);
            if (pixels > 0) material.EnableKeyword("OUTLINE_ON"); else material.DisableKeyword("OUTLINE_ON");
            ShaderUtilities.UpdateShaderRatios(material);
        }

        // pixels 是 PSD 外扩像素；glyphScale 是字号到图集采样字号的比例（含字形缩放）。
        public static void ConfigureOuterOutline(Material material, float pixels, float glyphScale, float faceWeight)
        {
            if (material == null || !float.IsFinite(pixels) || pixels < 0 ||
                !float.IsFinite(glyphScale) || glyphScale <= 0 ||
                !float.IsFinite(faceWeight) || faceWeight < 0 || faceWeight > 1)
                throw new ArgumentException("外描边参数无效。");
            if (!material.HasProperty("_GradientScale") || !material.HasProperty("_WeightNormal"))
                throw new InvalidOperationException("外描边需要 TMP SDF 字体材质。");
            float g = material.GetFloat("_GradientScale");
            float normalWeight = material.GetFloat("_WeightNormal");
            if (!float.IsFinite(g) || g <= 1 || !float.IsFinite(normalWeight) || normalWeight < 0)
                throw new InvalidOperationException("距离场 Gradient Scale 或字重无效。");
            // SDF alpha 斜率为 1/(2G)；完整宽度供 TMP GetPadding 使用，Shader 渲染时减半。
            float width = pixels / ((g - 1) * glyphScale);
            float maxWeight = Mathf.Max(normalWeight, faceWeight) / 4;
            // 留在 RatioA 的未饱和区，同时拒绝超出图集距离场余量的请求，不能静默夹紧。
            if (!float.IsFinite(width) || width > 1 || maxWeight + width > 1)
                throw new InvalidOperationException("当前字库余量不足以表达外描边。");
            var shader = Shader.Find(UserShaderName);
            if (shader == null) throw new InvalidOperationException("未找到工作台正式外描边 Shader。");
            material.shader = shader;
            material.SetFloat("_WeightBold", faceWeight);
            material.SetFloat("_FaceDilate", 0);
            material.SetFloat("_OutlineMode", 1);
            material.SetFloat("_PsdWidthConvention", 1);
            material.SetFloat("_OutlineWidth", width);
            material.SetFloat("_OutlineSoftness", 0);
            // 新模式使用固定的 (G-1)/G 比例；清除旧材质可能残留的 RATIO_OFF。
            material.DisableKeyword("RATIOS_OFF");
            if (pixels > 0) material.EnableKeyword("OUTLINE_ON"); else material.DisableKeyword("OUTLINE_ON");
            ShaderUtilities.GetShaderPropertyIDs();
            ShaderUtilities.UpdateShaderRatios(material);
        }
    }
}
