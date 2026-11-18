using System;
using TMPro;
using UnityEngine;

namespace PsdUiWorkbench.Infrastructure
{
    // 参数来自当前 TOXIA 原稿对照，不对未验证字体外推。
    public static class PsdTmpGlyphLocalBold
    {
        public const string ShaderName = "TextMeshPro/Mobile/Distance Field - PSD Outer Stroke - Glyph Local Bold";

        public static bool IsEnabled(PsdTmpRenderingMode mode, string fontKey) =>
            mode == PsdTmpRenderingMode.GlyphLocalBoldExperimental && fontKey == "TOXIA";

        public static void Configure(Material material, TMP_FontAsset font)
        {
            if (material == null || font == null || font.boldStyle != 0)
                throw new InvalidOperationException("字形局部补偿需要独立生成且 boldStyle 为 0 的字库；不能修改已有字库。");
            var shader = Shader.Find(ShaderName);
            if (shader == null) throw new InvalidOperationException("未找到字形局部补偿 Shader。");
            material.shader = shader;
            // 48 UI 单位字号时总水平扩张 1.75，纵向每侧 0.25；仅用于当前 TOXIA 校准。
            material.SetFloat("_PsdBoldAtlasWidth", font.faceInfo.pointSize * 3.5f / 96f);
            material.SetFloat("_PsdBoldVerticalRadius", font.faceInfo.pointSize * 0.5f / 96f);
            material.SetFloat("_PsdBoldSampling", 1);
            material.SetFloat("_PsdBoldBias", 0.5f);
            material.SetFloat("_WeightBold", 0);
            material.SetFloat("_FaceDilate", 0);
            ShaderUtilities.UpdateShaderRatios(material);
        }
    }
}
