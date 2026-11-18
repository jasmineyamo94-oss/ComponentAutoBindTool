using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using UnityEditor;
using UnityEditor.U2D.PSD;
using PsdUiWorkbench.Presentation;
using UnityEngine;
using UnityEngine.TestTools;
using System.Collections;
using UnityEngine.UIElements;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdWorkbenchTests
    {
        private static PsdNode Node(int id, int? parent = null, int order = 0, string name = "同名", bool group = false, bool flatten = false)
            => new PsdNode(id, parent, name, group, true, true, flatten, group && !flatten ? "" : "sprite" + id,
                group && !flatten ? default : new PixelRect(10, 20, 30, 40), order);
        private static PsdDocumentSnapshot Document(params PsdNode[] nodes) => new PsdDocumentSnapshot("asset", 100, 200, nodes);
        [Test]
        public void CoordinatesUseDocumentHeightRatherThanAtlasPosition()
        {
            var r = PixelRect.FromBottomLeft(426, 1946, 229, 35, 2400);
            Assert.That(r.Y, Is.EqualTo(419));
            Assert.That(r.Contains(426, 419), Is.True);
            Assert.That(r.Contains(655, 419), Is.False);
        }
        [Test]
        public void OverlapPicksTopmostAndGroupsUseDescendantBounds()
        {
            var session = new PsdSession();
            session.Refresh(Document(Node(1, group: true), Node(2, 1, 1), Node(3, 1, 2)));
            Assert.That(session.HitTest(15, 25), Is.EqualTo(2));
            Assert.That(session.Document.GetBounds(1).Width, Is.EqualTo(30));
        }
        [Test]
        public void FlattenedGroupDoesNotDrawChildrenTwice()
        {
            var doc = Document(Node(1, group: true, flatten: true), Node(2, 1, 1));
            Assert.That(doc.DrawNodes.Select(n => n.Id), Is.EqualTo(new[] { 1 }));
        }
        [Test]
        public void SelectionUsesIdentityAcrossRenameAndReorderAndClearsOnDelete()
        {
            var session = new PsdSession();
            session.Refresh(Document(Node(1), Node(2, order: 1))); session.Select(2);
            session.Refresh(Document(Node(2, order: 0, name: "新名称"), Node(1, order: 1)));
            Assert.That(session.SelectedId, Is.EqualTo(2));
            session.Refresh(Document(Node(1))); Assert.That(session.SelectedId, Is.Null);
        }
        [Test]
        public void InvalidHierarchyIsRejected()
        {
            Assert.Throws<System.ArgumentException>(() => Document(Node(1, 2), Node(2, 1)));
        }
        [Test]
        public void MissingAssetReturnsDiagnostic()
        {
            var result = new UnityPsdDocumentSource().Load("00000000000000000000000000000000");
            Assert.That(result.Document, Is.Null); Assert.That(result.Diagnostic, Is.Not.Empty);
        }
        [Test]
        public void BattleDocumentMatchesOriginalLayout()
        {
            const string path = "Assets/Psd/战斗-主菜单界面.psd";
            if (!(AssetImporter.GetAtPath(path) is PSDImporter)) Assert.Ignore("验收 PSD 未使用官方导入器。");
            var source = new UnityPsdDocumentSource();
            var result = source.Load(AssetDatabase.AssetPathToGUID(path));
            Assert.That(result.Document, Is.Not.Null, result.Diagnostic);
            Assert.That(result.Document.Width, Is.EqualTo(1080));
            Assert.That(result.Document.Height, Is.EqualTo(2400));
            var energy = result.Document.Nodes.Single(n => n.Name == "Energy Level");
            Assert.That(energy.Bounds.X, Is.EqualTo(426)); Assert.That(energy.Bounds.Y, Is.EqualTo(419));
            foreach (var node in result.Document.DrawNodes) Assert.That(source.Resolve(node.SpriteId), Is.Not.Null);
        }
        [UnityTest]
        public IEnumerator TemporaryCopySupportsVisibilityUndoRenameReimportAndExternalInspector()
        {
            const string sourcePath = "Assets/Psd/战斗-主菜单界面.psd";
            if (!(AssetImporter.GetAtPath(sourcePath) is PSDImporter)) Assert.Ignore("缺少官方导入的验收资源。");
            string folder = "Assets/PsdUiWorkbench/Tests/Temporary_" + System.Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/PsdUiWorkbench/Tests", System.IO.Path.GetFileName(folder));
            string path = folder + "/copy.psd";
            string guid = null;
            PsdWorkbenchWindow window = null;
            PSDImporterEditor officialEditor = null;
            var priorInspectors = Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w => w.GetType().Name == "PropertyEditor").ToArray();
            int psdImports = 0;
            System.Action<string[]> observe = paths => psdImports += paths.Count(p => p == path);
            try
            {
                Assert.That(AssetDatabase.CopyAsset(sourcePath, path), Is.True);
                guid = AssetDatabase.AssetPathToGUID(path);
                window = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
                window.ShowUtility();
                yield return null;
                window.OpenAsset(path);
                yield return null;
                yield return WaitUntilIdle(window);
                int selected = window.Session.Document.Nodes.First(n => n.Name == "Energy Level").Id;
                window.SelectLayer(selected);
                Assert.That(AssetDatabase.LoadAssetAtPath<PsdVisibilityConfig>(PsdVisibilityRepository.GetConfigPath(guid)), Is.Null, "首次打开不能创建配置。");
                byte[] psdBefore = System.IO.File.ReadAllBytes(path);
                byte[] metaBefore = System.IO.File.ReadAllBytes(path + ".meta");
                string settingsBefore = EditorJsonUtility.ToJson(AssetImporter.GetAtPath(path));
                var overrideBefore = AssetDatabase.GetImporterOverride(path);
                PsdAssetChanges.Changed += observe;
                Undo.IncrementCurrentGroup();
                window.SetNodeVisible(selected, false);
                Undo.FlushUndoRecordObjects();
                yield return null;
                Assert.That(window.SelectedLayerId, Is.EqualTo(selected));
                Assert.That(window.Session.GetVisibleBounds(selected).IsValid, Is.False);
                Assert.That(AssetDatabase.LoadAssetAtPath<PsdVisibilityConfig>(PsdVisibilityRepository.GetConfigPath(guid)), Is.Not.Null);
                Undo.IncrementCurrentGroup();
                Undo.PerformUndo();
                yield return null;
                Assert.That(window.Session.IsNodeEnabled(selected), Is.True, "Undo 应恢复显示。");
                Undo.PerformRedo();
                yield return null;
                Assert.That(window.Session.IsNodeEnabled(selected), Is.False, "Redo 应再次隐藏。");
                var selectedIds = new[] { selected, window.Session.Document.DrawNodes.First(n => n.Id != selected).Id };
                window.SelectLayers(selectedIds, selected);
                window.SetNodesExportable(selectedIds, false);
                window.Close();
                window = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
                window.ShowUtility();
                yield return null;
                window.OpenAsset(path);
                Assert.That(window.Session.IsNodeEnabled(selected), Is.False, "重开应恢复配置。");
                Assert.That(window.SelectedLayerId, Is.EqualTo(selected), "重开应恢复图层选择。");
                Assert.That(window.Session.SelectedIds, Is.EquivalentTo(selectedIds), "重开应恢复多选。");
                Assert.That(window.Session.CaptureExport().ExcludedLayerIds, Is.EquivalentTo(selectedIds));
                window.SelectLayers(selectedIds, selected);
                Assert.That(System.IO.File.ReadAllBytes(path), Is.EqualTo(psdBefore));
                Assert.That(System.IO.File.ReadAllBytes(path + ".meta"), Is.EqualTo(metaBefore));
                Assert.That(EditorJsonUtility.ToJson(AssetImporter.GetAtPath(path)), Is.EqualTo(settingsBefore));
                Assert.That(AssetDatabase.GetImporterOverride(path), Is.EqualTo(overrideBefore));
                Assert.That(psdImports, Is.Zero, "显隐和 Undo/Redo 不得触发 PSD 导入。");
                PsdAssetChanges.Changed -= observe;
                string renamed = folder + "/renamed.psd";
                Assert.That(AssetDatabase.MoveAsset(path, renamed), Is.Empty);
                path = renamed;
                yield return null;
                yield return WaitUntilIdle(window);
                Assert.That(window.SelectedLayerId, Is.EqualTo(selected));
                Assert.That(window.Session.SelectedIds, Is.EquivalentTo(selectedIds));
                Assert.That(window.Session.CaptureExport().ExcludedLayerIds, Is.EquivalentTo(selectedIds));
                Assert.That(window.Session.IsNodeEnabled(selected), Is.False);
                window.OpenOfficialInspector();
                Assert.That(Resources.FindObjectsOfTypeAll<EditorWindow>().Any(w => w.GetType().Name == "PropertyEditor" && !priorInspectors.Contains(w)), Is.True);
                // 原生窗口可能已经持有此 Importer 的 Editor；先释放本测试打开的窗口，避免重复所有权。
                foreach (var inspector in Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w => w.GetType().Name == "PropertyEditor" && !priorInspectors.Contains(w))) inspector.Close();
                yield return null;
                Assert.That(Resources.FindObjectsOfTypeAll<Editor>().OfType<PSDImporterEditor>().Any(editor => editor.target == AssetImporter.GetAtPath(path)), Is.False,
                    "测试接管 Editor 前，原生窗口必须先释放同一 Importer。");
                // EditMode Runner 中原生 Inspector 的跟踪器不保证构建；此处由测试拥有官方 Editor。
                // 外置窗口实际构建和 Apply 另用常规 Editor 会话验收，生产窗口不创建 Editor。
                officialEditor = (PSDImporterEditor)Editor.CreateEditor(AssetImporter.GetAtPath(path), typeof(PSDImporterEditor));
                var property = officialEditor.serializedObject.FindProperty("m_ImportHiddenLayers");
                bool next = !property.boolValue;
                property.boolValue = next;
                officialEditor.serializedObject.ApplyModifiedProperties();
                officialEditor.SaveChanges();
                yield return null;
                yield return WaitUntilIdle(window);
                using (var applied = new SerializedObject(AssetImporter.GetAtPath(path)))
                    Assert.That(applied.FindProperty("m_ImportHiddenLayers").boolValue, Is.EqualTo(next));
                Assert.That(window.SelectedLayerId, Is.EqualTo(selected));
                Assert.That(window.Session.IsNodeEnabled(selected), Is.False, "官方 Apply 后独立显隐仍生效。");
                Assert.That(window.Session.SelectedIds, Is.EquivalentTo(selectedIds), "重新导入后按稳定 ID 保留多选。");
                Assert.That(window.Session.CaptureExport().ExcludedLayerIds, Is.EquivalentTo(selectedIds));
                Assert.That(window.NodeCount, Is.GreaterThan(0));
                window.ResetVisibility();
                Assert.That(window.Session.IsNodeEnabled(selected), Is.True);
                Assert.That(window.Session.CaptureExport().ExcludedLayerIds, Is.EquivalentTo(selectedIds), "重置显示不能清除导出设置。");
            }
            finally
            {
                PsdAssetChanges.Changed -= observe;
                if (officialEditor != null) Object.DestroyImmediate(officialEditor);
                foreach (var inspector in Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w => w.GetType().Name == "PropertyEditor" && !priorInspectors.Contains(w))) inspector.Close();
                if (window != null) window.Close();
                if (guid != null) AssetDatabase.DeleteAsset(PsdVisibilityRepository.GetConfigPath(guid));
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static IEnumerator WaitUntilIdle(PsdWorkbenchWindow window)
        {
            double deadline = EditorApplication.timeSinceStartup + 10;
            while (window.IsBusy && EditorApplication.timeSinceStartup < deadline)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                window.Repaint();
                yield return null;
            }
            Assert.That(window.IsBusy, Is.False, "工作台在十秒内未完成导入刷新。");
        }

        [UnityTest]
        public IEnumerator TreeCanvasAndVirtualizedTogglesPreserveSelectionAndReuseImages()
        {
            const string path = "Assets/Psd/战斗-主菜单界面.psd";
            if (!(AssetImporter.GetAtPath(path) is PSDImporter)) Assert.Ignore("缺少验收资源。");
            var window = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
            window.ShowUtility();
            yield return null;
            // 用内存会话验证控件，避免给用户的 PSD 创建配置。
            try
            {
                window.OpenAsset(path);
                var preview = window.rootVisualElement.Q<PsdPreview>();
                var tree = window.rootVisualElement.Q<PsdLayerTree>("layers");
                Assert.That(window.rootVisualElement.Q("inspectorHost"), Is.Null);
                Assert.That(window.rootVisualElement.Q("importer"), Is.Null);
                var snapshot = window.Session.Document;
                int id = snapshot.Nodes.First(n => n.Name == "Energy Level").Id;
                preview.Fit();
                yield return null;
                var bounds = snapshot.Find(id).Bounds;
                var point = preview.Children().First().LocalToWorld(new Vector2(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
                using (var click = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0, mousePosition = point }))
                    preview.SendEvent(click);
                Assert.That(window.SelectedLayerId, Is.EqualTo(id), "画布点选应更新会话。");
                int other = snapshot.DrawNodes.First(n => n.Id != id).Id;
                tree.SetSelectionById(new[] { other });
                Assert.That(window.SelectedLayerId, Is.EqualTo(other));
                var session = new PsdSession();
                session.Refresh(snapshot);
                session.Select(id);
                // 使用新的独立树隔离配置写入。
                var testTree = new PsdLayerTree();
                tree.parent.Insert(0, testTree);
                tree.style.display = DisplayStyle.None;
                testTree.style.width = 320;
                testTree.SetDocument(session, "测试文档");
                testTree.VisibilityChanged += (nodeId, visible) =>
                {
                    if (nodeId.HasValue) session.SetNodeVisible(nodeId.Value, visible);
                    else session.SetDocumentVisible(visible);
                    testTree.RefreshItems();
                    preview.UpdateVisibility(session);
                };
                testTree.ExpandAll();
                testTree.SetSelectionById(new[] { other });
                testTree.ScrollToItemById(id);
                yield return null;
                Assert.That(testTree.columns.Count, Is.EqualTo(1), "显示开关应位于唯一的 Layers 列内。");
                var images = preview.Query<Image>(className: "layer-image").ToList().ToArray();
                var toggle = testTree.Query<Toggle>().ToList().First(t => t.userData is int layerId && layerId == id);
                Assert.That(toggle.parent.ClassListContains("layer-name-cell"), Is.True);
                Assert.That(toggle.parent.Children().First(), Is.SameAs(toggle));
                int parentId = snapshot.Find(id).ParentId.Value;
                var parentToggle = testTree.Query<Toggle>().ToList().First(t => t.userData is int layerId && layerId == parentId);
                Assert.That(toggle.worldBound.x, Is.GreaterThan(parentToggle.worldBound.x), "子层勾选应随层级向右缩进。");
                int siblingId = snapshot.Nodes.First(n => n.ParentId == parentId && n.Id != id).Id;
                var siblingToggle = testTree.Query<Toggle>().ToList().First(t => t.userData is int layerId && layerId == siblingId);
                Assert.That(toggle.worldBound.x, Is.EqualTo(siblingToggle.worldBound.x).Within(0.1f), "同级勾选应对齐。");
                testTree.style.width = 240;
                testTree.style.flexGrow = 0;
                testTree.style.flexBasis = 240;
                var nameLabel = toggle.parent.Q<Label>("name");
                nameLabel.text = new string('W', 160);
                yield return null;
                Assert.That(testTree.resolvedStyle.width, Is.EqualTo(240));
                Assert.That(nameLabel.worldBound.xMax, Is.LessThanOrEqualTo(toggle.parent.worldBound.xMax + 0.1f), "长名称应限制在单元格内。");
                Assert.That(nameLabel.resolvedStyle.textOverflow, Is.EqualTo(TextOverflow.Ellipsis));
                Assert.That(toggle.resolvedStyle.width, Is.EqualTo(18));
                nameLabel.text = snapshot.Find(id).Name;
                ClickToggle(toggle);
                Assert.That(session.IsNodeEnabled(id), Is.False);
                Assert.That(testTree.selectedItem, Is.EqualTo(snapshot.Find(other)), "点击另一行的勾选不能改变行选择。");
                Assert.That(session.SelectedId, Is.EqualTo(id));
                Assert.That(preview.Query<VisualElement>(className: "selection-outline").First().style.display.value, Is.EqualTo(DisplayStyle.None));
                foreach (var node in snapshot.Nodes)
                {
                    testTree.ScrollToItemById(node.Id);
                    yield return null;
                }
                testTree.ScrollToItemById(id);
                yield return null;
                toggle = testTree.Query<Toggle>().ToList().First(t => t.userData is int layerId && layerId == id);
                Assert.That(toggle.value, Is.False, "虚拟化复用不能覆盖独立状态。");
                toggle.value = true;
                Assert.That(session.IsNodeEnabled(id), Is.True);
                Assert.That(session.VisibleDrawNodes.Count, Is.EqualTo(snapshot.DrawNodes.Count));
                Assert.That(preview.Query<Image>(className: "layer-image").ToList(), Is.EqualTo(images), "显隐应复用 Image。");
                Assert.That(preview.Query<VisualElement>(className: "selection-outline").First().style.display.value, Is.EqualTo(DisplayStyle.Flex));
                testTree.ScrollToItemById(parentId);
                yield return null;
                parentToggle = testTree.Query<Toggle>().ToList().First(t => t.userData is int layerId && layerId == parentId);
                Assert.That(testTree.IsExpanded(parentId), Is.True);
                ClickToggle(parentToggle);
                Assert.That(testTree.IsExpanded(parentId), Is.True, "勾选组不能触发展开或折叠。");
                Assert.That(session.IsNodeEnabled(id), Is.True, "隐藏组保留子层自身勾选。");
                Assert.That(session.IsEffectivelyVisible(id), Is.False);
                toggle = testTree.Query<Toggle>().ToList().First(t => t.userData is int layerId && layerId == id);
                Assert.That(toggle.enabledInHierarchy, Is.True);
                yield return null;
                Assert.That(toggle.resolvedStyle.opacity, Is.EqualTo(1), "父级隐藏不应将子层勾选置灰。");
                Assert.That(toggle.parent.Q<Label>("name").resolvedStyle.opacity, Is.EqualTo(0.5f));
                ClickToggle(toggle);
                Assert.That(session.IsNodeEnabled(id), Is.False, "父级隐藏期间仍可修改子级。");
                ClickToggle(parentToggle);
                Assert.That(session.IsNodeEnabled(parentId), Is.True);
                Assert.That(session.IsEffectivelyVisible(id), Is.False, "恢复父级不能覆盖子级的新组合。");
                Assert.That(testTree.selectedItem, Is.EqualTo(snapshot.Find(other)));
            }
            finally
            {
                window.Close();
            }
            yield return null;
            Assert.That(window == null, Is.True);
        }

        private static void ClickToggle(Toggle toggle)
        {
            var point = toggle.worldBound.center;
            using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0, mousePosition = point })) toggle.SendEvent(down);
            using (var up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, button = 0, mousePosition = point })) toggle.SendEvent(up);
        }
    }
}
