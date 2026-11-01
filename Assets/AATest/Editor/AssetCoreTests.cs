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

        private Backend _backend;
        private AssetService _service;
        [SetUp] public void SetUp() { _backend = new Backend(); _service = new AssetService(_backend); }
        [TearDown] public void TearDown() { _service.Dispose(); Object.DestroyImmediate(_backend.Asset); }
        private static async Task<T> Consume<T>(Awaitable<T> operation) => await operation;

        [Test]
        public void SyncHandlesShareOneReferenceCountAndDisposeIsIdempotent()
        {
            var first = _service.LoadAssetSync<GameObject>("item");
            var second = _service.LoadAssetSync<GameObject>("item");
            first.Dispose(); first.Dispose();
            Assert.That(_backend.Releases, Is.Zero);
            Assert.That(second.Asset, Is.SameAs(_backend.Asset));
            second.Dispose();
            Assert.That(_backend.Releases, Is.EqualTo(1));
            Assert.That(first.Asset, Is.Null);
        }

        [Test]
        public void OwnerFailureDoesNotReleaseOtherCallersAsset()
        {
            var first = _service.LoadAssetSync<GameObject>("item");
            Assert.Throws<InvalidOperationException>(() => _service.LoadAssetSync<GameObject>("item", new ThrowingOwner()));
            Assert.That(first.Asset, Is.Not.Null);
            Assert.That(_backend.Releases, Is.Zero);
            first.Dispose();
            Assert.That(_backend.Releases, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ConcurrentLoadCancellationDoesNotCancelOtherCaller()
        {
            using var cancellation = new CancellationTokenSource();
            var first = Consume(_service.LoadAssetAsync<GameObject>("item", cancellationToken: cancellation.Token));
            var second = Consume(_service.LoadAssetAsync<GameObject>("item"));
            cancellation.Cancel();
            while (!first.IsCompleted) yield return null;
            Assert.That(first.IsCanceled, Is.True);
            Assert.That(second.IsCompleted, Is.False);
            _backend.Completion.SetResult(true);
            while (!second.IsCompleted) yield return null;
            Assert.That(second.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(_backend.AsyncLoads, Is.EqualTo(1));
            Assert.That(_backend.Releases, Is.Zero);
            second.Result.Dispose();
            Assert.That(_backend.Releases, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AllCanceledLoadsReleaseLateResultAndCanRetry()
        {
            using var cancellation = new CancellationTokenSource();
            var task = Consume(_service.LoadAssetAsync<GameObject>("item", cancellationToken: cancellation.Token));
            cancellation.Cancel();
            while (!task.IsCompleted) yield return null;
            _backend.Completion.SetResult(true);
            yield return null;
            Assert.That(_backend.Releases, Is.EqualTo(1));
            Assert.That(_service.TryGetLoadedAsset<GameObject>("item", out _), Is.False);
            _backend.Delay = false;
            var retry = Consume(_service.LoadAssetAsync<GameObject>("item"));
            while (!retry.IsCompleted) yield return null;
            Assert.That(retry.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            retry.Result.Dispose();
        }

        [UnityTest]
        public IEnumerator FailedSharedLoadCanRetryWithoutInvalidCache()
        {
            _backend.Fail = true;
            var first = Consume(_service.LoadAssetAsync<GameObject>("item"));
            var second = Consume(_service.LoadAssetAsync<GameObject>("item"));
            _backend.Completion.SetResult(true);
            while (!first.IsCompleted || !second.IsCompleted) yield return null;
            Assert.That(first.Exception?.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(second.Exception?.InnerException, Is.TypeOf<InvalidOperationException>());
            _backend.Delay = false;
            _backend.Fail = false;
            var retry = Consume(_service.LoadAssetAsync<GameObject>("item"));
            while (!retry.IsCompleted) yield return null;
            Assert.That(retry.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            retry.Result.Dispose();
        }

        [UnityTest]
        public IEnumerator DisposeWhileLoadingReleasesLateHandleThenBackend()
        {
            var task = Consume(_service.LoadAssetAsync<GameObject>("item"));
            _service.Dispose();
            Assert.That(_backend.Disposals, Is.Zero);
            _backend.Completion.SetResult(true);
            while (!task.IsCompleted) yield return null;
            Assert.That(task.Exception?.InnerException, Is.TypeOf<ObjectDisposedException>());
            Assert.That(_backend.Releases, Is.EqualTo(1));
            Assert.That(_backend.Disposals, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator SyncFallbackDuringAsyncLoadReleasesRedundantHandle()
        {
            var pending = Consume(_service.LoadAssetAsync<GameObject>("item"));
            var sync = _service.LoadAssetSync<GameObject>("item");
            _backend.Completion.SetResult(true);
            while (!pending.IsCompleted) yield return null;
            Assert.That(_backend.Releases, Is.EqualTo(1));
            sync.Dispose();
            Assert.That(_backend.Releases, Is.EqualTo(1));
            pending.Result.Dispose();
            Assert.That(_backend.Releases, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator SyncCompletionReentrancyDoesNotOverwriteAsyncRawHandle()
        {
            var pending = Consume(_service.LoadAssetAsync<GameObject>("item"));
            _backend.DuringSync = () => _backend.Completion.SetResult(true);
            var sync = _service.LoadAssetSync<GameObject>("item");
            while (!pending.IsCompleted) yield return null;
            Assert.That(_backend.Releases, Is.EqualTo(1));
            sync.Dispose(); pending.Result.Dispose();
            Assert.That(_backend.Releases, Is.EqualTo(2));
        }

        [Test]
        public void InstanceHandlesCanBeReleasedByGameObjectAndScope()
        {
            using var scope = _service.CreateReleaseScope();
            var first = _service.InstantiateSync("item", owner: scope);
            var second = _service.InstantiateSync("item", owner: scope);
            var instance = first.Instance;
            Assert.That(_service.ReleaseInstance(instance), Is.True);
            Assert.That(first.IsReleased, Is.True);
            Assert.That(_service.ReleaseInstance(instance), Is.False);
            Assert.That(_backend.Releases, Is.Zero);
            scope.Dispose();
            Assert.That(second.IsReleased, Is.True);
            Assert.That(_backend.Releases, Is.EqualTo(1));
        }

        [Test]
        public void DisposedScopeImmediatelyReleasesNewHandle()
        {
            using var scope = _service.CreateReleaseScope();
            scope.Dispose();
            var handle = _service.LoadAssetSync<GameObject>("item", scope);
            Assert.That(handle.IsReleased, Is.True);
            Assert.That(_backend.Releases, Is.EqualTo(1));
        }
    }
}
