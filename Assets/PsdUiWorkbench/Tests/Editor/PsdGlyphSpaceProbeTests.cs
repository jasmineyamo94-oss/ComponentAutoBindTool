using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PsdUiWorkbench.Tests
{
    public class PsdGlyphSpaceProbeTests
    {
        [TestCase(0.5f)]
        [TestCase(1f)]
        [TestCase(2f)]
        public void AtlasExpansionMatchesProportionalScreenExpansion(float scale)
        {
            string prefix = "glyph-space-test-" + Guid.NewGuid().ToString("N");
            int scenes = EditorSceneManager.previewSceneCount;
            int fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Count(f => !EditorUtility.IsPersistent(f));
            Texture2D a = null, b = null;
            try
            {
                string first = PsdGlyphSpaceProbe.Capture(prefix + "screen", scale, 0, false);
                string second = PsdGlyphSpaceProbe.Capture(prefix + "local", scale, 0, true);
                a = new Texture2D(2, 2); b = new Texture2D(2, 2);
                a.LoadImage(File.ReadAllBytes(first)); b.LoadImage(File.ReadAllBytes(second));
                var x = a.GetPixels32(); var y = b.GetPixels32();
                int visible = 0, maximum = 0; long difference = 0;
                for (int i = 0; i < x.Length; i++)
                {
                    if (x[i].r + x[i].g + y[i].r + y[i].g == 0) continue;
                    visible++;
                    int r = Math.Abs(x[i].r-y[i].r), g = Math.Abs(x[i].g-y[i].g);
                    maximum = Math.Max(maximum, Math.Max(r,g)); difference += r+g;
                }
                Assert.That(visible, Is.GreaterThan(500));
                Assert.That(maximum, Is.LessThanOrEqualTo(8), "导数转换仅允许边缘量化误差，不允许缩放后改变扩张强度。");
                Assert.That((double)difference/(visible*2), Is.LessThan(1));
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
                Assert.That(Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Count(f => !EditorUtility.IsPersistent(f)), Is.EqualTo(fonts));
            }
            finally
            {
                if (a != null) UnityEngine.Object.DestroyImmediate(a);
                if (b != null) UnityEngine.Object.DestroyImmediate(b);
                foreach (string suffix in new[] { "screen", "local" })
                {
                    string path = PsdStrokeVisualDiagnosis.Folder + prefix + suffix + ".png";
                    if (File.Exists(path)) File.Delete(path);
                    if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
                }
            }
        }
    }
}
