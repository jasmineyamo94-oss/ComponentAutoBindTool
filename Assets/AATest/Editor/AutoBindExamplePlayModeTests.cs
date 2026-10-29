using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using ComponentAutoBindTool.Example;
using ComponentAutoBindTool.Example.Editor;
using ComponentAutoBindTool.ViewCore;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace ComponentAutoBindTool.Tests
{
    public class AutoBindExamplePlayModeTests
    {
        private static AutoBindExampleScope Scope => Object.FindFirstObjectByType<AutoBindExampleScope>();
        private static LabWindowRecord Record(int id) => Scope.Telemetry.Windows[id];
        private static IEnumerator StartLab()
        {
            EditorSceneManager.OpenScene(AutoBindExampleSetup.ScenePath);
            yield return new EnterPlayMode();
            yield return null;
        }
        [UnityTest]
        public IEnumerator WindowAndNestedWidgetBindOpenCloseAndReopen()
        {
            EditorSceneManager.OpenScene(AutoBindExampleSetup.ScenePath);
            yield return new EnterPlayMode();
            yield return null;
            var scope = Scope;
            scope.Shell.SelectExperiment(4);
            int id = scope.Session.Open("Widgets");
            var record = Record(id);
            var presenter = (AutoBindTestPresenter)record.Presenter;
            var leaf = presenter.Group.Left;
            var widget = Object.FindFirstObjectByType<LabWidgetGroup>().GetComponentInChildren<AutoBindExampleWidget>();
            var ui = (AutoBindExampleWidget.UIView)widget.GetGeneratedUIView();
            Assert.That(ui, Is.SameAs(widget.GetGeneratedUIView()));
            ui.btnIncrement.onClick.Invoke();
            Assert.That(leaf.Count, Is.EqualTo(1));
            scope.Session.Manager.CloseView(id);
            Assert.That(record.Context.State, Is.EqualTo(WindowState.Cached));
            Assert.That(scope.Session.Open("Widgets"), Is.EqualTo(id));
            Assert.That(ui, Is.SameAs(widget.GetGeneratedUIView()));
            Assert.That(leaf.Count, Is.Zero);
            presenter.DetachGroup();
            ui.btnIncrement.onClick.Invoke();
            Assert.That(leaf.Destroyed, Is.True);
            Assert.That(leaf.Count, Is.Zero, "解绑移除按钮订阅");
            scope.Session.Manager.CloseAllViews(true);
            Assert.That(scope.Telemetry.ScopesDisposed, Is.EqualTo(scope.Telemetry.ScopesCreated));
            yield return null;
            Assert.That(Object.FindObjectsByType<AutoBindTest>(FindObjectsInactive.Include, FindObjectsSortMode.None), Is.Empty);
            LogAssert.NoUnexpectedReceived();
            yield return new ExitPlayMode();
        }
        [UnityTest]
        public IEnumerator LayersAndResetThreeRounds()
        {
            EditorSceneManager.OpenScene(AutoBindExampleSetup.ScenePath);
            yield return new EnterPlayMode();
            yield return null;
            for (int round = 0; round < 3; round++)
            {
                Scope.Shell.SelectExperiment(0);
                var session = Scope.Session;
                int hud = session.Open("HUD"), a = session.Open("AutoBindTest");
                int top = session.Open("Top"), system = session.Open("System");
                Assert.That(Record(a).Context.State, Is.EqualTo(WindowState.OpenAndActive));
                Assert.That(Record(hud).Context.Layer, Is.EqualTo(UILayer.HUD));
                var contexts = new[] { hud, a, top, system }.Select(id => (WindowEntry)Record(id).Context).ToArray();
                var orders = contexts.Select(c => c.ViewHost.GetComponent<Canvas>().sortingOrder).ToArray();
                Assert.That(orders, Is.Ordered);
                foreach (var c in contexts)
                    Assert.That(c.ViewHost.transform.Find("SubCanvas").GetComponent<Canvas>().sortingOrder,
                        Is.EqualTo(c.ViewHost.GetComponent<Canvas>().sortingOrder + 2));
                int cover = session.Open("Cover");
                Assert.That(Record(a).Context.State, Is.EqualTo(WindowState.Paused));
                session.Manager.CloseView(cover);
                Assert.That(Record(a).Context.State, Is.EqualTo(WindowState.OpenAndActive));
                session.Reset(0);
                Assert.That(Scope.Telemetry.ScopesCreated, Is.EqualTo(Scope.Telemetry.ScopesDisposed));
                yield return null;
                Assert.That(Object.FindObjectsByType<AutoBindTest>(FindObjectsInactive.Include, FindObjectsSortMode.None), Is.Empty);
            }
            yield return new ExitPlayMode();
        }
        [UnityTest]
        public IEnumerator SingletonParametersAndMultipleIndependenceThreeRounds()
        {
            EditorSceneManager.OpenScene(AutoBindExampleSetup.ScenePath);
            yield return new EnterPlayMode();
            yield return null;
            for (int round = 0; round < 3; round++)
            {
                Scope.Shell.SelectExperiment(1);
                var session = Scope.Session;
                int a = session.Open("AutoBindTest");
                int viewId = Record(a).UIViewId;
                session.Open("Cover");
                Assert.That(session.Open("AutoBindTest"), Is.EqualTo(a));
                Assert.That(Record(a).UIViewId, Is.EqualTo(viewId));
                Assert.That(Record(a).Opens, Is.EqualTo(2));
                Assert.That(Record(a).Parameter, Does.EndWith("3"));
                int m1 = session.Open("Multi"), m2 = session.Open("Multi");
                Assert.That(m1, Is.Not.EqualTo(m2));
                Assert.That(Record(m1).ScopeId, Is.Not.EqualTo(Record(m2).ScopeId));
                session.Manager.CloseView(m1);
                Assert.That(Record(m1).Context.State, Is.EqualTo(WindowState.Destroyed));
                Assert.That(Record(m2).Context.State, Is.EqualTo(WindowState.OpenAndActive));
                session.Reset(1);
                Assert.That(Scope.Telemetry.ScopesCreated, Is.EqualTo(Scope.Telemetry.ScopesDisposed));
                yield return null;
            }
            yield return new ExitPlayMode();
        }
        [UnityTest]
        public IEnumerator DelayedDestroyCancellationAndExpiryThreeRounds()
        {
            EditorSceneManager.OpenScene(AutoBindExampleSetup.ScenePath);
            yield return new EnterPlayMode();
            yield return null;
            for (int round = 0; round < 3; round++)
            {
                Scope.Shell.SelectExperiment(2);
                var session = Scope.Session;
                int id = session.Open("Delayed");
                var original = Record(id);
                session.Manager.CloseView(id);
                yield return new WaitForSecondsRealtime(0.15f);
                Assert.That(session.Open("Delayed"), Is.EqualTo(id));
                Assert.That(Record(id).UIViewId, Is.EqualTo(original.UIViewId));
                yield return new WaitForSecondsRealtime(3.1f);
                Assert.That(original.Context.State, Is.EqualTo(WindowState.OpenAndActive), "重开应取消旧销毁计时器");
                session.Manager.CloseView(id);
                yield return new WaitForSecondsRealtime(3.15f);
                Assert.That(original.Context.State, Is.EqualTo(WindowState.Destroyed));
                Assert.That(session.Open("Delayed"), Is.Not.EqualTo(id));
                Assert.That(Scope.Telemetry.SessionsReleased, Is.GreaterThan(0));
                session.Reset(2);
                Assert.That(Scope.Telemetry.ScopesCreated, Is.EqualTo(Scope.Telemetry.ScopesDisposed));
                yield return null;
            }
            yield return new ExitPlayMode();
        }
        [UnityTest]
        public IEnumerator NavigationQueueAndFailedReplacementThreeRounds()
        {
            EditorSceneManager.OpenScene(AutoBindExampleSetup.ScenePath);
            yield return new EnterPlayMode();
            yield return null;
            for (int round = 0; round < 3; round++)
            {
                Scope.Shell.SelectExperiment(3);
                var session = Scope.Session;
                int a = session.Open("AutoBindTest");
                session.Open("Cover", false);
                session.Open("Multi", false);
                Assert.That(session.Manager.IsViewOpen("Cover"), Is.False);
                LogAssert.Expect(UnityEngine.LogType.Error, new Regex(".*MissingPrefab.*"));
                LogAssert.Expect(UnityEngine.LogType.Error, new Regex(".*ReplaceTopView.*MissingPrefab.*"));
                Assert.That(session.Manager.ReplaceTopView("MissingPrefab"), Is.LessThan(1));
                Assert.That(Record(a).Context.State, Is.EqualTo(WindowState.OpenAndActive));
                session.Manager.TryNavigateBack();
                yield return null;
                Assert.That(session.Manager.IsViewOpen("Cover"), Is.True);
                session.Manager.TryNavigateBack();
                yield return null;
                Assert.That(session.Manager.IsViewOpen("Multi"), Is.True);
                session.Manager.ReplaceTopView("AutoBindTest");
                Assert.That(session.Manager.IsViewOpen("Multi"), Is.False);
                Assert.That(session.Manager.IsViewOpen("AutoBindTest"), Is.True);
                session.Reset(3);
                yield return null;
                Assert.That(session.Manager.IsViewOpen("Multi"), Is.False);
                Assert.That(Scope.Telemetry.ScopesCreated, Is.EqualTo(Scope.Telemetry.ScopesDisposed));
            }
            yield return new ExitPlayMode();
        }
        [UnityTest]
        public IEnumerator NestedWidgetVisibilityAndSharedScopeThreeRounds()
        {
            EditorSceneManager.OpenScene(AutoBindExampleSetup.ScenePath);
            yield return new EnterPlayMode();
            yield return null;
            for (int round = 0; round < 3; round++)
            {
                Scope.Shell.SelectExperiment(4);
                int id = Scope.Session.Open("Widgets");
                var group = ((AutoBindTestPresenter)Record(id).Presenter).Group;
                Assert.That(group.Left, Is.Not.SameAs(group.Right));
                Assert.That(group.Left.ScopeId, Is.EqualTo(group.Right.ScopeId));
                Assert.That(group.Left.ScopeId, Is.EqualTo(Record(id).ScopeId));
                group.Left.Increment();
                Assert.That(group.Right.Count, Is.Zero);
                group.ToggleLeft();
                int stopped = group.Left.Ticks, running = group.Right.Ticks;
                yield return null;
                Assert.That(group.Left.Ticks, Is.EqualTo(stopped));
                Assert.That(group.Right.Ticks, Is.GreaterThan(running));
                group.ToggleLeft();
                yield return null;
                Assert.That(group.Left.Ticks, Is.GreaterThan(stopped));
                int cover = Scope.Session.Open("Cover");
                group.ToggleLeft(); group.ToggleLeft();
                stopped = group.Left.Ticks;
                yield return null;
                Assert.That(group.Left.Ticks, Is.EqualTo(stopped));
                Scope.Session.Manager.CloseView(cover);
                yield return null;
                Assert.That(group.Left.Ticks, Is.GreaterThan(stopped));
                var presenter = (AutoBindTestPresenter)Record(id).Presenter;
                presenter.DetachGroup();
                Assert.That(group.Left.Destroyed, Is.True);
                Assert.That(group.Right.Destroyed, Is.True);
                Assert.That(Scope.Telemetry.Describe(), Does.Contain($"叶 Widget / Scope {group.Left.ScopeId} OnClose"));
                int detachedTicks = group.Left.Ticks;
                Scope.Session.Manager.CloseView(id);
                Assert.That(Scope.Session.Open("Widgets"), Is.EqualTo(id));
                Assert.That(presenter.Group, Is.Null, "缓存重开不重新创建已解绑的 Widget 实例。");
                yield return null;
                Assert.That(group.Left.Ticks, Is.EqualTo(detachedTicks));
                Scope.Session.Reset(4);
                Assert.That(group.Left.Destroyed, Is.True);
                Assert.That(Scope.Telemetry.ScopesCreated, Is.EqualTo(Scope.Telemetry.ScopesDisposed));
                yield return null;
            }
            yield return new ExitPlayMode();
        }
    }
}
