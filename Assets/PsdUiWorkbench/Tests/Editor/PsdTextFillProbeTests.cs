using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdTextFillProbeTests
    {
        [TestCase(1058)]
        [TestCase(948)]
        public void DirectionalEffectsKeepLayoutOpacityAndFaceWhenOutlineIsToggled(int layerId)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PsdTextFillProbe.PrefabPath);
            var texts = prefab.GetComponentsInChildren<TextMeshProUGUI>(true);
            var materials = texts.Select(t => t.fontSharedMaterial).Distinct().ToArray();
            var fonts = texts.Select(t => t.font).Distinct().ToArray();
            var materialValues = materials.Select(m => EditorJsonUtility.ToJson(m)).ToArray();
            var fontValues = fonts.Select(f => EditorJsonUtility.ToJson(f)).ToArray();
            var prefabBytes = File.ReadAllBytes(PsdTextFillProbe.PrefabPath);
            int scenes = EditorSceneManager.previewSceneCount;
            int temporaryFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Count(f => !EditorUtility.IsPersistent(f));
            string name = "effects-test-" + Guid.NewGuid().ToString("N");
            Texture2D outlined = null, faceOnly = null;
            Material material = null;
            try
            {
                var withOutline = JsonUtility.FromJson<PsdTextFillProbe.Report>(File.ReadAllText(
                    PsdTextFillProbe.CaptureEffects(name + "outline", layerId, true, true)));
                var withoutOutline = JsonUtility.FromJson<PsdTextFillProbe.Report>(File.ReadAllText(
                    PsdTextFillProbe.CaptureEffects(name + "face", layerId, true, true, outline: false)));
                Assert.That(withOutline.layoutPreserved && withoutOutline.layoutPreserved, Is.True);
                foreach (var report in new[] { withOutline, withoutOutline })
                {
                    Assert.That(report.after.fontStyle, Is.EqualTo(report.before.fontStyle));
                    Assert.That(report.after.anchoredPosition, Is.EqualTo(report.before.anchoredPosition));
                    Assert.That(report.after.glyphs.Select(g => g.originPsd), Is.EqualTo(report.before.glyphs.Select(g => g.originPsd)));
                    Assert.That(report.after.glyphs.Select(g => g.advancePsd), Is.EqualTo(report.before.glyphs.Select(g => g.advancePsd)));
                    Assert.That(report.after.characterSpacing, Is.EqualTo(report.before.characterSpacing));
                    Assert.That(report.after.characterHorizontalScale, Is.EqualTo(report.before.characterHorizontalScale));
                }
                material = new Material(Shader.Find(PsdUiWorkbench.Infrastructure.PsdTmpOuterStroke.ShaderName));
                EditorJsonUtility.FromJsonOverwrite(withOutline.sourceMaterialJson, material);
                float alpha = material.GetColor("_OutlineColor").a;
                float width = material.GetFloat("_OutlineWidth");
                EditorJsonUtility.FromJsonOverwrite(withOutline.probeMaterialJson, material);
                Assert.That(material.GetColor("_OutlineColor").a, Is.EqualTo(alpha));
                Assert.That(material.GetFloat("_OutlineWidth"), Is.EqualTo(width));
                Assert.That(material.GetFloat("_FaceDilate"), Is.Zero);
                if (layerId == 948) Assert.That(alpha, Is.EqualTo(0.8f).Within(0.0001f));

                outlined = new Texture2D(2, 2);
                faceOnly = new Texture2D(2, 2);
                Assert.That(outlined.LoadImage(File.ReadAllBytes(withOutline.png)), Is.True);
                Assert.That(faceOnly.LoadImage(File.ReadAllBytes(withoutOutline.png)), Is.True);
                var a = outlined.GetPixels32(); var b = faceOnly.GetPixels32();
                Assert.That(a.Any(p => p.g > 0), Is.True, "外描边必须实际绘制。");
                Assert.That(b.Any(p => p.g > 0), Is.False, "关闭描边后不得留下绿色描边。");
                // Hell 只取不与下一字描边相交的首 H；另一夹具为单字符。
                int left = layerId == 1058 ? 645 : 0, right = layerId == 1058 ? 685 : outlined.width;
                int top = layerId == 1058 ? 90 : 0, bottom = layerId == 1058 ? 150 : outlined.height;
                int maxDifference = 0, visible = 0;
                for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    int index = (outlined.height - 1 - y) * outlined.width + x;
                    maxDifference = Math.Max(maxDifference, Math.Abs(a[index].r - b[index].r));
                    if (a[index].r > 0) visible++;
                }
                Assert.That(visible, Is.GreaterThan(100), "必须比较真实可见字面。");
                Assert.That(maxDifference, Is.LessThanOrEqualTo(1), "开关描边不得膨胀字面；仅允许8位采样的一档误差。");
                Assert.That(materials.Select(m => EditorJsonUtility.ToJson(m)), Is.EqualTo(materialValues));
                Assert.That(fonts.Select(f => EditorJsonUtility.ToJson(f)), Is.EqualTo(fontValues));
                Assert.That(File.ReadAllBytes(PsdTextFillProbe.PrefabPath), Is.EqualTo(prefabBytes));
                Assert.That(Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Count(f => !EditorUtility.IsPersistent(f)), Is.EqualTo(temporaryFonts));
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
            }
            finally
            {
                if (outlined != null) UnityEngine.Object.DestroyImmediate(outlined);
                if (faceOnly != null) UnityEngine.Object.DestroyImmediate(faceOnly);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                foreach (string suffix in new[] { "outline", "face" })
                foreach (string extension in new[] { ".png", ".json" })
                {
                    string file = PsdStrokeVisualDiagnosis.Folder + name + suffix + extension;
                    if (File.Exists(file)) File.Delete(file);
                    if (File.Exists(file + ".meta")) File.Delete(file + ".meta");
                }
            }
        }

        [Test]
        public void SpacingProbeSeparatesCharacterAndWordAdvanceWithoutScalingGlyphs()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PsdTextFillProbe.PrefabPath);
            var texts = prefab.GetComponentsInChildren<TextMeshProUGUI>(true);
            var materials = texts.Select(t => t.fontSharedMaterial).Concat(texts.Select(t => t.font.material)).Distinct().ToArray();
            var materialValues = materials.Select(m => EditorJsonUtility.ToJson(m)).ToArray();
            var prefabBytes = File.ReadAllBytes(PsdTextFillProbe.PrefabPath);
            int scenes = EditorSceneManager.previewSceneCount;
            string name = "spacing-test-" + Guid.NewGuid().ToString("N");
            try
            {
                var source = JsonUtility.FromJson<PsdTextFillProbe.Report>(File.ReadAllText(PsdTextFillProbe.CaptureSpacing(name + "source")));
                var character = JsonUtility.FromJson<PsdTextFillProbe.Report>(File.ReadAllText(
                    PsdTextFillProbe.CaptureSpacing(name + "character", character: source.after.characterSpacing + 1)));
                var word = JsonUtility.FromJson<PsdTextFillProbe.Report>(File.ReadAllText(
                    PsdTextFillProbe.CaptureSpacing(name + "word", word: 1)));
                Assert.That(source.after.fontSize, Is.EqualTo(48));
                Assert.That(source.after.characterSpacing, Is.EqualTo(source.sourceTracking / 10));
                Assert.That(source.after.text, Does.Contain("  "), "标题夹具必须包含双空格，才能验证 Word 的逐空格累计。");
                foreach (var report in new[] {source, character, word})
                {
                    Assert.That(report.spacingProbe, Is.True);
                    Assert.That(report.after.fontStyle, Is.EqualTo("Normal"));
                    Assert.That(report.after.lineSpacing, Is.EqualTo(0));
                    Assert.That(report.after.paragraphSpacing, Is.EqualTo(0));
                    Assert.That(report.after.characterHorizontalScale, Is.EqualTo(1));
                    Assert.That(report.after.fontSize, Is.EqualTo(report.before.fontSize));
                    Assert.That(report.after.anchoredPosition, Is.EqualTo(report.before.anchoredPosition), "Spacing 探针不得整体挪动文字。");
                    Assert.That(report.after.fontFeatures, Is.EqualTo(report.before.fontFeatures));
                    Assert.That(report.after.glyphs.First(g => g.visible).originPsd, Is.EqualTo(source.after.glyphs.First(g => g.visible).originPsd));
                    Assert.That(report.after.glyphs.Select(g => g.scale), Is.EqualTo(source.after.glyphs.Select(g => g.scale)));
                    Assert.That(report.after.glyphs.Select(g => g.metricWidth), Is.EqualTo(source.after.glyphs.Select(g => g.metricWidth)));
                    Assert.That(report.after.glyphs.Select(g => g.metricHeight), Is.EqualTo(source.after.glyphs.Select(g => g.metricHeight)));
                }
                int spaces = 0;
                for (int i = 0; i < source.after.glyphs.Length; i++)
                {
                    var baseline = source.after.glyphs[i];
                    var characterGlyph = character.after.glyphs[i];
                    var wordGlyph = word.after.glyphs[i];
                    Assert.That(characterGlyph.originPsd.x - baseline.originPsd.x, Is.EqualTo(i * 0.48f).Within(0.001f));
                    Assert.That(characterGlyph.advancePsd.x - baseline.advancePsd.x, Is.EqualTo((i + 1) * 0.48f).Within(0.001f));
                    Assert.That(wordGlyph.originPsd.x - baseline.originPsd.x, Is.EqualTo(spaces * 0.48f).Within(0.001f));
                    if (char.IsWhiteSpace((char)baseline.unicode)) spaces++;
                    Assert.That(wordGlyph.advancePsd.x - baseline.advancePsd.x, Is.EqualTo(spaces * 0.48f).Within(0.001f));
                    Assert.That(characterGlyph.originPsd.y, Is.EqualTo(baseline.originPsd.y));
                    Assert.That(wordGlyph.originPsd.y, Is.EqualTo(baseline.originPsd.y));
                }
                Assert.That(File.ReadAllBytes(character.png), Is.Not.EqualTo(File.ReadAllBytes(source.png)));
                Assert.That(File.ReadAllBytes(word.png), Is.Not.EqualTo(File.ReadAllBytes(source.png)));
                Assert.That(materials.Select(m => EditorJsonUtility.ToJson(m)), Is.EqualTo(materialValues));
                Assert.That(File.ReadAllBytes(PsdTextFillProbe.PrefabPath), Is.EqualTo(prefabBytes));
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
            }
            finally
            {
                foreach (string suffix in new[] {"source", "character", "word"})
                foreach (string extension in new[] {".png", ".json"})
                {
                    string file = PsdStrokeVisualDiagnosis.Folder + name + suffix + extension;
                    if (File.Exists(file)) File.Delete(file);
                    if (File.Exists(file + ".meta")) File.Delete(file + ".meta");
                }
            }
        }

        [Test]
        public void DirectionalBoldProbeIsIdentityAtZeroAndChangesCoverageWithoutMovingLayout()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PsdTextFillProbe.PrefabPath);
            var texts = prefab.GetComponentsInChildren<TextMeshProUGUI>(true);
            var materials = texts.Select(t => t.fontSharedMaterial).Concat(texts.Select(t => t.font.material)).Distinct().ToArray();
            var materialValues = materials.Select(m => EditorJsonUtility.ToJson(m)).ToArray();
            var prefabBytes = File.ReadAllBytes(PsdTextFillProbe.PrefabPath);
            int temporaryFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Count(f => !EditorUtility.IsPersistent(f));
            int scenes = EditorSceneManager.previewSceneCount;
            string name = "directional-fill-test-" + Guid.NewGuid().ToString("N");
            try
            {
                var neutral = JsonUtility.FromJson<PsdTextFillProbe.Report>(File.ReadAllText(
                    PsdTextFillProbe.CaptureWithFont(name + "neutral", 1058, 96, 24, GlyphRenderMode.SDFAA, 0, meshBoldStyle: 0)));
                var zero = JsonUtility.FromJson<PsdTextFillProbe.Report>(File.ReadAllText(
                    PsdTextFillProbe.CaptureWithFont(name + "zero", 1058, 96, 24, GlyphRenderMode.SDFAA, 0,
                        meshBoldStyle: 0, directionalBoldX: 0, directionalBoldY: 0)));
                var directional = JsonUtility.FromJson<PsdTextFillProbe.Report>(File.ReadAllText(
                    PsdTextFillProbe.CaptureWithFont(name + "directional", 1058, 96, 24, GlyphRenderMode.SDFAA, 0,
                        meshBoldStyle: 0, directionalBoldX: 1.5f, directionalBoldY: 0)));
                Assert.That(File.ReadAllBytes(zero.png), Is.EqualTo(File.ReadAllBytes(neutral.png)),
                    "方向性 Shader 的零参数必须与相同字库、相同权重的原 Shader 输出完全一致。");
                Assert.That(File.ReadAllBytes(directional.png), Is.Not.EqualTo(File.ReadAllBytes(zero.png)),
                    "方向性增粗必须实际改变覆盖图，不能只修改报告或比较空白输出。");
                Assert.That(zero.directionalBoldEnabled && directional.directionalBoldEnabled, Is.True);
                Assert.That(neutral.directionalBoldEnabled, Is.False);
                foreach (var report in new[] {neutral, zero, directional})
                {
                    Assert.That(report.after.fontSize, Is.EqualTo(report.before.fontSize));
                    Assert.That(report.after.characterSpacing, Is.EqualTo(report.before.characterSpacing));
                    Assert.That(report.sourceTracking, Is.EqualTo(neutral.sourceTracking));
                    Assert.That(report.after.fontStyle, Is.EqualTo(report.before.fontStyle));
                    Assert.That((report.after.glyphs.First(g => g.visible).originPsd - report.before.glyphs.First(g => g.visible).originPsd).sqrMagnitude,
                        Is.LessThan(0.000001f));
                    Assert.That(report.after.glyphs.Select(g => g.originPsd), Is.EqualTo(neutral.after.glyphs.Select(g => g.originPsd)));
                    Assert.That(report.after.glyphs.Select(g => g.advancePsd), Is.EqualTo(neutral.after.glyphs.Select(g => g.advancePsd)));
                }
                Assert.That(materials.Select(m => EditorJsonUtility.ToJson(m)), Is.EqualTo(materialValues));
                Assert.That(File.ReadAllBytes(PsdTextFillProbe.PrefabPath), Is.EqualTo(prefabBytes));
                Assert.That(Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Count(f => !EditorUtility.IsPersistent(f)), Is.EqualTo(temporaryFonts));
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
            }
            finally
            {
                foreach (string suffix in new[] {"neutral", "zero", "directional"})
                foreach (string extension in new[] {".png", ".json"})
                {
                    string file = PsdStrokeVisualDiagnosis.Folder + name + suffix + extension;
                    if (File.Exists(file)) File.Delete(file);
                    if (File.Exists(file + ".meta")) File.Delete(file + ".meta");
                }
            }
        }

        [Test]
        public void GeneratedFontProbeAlignsFirstOriginAndDoesNotRetainTemporaryFont()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PsdTextFillProbe.PrefabPath);
            var texts = prefab.GetComponentsInChildren<TextMeshProUGUI>(true);
            var materials = texts.Select(t => t.fontSharedMaterial).Distinct().ToArray();
            var materialValues = materials.Select(m => EditorJsonUtility.ToJson(m)).ToArray();
            int temporaryFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Count(f => !EditorUtility.IsPersistent(f));
            int scenes = EditorSceneManager.previewSceneCount;
            string name = "generated-fill-test-" + Guid.NewGuid().ToString("N");
            try
            {
                string path = PsdTextFillProbe.CaptureWithFont(name, 1058, 48, 12, GlyphRenderMode.SDFAA_HINTED, 0, meshBoldStyle: 0);
                var report = JsonUtility.FromJson<PsdTextFillProbe.Report>(File.ReadAllText(path));
                Assert.That(report.generatedFont, Is.True);
                Assert.That(report.generatedSamplingPoint, Is.EqualTo(48));
                Assert.That(report.generatedRenderMode, Is.EqualTo("SDFAA_HINTED"));
                Assert.That(report.meshBoldStyleOverridden, Is.True);
                Assert.That(report.actualMeshBoldStyle, Is.EqualTo(0));
                Assert.That(report.after.fontSize, Is.EqualTo(report.before.fontSize));
                Assert.That(report.after.characterSpacing, Is.EqualTo(report.before.characterSpacing));
                Assert.That((report.after.glyphs.First(g => g.visible).originPsd - report.before.glyphs.First(g => g.visible).originPsd).sqrMagnitude,
                    Is.LessThan(0.000001f));
                Assert.That(report.unaligned.glyphs.Length, Is.EqualTo(report.after.glyphs.Length));
                Assert.That(materials.Select(m => EditorJsonUtility.ToJson(m)), Is.EqualTo(materialValues));
                Assert.That(Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Count(f => !EditorUtility.IsPersistent(f)), Is.EqualTo(temporaryFonts));
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
            }
            finally
            {
                foreach (string extension in new[] {".png", ".json"})
                {
                    string file = PsdStrokeVisualDiagnosis.Folder + name + extension;
                    if (File.Exists(file)) File.Delete(file);
                    if (File.Exists(file + ".meta")) File.Delete(file + ".meta");
                }
            }
        }

        [Test]
        public void WeightOnlyProbeChangesCoverageWithoutChangingLayoutOrSharedAssets()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PsdTextFillProbe.PrefabPath);
            var materials = prefab.GetComponentsInChildren<TextMeshProUGUI>(true).Select(t => t.fontSharedMaterial).Distinct().ToArray();
            var before = materials.Select(m => EditorJsonUtility.ToJson(m)).ToArray();
            var prefabBytes = File.ReadAllBytes(PsdTextFillProbe.PrefabPath);
            int scenes = EditorSceneManager.previewSceneCount;
            string name = "fill-test-" + Guid.NewGuid().ToString("N");
            try
            {
                var normal = JsonUtility.FromJson<PsdTextFillProbe.Report>(File.ReadAllText(PsdTextFillProbe.Capture(name + "normal", weight: 0)));
                var bold = JsonUtility.FromJson<PsdTextFillProbe.Report>(File.ReadAllText(PsdTextFillProbe.Capture(name + "bold")));
                Assert.That(normal.layoutPreserved && bold.layoutPreserved, Is.True);
                Assert.That(normal.layerId, Is.EqualTo(1058));
                Assert.That(normal.fontSourceGuid, Is.EqualTo(AssetDatabase.AssetPathToGUID("Assets/AAGame/UI/Fonts/Toxia_FRE.ttf")));
                Assert.That(normal.fontSourcePath, Is.EqualTo("Assets/AAGame/UI/Fonts/Toxia_FRE.ttf"));
                Assert.That(normal.after.text, Is.EqualTo("Hell"));
                Assert.That(normal.after.glyphs.Select(g => g.originPsd), Is.EqualTo(bold.after.glyphs.Select(g => g.originPsd)));
                Assert.That(normal.after.glyphs.Select(g => g.advancePsd), Is.EqualTo(bold.after.glyphs.Select(g => g.advancePsd)));
                Assert.That(File.ReadAllBytes(normal.png), Is.Not.EqualTo(File.ReadAllBytes(bold.png)), "两个字重必须产生真实的覆盖差异。");
                Assert.That(materials.Select(m => EditorJsonUtility.ToJson(m)), Is.EqualTo(before));
                Assert.That(File.ReadAllBytes(PsdTextFillProbe.PrefabPath), Is.EqualTo(prefabBytes));
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
            }
            finally
            {
                foreach (string mode in new[] {"normal", "bold"})
                foreach (string extension in new[] {".png", ".json"})
                {
                    string file = PsdStrokeVisualDiagnosis.Folder + name + mode + extension;
                    if (File.Exists(file)) File.Delete(file);
                    if (File.Exists(file + ".meta")) File.Delete(file + ".meta");
                }
            }
        }
    }
}
