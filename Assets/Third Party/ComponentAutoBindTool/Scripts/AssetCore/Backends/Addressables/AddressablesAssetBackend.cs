#if USE_ADDRESSABLES
using System;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.AddressableAssets.ResourceLocators;

namespace ComponentAutoBindTool.AssetCore
{
    /// <summary>适配 Addressables；同步加载不适用于 WebGL 的未完成请求。</summary>
    public sealed class AddressablesAssetBackend : IAssetBackend
    {
        private AsyncOperationHandle<IResourceLocator> _initialization;
        private bool _started;
        private bool _requiresRestart;
        private readonly Func<CancellationToken, Awaitable> _preflight;
        public AddressablesAssetBackend(Func<CancellationToken, Awaitable> preflight = null) => _preflight = preflight;
        private bool _disposed;
        public bool IsInitialized => !_disposed && _started && _initialization.IsValid() &&
            _initialization.Status == AsyncOperationStatus.Succeeded;
        public async Awaitable InitializeAsync(CancellationToken cancellationToken = default)
        {
            Check();
            cancellationToken.ThrowIfCancellationRequested();
            if (_requiresRestart)
                throw new InvalidOperationException("Addressables SDK 初始化已经失败。当前 SDK 没有公开的初始化重置接口，请退出运行、修复内容后重新进入运行。");
            if (!_started)
            {
                if (_preflight != null) await _preflight(cancellationToken);
                _initialization = Addressables.InitializeAsync(false);
                _started = true;
            }
            while (!_initialization.IsDone) await Awaitable.NextFrameAsync(cancellationToken);
            Check();
            cancellationToken.ThrowIfCancellationRequested();
            if (_initialization.Status != AsyncOperationStatus.Succeeded)
            {
                var error = _initialization.OperationException;
                Addressables.Release(_initialization);
                _started = false;
                _requiresRestart = true;
                throw new InvalidOperationException("Addressables SDK 初始化失败，已释放初始化句柄。请退出运行、修复内容后重新进入运行。", error);
            }
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
            if (_disposed) return;
            _disposed = true;
            if (_started && _initialization.IsValid()) Addressables.Release(_initialization);
        }
        private void Check()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AddressablesAssetBackend));
        }
        private static void EnsureSucceeded(AsyncOperationHandle handle, string key)
        {
            if (handle.Status != AsyncOperationStatus.Succeeded)
                throw new InvalidOperationException($"Addressables 操作失败：{key}。", handle.OperationException);
        }
    }
}
#endif
