using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace PsdUiWorkbench.Tests
{
    public class PsdThinStrokeTests
    {
        public const string LocalPrefab = "Assets/PsdUiWorkbench/Exports/战斗-主菜单界面_字形局部补偿.prefab";

        public static string Capture(string name, float sampling, float vertical = 0, float width = 3, int layerId = 1057, bool mask = true, bool configured = false, bool outline = true)
        {
            string path = PsdStrokeVisualDiagnosis.Folder + name + ".png";
            if (Path.GetFileName(name) != name || File.Exists(path)) throw new ArgumentException("探针路径无效或已存在。");
            Material copy = null;
            try
            {
                return PsdTmpCandidateExport.Capture(LocalPrefab, path, root =>
                {
                    var guid = AssetDatabase.AssetPathToGUID(PsdTmpCandidateExport.SourcePath);
                    var doc = new Infrastructure.UnityPsdDocumentSource().Load(guid).Document;
                    var plan = Core.PsdPrefabPlanner.Build(doc, new Core.PsdVisibilityState());
                    var nodes = new System.Collections.Generic.Dictionary<int, Transform>();
                    foreach (var node in plan.Nodes)
                    {
                        var parent = node.ParentId.HasValue ? nodes[node.ParentId.Value] : root.transform.GetChild(0);
                        var child = parent.GetChild(node.SiblingIndex);
                        if (child.name != node.Name) throw new InvalidOperationException("探针对照 Prefab 与源层级不符。");
                        nodes.Add(node.Id, child);
                    }
                    var target = nodes[layerId].GetComponentsInChildren<TextMeshProUGUI>(true).Single();
                    for (var current = target.transform; current != null; current = current.parent) current.gameObject.SetActive(true);
                    foreach (var image in root.GetComponentsInChildren<UnityEngine.UI.Image>(true)) image.enabled = false;
                    foreach (var text in root.GetComponentsInChildren<TextMeshProUGUI>(true)) text.enabled = text == target;
                    foreach (var obj in root.scene.GetRootGameObjects())
                        if (mask && obj.TryGetComponent<Camera>(out var camera)) camera.backgroundColor = Color.black;
                    copy = new Material(target.fontSharedMaterial);
                    if (configured) Infrastructure.PsdTmpGlyphLocalBold.Configure(copy, target.font);
                    else
                    {
                        copy.SetFloat("_PsdBoldAtlasWidth", width * target.font.faceInfo.pointSize / 96f);
                        if (copy.HasProperty("_PsdBoldSampling")) copy.SetFloat("_PsdBoldSampling", sampling);
                        if (copy.HasProperty("_PsdBoldVerticalRadius")) copy.SetFloat("_PsdBoldVerticalRadius", vertical);
                    }
                    if (mask)
                    {
                        copy.SetColor("_FaceColor", new Color(1, 0, 0, copy.GetColor("_FaceColor").a));
                        copy.SetColor("_OutlineColor", new Color(0, 1, 0, copy.GetColor("_OutlineColor").a));
                    }
                    if (!outline)
                    {
                        copy.SetFloat("_OutlineWidth", 0);
                        copy.DisableKeyword("OUTLINE_ON");
                        ShaderUtilities.UpdateShaderRatios(copy);
                    }
                    target.fontSharedMaterial = copy;
                });
            }
            finally { if (copy != null) UnityEngine.Object.DestroyImmediate(copy); }
        }

        [Test]
        public void RepairedFaceDoesNotGrowWhenOutlineIsEnabled()
        {
            string prefix = "four-outline-test-" + Guid.NewGuid().ToString("N");
            string a = null, b = null; Texture2D x = null, y = null;
            int scenes = UnityEditor.SceneManagement.EditorSceneManager.previewSceneCount;
            int materials = Resources.FindObjectsOfTypeAll<Material>().Count(m => !EditorUtility.IsPersistent(m));
            try
            {
                a = Capture(prefix + "on", 1, configured: true);
                b = Capture(prefix + "off", 1, configured: true, outline: false);
                x = new Texture2D(2, 2); y = new Texture2D(2, 2);
                x.LoadImage(File.ReadAllBytes(a)); y.LoadImage(File.ReadAllBytes(b));
                var first = x.GetPixels32(); var second = y.GetPixels32();
                int max = 0, visible = 0, total = 0;
                Assert.That(first.Count(c => c.g > 128), Is.GreaterThan(100));
                for (int i = 0; i < first.Length; i++)
                {
                    int difference = Math.Abs(first[i].r - second[i].r);
                    max = Math.Max(max, difference); total += difference;
                    if (first[i].r + second[i].r > 0) visible++;
                }
                Assert.That(visible, Is.GreaterThan(1000));
                Assert.That(max, Is.LessThanOrEqualTo(8));
                Assert.That((double)total / visible, Is.LessThan(0.5));
                Assert.That(UnityEditor.SceneManagement.EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
                Assert.That(Resources.FindObjectsOfTypeAll<Material>().Count(m => !EditorUtility.IsPersistent(m)), Is.EqualTo(materials));
            }
            finally
            {
                if (x != null) UnityEngine.Object.DestroyImmediate(x);
                if (y != null) UnityEngine.Object.DestroyImmediate(y);
                foreach (var path in new[] { a, b })
                    if (path != null) { File.Delete(path); if (File.Exists(path + ".meta")) File.Delete(path + ".meta"); }
            }
        }

        [Test]
        public void FourTopDiagonalDoesNotDevelopDarkNotch()
        {
            string name = "four-notch-test-" + Guid.NewGuid().ToString("N");
            string path = null; Texture2D texture = null;
            try
            {
                path = Capture(name, 1, configured: true);
                texture = new Texture2D(2, 2); texture.LoadImage(File.ReadAllBytes(path));
                // 固定 PSD 原位，三个像素位于 4 顶部原稿连续的金色斜线中。
                foreach (var point in new[] { new Vector2Int(433, 106), new Vector2Int(432, 107), new Vector2Int(432, 108) })
                    Assert.That(texture.GetPixel(point.x, texture.height - 1 - point.y).r, Is.GreaterThanOrEqualTo(0.65f),
                        "4 顶部斜线出现明显深色缺口，PSD 像素位置 " + point);
            }
            finally
            {
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                if (path != null) { File.Delete(path); if (File.Exists(path + ".meta")) File.Delete(path + ".meta"); }
            }
        }
    }
}
