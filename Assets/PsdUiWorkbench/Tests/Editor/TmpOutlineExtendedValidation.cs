using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace PsdUiWorkbench.Tests
{
    public static class TmpOutlineExtendedValidation
    {
        public const string OutputFolder = "Assets/PsdUiWorkbench/Documentation/TmpOutlineExtended";

        [Serializable]
        public sealed class FontCase
        {
            public string label, path, sample;

            public FontCase(string label, string path, string sample)
            {
                this.label = label;
                this.path = path;
                this.sample = sample;
            }
        }

        // 系统字库仅在本机读取，不复制进工程，也不随验证证据分发字体文件。
        private static readonly FontCase[] _catalog =
        {
            new FontCase("TOXIA", "Assets/AAGame/UI/Fonts/Toxia_FRE.ttf", "Hig8O"),
            new FontCase("Liberation Sans", "Assets/Third Party/ComponentAutoBindTool/Example/TextMesh Pro/Fonts/LiberationSans.ttf", "Hig8O"),
            new FontCase("Noto Sans CJK SC", "Assets/Third Party/ComponentAutoBindTool/Example/Fonts/NotoSansCJKsc-Regular.otf", "H永国鬱田"),
            new FontCase("Arial Regular", "C:/Windows/Fonts/arial.ttf", "Hig8O"),
            new FontCase("Arial Bold", "C:/Windows/Fonts/arialbd.ttf", "Hig8O"),
            new FontCase("Arial Italic", "C:/Windows/Fonts/ariali.ttf", "Hig8O"),
            new FontCase("Times New Roman", "C:/Windows/Fonts/times.ttf", "Hig8O"),
            new FontCase("Courier New", "C:/Windows/Fonts/cour.ttf", "Hig8O"),
            new FontCase("Segoe UI Light", "C:/Windows/Fonts/segoeuil.ttf", "Hig8O"),
            new FontCase("DengXian Light", "C:/Windows/Fonts/Dengl.ttf", "H永国鬱田"),
            new FontCase("DengXian Bold", "C:/Windows/Fonts/Dengb.ttf", "H永国鬱田"),
            new FontCase("SimSun face 0", "C:/Windows/Fonts/simsun.ttc", "H永国鬱田"),
            new FontCase("SimHei", "C:/Windows/Fonts/simhei.ttf", "H永国鬱田"),
            new FontCase("KaiTi", "C:/Windows/Fonts/simkai.ttf", "H永国鬱田"),
            new FontCase("Source Han Serif SC Heavy", "C:/Windows/Fonts/Source Han Serif SC Heavy (TrueType).ttf", "H永国田")
        };

        [Serializable]
        private sealed class BatchReport
        {
            public int formatVersion = 1, fontIndex;
            public string unity, graphicsDevice, colorSpace, generatedUtc, renderMode;
            public float sharpness;
            public FontCase source;
            public bool success;
            public double seconds;
            public List<string> diagnostics = new List<string>();
            public List<TmpOutlineValidation.Row> rows = new List<TmpOutlineValidation.Row>();
        }

        [Serializable]
        private sealed class BatchSummary
        {
            public bool success;
            public string path;
            public int rows, expressible, clipped, fallback, emptyFaceMask;
        }

        public static string RunBatch(int fontIndex, bool highQuality = false, float sharpness = 0)
        {
            if (fontIndex < 0 || fontIndex >= _catalog.Length)
                throw new ArgumentOutOfRangeException(nameof(fontIndex));

            var watch = Stopwatch.StartNew();
            int previewScenes = EditorSceneManager.previewSceneCount;
            var activeScene = EditorSceneManager.GetActiveScene();
            var activeTarget = RenderTexture.active;
            var source = _catalog[fontIndex];
            var mode = highQuality ? GlyphRenderMode.SDF16 : GlyphRenderMode.SDFAA;
            var report = new BatchReport
            {
                fontIndex = fontIndex, source = source, renderMode = mode.ToString(), sharpness = sharpness,
                unity = Application.unityVersion, graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                colorSpace = QualitySettings.activeColorSpace.ToString(), generatedUtc = DateTime.UtcNow.ToString("O")
            };
            Directory.CreateDirectory(OutputFolder);
            string suffix = sharpness == 0 ? "" : "-sharp" + (sharpness * 100).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            string path = OutputFolder + "/" + mode + "-" + fontIndex.ToString("D2") + suffix + ".json";
            try
            {
                if (!File.Exists(source.path)) throw new FileNotFoundException("本机不存在测试字库。", source.path);
                var matrix = new TmpOutlineValidation.Matrix { FontPath = source.path, Sample = source.sample, RenderMode = mode, Sharpness = sharpness };
                if (sharpness != 0)
                {
                    matrix.Paddings = new[] {12};
                    matrix.Spacings = new[] {100f};
                    matrix.SyntheticBold = new[] {false};
                    matrix.Strokes = new[] {1f};
                }
                else if (highQuality)
                {
                    matrix.Paddings = new[] {12};
                    matrix.Spacings = new[] {100f};
                    matrix.SyntheticBold = new[] {false};
                }
                else if (fontIndex == 6 || fontIndex == 8 || fontIndex == 11 || fontIndex == 13 || fontIndex == 14)
                {
                    matrix.EvidencePrefix = "TmpOutlineExtended/SDFAA-" + fontIndex.ToString("D2");
                }
                report.rows = TmpOutlineValidation.MeasureMatrix(matrix);
                if (report.rows.Any(r => !r.sourceFontMatches)) report.diagnostics.Add("检测到非源字体字形，不能作为该字体的验证结果。");
                if (report.rows.Any(r => r.frameClipped)) report.diagnostics.Add("渲染触及目标边缘，结果可能被裁剪。");
                if (report.rows.Any(r => Mathf.Abs(r.sourceScaleMax - r.sourceScaleMin) > 0.00001f))
                    report.diagnostics.Add("样本文字内部存在不同字形缩放，不能只使用首字符换算。");
            }
            catch (Exception exception)
            {
                report.diagnostics.Add(exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                if (EditorSceneManager.previewSceneCount != previewScenes || EditorSceneManager.GetActiveScene() != activeScene || RenderTexture.active != activeTarget)
                    report.diagnostics.Add("验证前后场景或渲染目标状态不一致。");
                report.seconds = watch.Elapsed.TotalSeconds;
                report.success = report.diagnostics.Count == 0 && report.rows.Count > 0;
                File.WriteAllText(path, JsonUtility.ToJson(report, true));
            }

            var valid = report.rows.Where(r => r.status.StartsWith("可表达")).ToArray();
            return JsonUtility.ToJson(new BatchSummary
            {
                success = report.success, path = path, rows = report.rows.Count, expressible = valid.Length,
                clipped = valid.Count(r => r.frameClipped), fallback = valid.Count(r => !r.sourceFontMatches),
                emptyFaceMask = valid.Count(r => r.baselineFaceMaskPixels == 0)
            });
        }
    }
}
