using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdSingleTextTests
    {
        [Test]
        public void MissingMixedFontUsesAvailableFontForTheWholeText()
        {
            var fill = new PsdTextColor(0.7f, 0.6f, 0.5f, 1);
            var outline = new PsdTextColor(0, 0, 0, 0.8f);
            PsdTextStyle Style(string font) => new PsdTextStyle(font, 30, true, 200, fill, 2, outline);
            var source = new PsdTextSource(7, " 1/10  ", 125.25f, 223.75f, PsdTextAlignment.Center,
                new[]
                {
                    new PsdTextSourceSpan(0, 2, Style("TOXIA")),
                    new PsdTextSourceSpan(2, 1, Style("Rubik-Medium")),
                    new PsdTextSourceSpan(3, 4, Style("TOXIA"))
                }, 100);

            var plan = PsdTextLayoutPlanner.Build(source, new PixelRect(100, 200, 100, 40),
                new HashSet<string> { "TOXIA" });

            Assert.That(plan.Runs.Count, Is.EqualTo(1));
            var run = plan.Runs.Single();
            Assert.That(run.Text, Is.EqualTo(" 1/10  "));
            Assert.That(run.Style.FontKey, Is.EqualTo("TOXIA"));
            Assert.That(run.IsPlaceholder, Is.False);
            Assert.That(run.Style.FontSize, Is.EqualTo(30));
            Assert.That(run.Style.Tracking, Is.EqualTo(200));
            Assert.That(run.Style.FauxBold, Is.True);
            Assert.That(run.Style.Color, Is.EqualTo(fill));
            Assert.That(run.Style.StrokeColor, Is.EqualTo(outline));
            Assert.That(plan.BaselineX, Is.EqualTo(-24.75f));
            Assert.That(plan.BaselineY, Is.EqualTo(-3.75f));
            Assert.That(source.Spans[1].Style.FontKey, Is.EqualTo("Rubik-Medium"));
            Assert.That(plan.FontSubstituted, Is.True);
            Assert.That(plan.ReferenceWidth, Is.Null, "替代字体后不能强行拟合原混排宽度。");
        }

        [Test]
        public void AllAuditedMixedLayersNormalizeToOneCompleteToxiaText()
        {
            var sources = PsdTmpCandidateExport.ReadSources();
            foreach (int id in new[] { 936, 947, 1015, 1020, 1067 })
            {
                var source = sources[id];
                var plan = PsdTextLayoutPlanner.Build(source, new PixelRect(0, 0, 1080, 2400),
                    new HashSet<string> { "TOXIA" });
                Assert.That(plan.Runs.Single().Text, Is.EqualTo(source.Text));
                Assert.That(plan.Runs.Single().Style.FontKey, Is.EqualTo("TOXIA"));
                Assert.That(plan.FontSubstituted, Is.True);
            }
        }

        [Test]
        public void UnavailableFontAndDifferentRunStylesAreRejectedWithoutPlaceholders()
        {
            var white = PsdTextColor.White;
            var a = new PsdTextStyle("Known", 30, false, 100, white, 0, white);
            var b = new PsdTextStyle("Known", 31, false, 100, white, 0, white);
            var source = new PsdTextSource(1, "AB", 0, 0, PsdTextAlignment.Left,
                new[] { new PsdTextSourceSpan(0, 1, a), new PsdTextSourceSpan(1, 1, b) });
            Assert.That(PsdTextLayoutPlanner.TryBuild(source, new PixelRect(0, 0, 100, 40),
                new HashSet<string> { "Known" }, out var plan, out string reason), Is.False);
            Assert.That(plan, Is.Null);
            Assert.That(reason, Is.Not.Empty);
            var single = new PsdTextSource(2, "A", 0, 0, PsdTextAlignment.Left,
                new[] { new PsdTextSourceSpan(0, 1, a) });
            Assert.That(PsdTextLayoutPlanner.TryBuild(single, new PixelRect(0, 0, 100, 40),
                new HashSet<string>(), out plan, out reason), Is.False);
            Assert.That(plan, Is.Null);
        }

        [Test]
        public void ExportUsesOneEditableTextAndRemovesRejectedTextImages()
        {
            string folder = "Assets/PsdUiWorkbench/Tests/SingleText_" + System.Guid.NewGuid().ToString("N");
            var font = TMP_Settings.defaultFontAsset;
            string materialBefore = EditorJsonUtility.ToJson(font.material);
            float boldBefore = font.boldStyle;
            try
            {
                var nodes = Enumerable.Range(1, 4).Select(i => new PsdNode(i, null, "Layer" + i,
                    false, true, true, false, "sprite" + i, new PixelRect(i * 100, 10, 100, 80), i));
                var plan = PsdPrefabPlanner.Build(new PsdDocumentSnapshot("asset", 600, 200, nodes), new PsdVisibilityState());
                PsdTextStyle Style(string key) => new PsdTextStyle(key, 30, false, 100,
                    PsdTextColor.White, 2, new PsdTextColor(0, 0, 0, 0.8f));
                var sources = new Dictionary<int, PsdTextSource>
                {
                    { 1, new PsdTextSource(1, "1/10", 150, 50, PsdTextAlignment.Center,
                        new[] { new PsdTextSourceSpan(0, 1, Style("Known")),
                            new PsdTextSourceSpan(1, 1, Style("Missing")),
                            new PsdTextSourceSpan(2, 2, Style("Known")) }) },
                    { 2, new PsdTextSource(2, ".", 250, 50, PsdTextAlignment.Left,
                        new[] { new PsdTextSourceSpan(0, 1, Style("Missing")) }) }
                };
                var sprite = AssetDatabase.LoadAllAssetsAtPath(PsdTmpCandidateExport.SourcePath).OfType<Sprite>().First();
                var report = PsdTmpPrefabExporter.Export(plan, sources,
                    new Dictionary<string, TMP_FontAsset> { { "Known", font } }, null,
                    folder + "/sample.prefab", _ => sprite, PsdTmpRenderingMode.OuterOutline,
                    new Dictionary<int, string> { { 3, "不支持活动投影" } });
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(report.AssetPath);
                Assert.That(report.TextLayers, Is.EqualTo(1));
                Assert.That(report.TextComponents, Is.EqualTo(1));
                Assert.That(report.FontSubstitutedLayers, Is.EqualTo(1));
                Assert.That(report.SkippedTextLayers.Keys, Is.EquivalentTo(new[] { 2, 3 }));
                Assert.That(report.ImageCount, Is.EqualTo(1));
                var text = prefab.GetComponentsInChildren<TextMeshProUGUI>(true).Single();
                Assert.That(text.text, Is.EqualTo("1/10"));
                var renderFont = text.font;
                Assert.That(renderFont.atlasTexture, Is.SameAs(font.atlasTexture), "度量副本共享原图集，不新增布局纹理。");
                Assert.That(renderFont.normalStyle, Is.Zero);
                Assert.That(renderFont.boldStyle, Is.Zero);
                Assert.That(text.fontSharedMaterial.shader.name, Is.EqualTo(PsdTmpOuterStroke.UserShaderName));
                Assert.That(prefab.GetComponentsInChildren<UnityEngine.UI.Image>(true).Length, Is.EqualTo(1));
                var scene = EditorSceneManager.NewPreviewScene();
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                try
                {
                    text = instance.GetComponentInChildren<TextMeshProUGUI>(true);
                    text.text = "99/100";
                    text.ForceMeshUpdate(true, true);
                    Assert.That(text.textInfo.characterCount, Is.EqualTo(6));
                    Assert.That(text.textInfo.characterInfo.Take(6).All(c => c.fontAsset == renderFont), Is.True);
                    Assert.That(text.alignment, Is.EqualTo(TextAlignmentOptions.Baseline));
                    Assert.That(instance.GetComponentsInChildren<TextMeshProUGUI>(true).Length, Is.EqualTo(1));
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                    EditorSceneManager.ClosePreviewScene(scene);
                }
                Assert.That(EditorJsonUtility.ToJson(font.material), Is.EqualTo(materialBefore));
                Assert.That(font.boldStyle, Is.EqualTo(boldBefore));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void DynamicFontCannotCreateAnIndependentMetricsCopyOverTheSameWritableAtlas()
        {
            string name = "DynamicMetrics_" + System.Guid.NewGuid().ToString("N");
            string folder = "Assets/PsdUiWorkbench/Tests/" + name;
            TMP_FontAsset font = null;
            try
            {
                AssetDatabase.CreateFolder("Assets/PsdUiWorkbench/Tests", name);
                var source = AssetDatabase.LoadAssetAtPath<Font>("Assets/AAGame/UI/Fonts/Toxia_FRE.ttf");
                font = TMP_FontAsset.CreateFontAsset(source, 96, 24,
                    UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 256, 256, AtlasPopulationMode.Dynamic, false);
                Assert.That(font.TryAddCharacters("A", out string missing), Is.True, missing);
                font.atlasTexture.Apply(false, false);
                AssetDatabase.CreateAsset(font, folder + "/dynamic.asset");
                AssetDatabase.AddObjectToAsset(font.material, font);
                AssetDatabase.AddObjectToAsset(font.atlasTexture, font);
                AssetDatabase.SaveAssetIfDirty(font);
                var before = font.atlasTexture.GetPixels32();
                var node = new PsdNode(1, null, "Text", false, true, true, false, "sprite",
                    new PixelRect(0, 0, 100, 40), 0);
                var plan = PsdPrefabPlanner.Build(new PsdDocumentSnapshot("asset", 100, 40, new[] { node }), new PsdVisibilityState());
                var style = new PsdTextStyle("Known", 30, true, 0, PsdTextColor.White, 2, new PsdTextColor(0, 0, 0, 1));
                var texts = new Dictionary<int, PsdTextSource>
                {
                    { 1, new PsdTextSource(1, "A", 0, 0, PsdTextAlignment.Left,
                        new[] { new PsdTextSourceSpan(0, 1, style) }) }
                };
                var sprite = AssetDatabase.LoadAllAssetsAtPath(PsdTmpCandidateExport.SourcePath).OfType<Sprite>().First();
                Assert.Throws<System.InvalidOperationException>(() => PsdTmpPrefabExporter.Export(plan, texts,
                    new Dictionary<string, TMP_FontAsset> { { "Known", font } }, null, folder + "/output.prefab", _ => sprite));
                Assert.That(System.IO.File.Exists(folder + "/output.prefab"), Is.False);
                Assert.That(font.atlasTexture.GetPixels32(), Is.EqualTo(before));
            }
            finally
            {
                if (font != null && !EditorUtility.IsPersistent(font))
                {
                    Object.DestroyImmediate(font.material);
                    foreach (var texture in font.atlasTextures) Object.DestroyImmediate(texture);
                    Object.DestroyImmediate(font);
                }
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
