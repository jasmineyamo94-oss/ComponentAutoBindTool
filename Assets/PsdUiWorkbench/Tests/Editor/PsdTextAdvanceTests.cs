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

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdTextAdvanceTests
    {
        [TestCase(103f, 102f, 48f, 4, true)]
        [TestCase(103f, 90f, 48f, 4, false)]
        [TestCase(103f, 102f, 48f, 1, false)]
        [TestCase(103f, 102f, 0f, 4, false)]
        [TestCase(float.NaN, 102f, 48f, 4, false)]
        public void CorrectionIsBoundedAndNeverChangesGlyphScale(float source, float measured, float size, int count, bool accepted)
        {
            bool actual = PsdTextAdvanceCorrection.TryGetSpacingDelta(source, measured, size, count, out float delta);
            Assert.That(actual, Is.EqualTo(accepted));
            if (actual) Assert.That(measured + delta * size * count / 100, Is.EqualTo(source).Within(0.0001f));
            else Assert.That(delta, Is.Zero);
        }

        [Test]
        public void ActualTmpAdvanceMatchesSmallSourceDifferenceAndRejectsLargeMismatch()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("逐字 advance 回归", typeof(RectTransform), typeof(Canvas));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            root.GetComponent<Canvas>().enabled = false;
            var rect = (RectTransform)root.transform; rect.sizeDelta = new Vector2(300, 100);
            var font = TMP_Settings.defaultFontAsset; var material = new Material(font.material);
            string before = EditorJsonUtility.ToJson(font.material);
            var style = new PsdTextStyle("Known", 48, true, 100, PsdTextColor.White, 0, PsdTextColor.White);
            TextMeshProUGUI Build(float? reference)
            {
                var source = new PsdTextSource(1, "Hell", 0, 0, PsdTextAlignment.Left,
                    new[] {new PsdTextSourceSpan(0, 4, style)}, reference);
                var plan = PsdTextLayoutPlanner.Build(source, new PixelRect(0, 0, 300, 100), new HashSet<string> {"Known"});
                return PsdTmpTextBuilder.Build(rect, plan, _ => font, (_, __, ___) => material, true).Single();
            }
            float Advance(TextMeshProUGUI text) => text.textInfo.characterInfo[text.textInfo.characterCount - 1].xAdvance - text.textInfo.characterInfo[0].origin;
            try
            {
                var baseline = Build(null); float width = Advance(baseline), spacing = baseline.characterSpacing;
                UnityEngine.Object.DestroyImmediate(baseline.gameObject);
                var corrected = Build(width + 0.8f);
                Assert.That(Advance(corrected), Is.EqualTo(width + 0.8f).Within(0.0001f), "源排版宽度的小额 advance 差异应被补偿。");
                Assert.That(corrected.rectTransform.localScale, Is.EqualTo(Vector3.one));
                Assert.That(corrected.fontSize, Is.EqualTo(48)); Assert.That(style.Tracking, Is.EqualTo(100));
                UnityEngine.Object.DestroyImmediate(corrected.gameObject);
                var rejected = Build(width * 1.1f);
                Assert.That(Advance(rejected), Is.EqualTo(width).Within(0.0001f));
                Assert.That(rejected.characterSpacing, Is.EqualTo(spacing));
                Assert.That(EditorJsonUtility.ToJson(font.material), Is.EqualTo(before));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(material);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
