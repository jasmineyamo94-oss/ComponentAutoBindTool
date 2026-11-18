using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using Object = UnityEngine.Object;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdMaterialReuseTests
    {
        [Test]
        public void ExportedMaterialsAreListedByTheActualTmpPresetSearch()
        {
            using var rig = new ExportRig();
            var texts = rig.Export(new[] { Style(0.3f), Style(0.4f) });
            var utility = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("TMPro.EditorUtilities.TMP_EditorUtility")).First(t => t != null);
            var search = utility.GetMethod("FindMaterialReferences",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(search, Is.Not.Null);
            var presets = (Material[])search.Invoke(null, new object[] { texts[0].font });
            foreach (var text in texts)
                Assert.That(presets, Does.Contain(text.fontSharedMaterial), "实际使用的材质必须出现在 TMP 的预设下拉列表中。");
        }

        [Test]
        public void NearbyThicknessesReuseTheFirstMaterialWithoutTransitiveDrift()
        {
            using var rig = new ExportRig();
            var texts = rig.Export(new[] { Style(0.3f), Style(0.304f), Style(0.308f) });
            Assert.That(texts[1].fontSharedMaterial, Is.SameAs(texts[0].fontSharedMaterial));
            Assert.That(texts[2].fontSharedMaterial, Is.Not.SameAs(texts[0].fontSharedMaterial), "不能按相邻请求值连锁扩大容差。");
            Assert.That(texts[0].fontSharedMaterial.GetFloat("_OutlineWidth"), Is.EqualTo(0.3f).Within(0.00001f), "复用不能回写或平均原材质的宽度。");
            Assert.That(texts.Select(t => t.fontSharedMaterial).Distinct().Count(), Is.EqualTo(2));
        }

        [Test]
        public void NormalizedThicknessToleranceCannotExceedTheReferencePixelBudget()
        {
            using var rig = new ExportRig();
            var texts = rig.Export(new[] { Style(0.3f), Style(0.304f, size: 400) });
            Assert.That(texts[1].fontSharedMaterial, Is.Not.SameAs(texts[0].fontSharedMaterial), "字号放大后 0.004 的差值约为 0.4 像素，不能合并。");
        }

        [Test]
        public void ThicknessOutsideToleranceAndZeroOutlineKeepSeparateMaterials()
        {
            using var rig = new ExportRig();
            var texts = rig.Export(new[] { Style(0.3f), Style(0.306f), Style(0), Style(0.001f) });
            Assert.That(texts[1].fontSharedMaterial, Is.Not.SameAs(texts[0].fontSharedMaterial));
            Assert.That(texts[3].fontSharedMaterial, Is.Not.SameAs(texts[2].fontSharedMaterial));
            Assert.That(texts[2].fontSharedMaterial.IsKeywordEnabled("OUTLINE_ON"), Is.False);
            Assert.That(texts[3].fontSharedMaterial.IsKeywordEnabled("OUTLINE_ON"), Is.True);
        }

        [Test]
        public void FillRgbAndWholeTextOpacityStayOnTheComponentWithoutMaterialCopies()
        {
            using var rig = new ExportRig();
            var texts = rig.Export(new[] { Style(0.3f, fill: new PsdTextColor(1, 0, 0, 1)), Style(0.3f, fill: new PsdTextColor(0, 0, 1, 1)) });
            Assert.That(texts[1].fontSharedMaterial, Is.SameAs(texts[0].fontSharedMaterial));
            Assert.That(texts[0].color, Is.EqualTo(Color.red));
            Assert.That(texts[1].color, Is.EqualTo(Color.blue));
            Assert.That(texts[0].fontSharedMaterial.GetColor("_FaceColor"), Is.EqualTo(Color.white));
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject instance = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(rig.Prefab, scene);
                var live = instance.GetComponentsInChildren<TextMeshProUGUI>(true).OrderBy(t => t.text).ToArray();
                var shared = live[0].fontSharedMaterial;
                live[0].color = new Color(1, 0, 0, 0.35f);
                foreach (var text in live) text.ForceMeshUpdate(true, true);
                Assert.That(live[0].fontSharedMaterial, Is.SameAs(shared));
                Assert.That(live[1].fontSharedMaterial, Is.SameAs(shared));
                Assert.That(live[0].textInfo.meshInfo[0].colors32[0].a, Is.EqualTo(89).Within(1));
                Assert.That(live[1].textInfo.meshInfo[0].colors32[0].a, Is.EqualTo(255));
                Assert.That(shared.GetColor("_OutlineColor").a, Is.EqualTo(1));
            }
            finally
            {
                if (instance != null) Object.DestroyImmediate(instance);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void IndependentFaceAlphaOutlineAlphaAndOutlineColorRemainDistinct()
        {
            using var rig = new ExportRig();
            var texts = rig.Export(new[]
            {
                Style(0.3f),
                Style(0.3f, fill: new PsdTextColor(1, 0, 0, 0.5f)),
                Style(0.3f, outline: new PsdTextColor(0, 0, 0, 0.5f)),
                Style(0.3f, outline: new PsdTextColor(1, 0, 0, 1))
            });
            Assert.That(texts.Select(t => t.fontSharedMaterial).Distinct().Count(), Is.EqualTo(4));
            Assert.That(texts[1].color.a, Is.EqualTo(1), "PSD 填充 Alpha 不能误作整段淡出的组件 Alpha。");
            Assert.That(texts[1].fontSharedMaterial.GetColor("_FaceColor").a, Is.EqualTo(0.5f));
            Assert.That(texts[1].fontSharedMaterial.GetColor("_OutlineColor").a, Is.EqualTo(1));
        }

        [Test]
        public void LegacyRenderingModeKeepsExactThicknessMatching()
        {
            using var rig = new ExportRig();
            var texts = rig.Export(new[] { Style(0.3f), Style(0.304f) }, PsdTmpRenderingMode.OuterStrokeExperimental);
            Assert.That(texts[1].fontSharedMaterial, Is.Not.SameAs(texts[0].fontSharedMaterial));
        }

        private static PsdTextStyle Style(float thickness, float size = 48, PsdTextColor? fill = null, PsdTextColor? outline = null)
        {
            // 夹具使用 96 点、24 padding 的字库；48 号字时宽度 0.3 对应外扩 3.6 像素。
            return new PsdTextStyle("Known", size, false, 0, fill ?? PsdTextColor.White,
                thickness * 24 * size / 96, outline ?? new PsdTextColor(0, 0, 0, 1));
        }

        private sealed class ExportRig : IDisposable
        {
            private readonly string _folder = "Assets/PsdUiWorkbench/Tests/MaterialReuse_" + Guid.NewGuid().ToString("N");
            private TMP_FontAsset _font;
            public GameObject Prefab { get; private set; }

            public ExportRig()
            {
                try
                {
                    AssetDatabase.CreateFolder("Assets/PsdUiWorkbench/Tests", System.IO.Path.GetFileName(_folder));
                    var source = AssetDatabase.LoadAssetAtPath<Font>("Assets/AAGame/UI/Fonts/Toxia_FRE.ttf");
                    _font = TMP_FontAsset.CreateFontAsset(source, 96, 24, GlyphRenderMode.SDFAA, 512, 512, AtlasPopulationMode.Dynamic, false);
                    Assert.That(_font.TryAddCharacters("0123456789", out string missing), Is.True, missing);
                    _font.atlasTexture.Apply(false, false);
                    _font.atlasPopulationMode = AtlasPopulationMode.Static;
                    _font.normalStyle = _font.boldStyle = 0;
                    AssetDatabase.CreateAsset(_font, _folder + "/Review Font.asset");
                    AssetDatabase.AddObjectToAsset(_font.material, _font);
                    foreach (var atlas in _font.atlasTextures) AssetDatabase.AddObjectToAsset(atlas, _font);
                    AssetDatabase.SaveAssetIfDirty(_font);
                }
                catch { Dispose(); throw; }
            }

            public TextMeshProUGUI[] Export(PsdTextStyle[] styles, PsdTmpRenderingMode mode = PsdTmpRenderingMode.OuterOutline)
            {
                var nodes = Enumerable.Range(0, styles.Length).Select(i => new PsdNode(i + 1, null, "Layer" + i,
                    false, true, true, false, "sprite" + i, new PixelRect(i * 100, 0, 100, 100), styles.Length - i)).ToArray();
                var plan = PsdPrefabPlanner.Build(new PsdDocumentSnapshot("fixture", styles.Length * 100, 100, nodes), new PsdVisibilityState());
                Assert.That(plan.Nodes.Select(n => n.Id), Is.EqualTo(Enumerable.Range(1, styles.Length)), "夹具按请求顺序创建代表材质。");
                var sources = Enumerable.Range(0, styles.Length).ToDictionary(i => i + 1, i => new PsdTextSource(i + 1,
                    i.ToString(), i * 100 + 50, 50, PsdTextAlignment.Center, new[] { new PsdTextSourceSpan(0, 1, styles[i]) }));
                var sprite = AssetDatabase.LoadAllAssetsAtPath(PsdTmpCandidateExport.SourcePath).OfType<Sprite>().First();
                var report = PsdTmpPrefabExporter.Export(plan, sources, new Dictionary<string, TMP_FontAsset> { { "Known", _font } },
                    null, _folder + "/sample.prefab", _ => sprite, mode);
                Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(report.AssetPath);
                return Prefab.GetComponentsInChildren<TextMeshProUGUI>(true).OrderBy(t => t.text).ToArray();
            }

            public void Dispose()
            {
                if (_font != null && !EditorUtility.IsPersistent(_font))
                {
                    Object.DestroyImmediate(_font.material);
                    foreach (var texture in _font.atlasTextures) Object.DestroyImmediate(texture);
                    Object.DestroyImmediate(_font);
                }
                AssetDatabase.DeleteAsset(_folder);
            }
        }
    }
}
