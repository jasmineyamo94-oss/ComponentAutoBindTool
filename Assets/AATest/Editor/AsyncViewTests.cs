using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComponentAutoBindTool.AssetCore;
using ComponentAutoBindTool.ViewCore;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Tests
{
    public class AsyncViewTests
    {
        private sealed class Environment : IAsyncViewPrefabHelper, IViewRootHelper, IDisposable
        {
            public readonly GameObject Root = new("异步开窗夹具");
            public readonly RouteViewHost Host;
            public readonly RouteProbe Probe = new();
            public readonly ViewManager Manager;
            public readonly AwaitableCompletionSource<bool> Completion = new();
            public readonly IObjectResolver Scope;
            public bool Delay = true, ThrowOnCancel;
            public int AsyncLoads, SyncLoads, Pending, Releases;
            public Environment()
            {
                var prefab = new GameObject("prefab");
                prefab.transform.SetParent(Root.transform);
                Host = prefab.AddComponent<RouteViewHost>();
                var builder = new ContainerBuilder();
                builder.RegisterInstance(Probe);
                Scope = builder.Build();
                Manager = new ViewManager(Scope, this, this);
            }
            public GameObject GetViewPrefab(string name) { SyncLoads++; Pending++; return Host.gameObject; }
            public async Awaitable<GameObject> GetViewPrefabAsync(string name, CancellationToken token = default)
            {
                AsyncLoads++;
                using var registration = token.Register(() => { if (ThrowOnCancel) throw new InvalidOperationException("取消回调故障"); });
                if (Delay) await Completion.Awaitable;
                // 故意忽略取消，以覆盖第三方 helper 迟到成功的归还路径。
                Pending++;
                return Host.gameObject;
            }
            public void ReleaseUninstantiatedPrefab(string name) { Pending--; Releases++; }
            public void ReleaseViewInstance(string name, GameObject instance)
            {
                Pending--; Releases++;
                Object.DestroyImmediate(instance);
            }
            public Transform GetViewRoot(string name, UILayer layer = UILayer.Normal) => Root.transform;
            public void Dispose() { try { Manager.Dispose(); } finally { Scope.Dispose(); Object.DestroyImmediate(Root); } }
        }
        private static readonly ViewRoute<RouteViewHost, RouteArgs> Route = new("window");
        private static async Task<int> Consume(Awaitable<int> operation) => await operation;

        [UnityTest]
        public IEnumerator ConcurrentSingletonOpensShareAnInstanceAndRefreshArgs()
        {
            using var env = new Environment();
            var first = Consume(env.Manager.ShowViewAsync(Route, new RouteArgs { Value = 1 }));
            var second = Consume(env.Manager.ShowViewAsync(Route, new RouteArgs { Value = 2 }));
            env.Completion.SetResult(true);
            while (!first.IsCompleted || !second.IsCompleted) yield return null;
            Assert.That(first.Result, Is.GreaterThan(0));
            Assert.That(second.Result, Is.EqualTo(first.Result));
            Assert.That(env.AsyncLoads, Is.EqualTo(1));
            Assert.That(env.Probe.LastValue, Is.EqualTo(2));
            Assert.That(env.Manager.TotalInstanceCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CloseDuringLoadReturnsLateLeaseWithoutCreatingWindow()
        {
            using var env = new Environment();
            var pending = Consume(env.Manager.ShowViewAsync(Route, new RouteArgs()));
            Assert.That(env.Manager.CloseView(Route), Is.True);
            env.Completion.SetResult(true);
            while (!pending.IsCompleted) yield return null;
            Assert.That(pending.Result, Is.Zero);
            Assert.That(env.Manager.TotalInstanceCount, Is.Zero);
            Assert.That(env.Pending, Is.Zero);
            Assert.That(env.Releases, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CallerCancelReturnsLateLeaseAndThrowsCancellation()
        {
            using var env = new Environment();
            using var cancel = new CancellationTokenSource();
            var pending = Consume(env.Manager.ShowViewAsync(Route, new RouteArgs(), cancel.Token));
            cancel.Cancel();
            env.Completion.SetResult(true);
            while (!pending.IsCompleted) yield return null;
            Assert.That(pending.IsCanceled, Is.True);
            Assert.That(env.Pending, Is.Zero);
        }

        [UnityTest]
        public IEnumerator SyncOpenDuringAsyncLoadUsesExistingSingletonAndCachedFastPath()
        {
            using var env = new Environment();
            var pending = Consume(env.Manager.ShowViewAsync(Route, new RouteArgs()));
            int syncId = env.Manager.ShowView(Route, new RouteArgs());
            env.Completion.SetResult(true);
            while (!pending.IsCompleted) yield return null;
            Assert.That(pending.Result, Is.EqualTo(syncId));
            env.Manager.CloseView(syncId);
            var cached = env.Manager.ShowViewAsync(Route, new RouteArgs()).GetAwaiter();
            Assert.That(cached.IsCompleted, Is.True);
            Assert.That(cached.GetResult(), Is.EqualTo(syncId));
            Assert.That(env.AsyncLoads, Is.EqualTo(1));
            Assert.That(env.Pending, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator MultipleWindowsKeepIndependentInstances()
        {
            using var env = new Environment { Delay = false };
            env.Host.InstanceMode = WindowInstanceMode.Multiple;
            var first = Consume(env.Manager.ShowViewAsync(Route, new RouteArgs()));
            var second = Consume(env.Manager.ShowViewAsync(Route, new RouteArgs()));
            while (!first.IsCompleted || !second.IsCompleted) yield return null;
            Assert.That(first.Result, Is.Not.EqualTo(second.Result));
            Assert.That(env.Manager.TotalInstanceCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator FailedReplacementKeepsOldWindowAndReleasesPrefabLease()
        {
            using var env = new Environment();
            int previous = env.Manager.ShowView(Route, new RouteArgs());
            var mismatch = new ViewRoute<EnhancementViewHost, RouteArgs>("other");
            var pending = Consume(env.Manager.ReplaceTopViewAsync(mismatch, new RouteArgs()));
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("宿主不匹配"));
            env.Completion.SetResult(true);
            while (!pending.IsCompleted) yield return null;
            Assert.That(pending.Result, Is.Zero);
            Assert.That(env.Manager.IsViewOpen("window"), Is.True);
            Assert.That(env.Pending, Is.EqualTo(1));
            Assert.That(previous, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator DisposeContinuesAfterThrowingLoadCancellationCallback()
        {
            using var env = new Environment { ThrowOnCancel = true };
            env.Manager.ShowView(Route, new RouteArgs());
            var pending = Consume(env.Manager.ShowViewAsync("other", new RouteArgs()));
            Assert.Throws<AggregateException>(() => env.Manager.Dispose());
            Assert.That(env.Manager.TotalInstanceCount, Is.Zero);
            Assert.That(env.Probe.ScopeDisposals, Is.EqualTo(1));
            env.Completion.SetResult(true);
            while (!pending.IsCompleted) yield return null;
            Assert.That(pending.Result, Is.Zero);
            Assert.That(env.Pending, Is.Zero);
        }

        [UnityTest]
        public IEnumerator CloseContinuesAfterThrowingPendingCancellationCallback()
        {
            using var env = new Environment { ThrowOnCancel = true };
            env.Host.InstanceMode = WindowInstanceMode.Multiple;
            env.Manager.ShowView(Route, new RouteArgs());
            var pending = Consume(env.Manager.ShowViewAsync(Route, new RouteArgs()));
            Assert.Throws<AggregateException>(() => env.Manager.CloseView(Route));
            Assert.That(env.Manager.IsViewOpen("window"), Is.False);
            Assert.That(env.Probe.Closes, Is.EqualTo(1));
            env.Completion.SetResult(true);
            while (!pending.IsCompleted) yield return null;
            Assert.That(pending.Result, Is.Zero);
            Assert.That(env.Pending, Is.EqualTo(1));
        }

        [Test]
        public void BridgeReleasesByInstanceAndValidationFailuresReturnLeases()
        {
            var prefab = new GameObject("桥接夹具");
            prefab.AddComponent<RouteViewHost>();
            using var assets = new AssetService(new DirectCatalogBackend(new Dictionary<string, Object> { ["window"] = prefab }));
            using var helper = new AssetViewPrefabHelper(assets);
            var source = helper.GetViewPrefab("window");
            var first = Object.Instantiate(source);
            helper.RegisterViewInstance("window", first);
            var second = Object.Instantiate(helper.GetViewPrefab("window"));
            helper.RegisterViewInstance("window", second);
            helper.ReleaseViewInstance("window", first);
            helper.ReleaseViewInstance("window", first);
            Assert.That(assets.TryGetLoadedAsset<GameObject>("window", out _), Is.True);
            helper.ReleaseViewInstance("window", second);
            Assert.That(assets.TryGetLoadedAsset<GameObject>("window", out _), Is.False);
            helper.GetViewPrefab("window");
            helper.ReleaseUninstantiatedPrefab("window");
            Assert.That(assets.TryGetLoadedAsset<GameObject>("window", out _), Is.False);
            Object.DestroyImmediate(prefab);
        }
    }
}
