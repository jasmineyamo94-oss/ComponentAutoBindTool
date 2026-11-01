using System;
using System.Collections;
using System.IO;
using System.Linq;
using ComponentAutoBindTool.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Tests
{
    public sealed class AutoBindViewTemplateDeletionTests
    {
        private string _root, _scripts, _prefab;
        private bool _autoRefreshSuppressed;

        [SetUp]
        public void SetUp()
        {
            _autoRefreshSuppressed = false;
            _root = "Assets/AATest/TemplateDiskTests" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/AATest", Path.GetFileName(_root));
            AssetDatabase.CreateFolder(_root, "Inventory");
            _scripts = _root + "/Inventory";
            _prefab = _root + "/InventoryView.prefab";
            AssetDatabase.DisallowAutoRefresh();
            _autoRefreshSuppressed = true;
        }

        [TearDown]
        public void TearDown()
        {
            TemplateDiskDeletionFault.Release();
            SessionState.SetString("AutoBind.TemplateDiskDeletion.Blocked", "");
            SessionState.SetString("AutoBind.TemplateDiskDeletion.LockFolderMeta", "");
            AssetDatabase.DeleteAsset(_root);
            if (_autoRefreshSuppressed) AssetDatabase.AllowAutoRefresh();
            _autoRefreshSuppressed = false;
        }

        private AutoBindTemplateAssetDeletion.Snapshot Locate() => AutoBindViewTemplateDeletion.Locate("Inventory", _scripts, _prefab);
        private string OrphanMeta(string name = "InventoryArgs.cs")
        {
            string path = _scripts + "/" + name + ".meta";
            File.WriteAllText(path, "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
            return path;
        }

        private string TextAsset(string name)
        {
            string path = _scripts + "/" + name + ".txt";
            File.WriteAllText(path, name);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        private static string Delete(AutoBindTemplateAssetDeletion.Snapshot snapshot)
        {
            AssetDatabase.StartAssetEditing();
            try { AutoBindTemplateAssetDeletion.Delete(snapshot, out string backup); return backup; }
            finally { AssetDatabase.StopAssetEditing(); }
        }

        [Test]
        public void LocateUsesAllFiveExactNamesWithoutAReceiptOrType()
        {
            File.WriteAllText(_scripts + "/InventoryView.cs", "// 已经编辑的宿主\n");
            string orphan = OrphanMeta();
            var preview = Locate();
            Assert.That(preview.Entries.Select(entry => Path.GetFileName(entry.Path)), Is.EqualTo(new[]
            {
                "InventoryView.cs", "InventoryPresenter.cs", "InventoryArgs.cs", "InventoryRoutes.cs", "InventoryView.BindComponents.cs", "InventoryView.prefab"
            }));
            Assert.That(preview.Entries[0].HasFile, Is.True);
            Assert.That(preview.Entries[2].HasFile, Is.False);
            Assert.That(preview.Entries[2].HasMeta, Is.True);
            Assert.That(preview.PhysicalFiles, Does.Contain(orphan));
            Assert.That(preview.Entries.Count(entry => entry.Exists), Is.EqualTo(2));
        }

        [TestCase(1)]
        [TestCase(2)]
        public void CancelAtEitherConfirmationLeavesFilesAndDirectoryUntouched(int cancelAt)
        {
            string meta = OrphanMeta();
            string original = File.ReadAllText(meta);
            int calls = 0;
            Assert.That(AutoBindViewTemplateDeletionWindow.ConfirmDeletion(Locate(), (title, message, button) => ++calls != cancelAt), Is.False);
            Assert.That(calls, Is.EqualTo(cancelAt));
            Assert.That(File.ReadAllText(meta), Is.EqualTo(original));
            Assert.That(Directory.Exists(_scripts), Is.True);
        }

        [Test]
        public void ChangeAfterSecondConfirmationStopsBeforeDeletingAnything()
        {
            string meta = OrphanMeta();
            var preview = Locate();
            int calls = 0;
            Assert.That(AutoBindViewTemplateDeletionWindow.ConfirmDeletion(preview, (title, message, button) =>
            {
                if (++calls == 2) File.AppendAllText(meta, "# 确认期间用户编辑\n");
                return true;
            }), Is.True);
            Assert.Throws<IOException>(() => Delete(preview));
            Assert.That(File.ReadAllText(meta), Does.Contain("确认期间"));
            Assert.That(Directory.Exists(_scripts), Is.True);
        }

        [Test]
        public void NewPreviouslyMissingAssetOrMetaInvalidatesTheFrozenList()
        {
            var preview = Locate();
            OrphanMeta();
            Assert.Throws<IOException>(preview.AssertUnchanged);
            var updated = Locate();
            File.WriteAllText(_scripts + "/Business.txt", "新增业务文件");
            Assert.Throws<IOException>(updated.AssertUnchanged);
        }

        [Test]
        public void ExtraFilesAndSharedFoldersArePreserved()
        {
            OrphanMeta();
            File.WriteAllText(_scripts + "/InventoryView.Extra.cs", "// 额外文件\n");
            var preview = Locate();
            Assert.That(preview.HasEmptyDirectory, Is.False);
            string backup = Delete(preview);
            Assert.That(File.Exists(_scripts + "/InventoryView.Extra.cs"), Is.True);
            Assert.That(Directory.Exists(_scripts), Is.True);
            Assert.That(Directory.Exists(backup), Is.True);
            Assert.That(AutoBindViewTemplateDeletion.Locate("Inventory", _root, _prefab).HasEmptyDirectory, Is.False);
        }

        [Test]
        public void ConfiguredSharedPrefabRootCannotBeRemovedAsAnEmptyLeaf()
        {
            var settings = AutoBindViewTemplateSettings.instance;
            string original = EditorJsonUtility.ToJson(settings);
            try
            {
                var data = new SerializedObject(settings);
                data.FindProperty("_prefabRoot").stringValue = _scripts;
                data.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(Locate().HasEmptyDirectory, Is.False);
            }
            finally { EditorJsonUtility.FromJsonOverwrite(original, settings); }
        }

        [Test]
        public void OrphanMetaAndEmptyLeafAreBackedUpIncludingFolderGuid()
        {
            string meta = OrphanMeta();
            string folderMeta = File.ReadAllText(_scripts + ".meta");
            var preview = Locate();
            Assert.That(preview.HasEmptyDirectory, Is.True);
            string backup = Delete(preview);
            Assert.That(File.Exists(meta), Is.False);
            Assert.That(Directory.Exists(_scripts), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(backup, _scripts + ".meta")), Is.EqualTo(folderMeta));
            Assert.That(File.Exists(Path.Combine(backup, meta)), Is.True);
            Assert.That(Directory.Exists(_root), Is.True);
        }

        [Test]
        public void SnapshotSerializationKeepsMissingFilesMissing()
        {
            OrphanMeta();
            var restored = JsonUtility.FromJson<AutoBindTemplateAssetDeletion.Snapshot>(JsonUtility.ToJson(Locate()));
            Assert.DoesNotThrow(restored.AssertUnchanged);
            Assert.That(restored.Entries.Count(entry => entry.Exists), Is.EqualTo(1));
            Assert.That(restored.DeletesScripts, Is.False);
        }

        [TestCase("../Outside", "Assets/UI/InventoryView.prefab")]
        [TestCase("Assets/UI", "../InventoryView.prefab")]
        [TestCase("Assets/UI", "Assets/UI/OtherView.prefab")]
        [TestCase("Assets/UI", "Assets/UI/InventoryView.unity")]
        public void OutsideOrMismatchedTargetsAreRejected(string scripts, string prefab)
        {
            Assert.Throws<ArgumentException>(() => AutoBindViewTemplateDeletion.Locate("Inventory", scripts, prefab));
        }

        [Test]
        public void DirectoryCannotPretendToBeAnAssetOrItsMeta()
        {
            Directory.CreateDirectory(_prefab);
            Assert.Throws<IOException>(() => Locate());
            Directory.Delete(_prefab);
            Directory.CreateDirectory(_scripts + "/InventoryArgs.cs.meta");
            Assert.Throws<IOException>(() => Locate());
        }

        [Test]
        public void DeleteFailureRestoresFilesAndOriginalGuids()
        {
            string first = TextAsset("First"), second = TextAsset("Second");
            string guid = AssetDatabase.AssetPathToGUID(second);
            var snapshot = AutoBindTemplateAssetDeletion.Capture(new[] { first, second }, _scripts);
            SessionState.SetString("AutoBind.TemplateDiskDeletion.Blocked", first);
            Assert.Throws<IOException>(() => Delete(snapshot));
            Assert.That(File.ReadAllText(first), Is.EqualTo("First"));
            Assert.That(File.ReadAllText(second), Is.EqualTo("Second"));
            Assert.That(AssetDatabase.AssetPathToGUID(second), Is.EqualTo(guid));
            Assert.DoesNotThrow(snapshot.AssertUnchanged);
        }

        [Test]
        public void FailureAfterLeafRemovalRestoresFolderAndItsGuid()
        {
            string asset = TextAsset("Last");
            string folderMeta = File.ReadAllText(_scripts + ".meta");
            var snapshot = AutoBindTemplateAssetDeletion.Capture(new[] { asset }, _scripts);
            SessionState.SetString("AutoBind.TemplateDiskDeletion.LockFolderMeta", asset);
            try { Assert.Throws<IOException>(() => Delete(snapshot)); }
            finally { TemplateDiskDeletionFault.Release(); }
            Assert.That(Directory.Exists(_scripts), Is.True);
            Assert.That(File.ReadAllText(_scripts + ".meta"), Is.EqualTo(folderMeta));
            Assert.That(File.ReadAllText(asset), Is.EqualTo("Last"));
            Assert.DoesNotThrow(snapshot.AssertUnchanged);
        }

        [UnityTest]
        public IEnumerator DeletionWindowShowsIndependentPathsAndReadOnlyTargets()
        {
            OrphanMeta();
            AutoBindViewTemplateDeletionWindow.Open("Inventory", _scripts, _prefab);
            var window = Resources.FindObjectsOfTypeAll<AutoBindViewTemplateDeletionWindow>().Single();
            try
            {
                yield return null;
                Assert.That(window.rootVisualElement.Q<TextField>("deleteBaseName").isReadOnly, Is.True);
                Assert.That(window.rootVisualElement.Q<TextField>("deleteScriptDirectory").isReadOnly, Is.False);
                var outputs = window.rootVisualElement.Query<TextField>().ToList().Where(field => field.name.StartsWith("deleteOutput")).ToArray();
                Assert.That(outputs.Length, Is.EqualTo(6));
                Assert.That(outputs.All(field => field.isReadOnly && Path.IsPathRooted(field.value)), Is.True);
                Assert.That(window.rootVisualElement.Q<Button>("deleteLocatedTemplate"), Is.Not.Null);
            }
            finally { window.Close(); }
        }

        [UnityTest]
        public IEnumerator EnterAndEscapeCancelTheConfirmationWindow()
        {
            foreach (var key in new[] { KeyCode.Return, KeyCode.KeypadEnter, KeyCode.Escape })
            {
                var window = ScriptableObject.CreateInstance<AutoBindViewTemplateConfirmationWindow>();
                window.Show();
                yield return null;
                var cancel = window.rootVisualElement.Q<Button>("cancelDeletionConfirmation");
                Assert.That(cancel, Is.Not.Null);
                using (var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = key })) window.rootVisualElement.SendEvent(evt);
                yield return null;
                Assert.That(window == null, Is.True, key + " 没有取消确认窗口。");
            }
        }
    }

    internal sealed class TemplateDiskDeletionFault : AssetModificationProcessor
    {
        private static FileStream s_FolderMetaLock;
        internal static void Release() { s_FolderMetaLock?.Dispose(); s_FolderMetaLock = null; }
        private static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions options)
        {
            if (SessionState.GetString("AutoBind.TemplateDiskDeletion.LockFolderMeta", "") == path)
                s_FolderMetaLock = new FileStream(Path.GetDirectoryName(path) + ".meta", FileMode.Open, FileAccess.Read, FileShare.Read);
            return SessionState.GetString("AutoBind.TemplateDiskDeletion.Blocked", "") == path ? AssetDeleteResult.FailedDelete : AssetDeleteResult.DidNotDelete;
        }
    }
}
