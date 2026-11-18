using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdExportSelectionTests
    {
        private static PsdNode Layer(int id, int? parent, int order, PixelRect bounds = default, bool group = false, bool imported = true)
            => new PsdNode(id, parent, "图层" + id, group, imported, true, false, group ? "" : "sprite" + id, bounds, order);

        [Test]
        public void ExcludedSubtreeIsRemovedWithoutMovingOrReorderingRemainingImages()
        {
            var document = new PsdDocumentSnapshot("asset", 100, 200, new[]
            {
                Layer(1, null, 0, group: true), Layer(2, 1, 1, group: true),
                Layer(3, 2, 2, new PixelRect(10, 20, 10, 10)), Layer(4, 1, 3, new PixelRect(70, 80, 20, 10)),
                Layer(5, null, 4, new PixelRect(40, 120, 10, 10)), Layer(6, null, 5, group: true),
                Layer(7, null, 6, new PixelRect(1, 1, 1, 1), imported: false)
            });
            var plan = PsdPrefabPlanner.Build(document, new PsdVisibilityState(true, new[] { 4 }), new PsdExportState(new[] { 2 }));
            Assert.That(plan.Nodes.Select(n => n.Id), Is.EqualTo(new[] { 5, 1, 4 }));
            Assert.That(plan.Nodes.Where(n => n.IsImage).Select(n => n.Id), Is.EqualTo(new[] { 5, 4 }));
            var rootImage = plan.Nodes.Single(n => n.Id == 5);
            Assert.That(rootImage.SiblingIndex, Is.Zero);
            Assert.That(rootImage.LocalX, Is.EqualTo(-5));
            Assert.That(rootImage.LocalY, Is.EqualTo(-25));
            var group = plan.Nodes.Single(n => n.Id == 1);
            var child = plan.Nodes.Single(n => n.Id == 4);
            Assert.That(group.SiblingIndex, Is.EqualTo(1));
            Assert.That(group.LocalX, Is.EqualTo(30));
            Assert.That(group.LocalY, Is.EqualTo(15));
            Assert.That(child.SiblingIndex, Is.Zero);
            Assert.That(child.LocalX, Is.Zero);
            Assert.That(child.LocalY, Is.Zero);
            Assert.That(child.Bounds.X, Is.EqualTo(70));
            Assert.That(child.Bounds.Y, Is.EqualTo(80));
            Assert.That(child.ActiveSelf, Is.False);
            Assert.That(plan.Skipped.Where(n => n.IsUserExcluded).Select(n => n.Id), Is.EqualTo(new[] { 2, 3 }));
            Assert.That(plan.ExcludedNodeCount, Is.EqualTo(2));
        }

        [Test]
        public void LastImageExclusionRejectsInsteadOfProducingAnEmptyPlan()
        {
            var document = new PsdDocumentSnapshot("asset", 100, 200, new[]
            { Layer(1, null, 0, group: true), Layer(2, 1, 1, new PixelRect(10, 20, 10, 10)) });
            Assert.Throws<InvalidOperationException>(() => PsdPrefabPlanner.Build(document,
                new PsdVisibilityState(), new PsdExportState(new[] { 1 })));
        }

        [Test]
        public void FullyExcludedBattleExportCreatesNoPrefabOrResourceDirectories()
        {
            string guid = AssetDatabase.AssetPathToGUID("Assets/Psd/战斗-主菜单界面.psd");
            var document = new UnityPsdDocumentSource().Load(guid).Document;
            var excluded = new PsdExportState(document.DrawNodes.Select(n => n.Id));
            string folder = "Assets/PsdUiWorkbench/Tests/Excluded_" + Guid.NewGuid().ToString("N");
            try
            {
                Assert.Throws<InvalidOperationException>(() => PsdTextPrefabExport.Export(guid, new PsdVisibilityState(),
                    folder + "/Empty.prefab", PsdTmpRenderingMode.OuterOutline, excluded));
                Assert.That(Directory.Exists(folder), Is.False, "空导出必须在创建输出目录和字库之前被拦截。");
                Assert.That(AssetDatabase.LoadMainAssetAtPath(folder + "/Empty.prefab"), Is.Null);
            }
            finally { if (Directory.Exists(folder)) AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void BattleDefaultStillUsesSeventeenTmpAndNineMaterialsWhileExcludingTextCreatesNoTextResources()
        {
            const string sourcePath = "Assets/Psd/战斗-主菜单界面.psd";
            string guid = AssetDatabase.AssetPathToGUID(sourcePath);
            var source = new UnityPsdDocumentSource();
            var document = source.Load(guid).Document;
            var metadata = AssetDatabase.LoadAllAssetsAtPath(sourcePath).OfType<PsdTextMetadataAsset>().Single().Read();
            var textIds = metadata.Sources.Keys.Concat(metadata.RejectedLayers.Keys).ToHashSet();
            string folder = "Assets/PsdUiWorkbench/Tests/NodeExport_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/PsdUiWorkbench/Tests", Path.GetFileName(folder));
            try
            {
                var baseline = PsdTextPrefabExport.Export(guid, new PsdVisibilityState(), folder + "/Default.prefab");
                var baselinePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(baseline.AssetPath);
                var tmp = baselinePrefab.GetComponentsInChildren<TextMeshProUGUI>(true);
                Assert.That(tmp.Length, Is.EqualTo(17));
                Assert.That(tmp.Select(t => t.fontSharedMaterial).Distinct().Count(), Is.EqualTo(9));
                var excluded = new PsdExportState(textIds);
                var plan = PsdPrefabPlanner.Build(document, new PsdVisibilityState(), excluded);
                var images = PsdPrefabExporter.Export(plan, folder + "/Images.prefab", source.Resolve);
                var result = PsdTextPrefabExport.Export(guid, new PsdVisibilityState(), folder + "/WithoutText.prefab",
                    PsdTmpRenderingMode.OuterOutline, excluded);
                var imagePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(images.AssetPath);
                var textPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.AssetPath);
                var expected = document.DrawNodes.Where(n => !textIds.Contains(n.Id)).ToArray();
                Assert.That(textPrefab.GetComponentsInChildren<TextMeshProUGUI>(true), Is.Empty);
                Assert.That(imagePrefab.GetComponentsInChildren<Image>(true).Select(i => i.sprite),
                    Is.EqualTo(expected.Select(n => source.Resolve(n.SpriteId))));
                Assert.That(textPrefab.GetComponentsInChildren<Image>(true).Select(i => i.sprite),
                    Is.EqualTo(expected.Select(n => source.Resolve(n.SpriteId))));
                Assert.That(textPrefab.GetComponentsInChildren<RectTransform>(true).Length, Is.EqualTo(plan.Nodes.Count + 2));
                Assert.That(result.SkippedTextLayers, Is.Zero, "主动排除不能统计成不支持的文本。");
                Assert.That(Directory.Exists(folder + "/WithoutText Fonts"), Is.False);
                Assert.That(Directory.Exists(folder + "/WithoutText Materials"), Is.False);
                var content = textPrefab.transform.GetChild(0);
                var actualImages = textPrefab.GetComponentsInChildren<Image>(true);
                for (int i = 0; i < expected.Length; i++)
                {
                    var corners = new Vector3[4];
                    actualImages[i].rectTransform.GetWorldCorners(corners);
                    var topLeft = content.InverseTransformPoint(corners[1]);
                    Assert.That(topLeft.x + document.Width / 2f, Is.EqualTo(expected[i].Bounds.X).Within(0.01f));
                    Assert.That(document.Height / 2f - topLeft.y, Is.EqualTo(expected[i].Bounds.Y).Within(0.01f));
                }
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
