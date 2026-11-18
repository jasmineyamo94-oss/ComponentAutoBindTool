using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdSingleGlyphExportTests
    {
        [Test]
        public void DefaultExportSharesEqualMaterialsAcrossSizesWithoutRuntimeObjectsOrLayoutTextures()
        {
            string name = "SingleGlyph_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/PsdUiWorkbench/Tests/" + name;
            AssetDatabase.CreateFolder("Assets/PsdUiWorkbench/Tests", name);
            var font = TMP_Settings.defaultFontAsset;
            string original = EditorJsonUtility.ToJson(font.material);
            try
            {
                var nodes = Enumerable.Range(1, 4).Select(i => new PsdNode(i, null, "Layer" + i, false, true, true, false,
                    "sprite" + i, new PixelRect(i * 100, 10, 100, 80), i)).ToArray();
                var plan = PsdPrefabPlanner.Build(new PsdDocumentSnapshot("asset", 600, 200, nodes), new PsdVisibilityState());
                var black = new PsdTextColor(0, 0, 0, 1);
                var styles = new[]
                {
                    new PsdTextStyle("Known", 16, false, 100, PsdTextColor.White, 0, black),
                    new PsdTextStyle("Known", 48, true, 200, PsdTextColor.White, 0, black),
                    new PsdTextStyle("Known", 48, true, 200, new PsdTextColor(1, 0, 0, 1), 0, black),
                    new PsdTextStyle("Known", 48, true, 200, PsdTextColor.White, 2, black)
                };
                var sources = Enumerable.Range(1, 4).ToDictionary(i => i, i => new PsdTextSource(i, ((char)('A' + i - 1)).ToString(),
                    0, 0, PsdTextAlignment.Left, new[] {new PsdTextSourceSpan(0, 1, styles[i - 1])}));
                var sprite = AssetDatabase.LoadAllAssetsAtPath(PsdTmpCandidateExport.SourcePath).OfType<Sprite>().First();
                var exported = PsdTmpPrefabExporter.Export(plan, sources, new Dictionary<string, TMP_FontAsset> {{"Known", font}},
                    font, folder + "/simple.prefab", _ => sprite);
                AssetDatabase.ImportAsset(exported.AssetPath, ImportAssetOptions.ForceUpdate);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(exported.AssetPath);
                var texts = prefab.GetComponentsInChildren<TextMeshProUGUI>(true).ToDictionary(t => t.text);
                Assert.That(texts["A"].fontSharedMaterial, Is.SameAs(texts["B"].fontSharedMaterial), "字号、字距和字形加粗标记不应拆分相同最终材质。");
                Assert.That(texts["C"].fontSharedMaterial, Is.SameAs(texts["B"].fontSharedMaterial), "填充 RGB 应由 TMP 组件表达，不拆分材质。");
                Assert.That(texts["C"].color, Is.EqualTo(Color.red));
                Assert.That(texts["B"].color, Is.EqualTo(Color.white));
                Assert.That(texts["D"].fontSharedMaterial, Is.Not.SameAs(texts["B"].fontSharedMaterial), "描边不同不能合并。");
                Assert.That(texts["A"].fontSize, Is.EqualTo(16));
                Assert.That(texts["B"].fontSize, Is.EqualTo(48));
                Assert.That(texts["A"].characterSpacing, Is.EqualTo(10));
                Assert.That(texts["B"].characterSpacing, Is.EqualTo(20 + PsdTmpTextBuilder.CandidateBoldAdvance - font.boldSpacing));
                Assert.That(prefab.GetComponentsInChildren<MonoBehaviour>(true).Any(c => c != null && c.GetType().Namespace?.StartsWith("PsdUiWorkbench") == true), Is.False);
                foreach (var text in texts.Values)
                {
                    Assert.That(text.fontSharedMaterial.shader.name, Is.EqualTo(PsdTmpOuterStroke.UserShaderName));
                    Assert.That(text.fontSharedMaterial.passCount, Is.EqualTo(1));
                    Assert.That(text.fontSharedMaterial.HasProperty("_PsdGlyphData"), Is.False);
                }
                Assert.That(AssetDatabase.FindAssets("t:Material", new[] {folder}).Length, Is.EqualTo(2));
                Assert.That(AssetDatabase.FindAssets("t:Texture2D", new[] {folder}), Is.Empty);
                Assert.That(EditorJsonUtility.ToJson(font.material), Is.EqualTo(original));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void ProductionAssembliesDoNotReferenceArchiveOrShipWorkbenchRuntime()
        {
            var player = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Player);
            Assert.That(player.Any(a => a.name.StartsWith("PsdUiWorkbench", StringComparison.Ordinal)), Is.False);
            var editor = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Editor);
            foreach (string name in new[] {"PsdUiWorkbench.Core", "PsdUiWorkbench.Infrastructure", "PsdUiWorkbench.Presentation"})
                Assert.That(editor.Single(a => a.name == name).assemblyReferences.Any(a => a.name == "PsdUiWorkbench.Runtime"), Is.False, name);
        }

        [Test]
        public void RetiredNeighborModeIsRejectedBeforeAssetCreation()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PsdTextPrefabExport.Export("unused", new PsdVisibilityState(),
                "Assets/PsdUiWorkbench/Exports/should-not-exist.prefab", (PsdTmpRenderingMode)2));
        }
    }
}
