using System;
using System.Collections.Generic;
using System.Collections;
using ComponentAutoBindTool.ViewCore;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.TestTools;
using VContainer;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Tests
{
    public class ViewCoreEnhancementTests
    {
        internal sealed class Environment : IViewPrefabHelper, IViewRootHelper, IDisposable
        {
            private readonly Dictionary<string, GameObject> m_Prefabs = new();
            private readonly GameObject m_Root = new("窗口调度测试");
            private readonly IObjectResolver m_Scope;
            public readonly EnhancementProbe Probe = new();
            public readonly ViewManager Manager;
            public int Releases { get; private set; }

            public Environment()
            {
                var builder = new ContainerBuilder();
                builder.RegisterInstance(Probe);
                m_Scope = builder.Build();
                Manager = new ViewManager(m_Scope, this, this);
            }

            public GameObject Add(string name, UILayer layer = UILayer.Normal,
                ViewCloseStrategy strategy = ViewCloseStrategy.NonDestroy)
            {
                var prefab = new GameObject(name);
                prefab.transform.SetParent(m_Root.transform);
                var host = prefab.AddComponent<EnhancementViewHost>();
                host.Layer = layer;
                host.CloseStrategy = strategy;
                m_Prefabs.Add(name, prefab);
                return prefab;
            }

            public GameObject GetViewPrefab(string viewName) => m_Prefabs.GetValueOrDefault(viewName);
            public Transform GetViewRoot(string viewName, UILayer layer = UILayer.Normal) => m_Root.transform;
            public void ReleaseViewInstance(string viewName, GameObject instance)
            {
                Releases++;
                Object.DestroyImmediate(instance);
            }

            public void Dispose()
            {
                try { Manager.Dispose(); }
                finally
                {
                    m_Scope.Dispose();
                    Object.DestroyImmediate(m_Root);
                }
            }
        }

        [Test]
        public void HigherLayerBlocksTheExistingNormalQueueUntilItCloses()
        {
            using var environment = new Environment();
            environment.Add("等待窗口");
            environment.Add("高层窗口", UILayer.Top);
            int top = environment.Manager.ShowView("高层窗口");
            environment.Manager.ShowView("等待窗口", null, false);

            environment.Manager.Tick();
            Assert.That(environment.Manager.IsViewOpen("等待窗口"), Is.False);

            environment.Manager.CloseView(top);
            environment.Manager.Tick();
            Assert.That(environment.Manager.IsViewOpen("等待窗口"), Is.True);
        }

        [Test]
        public void PriorityIsStableAndClearingQueuePreventsPendingRequestsFromOpening()
        {
            using var environment = new Environment();
            foreach (string name in new[] { "低", "先高", "后高" }) environment.Add(name);
            environment.Manager.EnqueueView("低");
            environment.Manager.EnqueueView("先高", priority: 10);
            environment.Manager.EnqueueView("后高", priority: 10);
            var queue = new List<QueuedRequestSnapshot>();
            environment.Manager.GetWaitingQueueSnapshots(queue);
            Assert.That(queue.ConvertAll(request => request.ViewName), Is.EqualTo(new[] { "先高", "后高", "低" }));
            environment.Manager.Tick();
            Assert.That(environment.Manager.IsViewOpen("先高"), Is.True);
            environment.Manager.CloseView("先高");
            environment.Manager.Tick();
            Assert.That(environment.Manager.IsViewOpen("后高"), Is.True);
            environment.Manager.ClearWaitingQueue();
            environment.Manager.CloseView("后高");
            environment.Manager.Tick();
            Assert.That(environment.Manager.IsViewOpen("低"), Is.False);
        }

        private static WindowDiagnosticSnapshot Find(Environment environment, int id)
        {
            var snapshots = new List<WindowDiagnosticSnapshot>();
            environment.Manager.GetLayerSnapshots(UILayer.Normal, snapshots);
            foreach (var snapshot in snapshots) if (snapshot.InstanceId == id) return snapshot;
            environment.Manager.GetInactiveSnapshots(snapshots);
            foreach (var snapshot in snapshots) if (snapshot.InstanceId == id) return snapshot;
            Assert.Fail("缺少预期实例 " + id);
            return default;
        }

        [Test]
        public void ReopeningDuringExitIgnoresStaleCompletionAndLocksInputOnlyDuringTransition()
        {
            using var environment = new Environment();
            environment.Add("动画").AddComponent<ManualViewTransition>();
            int id = environment.Manager.ShowView("动画");
            var snapshot = Find(environment, id);
            var transition = snapshot.HostObject.GetComponent<ManualViewTransition>();
            var raycaster = snapshot.HostObject.GetComponent<GraphicRaycaster>();
            Assert.That(snapshot.IsTransitioning, Is.True);
            Assert.That(raycaster.enabled, Is.False);
            transition.PendingEnter();
            Assert.That(raycaster.enabled, Is.True);
            environment.Manager.CloseView(id);
            Assert.That(environment.Manager.IsViewOpen("动画"), Is.False);
            Assert.That(snapshot.HostObject.activeSelf, Is.True);
            Action staleExit = transition.PendingExit;
            Assert.That(environment.Manager.ShowView("动画"), Is.EqualTo(id));
            staleExit();
            Assert.That(snapshot.HostObject.activeSelf, Is.True);
            Assert.That(Find(environment, id).IsTransitioning, Is.True);
            transition.PendingEnter();
            Assert.That(raycaster.enabled, Is.True);
        }

        [Test]
        public void CloseAwaitableCompletesAtLogicalClose()
        {
            using var environment = new Environment();
            environment.Add("等待");
            int id = environment.Manager.ShowView("等待");
            bool completed = false;
            async Awaitable Wait()
            {
                await environment.Manager.WaitViewClosedAsync(id);
                completed = true;
            }
            _ = Wait();
            Assert.That(completed, Is.False);
            environment.Manager.CloseView(id);
            Assert.That(completed, Is.True);
        }

        [Test]
        public void ImmediateCloseEndsLogicNowAndReleasesExactlyOnceAfterExit()
        {
            using var environment = new Environment();
            environment.Add("底层");
            environment.Add("销毁动画", strategy: ViewCloseStrategy.DestroyImmediate).AddComponent<ManualViewTransition>();
            int bottom = environment.Manager.ShowView("底层");
            int id = environment.Manager.ShowView("销毁动画");
            var snapshot = Find(environment, id);
            var transition = snapshot.HostObject.GetComponent<ManualViewTransition>();
            Action staleEnter = transition.PendingEnter;
            bool closed = false;
            async Awaitable Wait() { await environment.Probe.Contexts[id].WaitClosedAsync(); closed = true; }
            _ = Wait();

            environment.Manager.CloseView(id);

            Assert.That(closed, Is.True);
            Assert.That(Find(environment, bottom).State, Is.EqualTo(WindowState.OpenAndActive));
            Assert.That(environment.Probe.Events, Does.Contain("销毁动画.Pause"));
            Assert.That(environment.Probe.Events, Does.Contain("销毁动画.Close"));
            Assert.That(environment.Releases, Is.Zero);
            Assert.That(Find(environment, id).State, Is.EqualTo(WindowState.Destroyed));
            staleEnter();
            Assert.That(snapshot.HostObject.GetComponent<GraphicRaycaster>().enabled, Is.False);
            Action finish = transition.PendingExit;
            finish();
            finish();
            Assert.That(environment.Releases, Is.EqualTo(1));
            Assert.That(environment.Probe.Events, Does.Contain("销毁动画.Destroy"));
        }

        [Test]
        public void UnanimatedAndSynchronouslyAnimatedWindowsCloseWithoutAnExtraFrame()
        {
            using var environment = new Environment();
            environment.Add("无动画", strategy: ViewCloseStrategy.DestroyImmediate);
            var prefab = environment.Add("同步动画", strategy: ViewCloseStrategy.DestroyImmediate);
            prefab.AddComponent<ManualViewTransition>().CompleteSynchronously = true;
            foreach (string name in new[] { "无动画", "同步动画" })
            {
                int id = environment.Manager.ShowView(name);
                Assert.That(Find(environment, id).IsTransitioning, Is.False);
                environment.Manager.CloseView(id);
                Assert.That(environment.Manager.TotalInstanceCount, Is.Zero);
            }
            Assert.That(environment.Releases, Is.EqualTo(2));
        }

        [Test]
        public void SortingRefreshDoesNotRestartAnExistingEnterTransition()
        {
            using var environment = new Environment();
            environment.Add("入场").AddComponent<ManualViewTransition>();
            environment.Add("覆盖").GetComponent<ViewBase>().CoverMode = ViewCoverMode.Overlay;
            int id = environment.Manager.ShowView("入场");
            var transition = Find(environment, id).HostObject.GetComponent<ManualViewTransition>();
            environment.Manager.ShowView("覆盖");
            Assert.That(transition.EnterCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DelayedDestructionWaitsForBothTimerAndExitCompletion()
        {
            yield return new EnterPlayMode();
            using (var environment = new Environment())
            {
                var prefab = environment.Add("慢退场", strategy: ViewCloseStrategy.DestroyDelayed);
                prefab.GetComponent<ViewBase>().DestroyDelaySeconds = 0.01f;
                prefab.AddComponent<ManualViewTransition>();
                int id = environment.Manager.ShowView("慢退场");
                var transition = Find(environment, id).HostObject.GetComponent<ManualViewTransition>();
                transition.PendingEnter();
                environment.Manager.CloseView(id);
                float deadline = Time.realtimeSinceStartup + 0.2f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    environment.Manager.Tick();
                }
                Assert.That(environment.Releases, Is.Zero);
                Assert.That(Find(environment, id).RemainingDestroyTime, Is.Zero);
                transition.PendingExit();
                Assert.That(environment.Releases, Is.EqualTo(1));
            }
            yield return new ExitPlayMode();
        }

        [Test]
        public void ThrowingExitStillReleasesInstanceAndCompletesCloseWaiters()
        {
            using var environment = new Environment();
            environment.Add("退场异常", strategy: ViewCloseStrategy.DestroyImmediate).AddComponent<ManualViewTransition>().ThrowOnExit = true;
            int id = environment.Manager.ShowView("退场异常");
            bool closed = false;
            async Awaitable Wait() { await environment.Manager.WaitViewClosedAsync(id); closed = true; }
            _ = Wait();
            Assert.Throws<InvalidOperationException>(() => environment.Manager.CloseView(id));
            Assert.That(closed, Is.True);
            Assert.That(environment.Manager.TotalInstanceCount, Is.Zero);
            Assert.That(environment.Releases, Is.EqualTo(1));
        }

        [Test]
        public void DisposeContinuesAfterPresenterFailureAndWakesAllWaiters()
        {
            using var environment = new Environment();
            environment.Add("异常");
            environment.Add("正常");
            int first = environment.Manager.ShowView("异常");
            int second = environment.Manager.ShowView("正常");
            int completed = 0;
            async Awaitable Wait(int id) { await environment.Manager.WaitViewClosedAsync(id); completed++; }
            _ = Wait(first);
            _ = Wait(second);
            environment.Probe.Callback = (phase, context) =>
            {
                if (phase == "Close" && context.InstanceId == first) throw new InvalidOperationException("测试Close异常");
            };
            Assert.Throws<InvalidOperationException>(() => environment.Manager.Dispose());
            Assert.That(environment.Releases, Is.EqualTo(2));
            Assert.That(completed, Is.EqualTo(2));
            Assert.That(environment.Manager.TotalInstanceCount, Is.Zero);
            Assert.That(environment.Probe.Events, Does.Contain("异常.Destroy"));
            Assert.That(environment.Probe.Events, Does.Contain("正常.Destroy"));
        }

        [Test]
        public void CloseAllToleratesCloseCallbackRemovingAnotherWindow()
        {
            using var environment = new Environment();
            environment.Add("甲");
            environment.Add("乙");
            int first = environment.Manager.ShowView("甲");
            environment.Manager.ShowView("乙");
            environment.Manager.OnViewClosed += context =>
            {
                if (context.ViewName == "乙") environment.Manager.CloseView(first);
            };
            Assert.DoesNotThrow(() => environment.Manager.CloseAllViews(true));
            Assert.That(environment.Manager.TotalInstanceCount, Is.Zero);
            Assert.That(environment.Releases, Is.EqualTo(2));
        }

        [Test]
        public void WarmSnapshotFillingDoesNotAllocateOrMutateWindowState()
        {
            using var environment = new Environment();
            environment.Add("活动");
            environment.Add("缓存");
            environment.Manager.ShowView("缓存");
            environment.Manager.CloseView("缓存");
            environment.Manager.ShowView("活动");
            environment.Manager.EnqueueView("排队", priority: 7);
            var windows = new List<WindowDiagnosticSnapshot>(16);
            var queue = new List<QueuedRequestSnapshot>(16);
            for (int i = 0; i < 3; i++)
            {
                environment.Manager.GetLayerSnapshots(UILayer.Normal, windows);
                environment.Manager.GetInactiveSnapshots(windows);
                environment.Manager.GetWaitingQueueSnapshots(queue);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                environment.Manager.GetLayerSnapshots(UILayer.Normal, windows);
                environment.Manager.GetInactiveSnapshots(windows);
                environment.Manager.GetWaitingQueueSnapshots(queue);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
            Assert.That(environment.Manager.IsViewOpen("活动"), Is.True);
            Assert.That(environment.Manager.IsViewOpen("缓存"), Is.False);
            Assert.That(queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void DisposingManagerDuringCreateReleasesThePreparedInstance()
        {
            using var environment = new Environment();
            environment.Add("创建中释放");
            environment.Probe.Callback = (phase, context) =>
            {
                if (phase == "Create") environment.Manager.Dispose();
            };
            Assert.That(environment.Manager.ShowView("创建中释放"), Is.Zero);
            Assert.That(environment.Releases, Is.EqualTo(1));
            Assert.That(environment.Manager.TotalInstanceCount, Is.Zero);
        }

        [Test]
        public void WaitingFromOnOpenAttachesToTheNewSession()
        {
            using var environment = new Environment();
            environment.Add("打开会话");
            int completed = 0;
            async Awaitable Wait(IWindowContext context) { await context.WaitClosedAsync(); completed++; }
            environment.Probe.Callback = (phase, context) =>
            {
                if (phase == "Open") _ = Wait(context);
            };
            int id = environment.Manager.ShowView("打开会话");
            Assert.That(completed, Is.Zero);
            environment.Manager.CloseView(id);
            Assert.That(completed, Is.EqualTo(1));
            Assert.That(environment.Manager.ShowView("打开会话"), Is.EqualTo(id));
            Assert.That(completed, Is.EqualTo(1));
            environment.Manager.CloseView(id);
            Assert.That(completed, Is.EqualTo(2));
        }

        [Test]
        public void ClosingFromResumeDoesNotPublishAStaleOpenedEvent()
        {
            using var environment = new Environment();
            environment.Add("恢复中关闭");
            var events = new List<string>();
            environment.Manager.OnViewOpened += _ => events.Add("Opened");
            environment.Manager.OnViewClosed += _ => events.Add("Closed");
            environment.Probe.Callback = (phase, context) =>
            {
                if (phase == "Resume") context.CloseSelf();
            };
            environment.Manager.ShowView("恢复中关闭");
            Assert.That(events, Is.EqualTo(new[] { "Closed" }));
            Assert.That(environment.Manager.IsViewOpen("恢复中关闭"), Is.False);
        }

        [UnityTest]
        public IEnumerator ReopeningAnotherExpiredWindowCancelsItsPendingDestruction()
        {
            yield return new EnterPlayMode();
            using (var environment = new Environment())
            {
                foreach (string name in new[] { "先到期", "重开" })
                    environment.Add(name, strategy: ViewCloseStrategy.DestroyDelayed).GetComponent<ViewBase>().DestroyDelaySeconds = 0.001f;
                int first = environment.Manager.ShowView("先到期");
                int second = environment.Manager.ShowView("重开");
                environment.Manager.CloseView(first);
                environment.Manager.CloseView(second);
                environment.Probe.Callback = (phase, context) =>
                {
                    if (phase == "Destroy" && context.InstanceId == first)
                        Assert.That(environment.Manager.ShowView("重开"), Is.EqualTo(second));
                };
                yield return null;
                environment.Manager.Tick();
                Assert.That(environment.Manager.IsViewOpen("重开"), Is.True);
                Assert.That(environment.Releases, Is.EqualTo(1));
            }
            yield return new ExitPlayMode();
        }
    }
}
