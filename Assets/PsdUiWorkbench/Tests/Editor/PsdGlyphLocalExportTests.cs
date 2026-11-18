using System;
using System.IO;
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
    public class PsdGlyphLocalExportTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void LegacyCalibrationMatchesProbeAndNewModePreservesNormalWeight(bool bold)
        {
            string prefix = "local-export-test-" + Guid.NewGuid().ToString("N");
            string a = null, b = null;
            Texture2D x = null, y = null;
            try
            {
                a = PsdGlyphSpaceProbe.Capture(prefix + "probe", 1, 0, true, "Hell 60", false, bold);
                b = PsdGlyphSpaceProbe.Capture(prefix + "production", 1, 0, true, "Hell 60", true, bold, bold);
                x = new Texture2D(2, 2); y = new Texture2D(2, 2);
                x.LoadImage(File.ReadAllBytes(a)); y.LoadImage(File.ReadAllBytes(b));
                var first = x.GetPixels32(); var second = y.GetPixels32();
                Assert.That(first.Count(c => c.r + c.g > 0), Is.GreaterThan(500));
                int max = 0;
                for (int i = 0; i < first.Length; i++)
                    max = Math.Max(max, Math.Max(Math.Abs(first[i].r - second[i].r), Math.Abs(first[i].g - second[i].g)));
                Assert.That(max, Is.LessThanOrEqualTo(2), "普通字重不应应用局部粗体；粗体应保持探针结果。");
            }
            finally
            {
                if (x != null) UnityEngine.Object.DestroyImmediate(x);
                if (y != null) UnityEngine.Object.DestroyImmediate(y);
                foreach (string path in new[] { a, b })
                    if (path != null) { File.Delete(path); if (File.Exists(path + ".meta")) File.Delete(path + ".meta"); }
            }
        }

        [Test]
        public void OptionalExportKeepsLayoutSharingVisibilityAndOriginalAssets()
        {
            string folder = "Assets/PsdUiWorkbench/Tests/LocalExport_" + Guid.NewGuid().ToString("N");
            string guid = AssetDatabase.AssetPathToGUID(PsdTmpCandidateExport.SourcePath);
            var document = new UnityPsdDocumentSource().Load(guid).Document;
            int hidden = document.Find(1091).ParentId.Value;
            var state = new PsdVisibilityState(false, new[] { hidden });
            var plan = PsdPrefabPlanner.Build(document, state);
            var old = AssetDatabase.LoadAssetAtPath<GameObject>(PsdTextFillProbe.PrefabPath);
            string[] paths = new[] { PsdTmpCandidateExport.SourcePath, PsdTmpCandidateExport.SourcePath + ".meta", PsdTextFillProbe.PrefabPath }
                .Concat(old.GetComponentsInChildren<TextMeshProUGUI>(true).SelectMany(t => new[] { AssetDatabase.GetAssetPath(t.font), AssetDatabase.GetAssetPath(t.fontSharedMaterial) }))
                .Append(PsdVisibilityRepository.GetConfigPath(guid)).Distinct().Where(File.Exists).ToArray();
            var hashes = paths.Select(Hash).ToArray();
            int scenes = EditorSceneManager.previewSceneCount;
            try
            {
                var report = PsdTextPrefabExport.Export(guid, state, folder + "/Sample.prefab", PsdTmpRenderingMode.GlyphLocalBoldExperimental);
                Assert.That(report.TextComponents, Is.EqualTo(17)); Assert.That(report.PlaceholderRuns, Is.Zero);
                Assert.That(report.ImageCount, Is.EqualTo(32));
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(report.AssetPath);
                var texts = prefab.GetComponentsInChildren<TextMeshProUGUI>(true);
                Assert.That(texts.Select(t => t.fontSharedMaterial).Distinct().Count(), Is.EqualTo(13));
                Assert.That(prefab.GetComponentsInChildren<MonoBehaviour>(true).All(c => c != null && !c.GetType().Namespace.StartsWith("PsdUiWorkbench")), Is.True);
                foreach (var text in texts)
                {
                    if (text.name == "Placeholder")
                    {
                        Assert.That(text.font, Is.SameAs(TMP_Settings.defaultFontAsset));
                        Assert.That(text.fontSize, Is.EqualTo(36)); Assert.That(text.fontStyle, Is.EqualTo(FontStyles.Normal));
                        Assert.That(text.fontSharedMaterial.GetFloat("_OutlineWidth"), Is.Zero);
                    }
                    else
                    {
                        Assert.That(text.font.boldStyle, Is.Zero); Assert.That(text.font.boldSpacing, Is.EqualTo(7));
                        Assert.That(text.fontSharedMaterial.shader.name, Is.EqualTo(PsdTmpGlyphLocalBold.ShaderName));
                        Assert.That(text.fontSharedMaterial.GetFloat("_FaceDilate"), Is.Zero);
                        Assert.That(text.fontSharedMaterial.GetFloat("_WeightBold"), Is.Zero);
                        Assert.That(text.extraPadding, Is.True);
                        Assert.That(text.fontSharedMaterial.GetFloat("_PsdBoldSampling"), Is.EqualTo(1));
                        Assert.That(text.fontSharedMaterial.GetFloat("_PsdBoldAtlasWidth"), Is.EqualTo(3.5f));
                        Assert.That(text.fontSharedMaterial.GetFloat("_PsdBoldVerticalRadius"), Is.EqualTo(0.5f));
                    }
                }
                var transforms = new System.Collections.Generic.Dictionary<int, Transform>();
                var content = prefab.transform.GetChild(0);
                Assert.That(content.gameObject.activeSelf, Is.False);
                foreach (var node in plan.Nodes)
                {
                    var parent = node.ParentId.HasValue ? transforms[node.ParentId.Value] : content;
                    var rect = (RectTransform)parent.GetChild(node.SiblingIndex); transforms.Add(node.Id, rect);
                    Assert.That(rect.gameObject.activeSelf, Is.EqualTo(node.ActiveSelf));
                    Assert.That(rect.anchoredPosition, Is.EqualTo(new Vector2(node.LocalX, node.LocalY)));
                }
                var baseline = old.GetComponentsInChildren<TextMeshProUGUI>(true);
                var comparable = texts.Where(t => baseline.Any(b => b.text == t.text)).ToArray();
                Assert.That(comparable.Length, Is.GreaterThanOrEqualTo(13));
                for (int i = 0; i < comparable.Length; i++)
                {
                    var text = comparable[i];
                    var original = baseline.First(b => b.text == text.text);
                    Assert.That(text.fontSize, Is.EqualTo(original.fontSize));
                    Assert.That(text.rectTransform.anchoredPosition.y, Is.EqualTo(original.rectTransform.anchoredPosition.y).Within(0.01f));
                    Assert.That(text.characterSpacing, Is.EqualTo(original.characterSpacing).Within(0.001f));
                    Assert.That(text.wordSpacing, Is.EqualTo(original.wordSpacing));
                    Assert.That(text.lineSpacing, Is.EqualTo(original.lineSpacing));
                    Assert.That(text.paragraphSpacing, Is.EqualTo(original.paragraphSpacing));
                    Assert.That(text.rectTransform.localScale, Is.EqualTo(original.rectTransform.localScale));
                }
                Assert.That(paths.Select(Hash), Is.EqualTo(hashes));
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
            }
            finally { if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder); }
        }

        private static string Hash(string path)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToBase64String(sha.ComputeHash(stream));
        }
    }
}
