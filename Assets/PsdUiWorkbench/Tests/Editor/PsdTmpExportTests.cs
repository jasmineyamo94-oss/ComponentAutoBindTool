using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdTmpExportTests
    {
        private static PsdTextStyle Style(string font) => new PsdTextStyle(font, 30, true, 100, new PsdTextColor(0.3f, 0.4f, 0.5f, 1), 3, new PsdTextColor(0, 0, 0, 0.8f));

        [Test]
        public void PlannerPreservesFractionalBaselineAndNormalizesMixedFont()
        {
            var known = Style("Known"); var missing = Style("Missing");
            var source = new PsdTextSource(7, "1/2", 125.25f, 223.75f, PsdTextAlignment.Center,
                new[] {new PsdTextSourceSpan(0, 1, known), new PsdTextSourceSpan(1, 1, missing), new PsdTextSourceSpan(2, 1, known)});
            var plan = PsdTextLayoutPlanner.Build(source, new PixelRect(100, 200, 100, 40), new HashSet<string> {"Known"});
            Assert.That(plan.BaselineX, Is.EqualTo(-24.75f));
            Assert.That(plan.BaselineY, Is.EqualTo(-3.75f));
            Assert.That(plan.Runs.Select(r => r.Text), Is.EqualTo(new[] { "1/2" }));
            Assert.That(plan.Runs.Single().IsPlaceholder, Is.False);
            Assert.That(plan.Runs.Single().Style.FontKey, Is.EqualTo("Known"));
            Assert.That(plan.Runs.Single().Style.FontSize, Is.EqualTo(30));
            Assert.That(plan.Runs.Single().Style.StrokePixels, Is.EqualTo(3));
            Assert.That(source.Spans[1].Style.FontKey, Is.EqualTo("Missing"));
            Assert.That(source.Spans[1].Style.FontSize, Is.EqualTo(30));
        }

        [Test]
        public void SourceRejectsGapsOverlapsAndSplitSurrogates()
        {
            Assert.Throws<ArgumentException>(() => new PsdTextSource(1, "AB", 0, 0, PsdTextAlignment.Left, new[] {new PsdTextSourceSpan(1, 1, Style("Known"))}));
            Assert.Throws<ArgumentException>(() => new PsdTextSource(1, "A😀B", 0, 0, PsdTextAlignment.Left,
                new[] {new PsdTextSourceSpan(0, 2, Style("Known")), new PsdTextSourceSpan(2, 2, Style("Known"))}));
            Assert.Throws<ArgumentException>(() => new PsdTextSource(1, "A\nB", 0, 0, PsdTextAlignment.Left, new[] {new PsdTextSourceSpan(0, 3, Style("Known"))}));
        }

        [Test]
        public void CandidateReloadPreservesLayoutVisibilityReferencesWithoutPlaceholders()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("候选字体构建需要真实图形设备。");
            string folder = "Assets/PsdUiWorkbench/Tests/TmpExport_" + Guid.NewGuid().ToString("N");
            var activeScene = EditorSceneManager.GetActiveScene();
            int previews = EditorSceneManager.previewSceneCount;
            var source = new UnityPsdDocumentSource();
            string guid = AssetDatabase.AssetPathToGUID(PsdTmpCandidateExport.SourcePath);
            var document = source.Load(guid).Document;
            int hiddenParent = document.Find(1091).ParentId.Value;
            var visibility = new PsdVisibilityState(false, new[] {hiddenParent});
            var plan = PsdPrefabPlanner.Build(document, visibility);
            string defaultMaterial = EditorJsonUtility.ToJson(TMP_Settings.defaultFontAsset.material);
            float defaultBoldSpacing = TMP_Settings.defaultFontAsset.boldSpacing;
            try
            {
                var report = PsdTmpCandidateExport.Export(folder, visibility);
                Assert.That(report.textLayers, Is.EqualTo(17)); Assert.That(report.images, Is.EqualTo(32));
                Assert.That(report.textComponents, Is.EqualTo(17)); Assert.That(report.placeholders, Is.Zero);
                Assert.That(report.sourceUnchanged && report.metaUnchanged && report.defaultFontUnchanged && report.visibilityUnchanged, Is.True);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(report.prefabPath);
                Assert.That(prefab.activeSelf, Is.True); Assert.That(prefab.transform.localScale, Is.EqualTo(Vector3.one));
                var content = prefab.transform.GetChild(0);
                Assert.That(content.gameObject.activeSelf, Is.False);
                var transforms = new Dictionary<int, RectTransform>();
                foreach (var node in plan.Nodes)
                {
                    Transform parent = node.ParentId.HasValue ? transforms[node.ParentId.Value] : content;
                    var rect = (RectTransform)parent.GetChild(node.SiblingIndex); transforms.Add(node.Id, rect);
                    Assert.That(rect.gameObject.activeSelf, Is.EqualTo(node.ActiveSelf));
                    Assert.That(rect.anchoredPosition.x, Is.EqualTo(node.LocalX).Within(0.0001f));
                    Assert.That(rect.anchoredPosition.y, Is.EqualTo(node.LocalY).Within(0.0001f));
                    Assert.That(rect.sizeDelta, Is.EqualTo(new Vector2(node.Bounds.Width, node.Bounds.Height)));
                }
                Assert.That(transforms[1091].gameObject.activeSelf, Is.True);
                Assert.That(transforms[hiddenParent].gameObject.activeSelf, Is.False);
                Assert.That(transforms.ContainsKey(947), Is.False);
                var texts = prefab.GetComponentsInChildren<TextMeshProUGUI>(true);
                Assert.That(texts.Length, Is.EqualTo(17));
                foreach (var text in texts)
                {
                    Assert.That(EditorUtility.IsPersistent(text.font), Is.True); Assert.That(EditorUtility.IsPersistent(text.fontSharedMaterial), Is.True);
                    Assert.That(text.fontSharedMaterial.mainTexture, Is.Not.Null); Assert.That(text.enableAutoSizing, Is.False); Assert.That(text.raycastTarget, Is.False);
                    Assert.That(text.richText, Is.False);
                    Assert.That(text.name, Is.Not.EqualTo("Placeholder"));
                    Assert.That(text.fontSharedMaterial.GetFloat("_OutlineWidth"), Is.GreaterThan(0));
                    int layerId = transforms.Single(pair => pair.Value == text.transform.parent).Key;
                    float tracking = PsdTmpCandidateExport.ReadSources()[layerId].Spans[0].Style.Tracking;
                    Assert.That(text.characterSpacing + text.font.boldSpacing, Is.EqualTo(tracking / 10 + 2.5f).Within(0.0001f));
                }
                var candidateFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(report.fontPath);
                Assert.That(candidateFont.atlasPopulationMode, Is.EqualTo(AtlasPopulationMode.Static));
                Assert.That(candidateFont.sourceFontFile, Is.Null);
                Assert.That(prefab.GetComponentsInChildren<MonoBehaviour>(true).Any(c => c != null && c.GetType().Namespace?.StartsWith("PsdUiWorkbench") == true), Is.False);
                Assert.That(EditorJsonUtility.ToJson(TMP_Settings.defaultFontAsset.material), Is.EqualTo(defaultMaterial));
                Assert.That(TMP_Settings.defaultFontAsset.boldSpacing, Is.EqualTo(defaultBoldSpacing));
                Assert.That(EditorSceneManager.GetActiveScene(), Is.EqualTo(activeScene));
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(previews));
                Assert.That(CenterOriginDifference(transforms[937].GetComponentInChildren<TextMeshProUGUI>(true),
                    PsdTextLayoutPlanner.Build(PsdTmpCandidateExport.ReadSources()[937], document.Find(937).Bounds, new HashSet<string> {"TOXIA"})), Is.EqualTo(0).Within(0.0001f),
                    "居中宽度应与 TMP 原生排版一致，不应将末尾额外 Tracking 也计入宽度。");

                // 材质目录碰撞时必须保留原目录，不能因失败清理而删除已有内容。
                string collision = report.outputFolder + "/Collision.prefab";
                string existing = report.outputFolder + "/Collision Materials";
                AssetDatabase.CreateFolder(report.outputFolder, "Collision Materials");
                Assert.Throws<System.IO.IOException>(() => PsdTmpPrefabExporter.Export(plan, PsdTmpCandidateExport.ReadSources(),
                    new Dictionary<string, TMP_FontAsset> {{"TOXIA", candidateFont}}, TMP_Settings.defaultFontAsset, collision, source.Resolve));
                Assert.That(AssetDatabase.IsValidFolder(existing), Is.True);
                Assert.That(System.IO.File.Exists(collision), Is.False);
                var second = PsdTmpPrefabExporter.Export(plan, PsdTmpCandidateExport.ReadSources(), new Dictionary<string, TMP_FontAsset> {{"TOXIA", candidateFont}},
                    TMP_Settings.defaultFontAsset, report.prefabPath, source.Resolve);
                Assert.That(second.AssetPath, Is.Not.EqualTo(report.prefabPath));
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(report.prefabPath), Is.Not.Null);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        private static float CenterOriginDifference(TextMeshProUGUI saved, PsdTextLayoutPlan plan)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject root = null;
            try
            {
                root = new GameObject("居中基准", typeof(RectTransform));
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                root.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                root.transform.localScale = Vector3.one;
                var go = new GameObject("原生 TMP", typeof(RectTransform)); go.transform.SetParent(root.transform, false);
                var text = go.AddComponent<TextMeshProUGUI>();
                text.font = saved.font; text.fontSharedMaterial = saved.fontSharedMaterial; text.text = saved.text;
                text.fontSize = saved.fontSize; text.fontStyle = saved.fontStyle; text.characterSpacing = saved.characterSpacing;
                text.fontFeatures = new List<UnityEngine.TextCore.OTL_FeatureTag>();
                text.textWrappingMode = TextWrappingModes.NoWrap; text.overflowMode = TextOverflowModes.Overflow; text.enableAutoSizing = false;
                text.rectTransform.sizeDelta = saved.rectTransform.sizeDelta;
                text.alignment = TextAlignmentOptions.Baseline; text.ForceMeshUpdate(true, true);
                float expected = text.textInfo.characterInfo[0].origin + plan.BaselineX;
                text.alignment = saved.alignment; text.ForceMeshUpdate(true, true);
                return text.textInfo.characterInfo[0].origin + saved.rectTransform.anchoredPosition.x - expected;
            }
            finally { if (root != null) UnityEngine.Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
