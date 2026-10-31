using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComponentAutoBindTool.AssetCore;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Tests
{
    public class AssetCoreTests
    {
        private sealed class Backend : IAssetBackend
        {
            public readonly GameObject Asset = new("资源夹具");
            public readonly AwaitableCompletionSource<bool> Completion = new();
            public bool Delay = true, Fail;
            public int AsyncLoads, Releases, Disposals;
            public Action DuringSync;
            public bool IsInitialized => true;
            public async Awaitable InitializeAsync(CancellationToken token = default) { }
            public (T Asset, object RawHandle) LoadAssetSync<T>(string key) where T : Object
            {
                DuringSync?.Invoke();
                return ((T)(Object)Asset, new object());
            }
            public async Awaitable<(T Asset, object RawHandle)> LoadAssetAsync<T>(string key,
                CancellationToken token = default) where T : Object
            {
                AsyncLoads++;
                if (Delay) await Completion.Awaitable;
                if (Fail) throw new InvalidOperationException("预期加载失败");
                return ((T)(Object)Asset, new object());
            }
            public void ReleaseRawHandle(object handle) => Releases++;
            public async Awaitable<long> GetDownloadSizeAsync(string key, CancellationToken token = default) => 0;
            public async Awaitable DownloadDependenciesAsync(string key, CancellationToken token = default) { }
            public void Dispose() => Disposals++;
        }

        private sealed class ThrowingOwner : IAssetReleaseScope
        {
            public bool IsDisposed => false;
            public string ScopeName => "抛错作用域";
            public T Track<T>(T handle) where T : class, IAssetHandle => throw new InvalidOperationException("预期登记失败");
            public void Dispose() { }
        }

        private Backend m_Backend;
        private AssetService m_Service;
        [SetUp] public void SetUp() { m_Backend = new Backend(); m_Service = new AssetService(m_Backend); }
        [TearDown] public void TearDown() { m_Service.Dispose(); Object.DestroyImmediate(m_Backend.Asset); }
        private static async Task<T> Consume<T>(Awaitable<T> operation) => await operation;

        [Test]
        public void SyncHandlesShareOneReferenceCountAndDisposeIsIdempotent()
        {
            var first = m_Service.LoadAssetSync<GameObject>("item");
            var second = m_Service.LoadAssetSync<GameObject>("item");
            first.Dispose(); first.Dispose();
            Assert.That(m_Backend.Releases, Is.Zero);
            Assert.That(second.Asset, Is.SameAs(m_Backend.Asset));
            second.Dispose();
            Assert.That(m_Backend.Releases, Is.EqualTo(1));
            Assert.That(first.Asset, Is.Null);
        }

        [Test]
        public void OwnerFailureDoesNotReleaseOtherCallersAsset()
        {
            var first = m_Service.LoadAssetSync<GameObject>("item");
            Assert.Throws<InvalidOperationException>(() => m_Service.LoadAssetSync<GameObject>("item", new ThrowingOwner()));
            Assert.That(first.Asset, Is.Not.Null);
            Assert.That(m_Backend.Releases, Is.Zero);
            first.Dispose();
            Assert.That(m_Backend.Releases, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ConcurrentLoadCancellationDoesNotCancelOtherCaller()
        {
            using var cancellation = new CancellationTokenSource();
            var first = Consume(m_Service.LoadAssetAsync<GameObject>("item", cancellationToken: cancellation.Token));
            var second = Consume(m_Service.LoadAssetAsync<GameObject>("item"));
            cancellation.Cancel();
            while (!first.IsCompleted) yield return null;
            Assert.That(first.IsCanceled, Is.True);
            Assert.That(second.IsCompleted, Is.False);
            m_Backend.Completion.SetResult(true);
            while (!second.IsCompleted) yield return null;
            Assert.That(second.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(m_Backend.AsyncLoads, Is.EqualTo(1));
            Assert.That(m_Backend.Releases, Is.Zero);
            second.Result.Dispose();
            Assert.That(m_Backend.Releases, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AllCanceledLoadsReleaseLateResultAndCanRetry()
        {
            using var cancellation = new CancellationTokenSource();
            var task = Consume(m_Service.LoadAssetAsync<GameObject>("item", cancellationToken: cancellation.Token));
            cancellation.Cancel();
            while (!task.IsCompleted) yield return null;
            m_Backend.Completion.SetResult(true);
            yield return null;
            Assert.That(m_Backend.Releases, Is.EqualTo(1));
            Assert.That(m_Service.TryGetLoadedAsset<GameObject>("item", out _), Is.False);
            m_Backend.Delay = false;
            var retry = Consume(m_Service.LoadAssetAsync<GameObject>("item"));
            while (!retry.IsCompleted) yield return null;
            Assert.That(retry.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            retry.Result.Dispose();
        }

        [UnityTest]
        public IEnumerator FailedSharedLoadCanRetryWithoutInvalidCache()
        {
            m_Backend.Fail = true;
            var first = Consume(m_Service.LoadAssetAsync<GameObject>("item"));
            var second = Consume(m_Service.LoadAssetAsync<GameObject>("item"));
            m_Backend.Completion.SetResult(true);
            while (!first.IsCompleted || !second.IsCompleted) yield return null;
            Assert.That(first.Exception?.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(second.Exception?.InnerException, Is.TypeOf<InvalidOperationException>());
            m_Backend.Delay = false;
            m_Backend.Fail = false;
            var retry = Consume(m_Service.LoadAssetAsync<GameObject>("item"));
            while (!retry.IsCompleted) yield return null;
            Assert.That(retry.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            retry.Result.Dispose();
        }

        [UnityTest]
        public IEnumerator DisposeWhileLoadingReleasesLateHandleThenBackend()
        {
            var task = Consume(m_Service.LoadAssetAsync<GameObject>("item"));
            m_Service.Dispose();
            Assert.That(m_Backend.Disposals, Is.Zero);
            m_Backend.Completion.SetResult(true);
            while (!task.IsCompleted) yield return null;
            Assert.That(task.Exception?.InnerException, Is.TypeOf<ObjectDisposedException>());
            Assert.That(m_Backend.Releases, Is.EqualTo(1));
            Assert.That(m_Backend.Disposals, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator SyncFallbackDuringAsyncLoadReleasesRedundantHandle()
        {
            var pending = Consume(m_Service.LoadAssetAsync<GameObject>("item"));
            var sync = m_Service.LoadAssetSync<GameObject>("item");
            m_Backend.Completion.SetResult(true);
            while (!pending.IsCompleted) yield return null;
            Assert.That(m_Backend.Releases, Is.EqualTo(1));
            sync.Dispose();
            Assert.That(m_Backend.Releases, Is.EqualTo(1));
            pending.Result.Dispose();
            Assert.That(m_Backend.Releases, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator SyncCompletionReentrancyDoesNotOverwriteAsyncRawHandle()
        {
            var pending = Consume(m_Service.LoadAssetAsync<GameObject>("item"));
            m_Backend.DuringSync = () => m_Backend.Completion.SetResult(true);
            var sync = m_Service.LoadAssetSync<GameObject>("item");
            while (!pending.IsCompleted) yield return null;
            Assert.That(m_Backend.Releases, Is.EqualTo(1));
            sync.Dispose(); pending.Result.Dispose();
            Assert.That(m_Backend.Releases, Is.EqualTo(2));
        }

        [Test]
        public void InstanceHandlesCanBeReleasedByGameObjectAndScope()
        {
            using var scope = m_Service.CreateReleaseScope();
            var first = m_Service.InstantiateSync("item", owner: scope);
            var second = m_Service.InstantiateSync("item", owner: scope);
            var instance = first.Instance;
            Assert.That(m_Service.ReleaseInstance(instance), Is.True);
            Assert.That(first.IsReleased, Is.True);
            Assert.That(m_Service.ReleaseInstance(instance), Is.False);
            Assert.That(m_Backend.Releases, Is.Zero);
            scope.Dispose();
            Assert.That(second.IsReleased, Is.True);
            Assert.That(m_Backend.Releases, Is.EqualTo(1));
        }

        [Test]
        public void DisposedScopeImmediatelyReleasesNewHandle()
        {
            using var scope = m_Service.CreateReleaseScope();
            scope.Dispose();
            var handle = m_Service.LoadAssetSync<GameObject>("item", scope);
            Assert.That(handle.IsReleased, Is.True);
            Assert.That(m_Backend.Releases, Is.EqualTo(1));
        }
    }
}
