#if USE_ADDRESSABLES
using System;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.AddressableAssets.ResourceLocators;

namespace ComponentAutoBindTool.AssetCore
{
    /// <summary>适配 Addressables 2.x；同步加载不适用于 WebGL 的未完成请求。</summary>
    public sealed class AddressablesAssetBackend : IAssetBackend
    {
        private AsyncOperationHandle<IResourceLocator> m_Initialization;
        private bool m_Started;
        private bool m_Disposed;
        public bool IsInitialized => !m_Disposed && m_Started && m_Initialization.IsValid() &&
            m_Initialization.Status == AsyncOperationStatus.Succeeded;
        public async Awaitable InitializeAsync(CancellationToken cancellationToken = default)
        {
            Check();
            cancellationToken.ThrowIfCancellationRequested();
            if (!m_Started)
            {
                m_Initialization = Addressables.InitializeAsync(false);
                m_Started = true;
            }
            while (!m_Initialization.IsDone) await Awaitable.NextFrameAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSucceeded(m_Initialization, "初始化");
        }
        public (T Asset, object RawHandle) LoadAssetSync<T>(string key) where T : UnityEngine.Object
        {
            Check();
#if UNITY_WEBGL && !UNITY_EDITOR
            throw new NotSupportedException("WebGL 请使用异步资源加载和异步开窗。");
#else
            var handle = Addressables.LoadAssetAsync<T>(key);
            try
            {
                T asset = handle.WaitForCompletion();
                EnsureSucceeded(handle, key);
                if (asset == null) throw new InvalidOperationException($"Addressables 资源为空：{key}。");
                return (asset, (AsyncOperationHandle)handle);
            }
            catch { if (handle.IsValid()) Addressables.Release(handle); throw; }
#endif
        }
        public async Awaitable<(T Asset, object RawHandle)> LoadAssetAsync<T>(string key,
            CancellationToken cancellationToken = default) where T : UnityEngine.Object
        {
            await InitializeAsync(cancellationToken);
            var handle = Addressables.LoadAssetAsync<T>(key);
            try
            {
                while (!handle.IsDone) await Awaitable.NextFrameAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureSucceeded(handle, key);
                if (handle.Result == null) throw new InvalidOperationException($"Addressables 资源为空：{key}。");
                return (handle.Result, (AsyncOperationHandle)handle);
            }
            catch { if (handle.IsValid()) Addressables.Release(handle); throw; }
        }
        public void ReleaseRawHandle(object rawHandle)
        {
            if (rawHandle is AsyncOperationHandle handle && handle.IsValid()) Addressables.Release(handle);
        }
        public async Awaitable<long> GetDownloadSizeAsync(string key, CancellationToken cancellationToken = default)
        {
            await InitializeAsync(cancellationToken);
            var handle = Addressables.GetDownloadSizeAsync(key);
            try
            {
                while (!handle.IsDone) await Awaitable.NextFrameAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureSucceeded(handle, key);
                return handle.Result;
            }
            finally { if (handle.IsValid()) Addressables.Release(handle); }
        }
        public async Awaitable DownloadDependenciesAsync(string key, CancellationToken cancellationToken = default)
        {
            await InitializeAsync(cancellationToken);
            var handle = Addressables.DownloadDependenciesAsync(key, false);
            try
            {
                while (!handle.IsDone) await Awaitable.NextFrameAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureSucceeded(handle, key);
            }
            finally { if (handle.IsValid()) Addressables.Release(handle); }
        }
        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            if (m_Started && m_Initialization.IsValid()) Addressables.Release(m_Initialization);
        }
        private void Check()
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(AddressablesAssetBackend));
        }
        private static void EnsureSucceeded(AsyncOperationHandle handle, string key)
        {
            if (handle.Status != AsyncOperationStatus.Succeeded)
                throw new InvalidOperationException($"Addressables 操作失败：{key}。", handle.OperationException);
        }
    }
}
#endif
