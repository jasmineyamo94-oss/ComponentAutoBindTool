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

        private readonly IAssetBackend m_Backend;
        private readonly Dictionary<(Type, string), CacheEntry> m_Cache = new();
        private readonly HashSet<IAssetHandle> m_Handles = new();
        private readonly Dictionary<int, AssetInstanceHandle> m_Instances = new();
        private Task m_Initialization;
        private bool m_Disposed;
        private bool m_BackendDisposed;
        private int m_Operations;

        public AssetService(IAssetBackend backend) =>
            m_Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        public bool IsInitialized => !m_Disposed && m_Backend.IsInitialized;

        public async Awaitable InitializeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!m_Backend.IsInitialized)
            {
                if (m_Initialization == null || m_Initialization.IsFaulted || m_Initialization.IsCanceled)
                    m_Initialization = InitializeInternalAsync();
                await WaitForCallerAsync(m_Initialization, cancellationToken);
            }
            ThrowIfDisposed();
        }

        private async Task InitializeInternalAsync()
        {
            m_Operations++;
            try { await m_Backend.InitializeAsync(); }
            finally { m_Operations--; TryDisposeBackend(); }
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
            asset = m_Cache.TryGetValue((typeof(T), key), out var entry) ? entry.Asset as T : null;
            return asset != null;
        }

        public AssetHandle<T> LoadAssetSync<T>(string key, IAssetReleaseScope owner = null) where T : UnityEngine.Object
        {
            ThrowIfDisposed();
            ValidateKey(key);
            var cacheKey = (typeof(T), key);
            if (!m_Cache.TryGetValue(cacheKey, out var entry))
                m_Cache.Add(cacheKey, entry = new CacheEntry());
            entry.References++;
            bool transferred = false;
            try
            {
                if (entry.Asset == null)
                {
                    var result = m_Backend.LoadAssetSync<T>(key);
                    if (result.Asset == null)
                    {
                        if (result.RawHandle != null) m_Backend.ReleaseRawHandle(result.RawHandle);
                        throw new InvalidOperationException($"同步资源加载失败：{key} ({typeof(T).Name})。");
                    }
                    if (m_Disposed || entry.Asset != null)
                    {
                        if (result.RawHandle != null) m_Backend.ReleaseRawHandle(result.RawHandle);
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
            if (!m_Cache.TryGetValue(cacheKey, out var entry))
                m_Cache.Add(cacheKey, entry = new CacheEntry());
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
            m_Operations++;
            try
            {
                var result = await m_Backend.LoadAssetAsync<T>(key.Item2);
                // 同步兜底可能先完成；后一份原生句柄必须释放，不能覆盖尚在使用的句柄。
                if (m_Disposed || entry.References == 0 || entry.Asset != null || result.Asset == null)
                {
                    if (result.RawHandle != null) m_Backend.ReleaseRawHandle(result.RawHandle);
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
                m_Operations--;
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
                m_Handles.Remove(handle);
                Release(key, entry);
            });
            m_Handles.Add(handle);
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
            if (m_Cache.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) m_Cache.Remove(key);
            object raw = entry.RawHandle;
            entry.RawHandle = null;
            entry.Asset = null;
            if (raw != null) m_Backend.ReleaseRawHandle(raw);
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
                    m_Instances.Remove(id);
                    m_Handles.Remove(handle);
                    try { DestroyInstance(instance); }
                    finally { prefab.Dispose(); }
                });
                m_Instances.Add(id, handle);
                m_Handles.Add(handle);
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
            if (ReferenceEquals(instance, null) || !m_Instances.TryGetValue(instance.GetInstanceID(), out var handle)) return false;
            handle.Dispose();
            return true;
        }

        public async Awaitable<long> GetDownloadSizeAsync(string key, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            await InitializeAsync(cancellationToken);
            m_Operations++;
            try { return await m_Backend.GetDownloadSizeAsync(key, cancellationToken); }
            finally { m_Operations--; TryDisposeBackend(); }
        }

        public async Awaitable DownloadDependenciesAsync(string key, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            await InitializeAsync(cancellationToken);
            m_Operations++;
            try { await m_Backend.DownloadDependenciesAsync(key, cancellationToken); }
            finally { m_Operations--; TryDisposeBackend(); }
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            List<Exception> errors = null;
            var handles = new List<IAssetHandle>(m_Handles);
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
            if (!m_Disposed || m_BackendDisposed || m_Operations != 0 || m_Cache.Count != 0) return;
            m_BackendDisposed = true;
            m_Backend.Dispose();
        }
        private void ThrowIfDisposed()
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(AssetService));
        }
        private static void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("资源键不能为空。", nameof(key));
        }
    }
}
