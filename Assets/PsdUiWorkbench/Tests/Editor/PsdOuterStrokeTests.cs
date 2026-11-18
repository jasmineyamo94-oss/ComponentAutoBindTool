using System;
using System.IO;
using NUnit.Framework;
using PsdUiWorkbench.Infrastructure;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using TMPro;
using PsdUiWorkbench.Core;
using System.Collections.Generic;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdOuterStrokeTests
    {
        [Test]
        public void InitialLayoutDoesNotWriteCullingIntoSharedFontMaterial()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("共享材质回归", typeof(RectTransform), typeof(Canvas));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            root.GetComponent<Canvas>().enabled = false;
            var font = UnityEngine.Object.Instantiate(TMP_Settings.defaultFontAsset);
            var sourceMaterial = new Material(TMP_Settings.defaultFontAsset.material);
            var outputMaterial = new Material(sourceMaterial);
            font.material = sourceMaterial;
            try
            {
                using (var so = new SerializedObject(sourceMaterial))
                {
                    var floats = so.FindProperty("m_SavedProperties.m_Floats");
                    for (int i = floats.arraySize - 1; i >= 0; i--)
                        if (floats.GetArrayElementAtIndex(i).FindPropertyRelative("first").stringValue == "_CullMode") floats.DeleteArrayElementAtIndex(i);
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                string before = EditorJsonUtility.ToJson(sourceMaterial);
                var style = new PsdTextStyle("Known", 30, false, 100, PsdTextColor.White, 0, PsdTextColor.White);
                var source = new PsdTextSource(1, "AA", 0, 0, PsdTextAlignment.Left, new[] {new PsdTextSourceSpan(0, 2, style)});
                ((RectTransform)root.transform).sizeDelta = new Vector2(200, 100);
                var plan = PsdTextLayoutPlanner.Build(source, new PixelRect(0, 0, 200, 100), new HashSet<string> {"Known"});
                PsdTmpTextBuilder.Build((RectTransform)root.transform, plan, _ => font, (_, __, ___) =>
                {
                    Assert.That(EditorJsonUtility.ToJson(sourceMaterial), Is.EqualTo(before), "首次度量前后不应修改源字体材质。");
                    return outputMaterial;
                });
                Assert.That(EditorJsonUtility.ToJson(sourceMaterial), Is.EqualTo(before));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(outputMaterial);
                UnityEngine.Object.DestroyImmediate(sourceMaterial);
                // 字库是浅副本，图集属于原字体；销毁副本前解除共享图集，不能让 OnDestroy 释放原资产。
                font.atlasTextures = Array.Empty<Texture2D>();
                UnityEngine.Object.DestroyImmediate(font);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
        [Test]
        public void OuterStrokeKeepsIsolatedGlyphFillWithoutDilateOrExtraPass()
        {
            int scenes = EditorSceneManager.previewSceneCount;
            string prefix = "outer-test-" + Guid.NewGuid().ToString("N");
            string a = null, b = null, clippedPath = null;
            Color32[] Pixels(string path)
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                try { Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True); return texture.GetPixels32(); }
                finally { UnityEngine.Object.DestroyImmediate(texture); }
            }
            try
            {
                var shader = Shader.Find(PsdTmpOuterStroke.ShaderName);
                Assert.That(shader, Is.Not.Null); Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
                var material = new Material(shader);
                try
                {
                    material.SetFloat("_GradientScale", 25);
                    PsdTmpOuterStroke.Configure(material, 3, 0.5f, 0.375f);
                    Assert.That(material.passCount, Is.EqualTo(1)); Assert.That(material.GetFloat("_FaceDilate"), Is.Zero);
                    Assert.That(material.GetFloat("_OutlineWidth"), Is.EqualTo(0.25f).Within(0.00001f));
                    Assert.That(material.GetFloat("_PsdWidthConvention"), Is.EqualTo(1));
                }
                finally { UnityEngine.Object.DestroyImmediate(material); }
                a = PsdStrokeVisualDiagnosis.CaptureOuter(0.375f, 1, 1, prefix + "a", 0, true);
                b = PsdStrokeVisualDiagnosis.CaptureOuter(0.375f, 1, 1, prefix + "b", 5, true);
                var plain = Pixels(a); var outlined = Pixels(b);
                int greenGrowth = 0;
                // 原稿 ID 1090 的独立数字 0，避免相邻字形的绘制覆盖混入填充回归。
                for (int y = 301; y < 355; y++)
                for (int x = 520; x < 563; x++)
                {
                    int i = (2399 - y) * 1080 + x;
                    Assert.That(outlined[i].r, Is.EqualTo(plain[i].r), $"填充改变：{x},{y}");
                    greenGrowth += outlined[i].g - plain[i].g;
                }
                Assert.That(greenGrowth, Is.GreaterThan(10000));
                RectInt Bounds(Color32[] pixels)
                {
                    int left = 1080, right = 0, top = 2400, bottom = 0;
                    for (int y = 298; y < 362; y++)
                    for (int x = 510; x < 575; x++)
                    {
                        var pixel = pixels[(2399 - y) * 1080 + x];
                        if (pixel.r + pixel.g < 127) continue;
                        left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    }
                    return new RectInt(left, top, right - left + 1, bottom - top + 1);
                }
                var inner = Bounds(plain); var outer = Bounds(outlined);
                foreach (int growth in new[] {inner.xMin - outer.xMin, outer.xMax - inner.xMax, inner.yMin - outer.yMin, outer.yMax - inner.yMax})
                    Assert.That(growth, Is.InRange(4, 6), "目标外扩 5 px，允许 1 px 栅格化误差。");
                clippedPath = PsdStrokeVisualDiagnosis.CaptureOuter(0.375f, 1, 1, prefix + "clip", 5, true, true);
                var clipped = Pixels(clippedPath); int leftFill = 0, rightFill = 0;
                for (int y = 309; y < 347; y++)
                for (int x = 520; x < 563; x++)
                {
                    int red = clipped[(2399 - y) * 1080 + x].r;
                    if (x < 540) leftFill += red;
                    if (x >= 544) rightFill += red;
                }
                Assert.That(leftFill, Is.GreaterThan(10000)); Assert.That(rightFill, Is.Zero, "RectMask2D 应裁掉右半字形。");
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
            }
            finally
            {
                foreach (string path in new[] {a, b, clippedPath})
                    if (path != null) { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".meta")) File.Delete(path + ".meta"); }
            }
        }

        [Test]
        public void InvalidDistanceFieldAndEdgeParametersFailBeforeChangingMaterial()
        {
            var material = new Material(Shader.Find("TextMeshPro/Mobile/Distance Field"));
            try
            {
                string before = EditorJsonUtility.ToJson(material);
                Assert.Throws<ArgumentException>(() => PsdTmpOuterStroke.Configure(material, 3, 0.5f, 0.375f, float.NaN));
                material.SetFloat("_GradientScale", 25);
                Assert.Throws<InvalidOperationException>(() => PsdTmpOuterStroke.Configure(material, 100, 0.5f, 0.375f));
                material.SetFloat("_GradientScale", 5);
                Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(before));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }
    }
}
