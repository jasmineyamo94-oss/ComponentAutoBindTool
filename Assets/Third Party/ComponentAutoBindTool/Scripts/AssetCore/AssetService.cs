using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace ComponentAutoBindTool.AssetCore
{
    public sealed class AssetService : IAssetService
    {
        private sealed class CacheEntry
        {
            public UnityEngine.Object Asset;
            public object RawHandle;
            public int References;
            public Task Load;
            public Exception Error;
        }

        private readonly IAssetBackend _backend;
        private readonly Dictionary<(Type, string), CacheEntry> _cache = new();
        private readonly HashSet<IAssetHandle> _handles = new();
        private readonly Dictionary<int, AssetInstanceHandle> _instances = new();
        private Task _initialization;
        private bool _disposed;
        private bool _backendDisposed;
        private int _operations;

        public AssetService(IAssetBackend backend) =>
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        public bool IsInitialized => !_disposed && _backend.IsInitialized;

        public async Awaitable InitializeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_backend.IsInitialized)
            {
                if (_initialization == null || _initialization.IsFaulted || _initialization.IsCanceled)
                    _initialization = InitializeInternalAsync();
                await WaitForCallerAsync(_initialization, cancellationToken);
            }
            ThrowIfDisposed();
        }

        private async Task InitializeInternalAsync()
        {
            _operations++;
            try { await _backend.InitializeAsync(); }
            finally { _operations--; TryDisposeBackend(); }
        }

        // 共享 Task 可以多次等待；每个公开调用仍返回独立的 Unity Awaitable。
        private static async Task WaitForCallerAsync(Task operation, CancellationToken token)
        {
            if (!operation.IsCompleted && token.CanBeCanceled)
            {
                var canceled = new TaskCompletionSource<bool>();
                using (token.Register(() => canceled.TrySetResult(true)))
                {
                    if (await Task.WhenAny(operation, canceled.Task) != operation)
                        token.ThrowIfCancellationRequested();
                }
            }
            token.ThrowIfCancellationRequested();
            await operation;
        }

        public IAssetReleaseScope CreateReleaseScope(string scopeName = null)
        {
            ThrowIfDisposed();
            return new AssetReleaseScope(scopeName);
        }

        public bool TryGetLoadedAsset<T>(string key, out T asset) where T : UnityEngine.Object
        {
            ThrowIfDisposed();
            ValidateKey(key);
            asset = _cache.TryGetValue((typeof(T), key), out var entry) ? entry.Asset as T : null;
            return asset != null;
        }

        public AssetHandle<T> LoadAssetSync<T>(string key, IAssetReleaseScope owner = null) where T : UnityEngine.Object
        {
            ThrowIfDisposed();
            ValidateKey(key);
            var cacheKey = (typeof(T), key);
            if (!_cache.TryGetValue(cacheKey, out var entry))
                _cache.Add(cacheKey, entry = new CacheEntry());
            entry.References++;
            bool transferred = false;
            try
            {
                if (entry.Asset == null)
                {
                    var result = _backend.LoadAssetSync<T>(key);
                    if (result.Asset == null)
                    {
                        if (result.RawHandle != null) _backend.ReleaseRawHandle(result.RawHandle);
                        throw new InvalidOperationException($"同步资源加载失败：{key} ({typeof(T).Name})。");
                    }
                    if (_disposed || entry.Asset != null)
                    {
                        if (result.RawHandle != null) _backend.ReleaseRawHandle(result.RawHandle);
                        ThrowIfDisposed();
                    }
                    else
                    {
                        entry.Asset = result.Asset;
                        entry.RawHandle = result.RawHandle;
                        entry.Error = null;
                    }
                }
                transferred = true;
                return CreateHandle<T>(cacheKey, entry, owner);
            }
            catch { if (!transferred) Release(cacheKey, entry); throw; }
        }

        public async Awaitable<AssetHandle<T>> LoadAssetAsync<T>(string key, IAssetReleaseScope owner = null,
            CancellationToken cancellationToken = default) where T : UnityEngine.Object
        {
            ValidateKey(key);
            await InitializeAsync(cancellationToken);
            var cacheKey = (typeof(T), key);
            if (!_cache.TryGetValue(cacheKey, out var entry))
                _cache.Add(cacheKey, entry = new CacheEntry());
            entry.References++;
            bool transferred = false;
            try
            {
                if (entry.Asset == null)
                {
                    entry.Load ??= LoadEntryAsync<T>(cacheKey, entry);
                    await WaitForCallerAsync(entry.Load, cancellationToken);
                }
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Asset == null)
                    throw entry.Error ?? new InvalidOperationException($"异步资源加载失败：{key}。");
                transferred = true;
                return CreateHandle<T>(cacheKey, entry, owner);
            }
            catch { if (!transferred) Release(cacheKey, entry); throw; }
        }

        private async Task LoadEntryAsync<T>((Type, string) key, CacheEntry entry) where T : UnityEngine.Object
        {
            _operations++;
            try
            {
                var result = await _backend.LoadAssetAsync<T>(key.Item2);
                // 同步兜底可能先完成；后一份原生句柄必须释放，不能覆盖尚在使用的句柄。
                if (_disposed || entry.References == 0 || entry.Asset != null || result.Asset == null)
                {
                    if (result.RawHandle != null) _backend.ReleaseRawHandle(result.RawHandle);
                    if (result.Asset == null && entry.Asset == null)
                        entry.Error = new InvalidOperationException($"资源为空：{key.Item2}。");
                }
                else
                {
                    entry.Asset = result.Asset;
                    entry.RawHandle = result.RawHandle;
                }
            }
            catch (Exception error) { entry.Error = error; }
            finally
            {
                _operations--;
                if (entry.References == 0) RemoveEntry(key, entry);
                TryDisposeBackend();
            }
        }

        private AssetHandle<T> CreateHandle<T>((Type, string) key, CacheEntry entry, IAssetReleaseScope owner)
            where T : UnityEngine.Object
        {
            AssetHandle<T> handle = null;
            handle = new AssetHandle<T>(key.Item2, (T)entry.Asset, () =>
            {
                _handles.Remove(handle);
                Release(key, entry);
            });
            _handles.Add(handle);
            try { owner?.Track(handle); }
            catch { handle.Dispose(); throw; }
            return handle;
        }

        private void Release((Type, string) key, CacheEntry entry)
        {
            if (--entry.References != 0) return;
            // 未完成加载留在缓存里供新请求复用，完成后统一回收。
            if (entry.Load == null || entry.Load.IsCompleted) RemoveEntry(key, entry);
        }

        private void RemoveEntry((Type, string) key, CacheEntry entry)
        {
            if (_cache.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) _cache.Remove(key);
            object raw = entry.RawHandle;
            entry.RawHandle = null;
            entry.Asset = null;
            if (raw != null) _backend.ReleaseRawHandle(raw);
            TryDisposeBackend();
        }

        public AssetInstanceHandle InstantiateSync(string key, Transform parent = null, bool worldPositionStays = false,
            IObjectResolver resolver = null, IAssetReleaseScope owner = null) =>
            CreateInstance(key, LoadAssetSync<GameObject>(key), parent, worldPositionStays, resolver, owner);

        public async Awaitable<AssetInstanceHandle> InstantiateAsync(string key, Transform parent = null,
            bool worldPositionStays = false, IObjectResolver resolver = null, IAssetReleaseScope owner = null,
            CancellationToken cancellationToken = default)
        {
            var prefab = await LoadAssetAsync<GameObject>(key, cancellationToken: cancellationToken);
            return CreateInstance(key, prefab, parent, worldPositionStays, resolver, owner);
        }

        private AssetInstanceHandle CreateInstance(string key, AssetHandle<GameObject> prefab, Transform parent,
            bool worldPositionStays, IObjectResolver resolver, IAssetReleaseScope owner)
        {
            GameObject instance = null;
            AssetInstanceHandle handle = null;
            try
            {
                instance = resolver == null ? UnityEngine.Object.Instantiate(prefab.Asset, parent, worldPositionStays) :
                    resolver.Instantiate(prefab.Asset, parent, worldPositionStays);
                int id = instance.GetInstanceID();
                handle = new AssetInstanceHandle(key, instance, () =>
                {
                    _instances.Remove(id);
                    _handles.Remove(handle);
                    try { DestroyInstance(instance); }
                    finally { prefab.Dispose(); }
                });
                _instances.Add(id, handle);
                _handles.Add(handle);
                owner?.Track(handle);
                return handle;
            }
            catch
            {
                if (handle != null) handle.Dispose();
                else { DestroyInstance(instance); prefab.Dispose(); }
                throw;
            }
        }

        internal static void DestroyInstance(GameObject instance)
        {
            if (instance == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(instance);
            else UnityEngine.Object.DestroyImmediate(instance);
        }

        public bool ReleaseInstance(GameObject instance)
        {
            if (ReferenceEquals(instance, null) || !_instances.TryGetValue(instance.GetInstanceID(), out var handle)) return false;
            handle.Dispose();
            return true;
        }

        public async Awaitable<long> GetDownloadSizeAsync(string key, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            await InitializeAsync(cancellationToken);
            _operations++;
            try { return await _backend.GetDownloadSizeAsync(key, cancellationToken); }
            finally { _operations--; TryDisposeBackend(); }
        }

        public async Awaitable DownloadDependenciesAsync(string key, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            await InitializeAsync(cancellationToken);
            _operations++;
            try { await _backend.DownloadDependenciesAsync(key, cancellationToken); }
            finally { _operations--; TryDisposeBackend(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            List<Exception> errors = null;
            var handles = new List<IAssetHandle>(_handles);
            // 先释放实例，后释放其持有的资源句柄。
            foreach (var handle in handles)
                if (handle is AssetInstanceHandle)
                    try { handle.Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
            foreach (var handle in handles)
                try { handle.Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
            try { TryDisposeBackend(); } catch (Exception error) { (errors ??= new()).Add(error); }
            if (errors != null) throw new AggregateException("资源服务释放失败。", errors);
        }

        private void TryDisposeBackend()
        {
            if (!_disposed || _backendDisposed || _operations != 0 || _cache.Count != 0) return;
            _backendDisposed = true;
            _backend.Dispose();
        }
        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AssetService));
        }
        private static void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("资源键不能为空。", nameof(key));
        }
    }
}
