using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.AiNaming;
using PsdUiWorkbench.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdAiPanelTests
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

        public sealed class PanelHost : EditorWindow { }
        private sealed class Client : IPsdAiNamingClient
        {
            public bool Release;
            public int Calls;
            public IEnumerator Analyze(PsdAiNamingSnapshot snapshot, PsdAiRequestSettings settings, Action<IReadOnlyList<PsdAiSuggestion>> completed)
            {
                Calls++;
                while (!Release) yield return null;
                completed(snapshot.NodeKeys.Select((key, i) => new PsdAiSuggestion(key, "Name" + i, "中文理由", false)).ToArray());
            }
            public void Dispose() { }
        }
        private sealed class Preview : IPsdAiPreviewProvider
        {
            public PsdAiPreviewResult Capture(PsdDocumentSnapshot document, IReadOnlyList<int> targets, PsdVisibilityState visibility, Func<string, Sprite> resolve)
                => new PsdAiPreviewResult(Array.Empty<PsdAiImage>(), Array.Empty<int>());
        }
        private sealed class Repository : IPsdNodeNameRepository
        {
            public int Saves;
            public PsdNodeNameState Load(string guid) => new PsdNodeNameState();
            public void Save(string guid, PsdNodeNameState state) { Saves++; }
        }
        private static PsdSession Session(Repository repository = null)
        {
            var session = new PsdSession(nameRepository: repository);
            session.Refresh(new PsdDocumentSnapshot("fake", 100, 100, new[] {
                new PsdNode(1, null, "Group", true, false, true, false, "", new PixelRect(0, 0, 20, 20), 0),
                new PsdNode(2, 1, "Child", false, false, true, false, "", new PixelRect(0, 0, 20, 20), 1) }));
            return session;
        }
        private static void Click(PsdAiNamingPanel panel, string name)
        {
            var button = panel.Q<Button>(name);
            button.Focus();
            using (var submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = button;
                button.SendEvent(submit);
            }
        }

        [UnityTest]
        public IEnumerator InvalidSavedModelExplainsProblemWithoutDisablingSelectedNodeOrSendingRequest()
        {
            var settings = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            settings.Provider = PsdAiProvider.Zhipu;
            settings.Model = "glm-5.3-flash";
            settings.ReasoningEffort = PsdAiReasoningEffort.None;
            var session = Session();
            var client = new Client();
            using (var controller = new PsdAiNamingController(session, _ => null, settings: settings,
                client: client, previewProvider: new Preview()))
            using (var panel = new PsdAiNamingPanel(session, controller, () => new[] { 1 }))
            {
                var host = ScriptableObject.CreateInstance<PanelHost>();
                try
                {
                    host.rootVisualElement.Add(panel);
                    host.ShowUtility();
                    yield return null;
                    Assert.That(panel.Q<Button>("analyzeSelection").enabledSelf, Is.True);
                    Assert.That(panel.Q<Button>("analyzeSubtree").enabledSelf, Is.True);
                    Assert.That(panel.Q<Label>("modelIssue").text, Does.Contain("高（启用）"));
                    Click(panel, "analyzeSelection");
                    Assert.That(controller.IsBusy, Is.False);
                    Assert.That(controller.Error, Does.Contain("高（启用）"));
                    Assert.That(client.Calls, Is.Zero, "不兼容的模型设置不能发送请求。");
                    settings.ReasoningEffort = PsdAiReasoningEffort.High;
                    panel.Refresh();
                    yield return null;
                    Assert.That(panel.Q<Label>("modelIssue").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                    Click(panel, "analyzeSelection");
                    Assert.That(controller.IsBusy, Is.True);
                    controller.Cancel();
                }
                finally
                {
                    host.Close();
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            }
        }

        [UnityTest]
        public IEnumerator SelectedDocumentRootAnalyzesAllLayersThroughSubtreeButton()
        {
            var session = Session(); var client = new Client(); bool rootSelected = true;
            using (var controller = new PsdAiNamingController(session, _ => null, settings: _settings,
                client: client, previewProvider: new Preview()))
            using (var panel = new PsdAiNamingPanel(session, controller, () => Array.Empty<int>(), () => rootSelected))
            {
                var host = ScriptableObject.CreateInstance<PanelHost>();
                try
                {
                    host.rootVisualElement.Add(panel);
                    host.ShowUtility();
                    yield return null;
                    Assert.That(panel.Q<Button>("analyzeAll"), Is.Null);
                    Assert.That(panel.Q<Button>("analyzeSelection").enabledSelf, Is.False);
                    Assert.That(panel.Q<Button>("analyzeSubtree").enabledSelf, Is.True);
                    Click(panel, "analyzeSubtree");
                    Assert.That(controller.Rows.Select(row => row.LayerId), Is.EquivalentTo(new[] { 1, 2 }));
                    controller.Cancel();
                    rootSelected = false;
                    panel.RefreshSelection();
                    Assert.That(panel.Q<Button>("analyzeSubtree").enabledSelf, Is.False);
                }
                finally { host.Close(); }
            }
        }

        [UnityTest]
        public IEnumerator SelectionControlsScopeAndDisposeDoesNotOwnController()
        {
            var session = Session(); var client = new Client();
            IReadOnlyList<int> selected = Array.Empty<int>();
            using (var controller = new PsdAiNamingController(session, _ => null, settings: _settings,
                client: client, previewProvider: new Preview()))
            {
                var panel = new PsdAiNamingPanel(session, controller, () => selected);
                var host = ScriptableObject.CreateInstance<PanelHost>();
                try
                {
                    host.position = new Rect(100, 100, 420, 800);
                    host.rootVisualElement.Add(panel);
                    host.ShowUtility();
                    yield return null;
                    yield return null;
                    Assert.That(panel.Q<Button>("analyzeSelection").enabledSelf, Is.False);
                    selected = new[] { 1 }; panel.RefreshSelection();
                    Assert.That(panel.Q<Button>("analyzeSubtree").enabledSelf, Is.True);
                    Click(panel, "analyzeSelection");
                    Assert.That(controller.Rows.Select(r => r.LayerId), Is.EqualTo(new[] { 1 }));
                    Click(panel, "cancel");
                    Assert.That(controller.IsBusy, Is.False);
                    Click(panel, "analyzeSubtree");
                    Assert.That(controller.Rows.Select(r => r.LayerId), Is.EquivalentTo(new[] { 1, 2 }));
                    int closes = 0; panel.CloseRequested += () => closes++;
                    Click(panel, "close"); Assert.That(closes, Is.EqualTo(1));
                    panel.Dispose();
                    Assert.That(controller.IsBusy, Is.True, "面板不能释放或取消 Window 管理的控制器。");
                    Click(panel, "close"); Click(panel, "cancel");
                    Assert.That(closes, Is.EqualTo(1));
                    Assert.That(controller.IsBusy, Is.True);
                    controller.Cancel();
                }
                finally { panel.Dispose(); host.Close(); }
            }
        }

        [UnityTest]
        public IEnumerator RecycledResultEditsOnlyCurrentRowAndDoesNotPersistBeforeApply()
        {
            var repository = new Repository(); var session = Session(repository); var client = new Client { Release = true };
            using (var controller = new PsdAiNamingController(session, _ => null, settings: _settings,
                client: client, previewProvider: new Preview()))
            using (var panel = new PsdAiNamingPanel(session, controller, () => new[] { 1, 2 }))
            {
                var host = ScriptableObject.CreateInstance<PanelHost>();
                try
                {
                    host.position = new Rect(100, 100, 420, 800);
                    host.rootVisualElement.Add(panel);
                    host.ShowUtility();
                    yield return null;
                    yield return null;
                    controller.Start(new[] { 1, 2 }, false);
                    for (int i = 0; i < 100 && controller.IsBusy; i++) yield return null;
                    Assert.That(controller.IsBusy, Is.False);
                    Assert.That(panel.Q<Button>("analyzeSelection").enabledInHierarchy, Is.True);
                    Assert.That(panel.Q<Button>("analyzeSubtree").enabledInHierarchy, Is.True);
                    Assert.That(panel.Q<Button>("openSettings").enabledInHierarchy, Is.True);
                    Assert.That(panel.Q<Button>("apply").enabledInHierarchy, Is.False, "完成后仍需明确勾选。");
                    var list = panel.Q<ListView>("results");
                    var recycled = list.makeItem();
                    host.rootVisualElement.Add(recycled);
                    list.bindItem(recycled, 0);
                    yield return null;
                    recycled.Q<TextField>().value = "First";
                    recycled.Q<Toggle>().value = true;
                    Assert.That(panel.Q<Button>("apply").enabledInHierarchy, Is.True);
                    list.unbindItem(recycled, 0);
                    Assert.That(recycled.userData, Is.Null);
                    recycled.Q<TextField>().value = "Detached";
                    list.bindItem(recycled, 1);
                    yield return null;
                    recycled.Q<TextField>().value = "Second";
                    recycled.Q<Toggle>().value = true;
                    Assert.That(controller.Rows[0].SuggestedName, Is.EqualTo("First"));
                    Assert.That(controller.Rows[1].SuggestedName, Is.EqualTo("Second"));
                    Assert.That(controller.Rows.All(r => r.Selected), Is.True);
                    Assert.That(repository.Saves, Is.Zero);
                    Assert.That(session.CaptureNames().Overrides, Is.Empty);
                    list.unbindItem(recycled, 1);
                    recycled.RemoveFromHierarchy();
                }
                finally { host.Close(); }
            }
        }

        [UnityTest]
        public IEnumerator ApplyingSuggestionsRemovesReviewRowsAndToolbarFromLayout()
        {
            var repository = new Repository();
            var session = Session(repository);
            using (var controller = new PsdAiNamingController(session, _ => null, settings: _settings,
                client: new Client { Release = true }, previewProvider: new Preview()))
            using (var panel = new PsdAiNamingPanel(session, controller, () => new[] { 1, 2 }))
            {
                var host = ScriptableObject.CreateInstance<PanelHost>();
                try
                {
                    host.position = new Rect(100, 100, 420, 800);
                    host.rootVisualElement.Add(panel);
                    host.ShowUtility();
                    yield return null;
                    controller.Start(new[] { 1, 2 }, false);
                    for (int i = 0; i < 100 && controller.IsBusy; i++) yield return null;
                    Assert.That(controller.IsBusy, Is.False);
                    controller.SetSelection(true);
                    yield return null;
                    Assert.That(panel.Q<Button>("apply").enabledInHierarchy, Is.True);
                    Click(panel, "apply");
                    yield return null;
                    Assert.That(session.GetNodeName(1), Is.EqualTo("Name0"));
                    Assert.That(session.GetNodeName(2), Is.EqualTo("Name1"));
                    Assert.That(controller.Rows, Is.Empty);
                    Assert.That(controller.IsStale, Is.False);
                    var list = panel.Q<ListView>("results");
                    Assert.That(list.itemsSource.Count, Is.Zero);
                    Assert.That(list.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                    Assert.That(panel.Q<Button>("apply").parent.resolvedStyle.display,
                        Is.EqualTo(DisplayStyle.None), "已应用的审核工具条不再占据布局空间。");
                    Assert.That(repository.Saves, Is.EqualTo(1));
                }
                finally { host.Close(); }
            }
        }

        [Test]
        public void ImportPreviewProtectsExistingNamesAndDoesNotPersist()
        {
            var repository = new Repository(); var session = Session(repository);
            session.SetNodeNames(new Dictionary<int, string> { [1] = "WorkbenchName" });
            int saves = repository.Saves;
            var preview = PsdAiLegacyMigration.BuildNameImport(session, "{\"layers\":[{\"id\":1,\"name\":\"OldGroup\"},{\"id\":2,\"name\":\"OldChild\"}]}");
            Assert.That(preview.Changes.ContainsKey(1), Is.False);
            Assert.That(preview.Changes.ContainsKey(2), Is.True);
            Assert.That(repository.Saves, Is.EqualTo(saves));
            Assert.That(session.GetNodeName(2), Is.EqualTo("Child"));
        }
        [UnityTest]
        public IEnumerator SelectedNodeDescriptionCanBeSavedReplacedAndClearedAndReviewCanBeDiscarded()
        {
            var session = Session();
            IReadOnlyList<int> selected = new[] { 1 };
            using (var controller = new PsdAiNamingController(session, _ => null, settings: _settings,
                client: new Client { Release = true }, previewProvider: new Preview()))
            using (var panel = new PsdAiNamingPanel(session, controller, () => selected))
            {
                var host = ScriptableObject.CreateInstance<PanelHost>();
                try
                {
                    host.position = new Rect(100, 100, 420, 800);
                    host.rootVisualElement.Add(panel);
                    host.ShowUtility();
                    yield return null;
                    panel.RefreshSelection();
                    var input = panel.Q<TextField>("annotationInput");
                    input.value = "背包左侧装备槽";
                    Assert.That(session.GetNodeAnnotation(1), Is.Empty, "编辑但未保存的描述不能进入状态。");
                    Click(panel, "saveAnnotation");
                    Assert.That(session.GetNodeAnnotation(1), Is.EqualTo("背包左侧装备槽"));
                    selected = new[] { 2 };
                    panel.RefreshSelection();
                    Assert.That(input.value, Is.Empty);
                    input.value = "消耗品";
                    Click(panel, "saveAnnotation");
                    selected = new[] { 1 };
                    panel.RefreshSelection();
                    Assert.That(input.value, Is.EqualTo("背包左侧装备槽"));
                    Click(panel, "analyzeSelection");
                    for (int i = 0; i < 100 && controller.IsBusy; i++) yield return null;
                    Assert.That(controller.Rows, Has.Count.EqualTo(1));
                    Click(panel, "discard");
                    yield return null;
                    Assert.That(controller.Rows, Is.Empty);
                    Assert.That(panel.Q<Button>("discard").parent.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                    Click(panel, "clearAnnotation");
                    Assert.That(session.GetNodeAnnotation(1), Is.Empty);
                    selected = new[] { 1, 2 };
                    panel.RefreshSelection();
                    yield return null;
                    Assert.That(panel.Q<VisualElement>("annotationEditor").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                    Assert.That(session.GetNodeAnnotation(2), Is.EqualTo("消耗品"));
                }
                finally { host.Close(); }
            }
        }

    }
}
