using System;

namespace PsdUiWorkbench.Core
{
    public static class PsdTextAdvanceCorrection
    {
        // 仅补偿小额字体度量差；更大误差不能靠字距掩盖字体或布局错误。
        public const float MaximumRelativeError = 0.02f;
        public static bool TryGetSpacingDelta(float referenceWidth, float measuredAdvance, float fontSize, int characterCount, out float percent)
        {
            percent = 0;
            if (!float.IsFinite(referenceWidth) || !float.IsFinite(measuredAdvance) || !float.IsFinite(fontSize) ||
                referenceWidth <= 0 || measuredAdvance <= 0 || fontSize <= 0 || characterCount < 2)
                return false;
            float difference = referenceWidth - measuredAdvance;
            if (Math.Abs(difference) > referenceWidth * MaximumRelativeError) return false;
            percent = difference * 100 / (characterCount * fontSize);
            return true;
        }
    }
}
