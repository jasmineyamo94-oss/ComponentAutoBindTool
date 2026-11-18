using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdNodeNamingTests
    {
        private sealed class Repository : IPsdNodeNameRepository
        {
            public readonly Dictionary<string, PsdNodeNameState> States = new Dictionary<string, PsdNodeNameState>();
            public int Saves;
            public bool Fail;
            public PsdNodeNameState Load(string guid) => States.TryGetValue(guid, out var state) ? state : new PsdNodeNameState();
            public void Save(string guid, PsdNodeNameState state)
            {
                if (Fail) throw new IOException("模拟命名存储失败。");
                Saves++;
                States[guid] = state;
            }
        }

        private static PsdNode Layer(int id, string name, int? parent = null, bool group = false,
            bool imported = true, bool flatten = false, PsdLayerKind kind = PsdLayerKind.NonText)
            => new PsdNode(id, parent, name, group, imported, true, flatten,
                group && !flatten ? "" : "sprite" + id, new PixelRect(10 + id, 20 + id, 30, 40), id, kind);
        private static PsdDocumentSnapshot Document(params PsdNode[] nodes)
            => new PsdDocumentSnapshot("asset", 100, 200, nodes);

        [Test]
        public void NewSiblingCollisionRejectsWholeBatchButAtomicSwapIsAllowed()
        {
            var repository = new Repository();
            var session = new PsdSession(nameRepository: repository);
            session.Refresh(Document(Layer(1, "Left"), Layer(2, "Right"), Layer(3, "Other")));
            Assert.That(session.SetNodeNames(new Dictionary<int, string> { { 1, "Right" }, { 3, "Changed" } }), Is.False);
            Assert.That(session.GetNodeName(1), Is.EqualTo("Left"));
            Assert.That(session.GetNodeName(3), Is.EqualTo("Other"));
            Assert.That(repository.Saves, Is.Zero);
            Assert.That(session.SetNodeNames(new Dictionary<int, string> { { 1, "Right" }, { 2, "Left" } }), Is.True);
            Assert.That(session.GetNodeName(1), Is.EqualTo("Right"));
            Assert.That(session.GetNodeName(2), Is.EqualTo("Left"));
            Assert.That(repository.Saves, Is.EqualTo(1));
            Assert.That(session.Document.Find(1).Name, Is.EqualTo("Left"));
            Assert.That(session.Document.Find(2).Name, Is.EqualTo("Right"));
        }

        [Test]
        public void ExistingDuplicatesAndOtherBranchesDoNotBlockUnrelatedNames()
        {
            var session = new PsdSession();
            session.Refresh(Document(Layer(1, "Duplicate"), Layer(2, "Duplicate"), Layer(3, "Other"),
                Layer(4, "Group", group: true), Layer(5, "Same", 4)));
            Assert.That(session.SetNodeNames(new Dictionary<int, string> { { 3, "Same" } }), Is.True);
            Assert.That(session.GetNodeName(5), Is.EqualTo("Same"));
            Assert.That(session.SetNodeNames(new Dictionary<int, string> { { 3, "Duplicate" } }), Is.False);
            Assert.That(session.GetNodeName(3), Is.EqualTo("Same"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t")]
        [TestCase("A/B")]
        [TestCase("A\\B")]
        [TestCase("A\nB")]
        [TestCase("A\u007fB")]
        public void InvalidNameRejectsOtherValidChangesInSameBatch(string invalid)
        {
            var session = new PsdSession();
            session.Refresh(Document(Layer(1, "One"), Layer(2, "Two")));
            Assert.That(session.SetNodeNames(new Dictionary<int, string> { { 1, "合法_小写" }, { 2, invalid } }), Is.False);
            Assert.That(session.CaptureNames().Overrides, Is.Empty);
        }

        [Test]
        public void NamesCanTargetUnimportedAndMergedNodesButNeverUnknownIds()
        {
            var session = new PsdSession();
            session.Refresh(Document(Layer(1, "Merged", group: true, flatten: true), Layer(2, "Child", 1),
                Layer(3, "NotImported", imported: false)));
            Assert.That(session.CanToggle(2) || session.CanToggle(3), Is.False);
            Assert.That(session.SetNodeNames(new Dictionary<int, string> { { 2, "小写_child" }, { 3, "未导入名字" } }), Is.True);
            Assert.That(session.SetNodeNames(new Dictionary<int, string> { { 2, "新名" }, { 999, "Unknown" } }), Is.False);
            Assert.That(session.GetNodeName(2), Is.EqualTo("小写_child"));
            Assert.That(session.ResetNodeNames(new[] { 2, 999 }), Is.False);
            Assert.That(session.ResetNodeNames(new[] { 2, 3 }), Is.True);
            Assert.That(session.GetNodeName(2), Is.EqualTo("Child"));
            Assert.That(session.GetNodeName(3), Is.EqualTo("NotImported"));
        }

        [Test]
        public void ImmutableSnapshotsAndFailedPersistenceNeverLeakPartialState()
        {
            var input = new Dictionary<int, string> { { 1, "Frozen" } };
            var state = new PsdNodeNameState(input);
            input[1] = "Mutated";
            Assert.That(state.Resolve(Layer(1, "Source")), Is.EqualTo("Frozen"));
            Assert.Throws<NotSupportedException>(() => ((IDictionary<int, string>)state.Overrides)[1] = "Mutated");
            var repository = new Repository();
            var session = new PsdSession(nameRepository: repository);
            session.Refresh(Document(Layer(1, "One"), Layer(2, "Two")));
            session.SetNodeNames(new Dictionary<int, string> { { 1, "Before" } });
            var snapshot = session.CaptureNames();
            long version = session.ContextVersion;
            int changes = 0;
            session.ContextChanged += () => changes++;
            repository.Fail = true;
            Assert.That(session.SetNodeNames(new Dictionary<int, string> { { 1, "After" }, { 2, "AfterTwo" } }), Is.False);
            Assert.That(session.GetNodeName(1), Is.EqualTo("Before"));
            Assert.That(session.GetNodeName(2), Is.EqualTo("Two"));
            Assert.That(session.ContextVersion, Is.EqualTo(version));
            Assert.That(changes, Is.Zero);
            Assert.That(repository.Load("asset").Overrides.Keys, Is.EqualTo(new[] { 1 }));
            repository.Fail = false;
            Assert.That(session.ResetNodeNames(new[] { 1 }), Is.True);
            Assert.That(snapshot.Resolve(session.Document.Find(1)), Is.EqualTo("Before"));
        }

        [Test]
        public void ReimportRestoresStableIdsIncludingTemporarilyMissingNodesAndSeparatesDocuments()
        {
            var repository = new Repository();
            var session = new PsdSession(nameRepository: repository);
            session.Refresh(Document(Layer(1, "One"), Layer(2, "Two")));
            session.SetNodeNames(new Dictionary<int, string> { { 2, "Saved" } });
            session.Refresh(Document(Layer(1, "DifferentSource")));
            Assert.That(session.CaptureNames().Overrides[2], Is.EqualTo("Saved"));
            session.Refresh(Document(Layer(2, "RenamedSource")));
            Assert.That(session.GetNodeName(2), Is.EqualTo("Saved"));
            session.Refresh(new PsdDocumentSnapshot("other", 100, 200, new[] { Layer(2, "OtherSource") }));
            Assert.That(session.GetNodeName(2), Is.EqualTo("OtherSource"));
            session.SetNodeNames(new Dictionary<int, string> { { 2, "OtherSaved" } });
            session.Refresh(Document(Layer(2, "LatestSource")));
            Assert.That(session.GetNodeName(2), Is.EqualTo("Saved"));
            Assert.That(session.Document.Find(2).Name, Is.EqualTo("LatestSource"));
        }

        [Test]
        public void ContextChangesOnlyForActualEditsAndVisibilityRestorationDoesNotResetNames()
        {
            var repository = new Repository();
            var session = new PsdSession(nameRepository: repository);
            int changes = 0;
            session.ContextChanged += () => changes++;
            session.Refresh(Document(Layer(1, "One"), Layer(2, "Two")));
            Assert.That(session.ContextVersion, Is.EqualTo(1));
            session.Select(1);
            session.RestoreVisibility();
            session.RestoreNames();
            session.SetNodeVisible(1, true);
            session.SetNodeExportable(1, true);
            Assert.That(changes, Is.EqualTo(1));
            session.SetNodeNames(new Dictionary<int, string> { { 1, "Named" } });
            Assert.That(session.SetNodeNames(new Dictionary<int, string> { { 1, "Named" } }), Is.False);
            session.SetNodeVisible(1, false);
            session.SetNodeExportable(2, false);
            Assert.That(session.ContextVersion, Is.EqualTo(4));
            session.RestoreVisibility();
            Assert.That(session.GetNodeName(1), Is.EqualTo("Named"));
            Assert.That(session.ContextVersion, Is.EqualTo(5));
            session.RestoreVisibility();
            session.RestoreNames();
            Assert.That(session.ContextVersion, Is.EqualTo(5));
            repository.States["asset"] = new PsdNodeNameState(new Dictionary<int, string> { { 1, "Restored" } });
            session.RestoreNames();
            Assert.That(session.ContextVersion, Is.EqualTo(6));
            Assert.That(changes, Is.EqualTo(6));
            session.SetDocumentVisible(false);
            session.ResetVisibility();
            session.ResetNodeNames(new[] { 1 });
            Assert.That(session.IsNodeEnabled(1), Is.True);
            Assert.That(session.IsNodeExportEnabled(2), Is.True);
            Assert.That(session.CaptureNames().Overrides, Is.Empty);
        }

        [Test]
        public void NamingAndResetDoNotChangeVisibilityExportOrSelection()
        {
            var session = new PsdSession();
            session.Refresh(Document(Layer(1, "One"), Layer(2, "Two")));
            session.SetNodeVisible(1, false);
            session.SetNodeExportable(2, false);
            session.SelectMany(new[] { 1, 2 }, 2);
            var visibility = session.CaptureVisibility();
            var export = session.CaptureExport();
            session.SetNodeNames(new Dictionary<int, string> { { 1, "NamedOne" }, { 2, "NamedTwo" } });
            session.ResetNodeNames(new[] { 1, 2 });
            Assert.That(session.CaptureVisibility().HiddenLayerIds, Is.EqualTo(visibility.HiddenLayerIds));
            Assert.That(session.CaptureExport().ExcludedLayerIds, Is.EqualTo(export.ExcludedLayerIds));
            Assert.That(session.SelectedIds, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(session.SelectedId, Is.EqualTo(2));
            session.SetNodeNames(new Dictionary<int, string> { { 1, "Retained" } });
            session.ResetVisibility();
            Assert.That(session.GetNodeName(1), Is.EqualTo("Retained"));
            Assert.That(session.IsNodeExportEnabled(2), Is.False);
        }

        [Test]
        public void ImageAndTextPlansUseEffectiveNamesWithoutMovingSpritesOrSourceNodes()
        {
            var document = Document(Layer(1, "Group", group: true), Layer(2, "Image", 1),
                Layer(3, "Text", 1, kind: PsdLayerKind.Text), Layer(4, "Skipped", imported: false));
            var names = new PsdNodeNameState(new Dictionary<int, string>
                { { 1, "NamedGroup" }, { 2, "NamedImage" }, { 3, "NamedText" }, { 4, "NamedSkipped" } });
            var before = PsdPrefabPlanner.Build(document, new PsdVisibilityState(false, new[] { 2 }));
            var after = PsdPrefabPlanner.Build(document, new PsdVisibilityState(false, new[] { 2 }), names: names);
            Assert.That(after.Nodes.Select(n => n.Name), Is.EqualTo(new[] { "NamedGroup", "NamedText", "NamedImage" }));
            Assert.That(after.Skipped.Single().Name, Is.EqualTo("NamedSkipped"));
            Assert.That(after.ContentActive, Is.False);
            foreach (var node in after.Nodes)
            {
                var original = before.Nodes.Single(n => n.Id == node.Id);
                Assert.That(node.ParentId, Is.EqualTo(original.ParentId));
                Assert.That(node.Bounds, Is.EqualTo(original.Bounds));
                Assert.That(node.LocalX, Is.EqualTo(original.LocalX));
                Assert.That(node.LocalY, Is.EqualTo(original.LocalY));
                Assert.That(node.SiblingIndex, Is.EqualTo(original.SiblingIndex));
                Assert.That(node.SpriteId, Is.EqualTo(original.SpriteId));
                Assert.That(node.ActiveSelf, Is.EqualTo(original.ActiveSelf));
            }
            Assert.That(after.Nodes.Single(n => n.Id == 1).LocalX, Is.EqualTo(-22.5f));
            Assert.That(after.Nodes.Single(n => n.Id == 1).LocalY, Is.EqualTo(57.5f));
            Assert.That(document.Nodes.Select(n => n.Name), Is.EqualTo(new[] { "Group", "Image", "Text", "Skipped" }));
        }

        [Test]
        public void ImageAndTmpExportersKeepPlanNamesAndCoordinates()
        {
            string folder = "Assets/PsdUiWorkbench/Tests/NamingExport_" + Guid.NewGuid().ToString("N");
            var source = new UnityPsdDocumentSource();
            var sourceDocument = source.Load(AssetDatabase.AssetPathToGUID("Assets/Psd/战斗-主菜单界面.psd")).Document;
            var sprite = source.Resolve(sourceDocument.DrawNodes[0].SpriteId);
            var document = Document(Layer(1, "Group", group: true), Layer(2, "Image", 1), Layer(3, "Text", 1, kind: PsdLayerKind.Text));
            var names = new PsdNodeNameState(new Dictionary<int, string> { { 1, "RootGroup" }, { 2, "SpriteNode" }, { 3, "TmpNode" } });
            var plan = PsdPrefabPlanner.Build(document, new PsdVisibilityState(), names: names);
            var style = new PsdTextStyle("Known", 12, false, 0, new PsdTextColor(1, 1, 1, 1), 0, new PsdTextColor(0, 0, 0, 0));
            var text = new PsdTextSource(3, "A", 13, 35, PsdTextAlignment.Left, new[] { new PsdTextSourceSpan(0, 1, style) });
            try
            {
                string images = PsdPrefabExporter.Export(plan, folder + "/Images.prefab", _ => sprite).AssetPath;
                string tmp = PsdTmpPrefabExporter.Export(plan, new Dictionary<int, PsdTextSource> { { 3, text } },
                    new Dictionary<string, TMP_FontAsset> { { "Known", TMP_Settings.defaultFontAsset } }, null,
                    folder + "/Text.prefab", _ => sprite, PsdTmpRenderingMode.Standard).AssetPath;
                foreach (string path in new[] { images, tmp })
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    var transforms = new Dictionary<int, RectTransform>();
                    foreach (var node in plan.Nodes)
                    {
                        Transform parent = node.ParentId.HasValue ? transforms[node.ParentId.Value] : prefab.transform.GetChild(0);
                        var rect = (RectTransform)parent.GetChild(node.SiblingIndex);
                        transforms.Add(node.Id, rect);
                        Assert.That(rect.name, Is.EqualTo(names.Resolve(document.Find(node.Id))));
                        Assert.That(rect.anchoredPosition, Is.EqualTo(new Vector2(node.LocalX, node.LocalY)));
                        Assert.That(rect.sizeDelta, Is.EqualTo(new Vector2(node.Bounds.Width, node.Bounds.Height)));
                    }
                    Assert.That(transforms[2].GetComponent<UnityEngine.UI.Image>().sprite, Is.EqualTo(sprite));
                }
                var saved = AssetDatabase.LoadAssetAtPath<GameObject>(tmp);
                Assert.That(saved.GetComponentsInChildren<TextMeshProUGUI>(true).Single().transform.parent.name, Is.EqualTo("TmpNode"));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void OpeningOrSavingEmptyNamesCreatesNoAsset()
        {
            string guid = Guid.NewGuid().ToString("N");
            string path = PsdNodeNameRepository.GetConfigPath(guid);
            using (var repository = new PsdNodeNameRepository())
            {
                Assert.That(repository.Load(guid).Overrides, Is.Empty);
                repository.Save(guid, new PsdNodeNameState());
                repository.Flush();
                Assert.That(File.Exists(path) || File.Exists(path + ".meta"), Is.False);
            }
        }

        [Test]
        public void RealRepositoryBatchIsOneUndoAndRedoAndDoesNotSaveOtherAssets()
        {
            string guid = Guid.NewGuid().ToString("N");
            string path = PsdNodeNameRepository.GetConfigPath(guid);
            string unrelatedPath = "Assets/PsdUiWorkbench/Tests/NamingUnrelated_" + guid + ".asset";
            var unrelated = ScriptableObject.CreateInstance<PsdNodeNameConfig>();
            using (var repository = new PsdNodeNameRepository())
            {
                try
                {
                    AssetDatabase.CreateAsset(unrelated, unrelatedPath);
                    byte[] unrelatedDisk = File.ReadAllBytes(unrelatedPath);
                    var session = new PsdSession(nameRepository: repository);
                    session.Refresh(new PsdDocumentSnapshot(guid, 100, 200, new[] { Layer(1, "SourceOne"), Layer(2, "SourceTwo") }));
                    repository.Changed += session.RestoreNames;
                    session.SetNodeVisible(1, false);
                    session.SetNodeExportable(2, false);
                    // 无关对象的已有 Undo 必须留在命名批次之前。
                    Undo.IncrementCurrentGroup();
                    Undo.RegisterCompleteObjectUndo(unrelated, "无关对象修改");
                    unrelated.name = "UnrelatedChanged";
                    EditorUtility.SetDirty(unrelated);
                    Undo.IncrementCurrentGroup();
                    Assert.That(session.SetNodeNames(new Dictionary<int, string> { { 1, "One" }, { 2, "Two" } }), Is.True);
                    repository.Save(guid, session.CaptureNames()); // 无变化不得插入额外 Undo。
                    repository.Flush();
                    Assert.That(EditorUtility.IsDirty(unrelated), Is.True);
                    Assert.That(File.ReadAllBytes(unrelatedPath), Is.EqualTo(unrelatedDisk));
                    int changes = 0;
                    repository.Changed += () => changes++;
                    Undo.PerformUndo();
                    Assert.That(repository.Load(guid).Overrides, Is.Empty);
                    Assert.That(session.GetNodeName(1), Is.EqualTo("SourceOne"));
                    Assert.That(session.GetNodeName(2), Is.EqualTo("SourceTwo"));
                    Assert.That(session.IsNodeEnabled(1), Is.False);
                    Assert.That(session.IsNodeExportEnabled(2), Is.False);
                    Assert.That(unrelated.name, Is.EqualTo("UnrelatedChanged"));
                    Assert.That(changes, Is.EqualTo(1));
                    Undo.PerformRedo();
                    Assert.That(repository.Load(guid).Overrides.Keys.OrderBy(id => id), Is.EqualTo(new[] { 1, 2 }));
                    Assert.That(session.GetNodeName(1), Is.EqualTo("One"));
                    Assert.That(session.GetNodeName(2), Is.EqualTo("Two"));
                    Assert.That(session.IsNodeEnabled(1), Is.False);
                    Assert.That(session.IsNodeExportEnabled(2), Is.False);
                    Assert.That(changes, Is.EqualTo(2));
                    Assert.That(EditorUtility.IsDirty(AssetDatabase.LoadAssetAtPath<PsdNodeNameConfig>(path)), Is.False);
                    using (var reopened = new PsdNodeNameRepository())
                        Assert.That(reopened.Load(guid).Overrides[2], Is.EqualTo("Two"));
                }
                finally
                {
                    var config = AssetDatabase.LoadAssetAtPath<PsdNodeNameConfig>(path);
                    if (config != null) Undo.ClearUndo(config);
                    AssetDatabase.DeleteAsset(path);
                    Undo.ClearUndo(unrelated);
                    AssetDatabase.DeleteAsset(unrelatedPath);
                }
            }
        }

        [Test]
        public void RealRepositorySaveFailureRestoresMemoryDiskAndKeepsUnrelatedUndo()
        {
            string guid = Guid.NewGuid().ToString("N");
            string path = PsdNodeNameRepository.GetConfigPath(guid);
            var before = new PsdNodeNameState(new Dictionary<int, string> { { 1, "Before" } });
            using var repository = new PsdNodeNameRepository();
            var unrelated = ScriptableObject.CreateInstance<PsdNodeNameConfig>();
            try
            {
                repository.Save(guid, before);
                byte[] bytes = File.ReadAllBytes(path);
                Undo.IncrementCurrentGroup();
                Undo.RegisterCompleteObjectUndo(unrelated, "保留这次撤销");
                unrelated.name = "Changed";
                Undo.IncrementCurrentGroup();
                using (var failing = new PsdNodeNameRepository(config =>
                {
                    AssetDatabase.SaveAssetIfDirty(config);
                    throw new IOException("模拟写盘后失败。");
                }))
                {
                    Assert.Throws<IOException>(() => failing.Save(guid,
                        new PsdNodeNameState(new Dictionary<int, string> { { 1, "After" }, { 2, "Partial" } })));
                    Assert.That(failing.Load(guid).Overrides[1], Is.EqualTo("Before"));
                    Assert.That(failing.Load(guid).Overrides.ContainsKey(2), Is.False);
                }
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
                Undo.PerformUndo();
                Assert.That(unrelated.name, Is.Not.EqualTo("Changed"), "失败命名不得插入或清空无关 Undo。");
                Assert.That(repository.Load(guid).Overrides[1], Is.EqualTo("Before"));
            }
            finally
            {
                var config = AssetDatabase.LoadAssetAtPath<PsdNodeNameConfig>(path);
                if (config != null) Undo.ClearUndo(config);
                AssetDatabase.DeleteAsset(path);
                Undo.ClearUndo(unrelated);
                UnityEngine.Object.DestroyImmediate(unrelated);
            }
        }

        [Test]
        public void FirstSaveFailureDeletesOnlyItsNewConfigAndOccupiedPathIsPreserved()
        {
            string guid = Guid.NewGuid().ToString("N");
            string path = PsdNodeNameRepository.GetConfigPath(guid);
            using (var failing = new PsdNodeNameRepository(_ => throw new IOException("首次保存失败。")))
            {
                Assert.Throws<IOException>(() => failing.Save(guid, new PsdNodeNameState(new Dictionary<int, string> { { 1, "Name" } })));
                Assert.That(File.Exists(path) || File.Exists(path + ".meta"), Is.False);
            }
            try
            {
                File.WriteAllText(path, "保留路径占用内容");
                using var repository = new PsdNodeNameRepository();
                Assert.Throws<IOException>(() => repository.Save(guid, new PsdNodeNameState(new Dictionary<int, string> { { 1, "Name" } })));
                Assert.That(File.ReadAllText(path), Is.EqualTo("保留路径占用内容"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
            }
        }
    }
}
