using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using PsdUiWorkbench.Presentation;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdTextMetadataTests
    {
        private const string Battle = "Assets/Psd/战斗-主菜单界面.psd";
        [Test]
        public void NativeReaderMatchesAuditedContentStylesAndPerRunTracking()
        {
            using var stream = File.OpenRead(Battle);
            var result = new PsdTextMetadataReader().Read(stream);
            Assert.That(result.Status, Is.EqualTo(PsdMetadataStatus.Success), result.Diagnostic);
            var reference = PsdTmpCandidateExport.ReadSources();
            Assert.That(result.Sources.Keys, Is.EquivalentTo(reference.Keys));
            foreach (var pair in reference)
            {
                var actual = result.Sources[pair.Key]; var expected = pair.Value;
                Assert.That(actual.Text, Is.EqualTo(expected.Text));
                Assert.That(actual.Alignment, Is.EqualTo(expected.Alignment));
                Assert.That(actual.BaselineX, Is.EqualTo(expected.BaselineX).Within(0.0001f));
                Assert.That(actual.BaselineY, Is.EqualTo(expected.BaselineY).Within(0.0001f));
                Assert.That(actual.Spans.Count, Is.EqualTo(expected.Spans.Count));
                for (int i = 0; i < actual.Spans.Count; i++)
                {
                    var a = actual.Spans[i]; var e = expected.Spans[i];
                    Assert.That((a.Start, a.Length), Is.EqualTo((e.Start, e.Length)));
                    Assert.That(a.Style.FontKey, Is.EqualTo(e.Style.FontKey));
                    Assert.That(a.Style.FontSize, Is.EqualTo(e.Style.FontSize).Within(0.0001f));
                    Assert.That(a.Style.Tracking, Is.EqualTo(e.Style.Tracking));
                    Assert.That(a.Style.FauxBold, Is.EqualTo(e.Style.FauxBold));
                    Assert.That(a.Style.Color, Is.EqualTo(e.Style.Color));
                    Assert.That(a.Style.StrokePixels, Is.EqualTo(e.Style.StrokePixels));
                    Assert.That(a.Style.StrokeColor, Is.EqualTo(e.Style.StrokeColor));
                }
            }
            Assert.That(result.Sources[1067].Spans[0].Style.Tracking, Is.EqualTo(200));
            Assert.That(result.Sources[1091].Spans[0].Style.Tracking, Is.EqualTo(100));
            Assert.That(result.Sources[1091].Spans[0].Style.UseKerning, Is.True);
            Assert.That(result.Sources[1091].ReferenceWidth, Is.EqualTo(232.86f).Within(0.02f));
            Assert.That(result.Sources.ContainsKey(947), Is.True, "未导入文本也缓存源信息。");
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void BoundedReaderSupportsPsdPsbAndRejectsAmbiguousIds(bool large, bool duplicate)
        {
            byte[] bytes = Fixture(large, duplicate);
            using (var stream = new MemoryStream(bytes))
            {
                var result = new PsdTextMetadataReader().Read(stream);
                Assert.That(result.Status, Is.EqualTo(duplicate ? PsdMetadataStatus.Invalid : PsdMetadataStatus.Success), result.Diagnostic);
                Assert.That(result.Sources.Count, Is.EqualTo(duplicate ? 0 : 1));
            }
            foreach (int cut in new[] {0, 25, 35, bytes.Length / 2, bytes.Length - 4})
            {
                using var stream = new MemoryStream(bytes, 0, cut);
                var result = new PsdTextMetadataReader().Read(stream);
                Assert.That(result.Status, Is.EqualTo(PsdMetadataStatus.Invalid), "截断位置：" + cut);
                Assert.That(result.Sources, Is.Empty);
            }
            bytes[24] = 0; bytes[25] = 3; // 头部仍为合法 RGB；未知 TySh 版本须按图层记录拒绝，不能保留 Image。
            int tysh = Find(bytes, Encoding.ASCII.GetBytes("8BIMTySh")) + 12;
            bytes[tysh + 1] = 99;
            using var invalid = new MemoryStream(bytes);
            var partial = new PsdTextMetadataReader().Read(invalid);
            Assert.That(partial.Status, Is.EqualTo(duplicate ? PsdMetadataStatus.Invalid : PsdMetadataStatus.Partial));
            Assert.That(partial.RejectedLayers.Count, Is.EqualTo(duplicate ? 0 : 1));
            Assert.That(partial.Sources, Is.Empty);
        }

        [TestCase(-100f)]
        [TestCase(0f)]
        [TestCase(100f)]
        [TestCase(200f)]
        public void TrackingChangesActualGlyphDistanceInOneThousandthEm(float tracking)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("字距几何验证", typeof(RectTransform), typeof(Canvas));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            root.GetComponent<Canvas>().enabled = false;
            try
            {
                var rect = (RectTransform)root.transform; rect.sizeDelta = new Vector2(200, 100);
                float Distance(float value)
                {
                    var style = new PsdTextStyle("Known", 31.5f, false, value, PsdTextColor.White, 0, PsdTextColor.White);
                    var source = new PsdTextSource(1, "AA", 50, 50, PsdTextAlignment.Left, new[] {new PsdTextSourceSpan(0, 2, style)});
                    var plan = PsdTextLayoutPlanner.Build(source, new PixelRect(0, 0, 200, 100), new HashSet<string> {"Known"});
                    var text = PsdTmpTextBuilder.Build(rect, plan, _ => TMP_Settings.defaultFontAsset, (_, font, __) => font.material).Single();
                    float distance = text.textInfo.characterInfo[1].origin - text.textInfo.characterInfo[0].origin;
                    UnityEngine.Object.DestroyImmediate(text.gameObject); return distance;
                }
                Assert.That(Distance(tracking) - Distance(0), Is.EqualTo(tracking * 31.5f / 1000).Within(0.0001f));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(scene); }
        }

        [TestCase(PsdTmpRenderingMode.Standard)]
        [TestCase(PsdTmpRenderingMode.OuterStrokeExperimental)]
        [TestCase(PsdTmpRenderingMode.OuterOutline)]
        public void CacheReimportRenameAndFormalWindowExportPreserveReferencesAndSource(PsdTmpRenderingMode renderingMode)
        {
            string folder = "Assets/PsdUiWorkbench/Tests/TextCache_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/PsdUiWorkbench/Tests", Path.GetFileName(folder));
            string path = folder + "/copy.psd"; PsdWorkbenchWindow window = null;
            int scenes = EditorSceneManager.previewSceneCount;
            try
            {
                Assert.That(AssetDatabase.CopyAsset(Battle, path), Is.True);
                var cache = AssetDatabase.LoadAllAssetsAtPath(path).OfType<PsdTextMetadataAsset>().Single();
                Assert.That(cache.Read().Sources.Count, Is.EqualTo(20));
                Assert.That(cache.Read().Sources[1091].Spans[0].Style.UseKerning, Is.True);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(cache, out string guid, out long local);
                string Ids() => string.Join(",", AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().Select(s =>
                { AssetDatabase.TryGetGUIDAndLocalFileIdentifier(s, out string g, out long id); return id; }).OrderBy(i => i));
                string ids = Ids();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var refreshed = AssetDatabase.LoadAllAssetsAtPath(path).OfType<PsdTextMetadataAsset>().Single();
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(refreshed, out string nextGuid, out long nextLocal);
                Assert.That((nextGuid, nextLocal), Is.EqualTo((guid, local))); Assert.That(Ids(), Is.EqualTo(ids));
                string renamed = folder + "/renamed.psd"; Assert.That(AssetDatabase.MoveAsset(path, renamed), Is.Empty); path = renamed;
                var sourceBytes = File.ReadAllBytes(path); var metaBytes = File.ReadAllBytes(path + ".meta");
                string defaults = EditorJsonUtility.ToJson(TMP_Settings.defaultFontAsset.material);
                window = ScriptableObject.CreateInstance<PsdWorkbenchWindow>(); window.CreateGUI(); window.OpenAsset(path);
                var result = window.ExportTextPrefabTo(folder + "/export.prefab", renderingMode);
                Assert.That((result.TextLayers, result.TextComponents, result.PlaceholderRuns, result.ImageCount), Is.EqualTo((17, 17, 0, 32)));
                Assert.That(result.FontSubstitutedLayers, Is.EqualTo(4));
                Assert.That(result.SkippedTextLayers, Is.EqualTo(2));
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.AssetPath);
                Assert.That(prefab.GetComponentsInChildren<TextMeshProUGUI>(true).Length, Is.EqualTo(17));
                var energy = prefab.GetComponentsInChildren<TextMeshProUGUI>(true).Single(t => t.text == "Energy Level");
                if (renderingMode != PsdTmpRenderingMode.Standard)
                    foreach (var text in prefab.GetComponentsInChildren<TextMeshProUGUI>(true).Where(t => t.name != "Placeholder"))
                    {
                        Assert.That(text.fontSharedMaterial.shader.name, Is.EqualTo(renderingMode == PsdTmpRenderingMode.OuterOutline
                            ? PsdTmpOuterStroke.UserShaderName : PsdTmpOuterStroke.ShaderName));
                        Assert.That(text.fontSharedMaterial.passCount, Is.EqualTo(1));
                        Assert.That(text.fontSharedMaterial.GetFloat("_FaceDilate"), Is.Zero);
                        Assert.That(text.fontSharedMaterial.GetFloat("_WeightBold"), Is.EqualTo(PsdTmpOuterStroke.DefaultBoldWeight));
                    }
                Assert.That(energy.fontFeatures.Contains(UnityEngine.TextCore.OTL_FeatureTag.kern), Is.False, "原稿宽度校验不能被源布尔开关覆盖。");
                foreach (var text in prefab.GetComponentsInChildren<TextMeshProUGUI>(true).Where(t => t.name == "Placeholder"))
                    Assert.That(text.fontFeatures.Contains(UnityEngine.TextCore.OTL_FeatureTag.kern), Is.False);
                Assert.That(prefab.GetComponentsInChildren<UnityEngine.UI.Image>(true).Length, Is.EqualTo(32));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(sourceBytes)); Assert.That(File.ReadAllBytes(path + ".meta"), Is.EqualTo(metaBytes));
                Assert.That(EditorJsonUtility.ToJson(TMP_Settings.defaultFontAsset.material), Is.EqualTo(defaults));
                Assert.That(Ids(), Is.EqualTo(ids)); Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
                PsdImporterMigration.Switch(path, false);
                Assert.Throws<InvalidOperationException>(() => PsdTextPrefabExport.Export(
                    AssetDatabase.AssetPathToGUID(path), new PsdVisibilityState(), folder + "/official.prefab"),
                    "缺失缓存时不能把未识别文本静默回落为 Image。");
                Assert.That(File.Exists(folder + "/official.prefab"), Is.False);
                Assert.That(AssetImporter.GetAtPath(path).GetType(), Is.EqualTo(typeof(UnityEditor.U2D.PSD.PSDImporter)));
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [TestCase(232.86f, false)]
        [TestCase(210.28f, true)]
        public void SourceWidthRejectsKerningThatMakesActualTextLookCrowded(float referenceWidth, bool expectedKerning)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("字偶距回归", typeof(RectTransform), typeof(Canvas));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            root.GetComponent<Canvas>().enabled = false;
            var font = TMP_FontAsset.CreateFontAsset(AssetDatabase.LoadAssetAtPath<Font>("Assets/AAGame/UI/Fonts/Toxia_FRE.ttf"),
                96, 24, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, false);
            try
            {
                Assert.That(font.TryAddCharacters("Energy Level", out string missing, true), Is.True, missing);
                ((RectTransform)root.transform).sizeDelta = new Vector2(400, 100);
                var style = new PsdTextStyle("TOXIA", 34, true, 100, PsdTextColor.White, 0, PsdTextColor.White, true);
                var source = new PsdTextSource(1091, "Energy Level", 0, 0, PsdTextAlignment.Left,
                    new[] {new PsdTextSourceSpan(0, 12, style)}, referenceWidth);
                var plan = PsdTextLayoutPlanner.Build(source, new PixelRect(0, 0, 400, 100), new HashSet<string> {"TOXIA"});
                var text = PsdTmpTextBuilder.Build((RectTransform)root.transform, plan, _ => font, (_, f, __) => f.material).Single();
                Assert.That(text.fontFeatures.Contains(UnityEngine.TextCore.OTL_FeatureTag.kern), Is.EqualTo(expectedKerning));
                float width = text.textInfo.characterInfo[text.textInfo.characterCount - 1].xAdvance - text.textInfo.characterInfo[0].origin;
                Assert.That(width, Is.EqualTo(referenceWidth).Within(1));
                Assert.That(text.characterSpacing + font.boldSpacing, Is.EqualTo(12.5f).Within(0.0001f));
                Assert.That(source.Spans[0].Style.UseKerning, Is.True, "不改写源开关或 Tracking。");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (font != null)
                {
                    UnityEngine.Object.DestroyImmediate(font.material);
                    foreach (var atlas in font.atlasTextures) UnityEngine.Object.DestroyImmediate(atlas);
                    UnityEngine.Object.DestroyImmediate(font);
                }
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static int Find(byte[] bytes, byte[] target)
        {
            for (int i = 0; i <= bytes.Length - target.Length; i++)
                if (target.Select((b, j) => bytes[i + j] == b).All(v => v)) return i;
            throw new InvalidDataException("验证样本缺少 TySh。");
        }
        private static byte[] Fixture(bool large, bool duplicate)
        {
            var battle = File.ReadAllBytes(Battle); int offset = Find(battle, Encoding.ASCII.GetBytes("8BIMTySh"));
            int size = (battle[offset + 8] << 24) | (battle[offset + 9] << 16) | (battle[offset + 10] << 8) | battle[offset + 11];
            var payload = battle.Skip(offset + 12).Take(size).ToArray();
            byte[] Bytes(Action<MemoryStream> write) { using var s = new MemoryStream(); write(s); return s.ToArray(); }
            void N(Stream s, long value, int n = 4) { for (int i = n - 1; i >= 0; i--) s.WriteByte((byte)((ulong)value >> (i * 8))); }
            void Raw(Stream s, byte[] data) => s.Write(data, 0, data.Length);
            void Key(Stream s, string key) => Raw(s, Encoding.ASCII.GetBytes(key));
            void Block(Stream s, string key, byte[] data) { Key(s, "8BIM"); Key(s, key); N(s, data.Length); Raw(s, data); if (data.Length % 2 != 0) s.WriteByte(0); }
            var records = Bytes(s =>
            {
                N(s, duplicate ? 2 : 1, 2);
                for (int i = 0; i < (duplicate ? 2 : 1); i++)
                {
                    Raw(s, new byte[16]); N(s, 0, 2); Key(s, "8BIM"); Key(s, "norm"); Raw(s, new byte[] {255, 0, 0, 0});
                    var extra = Bytes(e => { N(e, 0); N(e, 0); Raw(e, new byte[4]); Block(e, "TySh", payload); Block(e, "zzzz", new byte[] {7}); Block(e, "lyid", Bytes(b => N(b, 42))); });
                    N(s, extra.Length); Raw(s, extra);
                }
            });
            return Bytes(s =>
            {
                Key(s, "8BPS"); N(s, large ? 2 : 1, 2); Raw(s, new byte[6]); N(s, 3, 2); N(s, 100); N(s, 100); N(s, 8, 2); N(s, 3, 2); N(s, 0); N(s, 0);
                var mask = Bytes(m => { N(m, records.Length, large ? 8 : 4); Raw(m, records); N(m, 0); });
                N(s, mask.Length, large ? 8 : 4); Raw(s, mask);
            });
        }
    }
}
