using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TextCore.LowLevel;

namespace PsdUiWorkbench.Tests
{
    public sealed class TmpOutlineTests
    {
        [SetUp]
        public void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("本验证需要真实图形设备，不能在 -nographics 下衡量 Shader 输出。");
            }
        }

        [TestCase(0.5f)]
        [TestCase(1f)]
        [TestCase(2f)]
        public void CompensationPreservesFaceAndShadowAcrossUniformScales(float screenScale)
        {
            int sceneCount = EditorSceneManager.previewSceneCount;
            var activeScene = EditorSceneManager.GetActiveScene();
            var renderTarget = RenderTexture.active;
            var row = TmpOutlineValidation.Probe(screenScale: screenScale);

            Assert.That(row.status, Is.EqualTo("可表达"));
            Assert.That(row.faceErrorCentered, Is.GreaterThan(0.05f));
            Assert.That(row.faceErrorCompensated, Is.LessThan(0.0001f));
            Assert.That(row.faceBoundaryPixelsChanged, Is.Zero);
            Assert.That(row.shadowErrorCorrected, Is.LessThan(0.00001f));
            Assert.That(Mathf.Abs(row.outerPixels - 2), Is.LessThanOrEqualTo(0.25f));
            Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(sceneCount));
            Assert.That(EditorSceneManager.GetActiveScene(), Is.EqualTo(activeScene));
            Assert.That(RenderTexture.active, Is.EqualTo(renderTarget));
        }

        [Test]
        public void RatioChangeNeedsWeightAndBaselineDilateCompensation()
        {
            var row = TmpOutlineValidation.Probe(64, 32, 4.5f, 40, 0.4f, 0.08f);

            Assert.That(row.ratioA1, Is.LessThan(row.ratioA0 - 0.1f));
            Assert.That(row.faceErrorSimpleCompensation, Is.GreaterThan(0.04f));
            Assert.That(row.faceErrorCompensated, Is.LessThan(0.00001f));
            Assert.That(row.faceBoundaryPixelsChanged, Is.Zero);
            Assert.That(row.status, Does.Contain("阴影需更大Padding"));
        }

        [Test]
        public void StrokeOutsideDistanceRangeIsRejected()
        {
            var row = TmpOutlineValidation.Probe(stroke: 100);
            Assert.That(row.status, Is.EqualTo("需要更大Padding或采样调整"));
        }

        [TestCase(GlyphRenderMode.SDFAA)]
        [TestCase(GlyphRenderMode.SDF16)]
        public void ProjectFontMatrixPreservesContoursWithoutFallbackOrClipping(GlyphRenderMode mode)
        {
            var matrix = SmallMatrix();
            matrix.RenderMode = mode;
            int sceneCount = EditorSceneManager.previewSceneCount;
            var rows = TmpOutlineValidation.MeasureMatrix(matrix);

            Assert.That(rows.Count, Is.EqualTo(2));
            foreach (var row in rows)
            {
                Assert.That(row.renderMode, Is.EqualTo(mode.ToString()));
                Assert.That(row.sourceFontMatches, Is.True);
                Assert.That(row.sourceSha256.Length, Is.EqualTo(64));
                Assert.That(row.frameClipped, Is.False);
                Assert.That(row.baselineFaceMaskPixels, Is.GreaterThan(0));
                Assert.That(row.outlineEquationError, Is.LessThan(0.000001f));
                Assert.That(row.innerEquationError, Is.LessThan(0.000001f));
                Assert.That(row.faceErrorCompensated, Is.LessThan(0.00001f));
                Assert.That(row.faceBoundaryPixelsChanged, Is.Zero);
            }
            Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(sceneCount));
        }

        [Test]
        public void SharpeningMustAlsoReportChangesAgainstOriginalFace()
        {
            var matrix = SmallMatrix();
            matrix.Strokes = new[] {1f};
            matrix.Sharpness = 0.5f;
            var rows = TmpOutlineValidation.MeasureMatrix(matrix);
            foreach (var row in rows)
            {
                Assert.That(row.faceErrorCompensated, Is.LessThan(0.00001f));
                Assert.That(row.sharpnessBaselineError, Is.GreaterThan(0.001f));
                Assert.That(row.faceErrorToNeutral, Is.GreaterThan(0.001f));
            }
        }

        [Test]
        public void MissingGlyphRejectsSampleInsteadOfUsingFallback()
        {
            var matrix = SmallMatrix();
            matrix.Sample = "H鬱";
            int sceneCount = EditorSceneManager.previewSceneCount;
            var error = Assert.Throws<System.InvalidOperationException>(() => TmpOutlineValidation.MeasureMatrix(matrix));
            Assert.That(error.Message, Does.Contain("缺少测试字符"));
            Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(sceneCount));
        }

        private static TmpOutlineValidation.Matrix SmallMatrix()
        {
            return new TmpOutlineValidation.Matrix
            {
                FontPath = "Assets/Third Party/ComponentAutoBindTool/Example/TextMesh Pro/Fonts/LiberationSans.ttf",
                Sample = "Hig8O", Paddings = new[] {12}, FontSizes = new[] {32},
                Strokes = new[] {2f}, Spacings = new[] {100f}, SyntheticBold = new[] {false}
            };
        }
    }
}
