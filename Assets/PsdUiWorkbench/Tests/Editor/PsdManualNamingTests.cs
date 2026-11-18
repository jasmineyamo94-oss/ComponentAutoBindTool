using System;
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
    public sealed class PsdManualNamingTests
    {
        private static PsdNode Layer(int id, string name, bool imported = true)
            => new PsdNode(id, null, name, false, imported, true, false,
                imported ? "sprite" + id : "", new PixelRect(10, 20, 30, 40), id);

        private static PsdDocumentSnapshot Document(string guid, params PsdNode[] nodes)
            => new PsdDocumentSnapshot(guid, 100, 200, nodes);

        private static TextField Input(PsdNodeRenameWindow window, int id)
            => window.rootVisualElement.Q<TextField>("nodeName_" + id);

        private static void DeleteNames(string guid)
        {
            string path = PsdNodeNameRepository.GetConfigPath(guid);
            var config = AssetDatabase.LoadAssetAtPath<PsdNodeNameConfig>(path);
            if (config != null) Undo.ClearUndo(config);
            AssetDatabase.DeleteAsset(path);
        }

        [Test]
        public void DocumentRootNameDefaultsToEnglishAndPersistsWithoutRenamingPsdLayers()
        {
            string guid = Guid.NewGuid().ToString("N");
            var owner = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
            PsdNodeRenameWindow rename = null;
            try
            {
                var document = Document(guid, Layer(1, "中文图层"));
                owner.Session.Refresh(document);
                Assert.That(owner.Session.GetDocumentRootName(), Is.EqualTo("UIRoot"));
                rename = owner.OpenDocumentRootRename();
                rename.CreateGUI();
                Assert.That(rename.rootVisualElement.Q<TextField>("nodeName_root").value, Is.EqualTo("UIRoot"));
                rename.rootVisualElement.Q<TextField>("nodeName_root").value = "BattleMainMenu";
                Assert.That(rename.Submit(), Is.True, rename.Error);
                Assert.That(owner.Session.GetDocumentRootName(), Is.EqualTo("BattleMainMenu"));
                Assert.That(owner.Session.GetNodeName(1), Is.EqualTo("中文图层"));
                Assert.That(PsdPrefabPlanner.Build(document, new PsdVisibilityState(), names: owner.Session.CaptureNames()).RootName,
                    Is.EqualTo("BattleMainMenu"));
                using (var repository = new PsdNodeNameRepository())
                    Assert.That(repository.Load(guid).RootName, Is.EqualTo("BattleMainMenu"));
                Undo.PerformUndo();
                Assert.That(owner.Session.GetDocumentRootName(), Is.EqualTo("UIRoot"));
                Undo.PerformRedo();
                Assert.That(owner.Session.GetDocumentRootName(), Is.EqualTo("BattleMainMenu"));
                Assert.That(owner.SetDocumentRootName("中文主界面"), Is.False, "文档根必须保持英文命名。");
                Assert.That(owner.Session.SetNodeNames(new System.Collections.Generic.Dictionary<int, string>
                    { [1] = "Equipment" }), Is.True);
                Assert.That(owner.Session.GetDocumentRootName(), Is.EqualTo("BattleMainMenu"),
                    "普通图层重命名不能丢失文档根名称。");
                Assert.That(owner.Session.ResetNodeNames(new[] { 1 }), Is.True);
                Assert.That(owner.Session.GetDocumentRootName(), Is.EqualTo("BattleMainMenu"));
            }
            finally
            {
                if (rename != null) rename.Close();
                UnityEngine.Object.DestroyImmediate(owner);
                DeleteNames(guid);
            }
        }

        [Test]
        public void IndependentBatchInputsSaveTogetherAndUndoWithoutChangingSourceNames()
        {
            string guid = Guid.NewGuid().ToString("N");
            var owner = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
            PsdNodeRenameWindow rename = null;
            try
            {
                owner.Session.Refresh(Document(guid, Layer(1, "Left"), Layer(2, "Right", false)));
                rename = owner.OpenNodeRename(new[] { 1, 2 });
                rename.CreateGUI();
                Assert.That(rename.rootVisualElement.Q<TextField>("sourceName_1").isReadOnly, Is.True);
                Assert.That(rename.rootVisualElement.Q<TextField>("effectiveName_2").value, Is.EqualTo("Right"));
                Input(rename, 1).value = "中文_左";
                Input(rename, 2).value = "小写 right";
                Assert.That(rename.Submit(), Is.True);
                Assert.That(owner.Session.GetNodeName(1), Is.EqualTo("中文_左"));
                Assert.That(owner.Session.GetNodeName(2), Is.EqualTo("小写 right"), "未导入节点也可重命名。");
                Assert.That(owner.Session.Document.Find(1).Name, Is.EqualTo("Left"));
                Assert.That(owner.Session.Document.Find(2).Name, Is.EqualTo("Right"));
                using (var reopened = new PsdNodeNameRepository())
                {
                    Assert.That(reopened.Load(guid).Overrides.Count, Is.EqualTo(2));
                    Assert.That(reopened.Load(guid).Overrides[2], Is.EqualTo("小写 right"));
                }
                Undo.PerformUndo();
                Assert.That(owner.Session.GetNodeName(1), Is.EqualTo("Left"));
                Assert.That(owner.Session.GetNodeName(2), Is.EqualTo("Right"));
                Undo.PerformRedo();
                Assert.That(owner.Session.GetNodeName(1), Is.EqualTo("中文_左"));
                Assert.That(owner.Session.GetNodeName(2), Is.EqualTo("小写 right"));
                owner.Session.Refresh(Document(guid, Layer(1, "ReimportedLeft"), Layer(2, "ReimportedRight", false)));
                rename = owner.OpenNodeRename(new[] { 1 });
                rename.CreateGUI();
                Assert.That(rename.rootVisualElement.Q<TextField>("sourceName_1").value, Is.EqualTo("ReimportedLeft"));
                Assert.That(rename.rootVisualElement.Q<TextField>("effectiveName_1").value, Is.EqualTo("中文_左"));
                Assert.That(Input(rename, 1).value, Is.EqualTo("中文_左"));
            }
            finally
            {
                if (rename != null) rename.Close();
                UnityEngine.Object.DestroyImmediate(owner);
                DeleteNames(guid);
            }
        }

        [TestCase("")]
        [TestCase(" \t")]
        [TestCase("bad/path")]
        [TestCase("bad\\path")]
        [TestCase("Right")]
        public void InvalidOrConflictingRowRejectsWholeBatchAndRetainsInputsForRetry(string invalid)
        {
            string guid = Guid.NewGuid().ToString("N");
            var owner = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
            PsdNodeRenameWindow rename = null;
            try
            {
                owner.Session.Refresh(Document(guid, Layer(1, "Left"), Layer(2, "Middle"), Layer(3, "Right")));
                rename = owner.OpenNodeRename(new[] { 1, 2 });
                rename.CreateGUI();
                Input(rename, 1).value = invalid;
                Input(rename, 2).value = "合法的第二行";
                Assert.That(rename.Submit(), Is.False);
                Assert.That(owner.Session.GetNodeName(1), Is.EqualTo("Left"));
                Assert.That(owner.Session.GetNodeName(2), Is.EqualTo("Middle"));
                Assert.That(Input(rename, 1).value, Is.EqualTo(invalid));
                Assert.That(Input(rename, 2).value, Is.EqualTo("合法的第二行"));
                Assert.That(rename.Error, Does.Contain("整批未应用"));
                Assert.That(AssetDatabase.LoadAssetAtPath<PsdNodeNameConfig>(PsdNodeNameRepository.GetConfigPath(guid)), Is.Null);
                Input(rename, 1).value = "合法的第一行";
                Assert.That(rename.Submit(), Is.True);
                Assert.That(owner.Session.GetNodeName(1), Is.EqualTo("合法的第一行"));
                Assert.That(owner.Session.GetNodeName(2), Is.EqualTo("合法的第二行"));
            }
            finally
            {
                if (rename != null) rename.Close();
                UnityEngine.Object.DestroyImmediate(owner);
                DeleteNames(guid);
            }
        }

        [Test]
        public void BatchNameSwapIsAtomicAndUnchangedSubmitCreatesNoConfig()
        {
            string guid = Guid.NewGuid().ToString("N");
            var owner = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
            PsdNodeRenameWindow rename = null;
            try
            {
                owner.Session.Refresh(Document(guid, Layer(1, "Left"), Layer(2, "Right")));
                long version = owner.Session.ContextVersion;
                rename = owner.OpenNodeRename(new[] { 1, 2 });
                rename.CreateGUI();
                Assert.That(rename.Submit(), Is.True);
                Assert.That(owner.Session.ContextVersion, Is.EqualTo(version));
                Assert.That(AssetDatabase.LoadAssetAtPath<PsdNodeNameConfig>(PsdNodeNameRepository.GetConfigPath(guid)), Is.Null);
                rename = owner.OpenNodeRename(new[] { 1, 2 });
                rename.CreateGUI();
                Input(rename, 1).value = "Right";
                Input(rename, 2).value = "Left";
                Assert.That(rename.Submit(), Is.True);
                Assert.That(owner.Session.GetNodeName(1), Is.EqualTo("Right"));
                Assert.That(owner.Session.GetNodeName(2), Is.EqualTo("Left"));
            }
            finally
            {
                if (rename != null) rename.Close();
                UnityEngine.Object.DestroyImmediate(owner);
                DeleteNames(guid);
            }
        }

        [TestCase("document")]
        [TestCase("missing")]
        [TestCase("visibility")]
        [TestCase("names")]
        [TestCase("reload")]
        public void StaleWindowNeverWritesAfterSessionContextChanges(string change)
        {
            string guid = Guid.NewGuid().ToString("N");
            string otherGuid = Guid.NewGuid().ToString("N");
            var owner = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
            PsdNodeRenameWindow rename = null;
            try
            {
                owner.Session.Refresh(Document(guid, Layer(1, "Original"), Layer(2, "Other")));
                rename = owner.OpenNodeRename(new[] { 1 });
                rename.CreateGUI();
                Input(rename, 1).value = "旧窗口不应写入";
                switch (change)
                {
                    case "document":
                        owner.Session.Refresh(Document(otherGuid, Layer(1, "NewDocument")));
                        break;
                    case "missing":
                        owner.Session.Refresh(Document(guid, Layer(2, "Other")));
                        break;
                    case "visibility":
                        owner.Session.SetNodeVisible(1, false);
                        break;
                    case "names":
                        owner.SetNodeNames(new System.Collections.Generic.Dictionary<int, string> { { 1, "外部命名" } });
                        break;
                    default:
                        owner.Session.Refresh(Document(guid, Layer(1, "Reimported"), Layer(2, "Other")));
                        break;
                }
                Assert.That(rename.Submit(), Is.False);
                Assert.That(rename.Error, Does.Contain("上下文已改变"));
                Assert.That(rename.rootVisualElement.Q<Button>("confirmRename").enabledSelf, Is.False);
                Assert.That(owner.Session.GetNodeName(1), Is.Not.EqualTo("旧窗口不应写入"));
                using (var reopened = new PsdNodeNameRepository())
                {
                    Assert.That(reopened.Load(otherGuid).Overrides, Is.Empty);
                    Assert.That(reopened.Load(guid).Overrides.Values, Does.Not.Contain("旧窗口不应写入"));
                }
            }
            finally
            {
                if (rename != null) rename.Close();
                UnityEngine.Object.DestroyImmediate(owner);
                DeleteNames(guid);
                DeleteNames(otherGuid);
                string visibilityPath = PsdVisibilityRepository.GetConfigPath(guid);
                var visibility = AssetDatabase.LoadAssetAtPath<PsdVisibilityConfig>(visibilityPath);
                if (visibility != null) Undo.ClearUndo(visibility);
                AssetDatabase.DeleteAsset(visibilityPath);
            }
        }

        [Test]
        public void OwnerClosesItsRenameWindowOnReplacementDisableAndGuiRebuild()
        {
            string guid = Guid.NewGuid().ToString("N");
            var owner = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
            PsdNodeRenameWindow rename = null;
            try
            {
                owner.Session.Refresh(Document(guid, Layer(1, "One"), Layer(2, "Two")));
                Assert.That(owner.OpenNodeRename(new[] { 999 }), Is.Null);
                var first = owner.OpenNodeRename(new[] { 1 });
                rename = owner.OpenNodeRename(new[] { 2 });
                Assert.That(first == null, Is.True, "同一工作台只保留自己的一个重命名窗口。");
                owner.CreateGUI();
                Assert.That(rename == null, Is.True, "重建工作台界面必须关闭旧重命名窗口。");
                owner.Session.Refresh(Document(guid, Layer(1, "One")));
                rename = owner.OpenNodeRename(new[] { 1 });
                UnityEngine.Object.DestroyImmediate(owner);
                Assert.That(rename == null, Is.True, "关闭工作台不得遗留重命名窗口。");
            }
            finally
            {
                if (rename != null) rename.Close();
                if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
                DeleteNames(guid);
            }
        }

        [UnityTest]
        public IEnumerator RightClickF2EnterEscapeAndRecycledRowsKeepCorrectTargets()
        {
            string guid = Guid.NewGuid().ToString("N");
            var owner = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
            var host = ScriptableObject.CreateInstance<RenameTreeHostWindow>();
            PsdNodeRenameWindow rename = null;
            host.ShowUtility();
            host.position = new Rect(200, 200, 460, 300);
            yield return null;
            try
            {
                host.rootVisualElement.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(
                    "Assets/PsdUiWorkbench/Editor/Presentation/PsdWorkbenchWindow.uss"));
                owner.Session.Refresh(Document(guid, Enumerable.Range(1, 80)
                    .Select(id => Layer(id, "Source" + id, id != 2)).ToArray()));
                var tree = new PsdLayerTree();
                tree.style.width = 420;
                tree.style.height = 180;
                tree.style.flexBasis = 180;
                tree.style.flexGrow = 0;
                tree.style.flexShrink = 0;
                host.rootVisualElement.Add(tree);
                tree.SetDocument(owner.Session, "测试文档");
                tree.ExpandAll();
                int requests = 0;
                tree.RenameRequested += ids =>
                {
                    requests++;
                    rename = ids.Count == 1 && ids[0] == tree.DocumentRootId ?
                        owner.OpenDocumentRootRename() : owner.OpenNodeRename(ids);
                };
                yield return WaitForRows(tree, 1, 2);
                tree.SetSelectionById(new[] { 1, 2 });
                Execute(OpenMenu(tree, 1), "批量重命名…");
                yield return null;
                Assert.That(tree.selectedIds, Is.EquivalentTo(new[] { 1, 2 }));
                Assert.That(Input(rename, 1), Is.Not.Null);
                Assert.That(Input(rename, 2), Is.Not.Null);
                Input(rename, 1).value = "取消这次修改";
                SendKey(Input(rename, 1), KeyCode.Escape);
                Assert.That(rename == null, Is.True);
                Assert.That(owner.Session.GetNodeName(1), Is.EqualTo("Source1"));
                SendKey(tree, KeyCode.F2);
                Assert.That(requests, Is.EqualTo(1), "F2 不为多选自动猜测批量规则。");
                int rootId = tree.GetRootIds().Single();
                tree.SetSelectionById(new[] { rootId });
                SendKey(tree, KeyCode.F2);
                Assert.That(requests, Is.EqualTo(2), "F2 应打开文档根的独立重命名窗口。");
                Assert.That(rename.rootVisualElement.Q<TextField>("nodeName_root").value, Is.EqualTo("UIRoot"));
                Assert.That(OpenMenu(tree, rootId).MenuItems().OfType<DropdownMenuAction>()
                    .Any(action => action.name == "重命名根节点…"), Is.True);
                int[] aiTargetIds = null;
                bool aiDescendants = false;
                tree.AiNamingRequested += (ids, descendants) =>
                {
                    aiTargetIds = ids.ToArray();
                    aiDescendants = descendants;
                };
                Execute(OpenMenu(tree, rootId), "AI 命名/分析所选子树");
                Assert.That(aiTargetIds, Is.EqualTo(new[] { rootId }));
                Assert.That(aiDescendants, Is.True);
                rename.Close();
                tree.SetSelectionById(new[] { 1 });
                var editor = new TextField();
                tree.hierarchy.Add(editor);
                SendKey(editor, KeyCode.F2);
                Assert.That(requests, Is.EqualTo(2), "文本编辑中的 F2 不能被树截获。");
                editor.RemoveFromHierarchy();
                SendKey(tree, KeyCode.F2);
                yield return null;
                Assert.That(Input(rename, 1), Is.Not.Null);
                Input(rename, 1).value = "F2 后中文命名";
                SendKey(Input(rename, 1), KeyCode.Return);
                Assert.That(rename == null, Is.True);
                Assert.That(owner.Session.GetNodeName(1), Is.EqualTo("F2 后中文命名"));
                host.Focus();
                tree.Focus();
                yield return null;
                tree.ScrollToItemById(80);
                yield return WaitForRows(tree, 80);
                yield return null;
                Execute(OpenMenu(tree, 80), "重命名…");
                Assert.That(Input(rename, 80).value, Is.EqualTo("Source80"));
                Assert.That(Input(rename, 1), Is.Null, "回收行不能残留旧节点的操作目标。");
                rename.Close();
                host.Focus();
                tree.Focus();
                yield return null;
                tree.ScrollToItemById(1);
                yield return WaitForRows(tree, 1);
                yield return null;
                Execute(OpenMenu(tree, 1), "重命名…");
                Assert.That(Input(rename, 1).value, Is.EqualTo("F2 后中文命名"));
            }
            finally
            {
                if (rename != null) rename.Close();
                host.Close();
                UnityEngine.Object.DestroyImmediate(owner);
                DeleteNames(guid);
            }
        }

        public sealed class RenameTreeHostWindow : EditorWindow { }

        private static void SendKey(VisualElement target, KeyCode code)
        {
            using (var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = code }))
            {
                evt.target = target;
                target.SendEvent(evt);
            }
        }

        private static VisualElement Row(PsdLayerTree tree, int id)
            => tree.Query<Toggle>("visibility").ToList().First(toggle => toggle.userData is int value && value == id).parent;

        private static IEnumerator WaitForRows(PsdLayerTree tree, params int[] ids)
        {
            double deadline = EditorApplication.timeSinceStartup + 5;
            while (!ids.All(id => tree.Query<Toggle>("visibility").ToList()
                .Any(toggle => toggle.userData is int value && value == id)) && EditorApplication.timeSinceStartup < deadline)
            {
                tree.MarkDirtyRepaint();
                EditorApplication.QueuePlayerLoopUpdate();
                yield return null;
            }
            Assert.That(ids.All(id => tree.Query<Toggle>("visibility").ToList()
                .Any(toggle => toggle.userData is int value && value == id)), Is.True,
                $"目标节点行未进入可见区域：布局 {tree.layout}，已附加 {tree.panel != null}，当前行 " +
                string.Join(",", tree.Query<Toggle>().ToList().Select(toggle => $"{toggle.name}:{toggle.userData}")));
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
                using (var down = PointerDownEvent.GetPooled(new Event
                {
                    type = EventType.MouseDown, button = 1, clickCount = 1, mousePosition = row.worldBound.center
                })) row.SendEvent(down);
                using (var up = PointerUpEvent.GetPooled(new Event
                {
                    type = EventType.MouseUp, button = 1, clickCount = 1, mousePosition = row.worldBound.center
                })) row.SendEvent(up);
                Assert.That(menu, Is.Not.Null, "真实右键应生成节点菜单。");
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
