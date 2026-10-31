using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using ComponentAutoBindTool.ViewCore;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
using VContainer;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Tests
{
    public class ViewRouteTests
    {
        [UnityTearDown]
        public IEnumerator LeavePlayMode()
        {
            if (UnityEditor.EditorApplication.isPlaying) yield return new ExitPlayMode();
        }
        private sealed class Environment : IViewPrefabHelper, IViewRootHelper, IDisposable
        {
            private readonly Dictionary<string, GameObject> m_Prefabs = new();
            private readonly GameObject m_Root = new("路由测试");
            private readonly IObjectResolver m_Scope;
            public readonly RouteProbe Probe = new();
            public readonly ViewManager Manager;
            public int Releases, Loads;
            public Environment()
            {
                var builder = new ContainerBuilder();
                builder.RegisterInstance(Probe);
                m_Scope = builder.Build();
                Manager = new ViewManager(m_Scope, this, this);
            }
            public T Add<T>(string key) where T : RouteViewHost
            {
                var prefab = new GameObject(key);
                prefab.transform.SetParent(m_Root.transform);
                var host = prefab.AddComponent<T>();
                m_Prefabs.Add(key, prefab);
                return host;
            }
            public GameObject GetViewPrefab(string name) { Loads++; return m_Prefabs.GetValueOrDefault(name); }
            public Transform GetViewRoot(string name, UILayer layer = UILayer.Normal) => m_Root.transform;
            public void ReleaseViewInstance(string name, GameObject instance) { Releases++; Object.DestroyImmediate(instance); }
            public void Dispose()
            {
                try { Manager.Dispose(); }
                finally { m_Scope.Dispose(); Object.DestroyImmediate(m_Root); }
            }
        }

        private static void Error(string expression) => LogAssert.Expect(UnityEngine.LogType.Error, new Regex(expression));

        [Test]
        public void PresenterMismatchIsRejectedBeforeCreateAndReleasesPreparedInstance()
        {
            using var environment = new ViewCoreEnhancementTests.Environment();
            environment.Add("route");
            var route = new ViewRoute<EnhancementViewHost, EmptyViewArgs>("route");
            LogAssert.Expect(UnityEngine.LogType.Error, new Regex("路由.*route.*Presenter 参数契约不匹配"));
            Assert.That(environment.Manager.ShowView(route), Is.Zero);
            Assert.That(environment.Probe.Events, Is.Empty);
            Assert.That(environment.Manager.TotalInstanceCount, Is.Zero);
            Assert.That(environment.Releases, Is.EqualTo(1));
        }

        [TestCase(RoutePresenterKind.Other)]
        [TestCase(RoutePresenterKind.MissingContract)]
        public void RejectedPresenterReleasesScopeWithoutCallingLifecycle(RoutePresenterKind kind)
        {
            using var environment = new Environment();
            environment.Add<RouteViewHost>("invalid").PresenterKind = kind;
            Error("路由.*invalid.*Presenter 参数契约不匹配");
            int id = environment.Manager.ShowView(new ViewRoute<RouteViewHost, RouteArgs>("invalid"), new RouteArgs());
            Assert.That(id, Is.Zero);
            Assert.That(environment.Probe.Creates, Is.Zero);
            Assert.That(environment.Probe.Destroys, Is.Zero);
            Assert.That(environment.Probe.ScopeDisposals, Is.EqualTo(1));
            Assert.That(environment.Releases, Is.EqualTo(1));
        }

        [Test]
        public void HostMismatchIsRejectedBeforeInstantiation()
        {
            using var environment = new Environment();
            environment.Add<RouteViewHost>("wrong");
            Error("路由.*wrong.*宿主不匹配");
            Assert.That(environment.Manager.ShowView(new ViewRoute<EnhancementViewHost, RouteArgs>("wrong"), new RouteArgs()), Is.Zero);
            Assert.That(environment.Probe.Creates, Is.Zero);
            Assert.That(environment.Releases, Is.Zero);
        }

        [Test]
        public void ExplicitAliasesReuseIndependentlyAndRefreshTheSameSession()
        {
            using var environment = new Environment();
            environment.Add<RouteViewHost>("a");
            environment.Add<RouteViewHost>("b");
            var a = new ViewRoute<RouteViewHost, RouteArgs>("a");
            var b = new ViewRoute<RouteViewHost, RouteArgs>("b");
            int first = environment.Manager.ShowView(a, new RouteArgs { Value = 1 });
            int second = environment.Manager.ShowView(b, new RouteArgs { Value = 2 });
            Assert.That(first, Is.GreaterThan(0));
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(environment.Manager.ShowView(a, new DerivedRouteArgs { Value = 3 }), Is.EqualTo(first));
            Assert.That(environment.Probe.LastValue, Is.EqualTo(3));
            Assert.That(environment.Probe.SessionDisposals, Is.EqualTo(1));
            environment.Manager.CloseView(a);
            Assert.That(environment.Manager.ShowView(a, new RouteArgs()), Is.EqualTo(first));
            Assert.That(environment.Probe.Creates, Is.EqualTo(2));
        }

        [Test]
        public void SameShortHostNamesUseTheirExplicitKeys()
        {
            using var environment = new Environment();
            environment.Add<RouteA.SameName>("left");
            environment.Add<RouteB.SameName>("right");
            Assert.That(environment.Manager.ShowView(new ViewRoute<RouteA.SameName, RouteArgs>("left"), new RouteArgs()), Is.GreaterThan(0));
            Assert.That(environment.Manager.ShowView(new ViewRoute<RouteB.SameName, RouteArgs>("right"), new RouteArgs()), Is.GreaterThan(0));
            Assert.That(environment.Probe.Creates, Is.EqualTo(2));
        }

        [Test]
        public void EmptyRouteAndDerivedHostAreSupported()
        {
            using var environment = new Environment();
            environment.Add<RouteA.SameName>("empty").PresenterKind = RoutePresenterKind.Empty;
            IViewManager manager = environment.Manager;
            Assert.That(manager.ShowView(new ViewRoute<RouteViewHost, EmptyViewArgs>("empty")), Is.GreaterThan(0));
            Assert.That(environment.Probe.LastArgs, Is.SameAs(EmptyViewArgs.Instance));
        }

        [Test]
        public void NullAndErasedWrongArgsDoNotTouchAnExistingSession()
        {
            using var environment = new Environment();
            environment.Add<RouteViewHost>("a");
            var route = new ViewRoute<RouteViewHost, RouteArgs>("a");
            int id = environment.Manager.ShowView(route, new RouteArgs());
            Error("路由.*a.*参数不匹配.*null");
            Assert.That(environment.Manager.ShowView(route, null), Is.Zero);
            Error("路由.*a.*参数不匹配");
            Assert.That(((IViewRouteManager)environment.Manager).ShowView(route, new OtherRouteArgs()), Is.Zero);
            Assert.That(environment.Probe.Opens, Is.EqualTo(1));
            Assert.That(environment.Probe.SessionDisposals, Is.Zero);
            Assert.That(environment.Probe.Contexts[id].State, Is.EqualTo(WindowState.OpenAndActive));
        }

        [Test]
        public void WrongRouteAgainstExistingInstanceKeepsSessionAndSortOrder()
        {
            using var environment = new Environment();
            environment.Add<RouteViewHost>("a");
            int id = environment.Manager.ShowView(new ViewRoute<RouteViewHost, RouteArgs>("a"), new RouteArgs());
            var snapshots = new List<WindowDiagnosticSnapshot>();
            environment.Manager.GetLayerSnapshots(UILayer.Normal, snapshots);
            int order = snapshots[0].SortingOrder;
            Error("路由.*a.*Presenter 参数契约不匹配");
            Assert.That(environment.Manager.ShowView(new ViewRoute<RouteViewHost, OtherRouteArgs>("a"), new OtherRouteArgs()), Is.Zero);
            environment.Manager.GetLayerSnapshots(UILayer.Normal, snapshots);
            Assert.That(snapshots[0].InstanceId, Is.EqualTo(id));
            Assert.That(snapshots[0].SortingOrder, Is.EqualTo(order));
            Assert.That(environment.Probe.SessionDisposals, Is.Zero);
        }

        [UnityTest]
        public IEnumerator RejectedReopenDoesNotCancelPendingDestruction()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            using var environment = new Environment();
            var host = environment.Add<RouteViewHost>("delayed");
            host.CloseStrategy = ViewCloseStrategy.DestroyDelayed;
            host.DestroyDelaySeconds = 0.01f;
            int id = environment.Manager.ShowView(new ViewRoute<RouteViewHost, RouteArgs>("delayed"), new RouteArgs());
            environment.Manager.CloseView(id);
            Error("路由.*delayed.*Presenter 参数契约不匹配");
            Assert.That(environment.Manager.ShowView(new ViewRoute<RouteViewHost, OtherRouteArgs>("delayed"), new OtherRouteArgs()), Is.Zero);
            Assert.That(environment.Probe.Contexts[id].State, Is.EqualTo(WindowState.PendingDestroy));
            float deadline = Time.realtimeSinceStartup + 0.2f;
            while (environment.Manager.TotalInstanceCount != 0 && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                environment.Manager.Tick();
            }
            Assert.That(environment.Manager.TotalInstanceCount, Is.Zero);
            Assert.That(environment.Releases, Is.EqualTo(1));
        }

        [Test]
        public void FailedReplacementKeepsTheOriginalWindowAndSession()
        {
            using var environment = new Environment();
            environment.Add<RouteViewHost>("old");
            environment.Add<RouteViewHost>("bad").PresenterKind = RoutePresenterKind.Other;
            int id = environment.Manager.ShowView(new ViewRoute<RouteViewHost, RouteArgs>("old"), new RouteArgs());
            Error("路由.*bad.*Presenter 参数契约不匹配");
            Error("ReplaceTopView 失败");
            Assert.That(environment.Manager.ReplaceTopView(new ViewRoute<RouteViewHost, RouteArgs>("bad"), new RouteArgs()), Is.Zero);
            Assert.That(environment.Probe.Contexts[id].State, Is.EqualTo(WindowState.OpenAndActive));
            Assert.That(environment.Probe.SessionDisposals, Is.Zero);
            Assert.That(environment.Manager.TotalInstanceCount, Is.EqualTo(1));
        }

        [Test]
        public void DeferredRouteKeepsArgsReferenceAndInvalidRequestDoesNotPoisonPooledLegacyRequest()
        {
            using var environment = new Environment();
            environment.Add<RouteViewHost>("a");
            var args = new RouteArgs { Value = 1 };
            var route = new ViewRoute<RouteViewHost, RouteArgs>("a");
            Assert.That(environment.Manager.ShowView(route, args, false), Is.Zero);
            Assert.That(environment.Loads, Is.Zero);
            args.Value = 7;
            environment.Manager.Tick();
            Assert.That(environment.Probe.LastArgs, Is.SameAs(args));
            Assert.That(environment.Probe.LastValue, Is.EqualTo(7));
            environment.Manager.CloseView(route);
            environment.Manager.EnqueueView(new ViewRoute<EnhancementViewHost, RouteArgs>("a"), args);
            Error("路由.*a.*宿主不匹配");
            environment.Manager.Tick();
            Assert.That(environment.Manager.WaitingQueueCount, Is.Zero);
            environment.Manager.EnqueueView("a", args);
            environment.Manager.Tick();
            Assert.That(environment.Manager.IsViewOpen("a"), Is.True);
        }

        [Test]
        public void TypedAndLegacyQueueShareStablePriorityAndContinueAfterFailure()
        {
            using var environment = new Environment();
            environment.Add<RouteViewHost>("valid");
            var route = new ViewRoute<RouteViewHost, RouteArgs>("valid");
            environment.Manager.EnqueueView(route, new RouteArgs(), 0);
            environment.Manager.EnqueueView(new ViewRoute<RouteViewHost, RouteArgs>("missing"), new RouteArgs(), 10);
            var queue = new List<QueuedRequestSnapshot>();
            environment.Manager.GetWaitingQueueSnapshots(queue);
            Assert.That(queue[0].ViewName, Is.EqualTo("missing"));
            Error("无法打开窗口.*missing");
            environment.Manager.Tick();
            Assert.That(environment.Manager.WaitingQueueCount, Is.EqualTo(1));
            environment.Manager.Tick();
            Assert.That(environment.Manager.IsViewOpen("valid"), Is.True);
        }

        [Test]
        public void TypedCloseValidatesSelectedInstanceWithoutLoadingPrefab()
        {
            using var environment = new Environment();
            environment.Add<RouteViewHost>("multi").InstanceMode = WindowInstanceMode.Multiple;
            var route = new ViewRoute<RouteViewHost, RouteArgs>("multi");
            int first = environment.Manager.ShowView(route, new RouteArgs());
            int last = environment.Manager.ShowView(route, new RouteArgs());
            int loads = environment.Loads;
            Error("路由.*multi.*宿主不匹配");
            Assert.That(environment.Manager.CloseView(new ViewRoute<EnhancementViewHost, RouteArgs>("multi")), Is.False);
            Assert.That(environment.Manager.CloseView(route), Is.True);
            Assert.That(environment.Probe.Contexts[last].State, Is.EqualTo(WindowState.Cached));
            Assert.That(environment.Probe.Contexts[first].State, Is.EqualTo(WindowState.OpenAndActive));
            Assert.That(environment.Loads, Is.EqualTo(loads));
        }

        [Test]
        public void InvalidKeysAndNullRouteAreReportedClearly()
        {
            Assert.Throws<ArgumentException>(() => new ViewRoute<RouteViewHost, RouteArgs>(" "));
            using var environment = new Environment();
            Error("窗口路由不能为空");
            Assert.That(environment.Manager.ShowView((ViewRoute<RouteViewHost, RouteArgs>)null, new RouteArgs()), Is.Zero);
            Error("无法打开窗口.*missing");
            Assert.That(environment.Manager.ShowView(new ViewRoute<RouteViewHost, RouteArgs>("missing"), new RouteArgs()), Is.Zero);
        }

        [Test]
        public void CloseDoesNotSkipAnIncompatibleTopInstance()
        {
            using var environment = new Environment();
            var prefab = environment.Add<RouteViewHost>("multi");
            prefab.InstanceMode = WindowInstanceMode.Multiple;
            var route = new ViewRoute<RouteViewHost, RouteArgs>("multi");
            int first = environment.Manager.ShowView(route, new RouteArgs());
            prefab.PresenterKind = RoutePresenterKind.Other;
            environment.Manager.ShowView("multi", new OtherRouteArgs());
            Error("路由.*multi.*Presenter 参数契约不匹配");
            Assert.That(environment.Manager.CloseView(route), Is.False);
            Assert.That(environment.Probe.Contexts[first].State, Is.EqualTo(WindowState.Paused));
            var snapshots = new List<WindowDiagnosticSnapshot>();
            environment.Manager.GetLayerSnapshots(UILayer.Normal, snapshots);
            Assert.That(snapshots.Count, Is.EqualTo(2));
        }

        [Test]
        public void ClearingTypedRequestsDoesNotLeakRouteIntoLegacyRequests()
        {
            using var environment = new Environment();
            environment.Add<RouteViewHost>("a");
            environment.Manager.EnqueueView(new ViewRoute<EnhancementViewHost, RouteArgs>("a"), new RouteArgs());
            environment.Manager.ClearWaitingQueue();
            environment.Manager.EnqueueView("a", new RouteArgs());
            environment.Manager.Tick();
            Assert.That(environment.Manager.IsViewOpen("a"), Is.True);
        }

        [Test]
        public void LegacyManagerCanStillImplementOnlyTheOriginalInterface()
        {
            IViewManager manager = new LegacyManager();
            var route = new ViewRoute<RouteViewHost, RouteArgs>("route");
            Assert.Throws<NotSupportedException>(() => manager.ShowView(route, new RouteArgs()));
            Assert.Throws<NotSupportedException>(() => manager.EnqueueView(route, new RouteArgs()));
            Assert.Throws<NotSupportedException>(() => manager.ReplaceTopView(route, new RouteArgs()));
            Assert.Throws<NotSupportedException>(() => manager.CloseView(route));
            Assert.That(manager.ShowView("legacy"), Is.EqualTo(99));
        }

        private sealed class LegacyManager : IViewManager
        {
            public event Action<IWindowContext> OnViewOpened { add { } remove { } }
            public event Action<IWindowContext> OnViewClosed { add { } remove { } }
            public int ShowView(string key, IViewArgs args = null, bool immediately = true) => 99;
            public int ShowView(string key, bool immediately, params object[] args) => 99;
            public void EnqueueView(string key, IViewArgs args = null, int priority = 0) { }
            public void ClearWaitingQueue() { }
            public int ReplaceTopView(string key, IViewArgs args = null, UILayer layer = UILayer.Normal) => 99;
            public bool CloseView(int id) => false;
            public bool CloseView(string key) => false;
            public bool TryNavigateBack() => false;
            public void CloseAllViews(bool destroyCached = false) { }
            public bool IsViewOpen(string key) => false;
            public async Awaitable WaitViewClosedAsync(int id, CancellationToken token = default) { await Awaitable.MainThreadAsync(); }
        }
    }
}
