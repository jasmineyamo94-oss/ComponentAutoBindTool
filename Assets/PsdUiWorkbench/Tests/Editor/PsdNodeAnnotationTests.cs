using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using UnityEditor;
using UnityEngine;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdNodeAnnotationTests
    {
        private sealed class Repository : IPsdNodeAnnotationRepository
        {
            public readonly Dictionary<string, PsdNodeAnnotationState> States = new Dictionary<string, PsdNodeAnnotationState>();
            public int Saves;
            public bool FailSave;
            public bool FailLoad;
            public PsdNodeAnnotationState Load(string guid)
            {
                if (FailLoad) throw new IOException("模拟批注读取失败。");
                return States.TryGetValue(guid, out var state) ? state : new PsdNodeAnnotationState();
            }
            public void Save(string guid, PsdNodeAnnotationState state)
            {
                if (FailSave) throw new IOException("模拟批注保存失败。");
                Saves++;
                States[guid] = state;
            }
        }

        private static PsdNode Layer(int id, string name = "源图层", bool imported = true)
            => new PsdNode(id, null, name, false, imported, true, false,
                "sprite" + id, new PixelRect(0, 0, 10, 20), id);
        private static PsdDocumentSnapshot Document(string guid = "asset", params PsdNode[] nodes)
            => new PsdDocumentSnapshot(guid, 100, 200, nodes.Length == 0 ? new[] { Layer(1), Layer(2) } : nodes);

        [Test]
        public void AnnotationPreservesMultilineTextAndOnlyRealChangesAdvanceContext()
        {
            var repository = new Repository();
            var session = new PsdSession(annotationRepository: repository);
            session.Refresh(Document());
            long version = session.ContextVersion;
            int changes = 0;
            session.ContextChanged += () => changes++;
            const string text = "  登录按钮\r\n点击后打开面板\n\t保留缩进  ";
            Assert.That(session.SetNodeAnnotation(1, text), Is.True);
            Assert.That(session.GetNodeAnnotation(1), Is.EqualTo(text));
            Assert.That(session.Document.Find(1).Name, Is.EqualTo("源图层"));
            Assert.That(session.SetNodeAnnotation(1, text), Is.False);
            Assert.That(session.ContextVersion, Is.EqualTo(version + 1));
            Assert.That(changes, Is.EqualTo(1));
            Assert.That(repository.Saves, Is.EqualTo(1));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t\r\n　")]
        public void BlankAnnotationRemovesRecordWithoutCreatingEmptyEntries(string blank)
        {
            var repository = new Repository();
            var session = new PsdSession(annotationRepository: repository);
            session.Refresh(Document());
            long version = session.ContextVersion;
            Assert.That(session.SetNodeAnnotation(1, blank), Is.False);
            Assert.That(session.ContextVersion, Is.EqualTo(version));
            Assert.That(repository.Saves, Is.Zero);
            Assert.That(session.SetNodeAnnotation(1, "用途"), Is.True);
            Assert.That(session.SetNodeAnnotation(1, blank), Is.True);
            Assert.That(session.GetNodeAnnotation(1), Is.Empty);
            Assert.That(session.CaptureAnnotations().Annotations, Is.Empty);
            Assert.That(repository.Load("asset").Annotations, Is.Empty);
            Assert.That(repository.Saves, Is.EqualTo(2));
        }

        [Test]
        public void SnapshotIsImmutableAndUnknownNodesAndOverlongTextAreRejected()
        {
            var input = new Dictionary<int, string> { { 1, "批注" }, { 2, " \n" } };
            var state = new PsdNodeAnnotationState(input);
            input[1] = "外部修改";
            Assert.That(state.Get(1), Is.EqualTo("批注"));
            Assert.That(state.Annotations.ContainsKey(2), Is.False);
            Assert.Throws<NotSupportedException>(() => ((IDictionary<int, string>)state.Annotations)[1] = "写入");
            var session = new PsdSession();
            Assert.That(session.SetNodeAnnotation(1, "未加载"), Is.False);
            session.Refresh(Document("asset", Layer(1, imported: false)));
            Assert.That(session.SetNodeAnnotation(1, new string('字', PsdNodeAnnotationState.MaxLength)), Is.True);
            var snapshot = session.CaptureAnnotations();
            long version = session.ContextVersion;
            Assert.That(session.SetNodeAnnotation(1, new string('字', PsdNodeAnnotationState.MaxLength + 1)), Is.False);
            Assert.That(session.SetNodeAnnotation(999, "不存在"), Is.False);
            Assert.That(session.ContextVersion, Is.EqualTo(version));
            Assert.That(session.GetNodeAnnotation(999), Is.Empty);
            Assert.That(session.SetNodeAnnotation(1, "后续"), Is.True);
            Assert.That(snapshot.Get(1).Length, Is.EqualTo(PsdNodeAnnotationState.MaxLength));
            Assert.Throws<ArgumentException>(() => new PsdNodeAnnotationState(
                new Dictionary<int, string> { { 1, new string('字', PsdNodeAnnotationState.MaxLength + 1) } }));
        }

        [Test]
        public void FailedSaveKeepsPublishedStateVersionAndStoredRecordsUnchanged()
        {
            var repository = new Repository();
            var session = new PsdSession(annotationRepository: repository);
            session.Refresh(Document());
            session.SetNodeAnnotation(1, "保存前");
            session.SetNodeAnnotation(2, "另一个节点");
            var snapshot = session.CaptureAnnotations();
            long version = session.ContextVersion;
            int changes = 0;
            session.ContextChanged += () => changes++;
            repository.FailSave = true;
            Assert.That(session.SetNodeAnnotation(1, "失败的新内容"), Is.False);
            Assert.That(session.SetNodeAnnotation(2, null), Is.False);
            Assert.That(session.CaptureAnnotations(), Is.SameAs(snapshot));
            Assert.That(repository.Load("asset"), Is.EqualTo(snapshot));
            Assert.That(session.ContextVersion, Is.EqualTo(version));
            Assert.That(changes, Is.Zero);
            repository.FailSave = false;
            Assert.That(session.SetNodeAnnotation(1, "重试成功"), Is.True);
            Assert.That(snapshot.Get(1), Is.EqualTo("保存前"));
        }

        [Test]
        public void RestoreAndRefreshReadFailuresNeverPublishPartialAnnotationsOrSwitchDocuments()
        {
            var repository = new Repository();
            var session = new PsdSession(annotationRepository: repository);
            session.Refresh(Document());
            session.SetNodeAnnotation(1, "原批注");
            session.Select(1);
            var document = session.Document;
            var snapshot = session.CaptureAnnotations();
            long version = session.ContextVersion;
            repository.FailLoad = true;
            Assert.Throws<IOException>(() => session.RestoreAnnotations());
            Assert.Throws<IOException>(() => session.Refresh(Document("other")));
            Assert.That(session.Document, Is.SameAs(document));
            Assert.That(session.CaptureAnnotations(), Is.SameAs(snapshot));
            Assert.That(session.SelectedId, Is.EqualTo(1));
            Assert.That(session.ContextVersion, Is.EqualTo(version));
            repository.FailLoad = false;
            repository.States["asset"] = new PsdNodeAnnotationState(new Dictionary<int, string> { { 1, "外部恢复" } });
            session.RestoreAnnotations();
            Assert.That(session.GetNodeAnnotation(1), Is.EqualTo("外部恢复"));
            Assert.That(session.ContextVersion, Is.EqualTo(version + 1));
            session.RestoreAnnotations();
            Assert.That(session.ContextVersion, Is.EqualTo(version + 1));
        }

        [Test]
        public void ReimportKeepsMissingStableIdsAndReloadsSameGuidWhileDocumentSwitchClearsOldState()
        {
            var repository = new Repository();
            var session = new PsdSession(annotationRepository: repository);
            session.Refresh(Document());
            session.SetNodeAnnotation(2, "稳定图层批注");
            session.Refresh(Document("asset", Layer(1, "重导入后")));
            Assert.That(session.CaptureAnnotations().Get(2), Is.EqualTo("稳定图层批注"));
            Assert.That(session.GetNodeAnnotation(2), Is.Empty);
            session.Refresh(Document("asset", Layer(2, "图层改名")));
            Assert.That(session.GetNodeAnnotation(2), Is.EqualTo("稳定图层批注"));
            repository.States["asset"] = new PsdNodeAnnotationState(new Dictionary<int, string> { { 2, "外部新快照" } });
            session.Refresh(Document());
            Assert.That(session.GetNodeAnnotation(2), Is.EqualTo("外部新快照"));
            session.Refresh(Document("other"));
            Assert.That(session.CaptureAnnotations().Annotations, Is.Empty);
            session.SetNodeAnnotation(2, "其他文档");
            session.Refresh(Document());
            Assert.That(session.GetNodeAnnotation(2), Is.EqualTo("外部新快照"));
            session.Refresh(null);
            Assert.That(session.CaptureAnnotations().Annotations, Is.Empty);
            Assert.That(session.GetNodeAnnotation(2), Is.Empty);
            var reopened = new PsdSession(annotationRepository: repository);
            reopened.Refresh(Document("other"));
            Assert.That(reopened.GetNodeAnnotation(2), Is.EqualTo("其他文档"));
        }

        [Test]
        public void MemoryOnlyAnnotationsSurviveRestoreAndReimportButNotDocumentSwitch()
        {
            var session = new PsdSession();
            session.Refresh(Document());
            session.SetNodeAnnotation(1, "内存批注");
            long version = session.ContextVersion;
            session.RestoreAnnotations();
            Assert.That(session.ContextVersion, Is.EqualTo(version));
            session.Refresh(Document("asset", Layer(1, "新名称")));
            Assert.That(session.GetNodeAnnotation(1), Is.EqualTo("内存批注"));
            session.Refresh(Document("other"));
            Assert.That(session.GetNodeAnnotation(1), Is.Empty);
            session.Refresh(Document());
            Assert.That(session.GetNodeAnnotation(1), Is.Empty);
        }

        [Test]
        public void AnnotationEditsAndRestoresAreIndependentOfNamingVisibilityExportAndSelection()
        {
            var repository = new Repository();
            var session = new PsdSession(annotationRepository: repository);
            session.Refresh(Document());
            session.SetNodeNames(new Dictionary<int, string> { { 1, "工作台名称" } });
            session.SetNodeVisible(1, false);
            session.SetNodeExportable(2, false);
            session.SelectMany(new[] { 1, 2 }, 2);
            session.SetNodeAnnotation(1, "不改其他状态");
            session.RestoreAnnotations();
            Assert.That(session.GetNodeName(1), Is.EqualTo("工作台名称"));
            Assert.That(session.IsNodeEnabled(1), Is.False);
            Assert.That(session.IsNodeExportEnabled(2), Is.False);
            Assert.That(session.SelectedIds, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(session.SelectedId, Is.EqualTo(2));
            session.RestoreNames();
            session.RestoreVisibility();
            Assert.That(session.GetNodeAnnotation(1), Is.EqualTo("不改其他状态"));
            session.ResetVisibility();
            Assert.That(session.GetNodeAnnotation(1), Is.EqualTo("不改其他状态"));
        }

        [Test]
        public void ReadingAndSavingEmptyStateCreatesNoAssetAndGuidPathsAreCanonical()
        {
            string guid = Guid.NewGuid().ToString("N");
            string path = PsdNodeAnnotationRepository.GetConfigPath(guid);
            using var repository = new PsdNodeAnnotationRepository();
            Assert.That(repository.Load(guid).Annotations, Is.Empty);
            repository.Save(guid, new PsdNodeAnnotationState(new Dictionary<int, string> { { 1, " \n" } }));
            repository.Flush();
            Assert.That(File.Exists(path) || File.Exists(path + ".meta"), Is.False);
            Assert.That(PsdNodeAnnotationRepository.GetConfigPath(guid.ToUpperInvariant()), Is.EqualTo(path));
            Assert.Throws<ArgumentException>(() => repository.Save("../escape", new PsdNodeAnnotationState()));
        }

        [Test]
        public void RealAssetsReopenFromDiskAndKeepDifferentGuidsSeparate()
        {
            string first = Guid.NewGuid().ToString("N");
            string second = Guid.NewGuid().ToString("N");
            string firstPath = PsdNodeAnnotationRepository.GetConfigPath(first);
            string secondPath = PsdNodeAnnotationRepository.GetConfigPath(second);
            try
            {
                using (var repository = new PsdNodeAnnotationRepository())
                {
                    var session = new PsdSession(annotationRepository: repository);
                    session.Refresh(Document(first));
                    session.SetNodeAnnotation(1, "第一份\n多行说明");
                    session.Refresh(Document(second));
                    Assert.That(session.GetNodeAnnotation(1), Is.Empty);
                    session.SetNodeAnnotation(1, "第二份");
                    Assert.That(repository.Load(first.ToUpperInvariant()).Get(1), Is.EqualTo("第一份\n多行说明"));
                }
                AssetDatabase.ImportAsset(firstPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(secondPath, ImportAssetOptions.ForceUpdate);
                using var reopened = new PsdNodeAnnotationRepository();
                var restored = new PsdSession(annotationRepository: reopened);
                restored.Refresh(Document(first));
                Assert.That(restored.GetNodeAnnotation(1), Is.EqualTo("第一份\n多行说明"));
                restored.Refresh(Document(second));
                Assert.That(restored.GetNodeAnnotation(1), Is.EqualTo("第二份"));
            }
            finally
            {
                DeleteConfig(firstPath);
                DeleteConfig(secondPath);
            }
        }

        [Test]
        public void RealSaveIsOneUndoRedoAndDoesNotSaveUnrelatedAssets()
        {
            string guid = Guid.NewGuid().ToString("N");
            string path = PsdNodeAnnotationRepository.GetConfigPath(guid);
            string unrelatedPath = "Assets/PsdUiWorkbench/Tests/AnnotationUnrelated_" + guid + ".asset";
            var unrelated = ScriptableObject.CreateInstance<PsdNodeNameConfig>();
            using var repository = new PsdNodeAnnotationRepository();
            var session = new PsdSession(annotationRepository: repository);
            repository.Changed += session.RestoreAnnotations;
            try
            {
                AssetDatabase.CreateAsset(unrelated, unrelatedPath);
                byte[] unrelatedDisk = File.ReadAllBytes(unrelatedPath);
                session.Refresh(Document(guid));
                session.SetNodeVisible(1, false);
                session.SetNodeExportable(2, false);
                session.SetNodeNames(new Dictionary<int, string> { { 1, "独立名字" } });
                session.Select(2);
                Undo.IncrementCurrentGroup();
                Undo.RegisterCompleteObjectUndo(unrelated, "无关修改");
                unrelated.name = "不应被保存";
                EditorUtility.SetDirty(unrelated);
                Undo.IncrementCurrentGroup();
                Assert.That(session.SetNodeAnnotation(1, "一次完整编辑\n第二行"), Is.True);
                Assert.That(session.SetNodeAnnotation(1, "一次完整编辑\n第二行"), Is.False);
                repository.Flush();
                Assert.That(EditorUtility.IsDirty(unrelated), Is.True);
                Assert.That(File.ReadAllBytes(unrelatedPath), Is.EqualTo(unrelatedDisk));
                int changes = 0;
                repository.Changed += () => changes++;
                long version = session.ContextVersion;
                Undo.PerformUndo();
                Assert.That(session.GetNodeAnnotation(1), Is.Empty);
                Assert.That(repository.Load(guid).Annotations, Is.Empty);
                Assert.That(unrelated.name, Is.EqualTo("不应被保存"));
                Assert.That(session.ContextVersion, Is.EqualTo(version + 1));
                Undo.PerformRedo();
                Assert.That(session.GetNodeAnnotation(1), Is.EqualTo("一次完整编辑\n第二行"));
                Assert.That(session.ContextVersion, Is.EqualTo(version + 2));
                Assert.That(changes, Is.EqualTo(2));
                Assert.That(session.GetNodeName(1), Is.EqualTo("独立名字"));
                Assert.That(session.IsNodeEnabled(1), Is.False);
                Assert.That(session.IsNodeExportEnabled(2), Is.False);
                Assert.That(session.SelectedId, Is.EqualTo(2));
                Assert.That(EditorUtility.IsDirty(AssetDatabase.LoadAssetAtPath<PsdNodeAnnotationConfig>(path)), Is.False);
                Assert.That(File.ReadAllBytes(unrelatedPath), Is.EqualTo(unrelatedDisk));
                Assert.That(session.SetNodeAnnotation(1, " \n"), Is.True);
                Undo.PerformUndo();
                Assert.That(session.GetNodeAnnotation(1), Is.EqualTo("一次完整编辑\n第二行"));
                Undo.PerformRedo();
                Assert.That(session.CaptureAnnotations().Annotations, Is.Empty);
            }
            finally
            {
                repository.Changed -= session.RestoreAnnotations;
                DeleteConfig(path);
                Undo.ClearUndo(unrelated);
                AssetDatabase.DeleteAsset(unrelatedPath);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RealSaveFailureRestoresMemoryDiskAndUnrelatedUndo(bool failAfterWrite)
        {
            string guid = Guid.NewGuid().ToString("N");
            string path = PsdNodeAnnotationRepository.GetConfigPath(guid);
            var unrelated = ScriptableObject.CreateInstance<PsdNodeNameConfig>();
            using var repository = new PsdNodeAnnotationRepository();
            try
            {
                repository.Save(guid, new PsdNodeAnnotationState(new Dictionary<int, string> { { 1, "原批注" } }));
                byte[] disk = File.ReadAllBytes(path);
                Undo.IncrementCurrentGroup();
                Undo.RegisterCompleteObjectUndo(unrelated, "保留撤销");
                unrelated.name = "无关变化";
                Undo.IncrementCurrentGroup();
                using (var failing = new PsdNodeAnnotationRepository(config =>
                {
                    if (failAfterWrite) AssetDatabase.SaveAssetIfDirty(config);
                    throw new IOException("模拟批注存储失败。");
                }))
                {
                    var session = new PsdSession(annotationRepository: failing);
                    session.Refresh(Document(guid));
                    long version = session.ContextVersion;
                    Assert.That(session.SetNodeAnnotation(1, "失败更新"), Is.False);
                    Assert.That(session.GetNodeAnnotation(1), Is.EqualTo("原批注"));
                    Assert.That(session.ContextVersion, Is.EqualTo(version));
                    Assert.Throws<IOException>(() => failing.Save(guid, new PsdNodeAnnotationState(
                        new Dictionary<int, string> { { 1, "失败更新" }, { 2, "不得部分保存" } })));
                    Assert.That(failing.Load(guid).Annotations.Count, Is.EqualTo(1));
                    Assert.That(failing.Load(guid).Get(1), Is.EqualTo("原批注"));
                }
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(disk));
                Undo.PerformUndo();
                Assert.That(unrelated.name, Is.Not.EqualTo("无关变化"), "失败保存不得新增或清空无关撤销记录。");
                Assert.That(repository.Load(guid).Get(1), Is.EqualTo("原批注"));
            }
            finally
            {
                DeleteConfig(path);
                Undo.ClearUndo(unrelated);
                UnityEngine.Object.DestroyImmediate(unrelated);
            }
        }

        [Test]
        public void FirstSaveFailureDeletesNewAssetAndNeverOverwritesAnOccupiedPath()
        {
            string guid = Guid.NewGuid().ToString("N");
            string path = PsdNodeAnnotationRepository.GetConfigPath(guid);
            using (var failing = new PsdNodeAnnotationRepository(_ => throw new IOException("首次保存失败。")))
            {
                Assert.Throws<IOException>(() => failing.Save(guid,
                    new PsdNodeAnnotationState(new Dictionary<int, string> { { 1, "批注" } })));
                Assert.That(File.Exists(path) || File.Exists(path + ".meta"), Is.False);
            }
            try
            {
                File.WriteAllText(path, "保留其他文件");
                using var repository = new PsdNodeAnnotationRepository();
                Assert.Throws<IOException>(() => repository.Save(guid,
                    new PsdNodeAnnotationState(new Dictionary<int, string> { { 1, "批注" } })));
                Assert.That(File.ReadAllText(path), Is.EqualTo("保留其他文件"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
            }
        }

        private static void DeleteConfig(string path)
        {
            var config = AssetDatabase.LoadAssetAtPath<PsdNodeAnnotationConfig>(path);
            if (config != null) Undo.ClearUndo(config);
            AssetDatabase.DeleteAsset(path);
        }
    }
}
