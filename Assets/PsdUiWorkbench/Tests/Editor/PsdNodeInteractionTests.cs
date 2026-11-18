using System.Collections;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using PsdUiWorkbench.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdNodeInteractionTests
    {
        private static PsdNode Layer(int id, int? parent = null, bool group = false)
            => new PsdNode(id, parent, group ? "分组" : "节点" + id, group, true, true, false,
                group ? "" : "sprite" + id, group ? default : new PixelRect(10, 20, 30, 40), id);

        [Test]
        public void BatchActivationAndExportAreSeparateUndoStepsAndPersistTogether()
        {
            string guid = System.Guid.NewGuid().ToString("N");
            string path = PsdVisibilityRepository.GetConfigPath(guid);
            var repository = new PsdVisibilityRepository();
            try
            {
                var document = new PsdDocumentSnapshot(guid, 100, 200, new[] { Layer(1, group: true), Layer(2, 1), Layer(3, 1) });
                var session = new PsdSession(repository);
                session.Refresh(document);
                repository.Changed += session.RestoreVisibility;
                session.SelectMany(new[] { 2, 3 });
                session.SetNodesVisible(new[] { 2, 3, 2 }, false);
                Undo.FlushUndoRecordObjects();
                session.SetNodesExportable(new[] { 2, 3, 3 }, false);
                Undo.FlushUndoRecordObjects();
                Assert.That(session.ExportableDrawNodeCount, Is.Zero);
                Undo.PerformUndo();
                Assert.That(session.ExportableDrawNodeCount, Is.EqualTo(2), "一次撤销恢复整批导出设置。");
                Assert.That(session.CaptureVisibility().HiddenLayerIds, Is.EqualTo(new[] { 2, 3 }));
                Undo.PerformUndo();
                Assert.That(session.CaptureVisibility().HiddenLayerIds, Is.Empty, "下一次撤销恢复整批激活设置。");
                Assert.That(session.SelectedIds, Is.EqualTo(new[] { 2, 3 }));
                Undo.PerformRedo();
                Undo.PerformRedo();
                repository.Flush();
                var reopened = new PsdSession(repository);
                reopened.Refresh(document);
                Assert.That(reopened.CaptureVisibility().HiddenLayerIds, Is.EqualTo(new[] { 2, 3 }));
                Assert.That(reopened.CaptureExport().ExcludedLayerIds, Is.EqualTo(new[] { 2, 3 }));
                reopened.ResetVisibility();
                Assert.That(reopened.CaptureExport().ExcludedLayerIds, Is.EqualTo(new[] { 2, 3 }));
                Assert.That(reopened.CaptureVisibility().HiddenLayerIds, Is.Empty);
            }
            finally
            {
                repository.Dispose();
                var config = AssetDatabase.LoadAssetAtPath<PsdVisibilityConfig>(path);
                if (config != null) Undo.ClearUndo(config);
                AssetDatabase.DeleteAsset(path);
            }
        }

        [UnityTest]
        public IEnumerator RightClickBatchAndInheritedBadgesSurviveVirtualization()
        {
            var window = ScriptableObject.CreateInstance<TreeHostWindow>();
            window.minSize = new Vector2(400, 300);
            window.ShowUtility();
            window.position = new Rect(300, 200, 400, 300);
            yield return null;
            try
            {
                window.rootVisualElement.Clear();
                window.rootVisualElement.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(
                    "Assets/PsdUiWorkbench/Editor/Presentation/PsdWorkbenchWindow.uss"));
                var session = new PsdSession();
                session.Refresh(new PsdDocumentSnapshot("memory", 100, 200,
                    new[] { Layer(1, group: true), Layer(2, 1), Layer(3, 1) }.Concat(Enumerable.Range(4, 70).Select(id => Layer(id)))));
                var tree = new PsdLayerTree();
                tree.style.width = 320;
                tree.style.height = 180;
                tree.style.flexBasis = 180;
                tree.style.flexGrow = 0;
                tree.style.flexShrink = 0;
                window.rootVisualElement.Add(tree);
                tree.SetDocument(session, "内存测试");
                tree.NodesVisibilityChanged += (ids, visible) => { session.SetNodesVisible(ids, visible); tree.RefreshItems(); };
                tree.NodesExportChanged += (ids, exportable) => { session.SetNodesExportable(ids, exportable); tree.RefreshItems(); };
                tree.ExpandAll();
                yield return WaitForRows(tree, 2, 3, 4);
                ClickRow(tree, 2);
                Assert.That(tree.selectedIds, Is.EqualTo(new[] { 2 }), "单击应选择对应节点。");
                ClickRow(tree, 4, EventModifiers.Shift);
                Assert.That(tree.selectedIds, Is.EquivalentTo(new[] { 2, 3, 4 }), "Shift 应连续选择。");
                ClickRow(tree, 3, EventModifiers.Control);
                Assert.That(tree.selectedIds, Is.EquivalentTo(new[] { 2, 4 }), "Ctrl 应取消单个选择。");
                tree.SetSelectionById(new[] { 2, 3 });
                Execute(OpenMenu(tree, 2), "设为不激活");
                Assert.That(tree.selectedIds, Is.EquivalentTo(new[] { 2, 3 }), "右键已选节点不得丢弃多选。");
                Assert.That(session.IsNodeEnabled(2) || session.IsNodeEnabled(3), Is.False);
                Execute(OpenMenu(tree, 2), "设为不导出");
                Assert.That(session.CaptureExport().ExcludedLayerIds, Is.EqualTo(new[] { 2, 3 }));
                yield return null;
                session.SetNodeAnnotation(2, "组内武器按钮");
                tree.RefreshItems();
                yield return null;
                Assert.That(Row(tree, 2).Q<Label>("annotationBadge").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(Row(tree, 3).Q<Label>("annotationBadge").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                Assert.That(Row(tree, 2).Q<Label>("exportBadge").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
                session.SetNodeExportable(1, false);
                session.SetNodeExportable(3, true);
                tree.RefreshItems();
                yield return null;
                var inherited = Row(tree, 3).Q<Label>("exportBadge");
                Assert.That(inherited.ClassListContains("layer-export-inherited"), Is.True);
                Assert.That(inherited.tooltip, Does.Contain("分组"));
                session.SetNodeExportable(1, true);
                tree.RefreshItems();
                tree.ScrollToItemById(73);
                yield return null;
                tree.ScrollToItemById(2);
                yield return null;
                tree.ScrollToItemById(3);
                yield return WaitForRows(tree, 2, 3);
                Assert.That(Row(tree, 2).Q<Label>("exportBadge").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(Row(tree, 3).Q<Label>("exportBadge").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                Assert.That(Row(tree, 2).Q<Label>("annotationBadge").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(Row(tree, 3).Q<Label>("annotationBadge").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                session.SetNodeAnnotation(2, "");
                tree.RefreshItems();
                yield return null;
                Assert.That(Row(tree, 2).Q<Label>("annotationBadge").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                Assert.That(Row(tree, 3).Q<Label>("exportBadge").ClassListContains("layer-export-inherited"), Is.False);
                tree.ScrollToItemById(4);
                yield return WaitForRows(tree, 4);
                Execute(OpenMenu(tree, 4), "设为不导出");
                Assert.That(tree.selectedIds, Is.EqualTo(new[] { 4 }), "右键未选节点应切换到该节点。");
                Assert.That(session.IsNodeExportEnabled(3), Is.True);
                Assert.That(session.IsNodeExportEnabled(4), Is.False);
            }
            finally { window.Close(); }
        }

        public sealed class TreeHostWindow : EditorWindow
        {
            public void CreateGUI()
            {
                rootVisualElement.style.flexGrow = 1;
            }
        }

        private static IEnumerator WaitForRows(PsdLayerTree tree, params int[] ids)
        {
            double deadline = EditorApplication.timeSinceStartup + 5;
            while (!ids.All(id => tree.Query<Toggle>("visibility").ToList().Any(toggle => toggle.userData is int value && value == id)) &&
                EditorApplication.timeSinceStartup < deadline)
            {
                tree.MarkDirtyRepaint();
                EditorApplication.QueuePlayerLoopUpdate();
                yield return null;
            }
            Assert.That(ids.All(id => tree.Query<Toggle>("visibility").ToList().Any(toggle => toggle.userData is int value && value == id)),
                Is.True, $"节点行在布局后仍未进入可视区域：布局 {tree.layout}，已附加 {tree.panel != null}，当前行 " +
                    string.Join(",", tree.Query<Toggle>().ToList().Select(toggle => $"{toggle.name}:{toggle.userData}")));
        }

        private static VisualElement Row(PsdLayerTree tree, int id)
            => tree.Query<Toggle>("visibility").ToList().First(toggle => toggle.userData is int value && value == id).parent;

        private static void ClickRow(PsdLayerTree tree, int id, EventModifiers modifiers = EventModifiers.None)
        {
            var row = Row(tree, id);
            var input = new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = row.worldBound.center, modifiers = modifiers };
            using (var down = PointerDownEvent.GetPooled(input)) row.SendEvent(down);
            input.type = EventType.MouseUp;
            using (var up = PointerUpEvent.GetPooled(input)) row.SendEvent(up);
        }

        private static DropdownMenu OpenMenu(PsdLayerTree tree, int id)
        {
            var row = Row(tree, id);
            DropdownMenu menu = null;
            EventCallback<ContextualMenuPopulateEvent> capture = evt =>
            {
                menu = new DropdownMenu();
                menu.MenuItems().AddRange(evt.menu.MenuItems());
                evt.menu.ClearItems();
            };
            row.RegisterCallback(capture);
            try
            {
                using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 1, clickCount = 1, mousePosition = row.worldBound.center }))
                    row.SendEvent(down);
                using (var up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, button = 1, clickCount = 1, mousePosition = row.worldBound.center }))
                    row.SendEvent(up);
                Assert.That(menu, Is.Not.Null, "鼠标右键必须实际触发节点菜单。");
                menu.PrepareForDisplay(null);
                return menu;
            }
            finally { row.UnregisterCallback(capture); }
        }

        private static void Execute(DropdownMenu menu, string name)
        {
            var action = menu.MenuItems().OfType<DropdownMenuAction>().Single(item => item.name == name);
            Assert.That(action.status & DropdownMenuAction.Status.Disabled, Is.EqualTo(DropdownMenuAction.Status.None));
            action.Execute();
        }
    }
}
