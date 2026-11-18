using System;

namespace PsdUiWorkbench.Infrastructure
{
    // 标准 SDF Shader、Ratios 启用、Outline Softness=0 时的两个解析分支。
    public static class TmpOutlineCompensation
    {
        public static bool TrySolve(float gradient, float thresholdShift, float weight, float maxWeight, float baselineDilate, float baselineRatio,
            out float width, out float dilate)
        {
            width = dilate = 0;
            if (!float.IsFinite(gradient) || gradient <= 1 || !float.IsFinite(thresholdShift) || thresholdShift < 0 ||
                !float.IsFinite(weight) || !float.IsFinite(maxWeight) || maxWeight < weight || !float.IsFinite(baselineDilate) || !float.IsFinite(baselineRatio) || baselineRatio <= 0)
                return false;
            float r = (gradient - 1) / gradient;
            float baseline = (weight + baselineDilate) * baselineRatio;
            float a = r;
            if (maxWeight + (2 * thresholdShift + baseline) / a - weight > 1)
            {
                float denominator = maxWeight - weight;
                if (denominator <= 0.000001f) return false;
                a = (r - 2 * thresholdShift - baseline) / denominator;
            }
            if (a <= 0 || !float.IsFinite(a)) return false;
            width = thresholdShift / a;
            dilate = (thresholdShift + baseline) / a - weight;
            float actual = r / Math.Max(1, maxWeight + dilate + width);
            return width >= 0 && width <= 1 && dilate >= -1 && dilate <= 1 && Math.Abs(width * actual - thresholdShift) < 0.0001f;
        }
    }
}
