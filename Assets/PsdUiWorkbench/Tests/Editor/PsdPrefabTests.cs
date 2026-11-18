using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdPrefabTests
    {
        private static PsdNode Layer(int id, int? parent, PixelRect bounds, int order, bool group = false, bool flatten = false, bool imported = true)
            => new PsdNode(id, parent, "同名", group, imported, true, flatten, group && !flatten ? "" : "sprite" + id, bounds, order);
        private static PsdDocumentSnapshot Document(params PsdNode[] nodes) => new PsdDocumentSnapshot("asset", 100, 200, nodes);
        [Test]
        public void PlannerKeepsBoundsAndOwnActivationAcrossHiddenAncestors()
        {
            var document = Document(Layer(1, null, default, 0, group: true),
                Layer(2, 1, default, 1, group: true), Layer(3, 2, new PixelRect(10, 20, 11, 9), 2),
                Layer(4, 1, new PixelRect(40, 50, 20, 20), 3), Layer(5, null, default, 4, imported: false));
            var plan = PsdPrefabPlanner.Build(document, new PsdVisibilityState(false, new[] {1, 4, 999}));
            var byId = plan.Nodes.ToDictionary(n => n.Id);
            Assert.That(plan.ContentActive, Is.False);
            Assert.That(byId[1].ActiveSelf, Is.False);
            Assert.That(byId[2].ActiveSelf, Is.True);
            Assert.That(byId[3].ActiveSelf, Is.True);
            Assert.That(byId[4].ActiveSelf, Is.False);
            Assert.That(byId[1].Bounds.Width, Is.EqualTo(50));
            Assert.That(byId[1].LocalX, Is.EqualTo(-15));
            Assert.That(byId[1].LocalY, Is.EqualTo(55));
            Assert.That(byId[3].Bounds.Width, Is.EqualTo(11));
            Assert.That(plan.Nodes.Where(n => n.IsImage).Select(n => n.Id), Is.EqualTo(new[] {4, 3}));
            Assert.That(plan.Skipped.Single().Id, Is.EqualTo(5));
            var visible = PsdPrefabPlanner.Build(document, new PsdVisibilityState());
            Assert.That(visible.Nodes.Select(n => n.Bounds), Is.EqualTo(plan.Nodes.Select(n => n.Bounds)));
        }
        [Test]
        public void PlannerHandlesHalfPixelsAndMergedGroupsWithoutDuplicateChildren()
        {
            var document = Document(Layer(1, null, new PixelRect(10, 20, 11, 9), 0, group: true, flatten: true),
                Layer(2, 1, new PixelRect(10, 20, 1, 1), 1));
            var plan = PsdPrefabPlanner.Build(document, new PsdVisibilityState());
            Assert.That(plan.Nodes.Count, Is.EqualTo(1));
            Assert.That(plan.Nodes[0].LocalX, Is.EqualTo(-34.5f));
            Assert.That(plan.Nodes[0].LocalY, Is.EqualTo(75.5f));
            Assert.That(plan.Skipped.Single().Reason, Does.Contain("合并"));
            Assert.Throws<InvalidOperationException>(() => PsdPrefabPlanner.Build(Document(Layer(3, null, default, 0, imported: false)), new PsdVisibilityState()));
        }
        [Test]
        public void PlannerRejectsHierarchyThatWouldChangeDrawOrder()
        {
            var document = Document(Layer(1, null, default, 0, group: true), Layer(2, null, new PixelRect(0, 0, 1, 1), 1),
                Layer(3, 1, new PixelRect(0, 0, 1, 1), 2));
            Assert.Throws<InvalidOperationException>(() => PsdPrefabPlanner.Build(document, new PsdVisibilityState()));
        }
        private static string Hash(string path)
        {
            if (!File.Exists(path)) return "不存在";
            using var hash = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToBase64String(hash.ComputeHash(stream));
        }
        [Test]
        public void BothOfficialAndWorkbenchImportersCanExport()
        {
            string folder = "Assets/PsdUiWorkbench/Tests/PrefabModes_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/PsdUiWorkbench/Tests", Path.GetFileName(folder));
            string path = folder + "/copy.psd";
            try
            {
                Assert.That(AssetDatabase.CopyAsset("Assets/Psd/战斗-主菜单界面.psd", path), Is.True);
                foreach (bool workbench in new[] {false, true})
                {
                    PsdImporterMigration.Switch(path, workbench);
                    var source = new UnityPsdDocumentSource();
                    var loaded = source.Load(AssetDatabase.AssetPathToGUID(path));
                    Assert.That(loaded.Document, Is.Not.Null, loaded.Diagnostic);
                    var plan = PsdPrefabPlanner.Build(loaded.Document, new PsdVisibilityState());
                    var result = PsdPrefabExporter.Export(plan, folder + "/mode.prefab", source.Resolve);
                    VerifyPrefab(result.AssetPath, plan, source);
                }
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
        private static void VerifyPrefab(string path, PsdPrefabPlan plan, UnityPsdDocumentSource source)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(asset.transform.localScale, Is.EqualTo(Vector3.one));
            Assert.That(asset.activeSelf, Is.True);
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Assert.That(root.GetComponent<Canvas>().renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
                Assert.That(root.GetComponentsInChildren<Canvas>(true).Length, Is.EqualTo(1));
                Assert.That(root.GetComponent<UnityEngine.UI.GraphicRaycaster>(), Is.Not.Null);
                var scaler = root.GetComponent<UnityEngine.UI.CanvasScaler>();
                Assert.That(scaler.uiScaleMode, Is.EqualTo(UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize));
                Assert.That(scaler.referenceResolution, Is.EqualTo(new Vector2(plan.Width, plan.Height)));
                Assert.That(scaler.matchWidthOrHeight, Is.EqualTo(0.5f));
                Assert.That(scaler.referencePixelsPerUnit, Is.EqualTo(100));
                var content = (RectTransform)root.transform.GetChild(0);
                Assert.That(content.name, Is.EqualTo("Content"));
                Assert.That(content.sizeDelta, Is.EqualTo(new Vector2(plan.Width, plan.Height)));
                Assert.That(content.gameObject.activeSelf, Is.EqualTo(plan.ContentActive));
                var rects = content.GetComponentsInChildren<RectTransform>(true).Skip(1).ToArray();
                Assert.That(rects.Length, Is.EqualTo(plan.Nodes.Count));
                var transforms = new Dictionary<int, RectTransform>();
                var corners = new Vector3[4];
                for (int i = 0; i < plan.Nodes.Count; i++)
                {
                    var node = plan.Nodes[i];
                    var rect = rects[i];
                    transforms.Add(node.Id, rect);
                    Assert.That(rect.name, Is.EqualTo(node.Name));
                    Assert.That(rect.parent, Is.EqualTo(node.ParentId.HasValue ? transforms[node.ParentId.Value] : content));
                    Assert.That(rect.GetSiblingIndex(), Is.EqualTo(node.SiblingIndex));
                    Assert.That(rect.anchorMin, Is.EqualTo(new Vector2(.5f, .5f)));
                    Assert.That(rect.anchorMax, Is.EqualTo(rect.anchorMin));
                    Assert.That(rect.pivot, Is.EqualTo(rect.anchorMin));
                    Assert.That(rect.localScale, Is.EqualTo(Vector3.one));
                    Assert.That(rect.localRotation, Is.EqualTo(Quaternion.identity));
                    Assert.That(rect.gameObject.activeSelf, Is.EqualTo(node.ActiveSelf));
                    rect.GetWorldCorners(corners);
                    Vector3 topLeft = content.InverseTransformPoint(corners[1]);
                    Vector3 bottomRight = content.InverseTransformPoint(corners[3]);
                    Assert.That(topLeft.x + plan.Width / 2f, Is.EqualTo(node.Bounds.X).Within(.01), "横坐标 ID " + node.Id);
                    Assert.That(plan.Height / 2f - topLeft.y, Is.EqualTo(node.Bounds.Y).Within(.01), "纵坐标 ID " + node.Id);
                    Assert.That(bottomRight.x - topLeft.x, Is.EqualTo(node.Bounds.Width).Within(.01));
                    Assert.That(topLeft.y - bottomRight.y, Is.EqualTo(node.Bounds.Height).Within(.01));
                    var image = rect.GetComponent<UnityEngine.UI.Image>();
                    Assert.That(image != null, Is.EqualTo(node.IsImage));
                    if (image == null) continue;
                    Assert.That(image.sprite, Is.EqualTo(source.Resolve(node.SpriteId)));
                    Assert.That(image.type, Is.EqualTo(UnityEngine.UI.Image.Type.Simple));
                    Assert.That(image.preserveAspect || image.useSpriteMesh || image.raycastTarget, Is.False);
                    Assert.That(image.color, Is.EqualTo(Color.white));
                }
                Assert.That(root.GetComponentsInChildren<UnityEngine.UI.Image>(true).Select(i => i.sprite),
                    Is.EqualTo(plan.Nodes.Where(n => n.IsImage).Select(n => source.Resolve(n.SpriteId))));
                Assert.That(root.GetComponentsInChildren<UnityEngine.UI.LayoutGroup>(true), Is.Empty);
                Assert.That(root.GetComponentsInChildren<UnityEngine.UI.Mask>(true), Is.Empty);
                Assert.That(root.GetComponentsInChildren<UnityEngine.EventSystems.EventSystem>(true), Is.Empty);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        [UnityTest]
        public IEnumerator BattleExportPreservesLayoutVisibilityReferencesAndSourceAssets()
        {
            const string sourcePath = "Assets/Psd/战斗-主菜单界面.psd";
            string guid = AssetDatabase.AssetPathToGUID(sourcePath);
            var source = new UnityPsdDocumentSource();
            var loaded = source.Load(guid);
            Assert.That(loaded.Document, Is.Not.Null, loaded.Diagnostic);
            string folder = "Assets/PsdUiWorkbench/Tests/Prefab_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/PsdUiWorkbench/Tests", Path.GetFileName(folder));
            var currentScene = SceneManager.GetActiveScene();
            bool dirty = currentScene.isDirty;
            int sceneCount = EditorSceneManager.previewSceneCount;
            int[] roots = currentScene.GetRootGameObjects().Select(g => g.GetInstanceID()).ToArray();
            string sourceHash = Hash(sourcePath), metaHash = Hash(sourcePath + ".meta");
            string configPath = PsdVisibilityRepository.GetConfigPath(guid);
            string configHash = Hash(configPath);
            string importerJson = EditorJsonUtility.ToJson(AssetImporter.GetAtPath(sourcePath));
            var originalOverride = AssetDatabase.GetImporterOverride(sourcePath);
            try
            {
                var plan = PsdPrefabPlanner.Build(loaded.Document, new PsdVisibilityState(true, new[] {1093, 1091}));
                var result = PsdPrefabExporter.Export(plan, folder + "/battle.prefab", source.Resolve);
                Assert.That(result.ImageCount, Is.EqualTo(loaded.Document.DrawNodes.Count));
                VerifyPrefab(result.AssetPath, plan, source);
                Assert.That(currentScene.isDirty, Is.EqualTo(dirty));
                Assert.That(currentScene.GetRootGameObjects().Select(g => g.GetInstanceID()), Is.EqualTo(roots));
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(sceneCount));
                string prefabHash = Hash(result.AssetPath);
                var second = PsdPrefabExporter.Export(plan, result.AssetPath, source.Resolve);
                Assert.That(second.AssetPath, Is.Not.EqualTo(result.AssetPath));
                Assert.That(Hash(result.AssetPath), Is.EqualTo(prefabHash));
                Assert.Throws<InvalidOperationException>(() => PsdPrefabExporter.Export(plan, folder + "/missing.prefab", _ => null));
                Assert.That(File.Exists(folder + "/missing.prefab"), Is.False);
                Assert.Throws<ArgumentException>(() => PsdPrefabExporter.Export(plan, "../outside.prefab", source.Resolve));
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(sceneCount));
                var off = PsdPrefabPlanner.Build(loaded.Document, new PsdVisibilityState(false, new[] {1093}));
                var offResult = PsdPrefabExporter.Export(off, folder + "/off.prefab", source.Resolve);
                VerifyPrefab(offResult.AssetPath, off, source);
                var prefabRoot = PrefabUtility.LoadPrefabContents(offResult.AssetPath);
                try
                {
                    var content = prefabRoot.transform.GetChild(0);
                    Assert.That(content.gameObject.activeSelf, Is.False);
                    content.gameObject.SetActive(true);
                    var all = content.GetComponentsInChildren<RectTransform>(true).Skip(1).ToArray();
                    var group = all[off.Nodes.ToList().FindIndex(n => n.Id == 1093)];
                    var text = all[off.Nodes.ToList().FindIndex(n => n.Id == 1091)];
                    Assert.That(group.gameObject.activeSelf, Is.False);
                    Assert.That(text.gameObject.activeSelf, Is.True);
                    Assert.That(text.gameObject.activeInHierarchy, Is.False);
                    group.gameObject.SetActive(true);
                    Assert.That(text.gameObject.activeInHierarchy, Is.True);
                }
                finally { PrefabUtility.UnloadPrefabContents(prefabRoot); }
                yield return null;
                Assert.That(Hash(sourcePath), Is.EqualTo(sourceHash));
                Assert.That(Hash(sourcePath + ".meta"), Is.EqualTo(metaHash));
                Assert.That(Hash(configPath), Is.EqualTo(configHash));
                Assert.That(EditorJsonUtility.ToJson(AssetImporter.GetAtPath(sourcePath)), Is.EqualTo(importerJson));
                Assert.That(AssetDatabase.GetImporterOverride(sourcePath), Is.EqualTo(originalOverride));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
