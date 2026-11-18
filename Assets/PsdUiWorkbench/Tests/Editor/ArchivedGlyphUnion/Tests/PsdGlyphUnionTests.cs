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
    [Explicit("已退役的邻字合成历史实验，不进入常规回归。")]
    public sealed class PsdGlyphUnionTests
    {
        private static Color32[] Read(string path)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try { Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True); return texture.GetPixels32(); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }
        private static void DeleteEvidence(string name)
        {
            string path = PsdStrokeVisualDiagnosis.Folder + name + ".png";
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
        }
        [Test]
        public void NeighborUnionKeepsWholeRunFillAcrossOutlineChanges()
        {
            string prefix = "union-test-" + Guid.NewGuid().ToString("N");
            int scenes = EditorSceneManager.previewSceneCount;
            try
            {
                var a = Read(PsdGlyphUnionPrototype.Capture(0, prefix + "zero"));
                var b = Read(PsdGlyphUnionPrototype.Capture(5, prefix + "five"));
                var stockA = Read(PsdGlyphUnionPrototype.Capture(0, prefix + "stockzero", false, false));
                var stockB = Read(PsdGlyphUnionPrototype.Capture(5, prefix + "stockfive", false, false));
                int growth = 0, baselineChanged = 0;
                foreach (var rect in new[] {new RectInt(415,410,260,53), new RectInt(645,93,114,56), new RectInt(0,2260,220,60)})
                for (int y = rect.yMin; y < rect.yMax; y++)
                for (int x = rect.xMin; x < rect.xMax; x++)
                {
                    int i = (2399-y)*1080+x;
                    Assert.That(b[i].r, Is.EqualTo(a[i].r), $"邻字填充被覆盖：{x},{y}");
                    if (stockA[i].r != stockB[i].r) baselineChanged++;
                    growth += b[i].g-a[i].g;
                }
                Assert.That(growth, Is.GreaterThan(10000));
                Assert.That(baselineChanged, Is.GreaterThan(0), "基线应复现邻字覆盖，不能用没有交叠的场景证明修复。");
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
            }
            finally { foreach (string suffix in new[] {"zero", "five", "stockzero", "stockfive"}) DeleteEvidence(prefix + suffix); }
        }

        [Test]
        public void StaticPrototypePersistsLayoutTexturesAndSinglePassWithoutRuntimeScripts()
        {
            string name = "union-persist-" + Guid.NewGuid().ToString("N");
            string folder = "Assets/PsdUiWorkbench/Tests/" + name;
            AssetDatabase.CreateFolder("Assets/PsdUiWorkbench/Tests", name);
            try
            {
                string path = PsdGlyphUnionPrototype.ExportStatic(folder + "/prototype.prefab", name);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var all = prefab.GetComponentsInChildren<TextMeshProUGUI>(true);
                Assert.That(all.Length, Is.EqualTo(27));
                var union = all.Where(t => t.name != "Placeholder").ToArray();
                Assert.That(union.Length, Is.EqualTo(21));
                foreach (var text in union)
                {
                    var material = text.fontSharedMaterial;
                    Assert.That(material.shader.name, Is.EqualTo(PsdGlyphUnionPrototype.ShaderName));
                    Assert.That(material.passCount, Is.EqualTo(1));
                    Assert.That(EditorUtility.IsPersistent(material.GetTexture("_PsdGlyphData")), Is.True);
                    Assert.That(material.GetFloat("_PsdGlyphCount"), Is.InRange(1,32));
                    Assert.That(text.horizontalMapping, Is.EqualTo(TextureMappingOptions.Paragraph));
                    Assert.That(text.verticalMapping, Is.EqualTo(TextureMappingOptions.Paragraph));
                }
                Assert.That(prefab.GetComponentsInChildren<MonoBehaviour>(true).Any(c => c != null && c.GetType().Namespace?.StartsWith("PsdUiWorkbench") == true), Is.False);
            }
            finally { AssetDatabase.DeleteAsset(folder); DeleteEvidence(name); }
        }
    }
}
