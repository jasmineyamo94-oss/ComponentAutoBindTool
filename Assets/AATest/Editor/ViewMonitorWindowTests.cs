using System;
using System.Collections;
using System.Collections.Generic;
using ComponentAutoBindTool.Editor;
using ComponentAutoBindTool.ViewCore;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Tests
{
    public sealed class ViewMonitorWindowTests
    {
        [UnityTearDown]
        public IEnumerator LeavePlayMode()
        {
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }
        }

        [Test]
        public void RegistryUsesIdentityAndRejectsDuplicatesAndDisposedManagers()
        {
            var first = new EqualDiagnostics();
            var second = new EqualDiagnostics();
            int originalCount = ViewManagerDebugRegistry.ActiveManagers.Count;
            try
            {
                ViewManagerDebugRegistry.Register(null);
                ViewManagerDebugRegistry.Register(first);
                ViewManagerDebugRegistry.Register(first);
                ViewManagerDebugRegistry.Register(second);
                Assert.That(ViewManagerDebugRegistry.ActiveManagers.Count, Is.EqualTo(originalCount + 2));
                ViewManagerDebugRegistry.Unregister(first);
                Assert.That(ContainsReference(second), Is.True);
                Assert.That(ContainsReference(first), Is.False);
                second.IsDisposed = true;
                Assert.That(ViewManagerDebugRegistry.ActiveManagers.Count, Is.EqualTo(originalCount));
                ViewManagerDebugRegistry.Register(second);
                Assert.That(ContainsReference(second), Is.False);
            }
            finally
            {
                ViewManagerDebugRegistry.Unregister(first);
                ViewManagerDebugRegistry.Unregister(second);
            }
        }

        [Test]
        public void RegistryDoesNotExposeMutableBackingList()
        {
            var managers = ViewManagerDebugRegistry.ActiveManagers;
            Assert.That(managers, Is.Not.InstanceOf<List<IViewManagerDiagnostics>>());
            Assert.That(((ICollection<IViewManagerDiagnostics>)managers).IsReadOnly, Is.True);
            Assert.Throws<NotSupportedException>(() => ((ICollection<IViewManagerDiagnostics>)managers).Add(new FakeDiagnostics()));
        }

        [Test]
        public void EditModeDisablesActionsEvenWithRegisteredManager()
        {
            var manager = new FakeDiagnostics();
            ViewManagerDebugRegistry.Register(manager);
            var window = ScriptableObject.CreateInstance<ViewMonitorWindow>();
            try
            {
                window.CreateGUI();
                window.RefreshView();
                Assert.That(window.rootVisualElement.Q("actions").enabledSelf, Is.False);
                Assert.That(window.rootVisualElement.Q<PopupField<string>>("managerSelector").enabledSelf, Is.False);
                Assert.That(window.rootVisualElement.Q<Label>("summary").text, Does.Contain("Play Mode"));
                Assert.That(window.rootVisualElement.styleSheets.count, Is.EqualTo(1));
                Assert.That(manager.SnapshotReads, Is.Zero, "编辑状态不抓取运行期快照。");
                window.CreateGUI();
                Assert.That(window.rootVisualElement.styleSheets.count, Is.EqualTo(1), "重建面板不重复添加样式。");
            }
            finally
            {
                Object.DestroyImmediate(window);
                ViewManagerDebugRegistry.Unregister(manager);
            }
        }

        [UnityTest]
        public IEnumerator MonitorAndRegistryResetWhenDomainReloadIsDisabled()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            bool previousEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            var previousOptions = EditorSettings.enterPlayModeOptions;
            var editManager = new FakeDiagnostics();
            var playManager = new FakeDiagnostics();
            var window = ScriptableObject.CreateInstance<ViewMonitorWindow>();
            try
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
                ViewManagerDebugRegistry.Register(editManager);
                window.Show();
                window.CreateGUI();
                yield return new EnterPlayMode();
                Assert.That(ContainsReference(editManager), Is.False, "进入Play时清理此前静态注册引用。");
                playManager.Windows.Add(Snapshot(1, "Runtime", UILayer.Normal, WindowState.OpenAndActive, null));
                ViewManagerDebugRegistry.Register(playManager);
                window.RefreshView();
                var row = window.rootVisualElement.Q("layerNormal").Q(className: "monitor-row");
                Assert.That(row.ClassListContains("monitor-hidden"), Is.False);
                yield return new ExitPlayMode();
                Assert.That(window.rootVisualElement.Q("actions").enabledSelf, Is.False);
                Assert.That(row.ClassListContains("monitor-hidden"), Is.True);
                Assert.That(row.Q<Label>(className: "monitor-row-info").text, Is.Empty);
                yield return new EnterPlayMode();
                Assert.That(ContainsReference(playManager), Is.False, "再次进入Play不能残留上次管理器。");
                yield return new ExitPlayMode();
            }
            finally
            {
                EditorSettings.enterPlayModeOptionsEnabled = previousEnabled;
                EditorSettings.enterPlayModeOptions = previousOptions;
                if (window != null)
                {
                    window.Close();
                }
                ViewManagerDebugRegistry.Unregister(editManager);
                ViewManagerDebugRegistry.Unregister(playManager);
            }
        }

        [UnityTest]
        public IEnumerator RuntimePanelReusesRowsAndActionsReadCurrentManagerAndState()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            var first = new FakeDiagnostics();
            var second = new FakeDiagnostics();
            var firstHost = new GameObject("监控测试窗口一");
            var secondHost = new GameObject("监控测试窗口二");
            first.Windows.Add(Snapshot(1, "First", UILayer.Normal, WindowState.OpenAndActive, firstHost));
            first.Windows.Add(Snapshot(2, "Cached", UILayer.HUD, WindowState.Cached, firstHost));
            first.Queue.Add(new QueuedRequestSnapshot(0, "Queued", 10, "TestArgs"));
            second.Windows.Add(Snapshot(1, "Second", UILayer.Top, WindowState.Paused, secondHost));
            second.Windows.Add(Snapshot(2, "Delayed", UILayer.Normal, WindowState.PendingDestroy, secondHost));
            second.Queue.Add(new QueuedRequestSnapshot(0, "SecondQueued", 20, "TestArgs"));
            ViewManagerDebugRegistry.Register(first);
            ViewManagerDebugRegistry.Register(second);
            var window = ScriptableObject.CreateInstance<ViewMonitorWindow>();
            try
            {
                window.Show();
                window.CreateGUI();
                yield return null;
                window.RefreshView();
                var root = window.rootVisualElement;
                Assert.That(root.Q("actions").enabledSelf, Is.True);
                Assert.That(root.Q("layerSystem"), Is.Not.Null);
                Assert.That(root.Q("layerTop"), Is.Not.Null);
                Assert.That(root.Q("layerNormal"), Is.Not.Null);
                Assert.That(root.Q("layerHUD"), Is.Not.Null);
                var firstRow = root.Q("layerNormal").Q(className: "monitor-row");
                var firstClose = firstRow.Q<Button>("close");
                window.RefreshView();
                Assert.That(root.Q("layerNormal").Q(className: "monitor-row"), Is.SameAs(firstRow));

                Submit(firstRow.Q<Button>("ping"));
                Assert.That(Selection.activeGameObject, Is.SameAs(firstHost));
                first.Windows[0] = Snapshot(1, "First", UILayer.Normal, WindowState.Cached, firstHost);
                Submit(firstClose);
                Assert.That(first.CloseCalls, Is.Zero, "旧的活动行不能关闭已缓存实例。");

                first.Windows[0] = Snapshot(1, "First", UILayer.Normal, WindowState.OpenAndActive, firstHost);
                window.RefreshView();
                Submit(firstClose);
                Assert.That(first.CloseCalls, Is.EqualTo(1));
                var selector = root.Q<PopupField<string>>("managerSelector");
                selector.value = selector.choices[1];
                Assert.That(root.Q("layerTop").Q<Label>(className: "monitor-row-info").text, Does.Contain("Second"));
                Submit(root.Q<Button>("navigateBack"));
                Assert.That(second.BackCalls, Is.EqualTo(1));
                Assert.That(first.BackCalls, Is.Zero);
                Submit(root.Q<Button>("clearQueue"));
                Assert.That(second.Queue, Is.Empty);
                Assert.That(first.Queue.Count, Is.EqualTo(1));
                Submit(root.Q<Button>("closeAll"));
                Submit(root.Q<Button>("destroyAll"));
                CollectionAssert.AreEqual(new[] { false, true }, second.CloseAllCalls);
                Assert.That(root.Q("inactive").Q<Button>("close").enabledSelf, Is.False);
                Assert.That(root.Q("inactive").Q<Label>(className: "monitor-row-info").text, Does.Contain("2.5s"));

                second.IsDisposed = true;
                Submit(root.Q<Button>("clearQueue"));
                Assert.That(second.ClearQueueCalls, Is.EqualTo(1), "已释放管理器的旧按钮不可继续操作。");
                Assert.That(first.ClearQueueCalls, Is.Zero, "失效按钮也不可转发到另一个管理器。");
                Assert.That(selector.value, Does.EndWith("#1"));
                ViewManagerDebugRegistry.Unregister(first);
                window.RefreshView();
                Assert.That(root.Q("actions").enabledSelf, Is.False);
                Assert.That(firstRow.ClassListContains("monitor-hidden"), Is.True);
                Assert.That(firstRow.Q<Label>(className: "monitor-row-info").text, Is.Empty);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                window.Close();
                ViewManagerDebugRegistry.Unregister(first);
                ViewManagerDebugRegistry.Unregister(second);
                Object.DestroyImmediate(firstHost);
                Object.DestroyImmediate(secondHost);
                Selection.activeObject = null;
            }
            yield return new ExitPlayMode();
        }

        private static void Submit(Button button)
        {
            Assert.That(button, Is.Not.Null);
            Assert.That(button.panel, Is.Not.Null, "按钮必须挂载到真实编辑器面板。");
            Assert.That(button.enabledInHierarchy, Is.True, "测试应从可交互按钮触发操作。");
            int clickCount = 0;
            Action recordClick = () => clickCount++;
            button.clicked += recordClick;
            try
            {
                using var submit = NavigationSubmitEvent.GetPooled();
                submit.target = button;
                button.SendEvent(submit);
                Assert.That(clickCount, Is.EqualTo(1), "导航事件必须实际触发目标按钮的点击回调。");
            }
            finally
            {
                button.clicked -= recordClick;
            }
        }

        private static WindowDiagnosticSnapshot Snapshot(int id, string name, UILayer layer,
            WindowState state, GameObject host)
        {
            return new WindowDiagnosticSnapshot(id, name, layer, state, WindowInstanceMode.Singleton,
                ViewCoverMode.Overlay, ViewCloseStrategy.DestroyDelayed, 100, 2, 2.5f,
                state == WindowState.PendingDestroy, true, host);
        }

        private static bool ContainsReference(IViewManagerDiagnostics target)
        {
            var managers = ViewManagerDebugRegistry.ActiveManagers;
            for (int i = 0; i < managers.Count; i++)
            {
                if (ReferenceEquals(managers[i], target))
                {
                    return true;
                }
            }
            return false;
        }

        private class FakeDiagnostics : IViewManagerDiagnostics
        {
            public readonly List<WindowDiagnosticSnapshot> Windows = new();
            public readonly List<QueuedRequestSnapshot> Queue = new();
            public readonly List<bool> CloseAllCalls = new();
            public bool IsDisposed { get; set; }
            public int TotalInstanceCount => Windows.Count;
            public int WaitingQueueCount => Queue.Count;
            public int SnapshotReads;
            public int CloseCalls;
            public int BackCalls;
            public int ClearQueueCalls;

            public void GetLayerSnapshots(UILayer layer, List<WindowDiagnosticSnapshot> results)
            {
                SnapshotReads++;
                results.Clear();
                foreach (var snapshot in Windows)
                {
                    if (snapshot.Layer == layer &&
                        (snapshot.State == WindowState.OpenAndActive || snapshot.State == WindowState.Paused))
                    {
                        results.Add(snapshot);
                    }
                }
            }

            public void GetInactiveSnapshots(List<WindowDiagnosticSnapshot> results)
            {
                SnapshotReads++;
                results.Clear();
                foreach (var snapshot in Windows)
                {
                    if (snapshot.State == WindowState.Cached || snapshot.State == WindowState.PendingDestroy)
                    {
                        results.Add(snapshot);
                    }
                }
            }

            public void GetWaitingQueueSnapshots(List<QueuedRequestSnapshot> results)
            {
                SnapshotReads++;
                results.Clear();
                results.AddRange(Queue);
            }

            public bool CloseView(int instanceId)
            {
                CloseCalls++;
                return true;
            }

            public bool TryNavigateBack()
            {
                BackCalls++;
                return true;
            }

            public void CloseAllViews(bool destroyCached = false)
            {
                CloseAllCalls.Add(destroyCached);
            }

            public void ClearWaitingQueue()
            {
                ClearQueueCalls++;
                Queue.Clear();
            }
        }

        private sealed class EqualDiagnostics : FakeDiagnostics
        {
            public override bool Equals(object obj) => obj is EqualDiagnostics;
            public override int GetHashCode() => 0;
        }
    }
}
