using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.AiNaming;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdAiControllerTests
    {
        private PsdAiNamingSettings _settings;

        [OneTimeSetUp]
        public void CreateTestSettings()
        {
            _settings = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            _settings.Provider = PsdAiProvider.Zhipu;
            _settings.Model = "glm-4.6v";
        }

        [OneTimeTearDown]
        public void DisposeTestSettings() => UnityEngine.Object.DestroyImmediate(_settings);

        private sealed class Client : IPsdAiNamingClient
        {
            public PsdAiNamingSnapshot Snapshot;
            public PsdAiRequestSettings Settings;
            public Action<IReadOnlyList<PsdAiSuggestion>> Callback;
            public bool Release;
            public int Disposals;
            public IEnumerator Analyze(PsdAiNamingSnapshot snapshot, PsdAiRequestSettings settings, Action<IReadOnlyList<PsdAiSuggestion>> completed)
            {
                Snapshot = snapshot; Settings = settings; Callback = completed;
                while (!Release) yield return null;
                completed(snapshot.NodeKeys.Select((k, i) => new PsdAiSuggestion(k, "Result" + i, "测试建议", false)).ToArray());
            }
            public void Dispose() { Disposals++; }
        }
        private sealed class ImmediateClient : IPsdAiNamingClient
        {
            public int Disposals;
            public IEnumerator Analyze(PsdAiNamingSnapshot snapshot, PsdAiRequestSettings settings, Action<IReadOnlyList<PsdAiSuggestion>> completed)
            {
                completed(snapshot.NodeKeys.Select(k => new PsdAiSuggestion(k, "Confirm", "同步完成", false)).ToArray());
                return Array.Empty<object>().GetEnumerator();
            }
            public void Dispose() { Disposals++; }
        }
        private sealed class Preview : IPsdAiPreviewProvider
        {
            public PsdAiPreviewResult Capture(PsdDocumentSnapshot document, IReadOnlyList<int> targets, PsdVisibilityState visibility, Func<string, Sprite> resolve)
                => new PsdAiPreviewResult(Array.Empty<PsdAiImage>(), Array.Empty<int>());
        }
        private sealed class IncrementalPreview : IPsdAiIncrementalPreviewProvider
        {
            public int Steps, Disposals, SynchronousCaptures;
            public PsdAiPreviewResult Capture(PsdDocumentSnapshot document, IReadOnlyList<int> targets, PsdVisibilityState visibility, Func<string, Sprite> resolve)
            {
                SynchronousCaptures++;
                throw new InvalidOperationException("不应调用同步路径。");
            }
            public IEnumerator CaptureIncrementally(PsdDocumentSnapshot document, IReadOnlyList<int> targets,
                PsdVisibilityState visibility, Func<string, Sprite> resolve, Action<PsdAiPreviewResult> completed)
            {
                try
                {
                    foreach (int ignored in targets) { Steps++; yield return null; }
                    completed(new PsdAiPreviewResult(Array.Empty<PsdAiImage>(), Array.Empty<int>()));
                }
                finally { Disposals++; }
            }
        }
        private sealed class MissingCredentialsClient : IPsdAiNamingClient, IPsdAiNamingPreflight
        {
            public bool HasCredentials => false;
            public int Disposals;
            public IEnumerator Analyze(PsdAiNamingSnapshot snapshot, PsdAiRequestSettings settings, Action<IReadOnlyList<PsdAiSuggestion>> completed)
                => throw new InvalidOperationException("缺凭据时不应调用。");
            public void Dispose() { Disposals++; }
        }
        private sealed class Repository : IPsdNodeNameRepository
        {
            public int Saves;
            public bool Fail;
            public PsdNodeNameState Load(string guid) => new PsdNodeNameState();
            public void Save(string guid, PsdNodeNameState state)
            {
                if (Fail) throw new IOException("模拟失败");
                Saves++;
            }
        }
        private static PsdNode Node(int id, int? parent = null, bool group = false) =>
            new PsdNode(id, parent, "Original" + id, group, false, true, false, "", new PixelRect(0, 0, 20, 20), id);
        private static PsdSession Session(Repository repository = null, params PsdNode[] nodes)
        {
            var session = new PsdSession(nameRepository: repository);
            session.Refresh(new PsdDocumentSnapshot("fake", 100, 100, nodes.Length == 0 ? new[] { Node(1), Node(2) } : nodes));
            return session;
        }
        private static IEnumerator WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 100 && !condition(); i++) yield return null;
            Assert.That(condition(), Is.True, "编辑器帧推进未完成。");
        }

        [Test]
        public void ContextChangeBeforeFirstAnalysisKeepsIdleControllerNonStale()
        {
            var session = Session();
            using (var controller = new PsdAiNamingController(session, _ => null, settings: _settings, client: new Client(), previewProvider: new Preview()))
            {
                session.SetDocumentVisible(false);
                Assert.That(controller.IsStale, Is.False);
                Assert.That(controller.IsBusy, Is.False);
                Assert.That(controller.Rows, Is.Empty);
                Assert.That(controller.Error, Is.Empty);
            }
        }

        [Test]
        public void DisposeWithoutEverStartingReleasesClientExactlyOnce()
        {
            var client = new Client();
            var controller = new PsdAiNamingController(Session(), _ => null, settings: _settings, client: client, previewProvider: new Preview());
            controller.Dispose();
            controller.Dispose();
            Assert.That(client.Disposals, Is.EqualTo(1));
        }

        [Test]
        public void MissingCredentialsFailBeforePreviewAndIdleDisposeReleasesClient()
        {
            var client = new MissingCredentialsClient(); var preview = new IncrementalPreview();
            var controller = new PsdAiNamingController(Session(), _ => null, settings: _settings, client: client, previewProvider: preview);
            controller.Start(new[] { 1 }, false);
            Assert.That(controller.IsBusy, Is.False);
            Assert.That(controller.Error, Does.Contain("API Key"));
            Assert.That(preview.Steps + preview.SynchronousCaptures, Is.Zero);
            controller.Dispose();
            Assert.That(client.Disposals, Is.EqualTo(1));
            controller.Dispose();
            Assert.That(client.Disposals, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator PreviewAdvancesByFramesAndCancellationDisposesItBeforeNetwork()
        {
            var client = new Client(); var preview = new IncrementalPreview();
            using (var controller = new PsdAiNamingController(Session(null, Enumerable.Range(1, 60).Select(id => Node(id)).ToArray()),
                _ => null, settings: _settings, client: client, previewProvider: preview))
            {
                controller.Start(Enumerable.Range(1, 60).ToArray(), false);
                Assert.That(controller.IsBusy, Is.True);
                Assert.That(preview.Steps, Is.Zero);
                yield return WaitFor(() => preview.Steps > 0);
                Assert.That(preview.Steps, Is.LessThan(60));
                controller.Cancel();
                int steps = preview.Steps;
                yield return null;
                Assert.That(preview.Steps, Is.EqualTo(steps));
                Assert.That(preview.Disposals, Is.EqualTo(1));
                Assert.That(preview.SynchronousCaptures, Is.Zero);
                Assert.That(client.Callback, Is.Null);
            }
        }

        [UnityTest]
        public IEnumerator IncrementalPreviewFinishesBeforeAnalysisAndKeepsSettingsSnapshot()
        {
            var client = new Client(); var preview = new IncrementalPreview();
            var settings = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            try
            {
                settings.Rules = "预览启动前规则";
                using (var controller = new PsdAiNamingController(Session(), _ => null, settings, client, preview))
                {
                    controller.Start(new[] { 1, 2 }, false);
                    settings.Rules = "预览期间修改规则";
                    Assert.That(client.Callback, Is.Null);
                    yield return WaitFor(() => client.Callback != null);
                    Assert.That(preview.Steps, Is.EqualTo(2));
                    Assert.That(preview.Disposals, Is.EqualTo(1));
                    Assert.That(client.Settings.Rules, Is.EqualTo("预览启动前规则"));
                    client.Release = true;
                    yield return WaitFor(() => !controller.IsBusy);
                    Assert.That(controller.Rows.All(row => row.CanApply), Is.True);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [UnityTest]
        public IEnumerator ContextChangeCancelsPendingPreviewBeforeClientStarts()
        {
            var session = Session(); var client = new Client(); var preview = new IncrementalPreview();
            using (var controller = new PsdAiNamingController(session, _ => null, settings: _settings, client: client, previewProvider: preview))
            {
                controller.Start(new[] { 1, 2 }, false);
                yield return WaitFor(() => preview.Steps > 0);
                session.SetDocumentVisible(false);
                Assert.That(controller.IsStale, Is.True);
                Assert.That(controller.IsBusy, Is.False);
                Assert.That(preview.Disposals, Is.EqualTo(1));
                yield return null;
                Assert.That(client.Callback, Is.Null);
            }
        }

        [UnityTest]
        public IEnumerator BusyStartDoesNotReplaceOrCancelCurrentRequest()
        {
            var client = new Client();
            using (var controller = new PsdAiNamingController(Session(), _ => null, settings: _settings, client: client, previewProvider: new Preview()))
            {
                controller.Start(new[] { 1 }, false);
                yield return WaitFor(() => client.Callback != null);
                var callback = client.Callback;
                controller.Start(new[] { 2 }, false);
                Assert.That(controller.IsBusy, Is.True);
                Assert.That(controller.Rows.Single().LayerId, Is.EqualTo(1));
                Assert.That(client.Callback, Is.SameAs(callback));
                Assert.That(client.Disposals, Is.Zero);
                client.Release = true;
                yield return WaitFor(() => !controller.IsBusy);
                Assert.That(controller.Rows.Single().LayerId, Is.EqualTo(1));
            }
        }

        [UnityTest]
        public IEnumerator CompletionObserversSeeIdleStateAndNoFurtherFrameNotifications()
        {
            var client = new Client { Release = true };
            using (var controller = new PsdAiNamingController(Session(), _ => null, settings: _settings, client: client, previewProvider: new Preview()))
            {
                var completedStates = new List<bool>();
                int notifications = 0;
                controller.Changed += () =>
                {
                    notifications++;
                    if (controller.Rows.Any(row => row.CanApply)) completedStates.Add(controller.IsBusy);
                };
                controller.Start(new[] { 1 }, false);
                yield return WaitFor(() => !controller.IsBusy);
                Assert.That(completedStates, Is.Not.Empty);
                Assert.That(completedStates.All(busy => !busy), Is.True, "结果完成通知必须在解除 busy 后触发。");
                int finishedNotifications = notifications;
                yield return null;
                yield return null;
                Assert.That(notifications, Is.EqualTo(finishedNotifications));
                Assert.That(client.Disposals, Is.EqualTo(1));
                Assert.That(controller.IsBusy, Is.False);
            }
        }

        [Test]
        public void SynchronousCompletionDoesNotRemainBusyOrLoseResult()
        {
            var client = new ImmediateClient();
            using (var controller = new PsdAiNamingController(Session(), _ => null, settings: _settings, client: client, previewProvider: new Preview()))
            {
                bool? completedBusy = null;
                controller.Changed += () =>
                {
                    if (controller.Rows.Any(row => row.CanApply)) completedBusy = controller.IsBusy;
                };
                controller.Start(new[] { 1 }, false);
                Assert.That(controller.IsBusy, Is.False);
                Assert.That(controller.Rows.Single().CanApply, Is.True);
                Assert.That(client.Disposals, Is.EqualTo(1));
                Assert.That(completedBusy, Is.EqualTo(false));
            }
        }

        [UnityTest]
        public IEnumerator SelectionDoesNotInvalidateAndNoPreviewForcesReview()
        {
            var session = Session(); var client = new Client();
            using (var controller = new PsdAiNamingController(session, _ => null, settings: _settings, client: client, previewProvider: new Preview()))
            {
                controller.Start(new[] { 1, 2 }, false);
                yield return WaitFor(() => client.Callback != null);
                session.Select(2);
                Assert.That(controller.IsStale, Is.False);
                client.Release = true;
                yield return WaitFor(() => !controller.IsBusy);
                Assert.That(controller.Rows.All(r => r.NeedsReview && r.CanApply && !r.Selected), Is.True);
                Assert.That(client.Disposals, Is.EqualTo(1));
            }
        }

        [UnityTest]
        public IEnumerator CancelAndContextMutationRejectLateCallbacks()
        {
            var session = Session(); var client = new Client();
            using (var controller = new PsdAiNamingController(session, _ => null, settings: _settings, client: client, previewProvider: new Preview()))
            {
                controller.Start(new[] { 1 }, false);
                yield return WaitFor(() => client.Callback != null);
                var old = client.Callback;
                controller.Cancel();
                old(new[] { new PsdAiSuggestion("n1", "Late", "迟到", false) });
                Assert.That(controller.Rows.Any(r => r.CanApply), Is.False);
                controller.Start(new[] { 1 }, false);
                client.Callback = null;
                yield return WaitFor(() => client.Callback != null);
                old = client.Callback;
                session.SetDocumentVisible(false);
                Assert.That(controller.IsBusy, Is.False);
                Assert.That(controller.IsStale, Is.True);
                old(new[] { new PsdAiSuggestion("n1", "Late", "迟到", false) });
                Assert.That(controller.ApplySelected(), Is.False);
            }
        }

        [UnityTest]
        public IEnumerator ScopeDeduplicatesSubtreesWithoutFilteringUnimportedNodesAndSnapshotsSettings()
        {
            var session = Session(null, Node(1, group: true), Node(2, 1), Node(3, 1));
            var client = new Client(); var settings = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            try
            {
                settings.Rules = "启动规则";
                using (var controller = new PsdAiNamingController(session, _ => null, settings, client, new Preview()))
                {
                    controller.Start(new[] { 1, 2, 1 }, true);
                    settings.Rules = "修改后规则";
                    yield return WaitFor(() => client.Callback != null);
                    Assert.That(controller.Rows.Select(r => r.LayerId), Is.EquivalentTo(new[] { 1, 2, 3 }));
                    Assert.That(client.Snapshot.NodeKeys.Distinct().Count(), Is.EqualTo(3));
                    Assert.That(client.Settings.Rules, Is.EqualTo("启动规则"));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void TooManyAndVirtualRootFailBeforeClientStarts()
        {
            var session = Session(null, Enumerable.Range(1, 61).Select(id => Node(id)).ToArray());
            var client = new Client();
            using (var controller = new PsdAiNamingController(session, _ => null, settings: _settings, client: client, previewProvider: new Preview()))
            {
                controller.Start(Enumerable.Range(1, 61).ToArray(), false);
                Assert.That(controller.IsBusy, Is.False);
                Assert.That(controller.Error, Is.Not.Empty);
                controller.Start(new[] { -999 }, true);
                Assert.That(controller.Error, Is.Not.Empty);
                Assert.That(client.Callback, Is.Null);
            }
        }

        [UnityTest]
        public IEnumerator MissingSuggestionCannotBeAppliedEvenWhenEdited()
        {
            var client = new Client();
            using (var controller = new PsdAiNamingController(Session(), _ => null, settings: _settings, client: client, previewProvider: new Preview()))
            {
                controller.Start(new[] { 1, 2 }, false);
                yield return WaitFor(() => client.Callback != null);
                client.Callback(new[] { new PsdAiSuggestion("n1", "Confirm", "确认", false) });
                controller.Rows.Single(r => r.LayerId == 2).SuggestedName = "Manual";
                Assert.That(controller.Rows.Single(r => r.LayerId == 2).CanApply, Is.False);
                Assert.That(controller.Rows.Single(r => r.LayerId == 1).CanApply, Is.True);
            }
        }

        [Test]
        public void SuccessfulApplyClearsEntireRoundWithoutStaleNotificationAndCanAnalyzeAgain()
        {
            var repository = new Repository();
            var session = Session(repository);
            using (var controller = new PsdAiNamingController(session, _ => null,
                settings: _settings, client: new ImmediateClient(), previewProvider: new Preview()))
            {
                controller.Start(new[] { 1, 2 }, false);
                Assert.That(controller.ApplySelected(), Is.False);
                Assert.That(controller.Error, Is.Not.Empty);
                controller.Rows[0].Selected = true;
                var notifiedRowCounts = new List<int>();
                var notifiedStaleStates = new List<bool>();
                controller.Changed += () =>
                {
                    notifiedRowCounts.Add(controller.Rows.Count);
                    notifiedStaleStates.Add(controller.IsStale);
                };

                Assert.That(controller.ApplySelected(), Is.True);
                Assert.That(notifiedRowCounts, Is.EqualTo(new[] { 0 }),
                    "自身保存只能在清空整轮结果后通知，不能先用过期结果重建界面。");
                Assert.That(notifiedStaleStates, Is.EqualTo(new[] { false }));
                Assert.That(controller.Rows, Is.Empty);
                Assert.That(controller.IsStale, Is.False);
                Assert.That(controller.Error, Is.Empty);
                Assert.That(repository.Saves, Is.EqualTo(1));
                Assert.That(session.GetNodeName(1), Is.EqualTo("Confirm"));
                Assert.That(session.GetNodeName(2), Is.EqualTo("Original2"));
                Assert.That(session.Document.Find(1).Name, Is.EqualTo("Original1"));
                Assert.That(controller.ApplySelected(), Is.False);
                Assert.That(repository.Saves, Is.EqualTo(1));

                controller.Start(new[] { 2 }, false);
                Assert.That(controller.Rows.Single().LayerId, Is.EqualTo(2));
                Assert.That(controller.Rows.Single().CanApply, Is.True);
                Assert.That(controller.IsBusy, Is.False);
                Assert.That(controller.IsStale, Is.False);
                Assert.That(controller.Error, Is.Empty);
            }
        }

        [Test]
        public void ApplyingUnchangedNameKeepsSelectedSuggestionForCorrection()
        {
            var repository = new Repository();
            var session = Session(repository);
            using (var controller = new PsdAiNamingController(session, _ => null,
                settings: _settings, client: new ImmediateClient(), previewProvider: new Preview()))
            {
                controller.Start(new[] { 1 }, false);
                var row = controller.Rows.Single();
                row.SuggestedName = "Original1";
                row.Selected = true;

                Assert.That(controller.ApplySelected(), Is.False);
                Assert.That(controller.Rows.Single(), Is.SameAs(row));
                Assert.That(row.SuggestedName, Is.EqualTo("Original1"));
                Assert.That(row.Selected, Is.True);
                Assert.That(row.CanApply, Is.True);
                Assert.That(controller.IsStale, Is.False);
                Assert.That(controller.Error, Is.Not.Empty);
                Assert.That(repository.Saves, Is.Zero);

                row.SuggestedName = "Corrected";
                Assert.That(controller.ApplySelected(), Is.True);
                Assert.That(session.GetNodeName(1), Is.EqualTo("Corrected"));
                Assert.That(controller.Rows, Is.Empty);
            }
        }

        [Test]
        public void ApplyDoesNotSuppressUnrelatedReentrantContextChanges()
        {
            var session = Session();
            using (var controller = new PsdAiNamingController(session, _ => null,
                settings: _settings, client: new ImmediateClient(), previewProvider: new Preview()))
            {
                controller.Start(new[] { 1 }, false);
                controller.SetSelection(true);
                bool sawExternalInvalidation = false;
                controller.Changed += () => sawExternalInvalidation |= controller.IsStale;
                session.ContextChanged += () =>
                {
                    if (session.DocumentVisible) session.SetDocumentVisible(false);
                };

                Assert.That(controller.ApplySelected(), Is.True);
                Assert.That(session.DocumentVisible, Is.False);
                Assert.That(sawExternalInvalidation, Is.True,
                    "只允许延后自身命名保存的通知，外部重入的上下文变更仍必须失效。");
                Assert.That(controller.Rows, Is.Empty);
                Assert.That(controller.IsStale, Is.False);
            }
        }

        [Test]
        public void SuccessfulBatchRemainsOnePersistentUndoStepWithoutRevivingSuggestions()
        {
            string guid = Guid.NewGuid().ToString("N");
            string path = PsdNodeNameRepository.GetConfigPath(guid);
            var repository = new PsdNodeNameRepository();
            var document = new PsdDocumentSnapshot(guid, 100, 100, new[] { Node(1), Node(2) });
            var session = new PsdSession(nameRepository: repository);
            session.Refresh(document);
            repository.Changed += session.RestoreNames;
            try
            {
                using (var controller = new PsdAiNamingController(session, _ => null,
                    settings: _settings, client: new ImmediateClient(), previewProvider: new Preview()))
                {
                    controller.Start(new[] { 1, 2 }, false);
                    controller.Rows[1].SuggestedName = "Cancel";
                    controller.SetSelection(true);
                    Undo.IncrementCurrentGroup();
                    Assert.That(controller.ApplySelected(), Is.True);
                    Undo.FlushUndoRecordObjects();
                    Undo.IncrementCurrentGroup();
                    Assert.That(controller.Rows, Is.Empty);

                    Undo.PerformUndo();
                    Assert.That(session.GetNodeName(1), Is.EqualTo("Original1"));
                    Assert.That(session.GetNodeName(2), Is.EqualTo("Original2"));
                    Assert.That(controller.Rows, Is.Empty);
                    Assert.That(controller.IsStale, Is.False);
                    Assert.That(controller.Error, Is.Empty);
                    var undone = new PsdSession(nameRepository: repository);
                    undone.Refresh(document);
                    Assert.That(undone.GetNodeName(1), Is.EqualTo("Original1"));
                    Assert.That(undone.GetNodeName(2), Is.EqualTo("Original2"));

                    Undo.PerformRedo();
                    repository.Flush();
                    Assert.That(session.GetNodeName(1), Is.EqualTo("Confirm"));
                    Assert.That(session.GetNodeName(2), Is.EqualTo("Cancel"));
                    Assert.That(controller.Rows, Is.Empty);
                    Assert.That(controller.IsStale, Is.False);
                    Assert.That(controller.Error, Is.Empty);
                    using (var reopenedRepository = new PsdNodeNameRepository())
                    {
                        var reopened = new PsdSession(nameRepository: reopenedRepository);
                        reopened.Refresh(document);
                        Assert.That(reopened.GetNodeName(1), Is.EqualTo("Confirm"));
                        Assert.That(reopened.GetNodeName(2), Is.EqualTo("Cancel"));
                    }
                    Assert.That(document.Find(1).Name, Is.EqualTo("Original1"));
                    Assert.That(document.Find(2).Name, Is.EqualTo("Original2"));
                }
            }
            finally
            {
                repository.Changed -= session.RestoreNames;
                repository.Dispose();
                var config = AssetDatabase.LoadMainAssetAtPath(path);
                if (config != null) Undo.ClearUndo(config);
                AssetDatabase.DeleteAsset(path);
            }
        }

        [UnityTest]
        public IEnumerator WholeBatchUsesSingleSaveAndPersistenceFailureLeavesAllNamesUntouched()
        {
            var repository = new Repository(); var session = Session(repository); var client = new Client { Release = true };
            using (var controller = new PsdAiNamingController(session, _ => null, settings: _settings, client: client, previewProvider: new Preview()))
            {
                controller.Start(new[] { 1, 2 }, false);
                yield return WaitFor(() => !controller.IsBusy);
                controller.SetSelection(true);
                repository.Fail = true;
                Assert.That(controller.ApplySelected(), Is.False);
                Assert.That(session.GetNodeName(1), Is.EqualTo("Original1"));
                Assert.That(session.GetNodeName(2), Is.EqualTo("Original2"));
                Assert.That(controller.Rows.Count, Is.EqualTo(2));
                Assert.That(controller.Rows.All(row => row.Selected && row.CanApply), Is.True);
                Assert.That(controller.Rows.Select(row => row.SuggestedName), Is.EqualTo(new[] { "Result0", "Result1" }));
                Assert.That(controller.IsStale, Is.False);
                Assert.That(controller.Error, Is.Not.Empty);
                repository.Fail = false;
                Assert.That(controller.ApplySelected(), Is.True);
                Assert.That(repository.Saves, Is.EqualTo(1));
                Assert.That(session.Document.Find(1).Name, Is.EqualTo("Original1"));
                Assert.That(controller.Rows, Is.Empty);
                Assert.That(controller.IsStale, Is.False);
                Assert.That(controller.Error, Is.Empty);
            }
        }

        [UnityTest]
        public IEnumerator SiblingCollisionRejectsWholeBatchAndEditedInvalidNameIsRejected()
        {
            var repository = new Repository(); var session = Session(repository); var client = new Client { Release = true };
            using (var controller = new PsdAiNamingController(session, _ => null, settings: _settings, client: client, previewProvider: new Preview()))
            {
                controller.Start(new[] { 1, 2 }, false);
                yield return WaitFor(() => !controller.IsBusy);
                controller.Rows[0].SuggestedName = "Same";
                controller.Rows[1].SuggestedName = "Same";
                controller.SetSelection(true);
                Assert.That(controller.ApplySelected(), Is.False);
                Assert.That(repository.Saves, Is.Zero);
                Assert.That(session.GetNodeName(1), Is.EqualTo("Original1"));
                Assert.That(session.GetNodeName(2), Is.EqualTo("Original2"));
                Assert.That(controller.Rows.Count, Is.EqualTo(2));
                Assert.That(controller.Rows.All(row => row.Selected && row.CanApply && row.SuggestedName == "Same"), Is.True);
                Assert.That(controller.IsStale, Is.False);
                Assert.That(controller.Error, Is.Not.Empty);
                controller.Rows[0].SuggestedName = "btn_Invalid";
                controller.Rows[1].SuggestedName = "Other";
                Assert.That(controller.ApplySelected(), Is.False);
                Assert.That(repository.Saves, Is.Zero);
                Assert.That(controller.Rows.Count, Is.EqualTo(2));
                Assert.That(controller.Rows.All(row => row.Selected), Is.True);
                Assert.That(controller.Rows.Select(row => row.SuggestedName), Is.EqualTo(new[] { "btn_Invalid", "Other" }));
                Assert.That(controller.IsStale, Is.False);
            }
        }

        [UnityTest]
        public IEnumerator ExactSixtyIsAllowedAndSelectionOnlyDoesNotIncludeChildren()
        {
            var nodes = Enumerable.Range(1, 60).Select(id => Node(id)).ToArray();
            var client = new Client();
            using (var controller = new PsdAiNamingController(Session(null, nodes), _ => null, settings: _settings, client: client, previewProvider: new Preview()))
            {
                controller.Start(Enumerable.Range(1, 60).ToArray(), false);
                yield return WaitFor(() => client.Callback != null);
                Assert.That(controller.Rows.Count, Is.EqualTo(60));
            }
            client = new Client();
            using (var controller = new PsdAiNamingController(Session(null, Node(1, group: true), Node(2, 1)), _ => null, settings: _settings, client: client, previewProvider: new Preview()))
            {
                controller.Start(new[] { 1 }, false);
                yield return WaitFor(() => client.Callback != null);
                Assert.That(controller.Rows.Select(r => r.LayerId), Is.EqualTo(new[] { 1 }));
            }
        }

        [UnityTest]
        public IEnumerator CrossBranchDuplicatesWarnWithoutBlockingApplication()
        {
            var session = Session(null, Node(1, group: true), Node(2, 1), Node(3, group: true), Node(4, 3));
            var client = new Client();
            using (var controller = new PsdAiNamingController(session, _ => null, settings: _settings, client: client, previewProvider: new Preview()))
            {
                controller.Start(new[] { 2, 4 }, false);
                yield return WaitFor(() => client.Callback != null);
                client.Callback(new[]
                {
                    new PsdAiSuggestion("n2", "Confirm", "确认", false),
                    new PsdAiSuggestion("n4", "Confirm", "确认", false)
                });
                Assert.That(controller.Rows.All(r => r.CanApply && r.NeedsReview), Is.True);
                Assert.That(controller.Rows.All(r => r.Reason.Contains("跨分支")), Is.True);
                controller.SetSelection(true);
                Assert.That(controller.ApplySelected(), Is.True);
            }
        }

        [UnityTest]
        public IEnumerator DisposeReleasesRequestAndRejectsCallback()
        {
            var client = new Client(); var controller = new PsdAiNamingController(Session(), _ => null, settings: _settings, client: client, previewProvider: new Preview());
            controller.Start(new[] { 1 }, false);
            yield return WaitFor(() => client.Callback != null);
            var callback = client.Callback;
            controller.Dispose();
            callback(new[] { new PsdAiSuggestion("n1", "Late", "迟到", false) });
            Assert.That(controller.IsBusy, Is.False);
            Assert.That(client.Disposals, Is.EqualTo(1));
            Assert.That(controller.Rows.Any(r => r.CanApply), Is.False);
        }
        [UnityTest]
        public IEnumerator WholeDocumentSkipsExcludedSubtreesAndIncludesSavedAncestorDescriptions()
        {
            var nodes = new[]
            {
                new PsdNode(1, null, "Weapons", true, false, true, false, "", new PixelRect(0, 0, 40, 40), 0),
                new PsdNode(2, 1, "Magazine", false, true, true, false, "sprite2", new PixelRect(0, 0, 20, 20), 1),
                new PsdNode(3, 1, "Unused", false, true, true, false, "sprite3", new PixelRect(20, 0, 20, 20), 2),
                new PsdNode(4, null, "Footer", false, true, true, false, "sprite4", new PixelRect(0, 40, 20, 20), 3)
            };
            var session = Session(null, nodes);
            session.SetNodeAnnotation(1, "这是武器栏");
            session.SetNodeAnnotation(2, "当前弹匣");
            session.SetNodeAnnotation(3, "机密：不导出");
            session.SetNodeExportable(3, false);
            var client = new Client();
            using (var controller = new PsdAiNamingController(session, _ => null,
                settings: _settings, client: client, previewProvider: new Preview()))
            {
                controller.StartWholeDocument();
                yield return WaitFor(() => client.Callback != null);
                Assert.That(controller.Rows.Select(r => r.LayerId), Is.EqualTo(new[] { 1, 2, 4 }));
                Assert.That(client.Snapshot.NodeKeys, Is.EqualTo(new[] { "n1", "n2", "n4" }));
                Assert.That(client.Snapshot.ContextJson, Does.Contain("这是武器栏"));
                Assert.That(client.Snapshot.ContextJson, Does.Contain("当前弹匣"));
                Assert.That(client.Snapshot.ContextJson, Does.Contain("ancestorDescriptions"));
                Assert.That(client.Snapshot.ContextJson, Does.Not.Contain("机密：不导出"));
                session.SetNodeAnnotation(1, "换一条说明");
                Assert.That(controller.IsStale, Is.True);
                Assert.That(controller.IsBusy, Is.False);
                Assert.That(controller.ApplySelected(), Is.False);
            }
        }

        [Test]
        public void AllExcludedStopsBeforeRequestAndCompletedResultsCanBeDiscardedWithoutSaving()
        {
            var node = new PsdNode(1, null, "Inventory", false, true, true, false, "sprite",
                new PixelRect(0, 0, 20, 20), 0);
            var session = Session(null, node);
            session.SetNodeExportable(1, false);
            var client = new Client();
            using (var controller = new PsdAiNamingController(session, _ => null,
                settings: _settings, client: client, previewProvider: new Preview()))
            {
                controller.StartWholeDocument();
                Assert.That(controller.IsBusy, Is.False);
                Assert.That(controller.Rows, Is.Empty);
                Assert.That(client.Callback, Is.Null);
                Assert.That(controller.Error, Does.Contain("没有可导出节点"));
                session.SetNodeExportable(1, true);
                controller.StartWholeDocument();
                Assert.That(controller.Rows, Has.Count.EqualTo(1));
                Assert.That(controller.IsBusy, Is.True);
                controller.Cancel();
            }
            using (var controller = new PsdAiNamingController(session, _ => null,
                settings: _settings, client: new ImmediateClient(), previewProvider: new Preview()))
            {
                controller.StartWholeDocument();
                Assert.That(controller.Rows, Has.Count.EqualTo(1));
                controller.SetSelection(true);
                controller.Discard();
                Assert.That(controller.Rows, Is.Empty);
                Assert.That(controller.IsStale, Is.False);
                Assert.That(controller.ApplySelected(), Is.False);
                Assert.That(session.GetNodeName(1), Is.EqualTo("Inventory"));
            }
        }

    }
}
