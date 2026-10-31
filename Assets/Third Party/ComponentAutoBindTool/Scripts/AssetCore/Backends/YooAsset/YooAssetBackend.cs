#if USE_YOOASSET
using System;
using System.Threading;
using UnityEngine;
using YooAsset;
using YooAssetHandle = YooAsset.AssetHandle;

namespace ComponentAutoBindTool.AssetCore
{
    /// <summary>适配 YooAsset 3.0.6+。运行模式、文件系统、版本与清单由组合根配置。</summary>
    public sealed class YooAssetBackend : IAssetBackend
    {
        private readonly ResourcePackage m_Package;
        private readonly Func<ResourcePackage, CancellationToken, Awaitable> m_Initialize;
        private bool m_Disposed;
        public YooAssetBackend(ResourcePackage package,
            Func<ResourcePackage, CancellationToken, Awaitable> initialize = null)
        {
            m_Package = package ?? throw new ArgumentNullException(nameof(package));
            m_Initialize = initialize;
        }
        public bool IsInitialized => !m_Disposed && m_Package.PackageValid;
        public async Awaitable InitializeAsync(CancellationToken cancellationToken = default)
        {
            Check();
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsInitialized && m_Initialize != null) await m_Initialize(m_Package, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsInitialized) throw new InvalidOperationException("YooAsset 包尚未配置完成或资源清单未加载。");
        }
        public (T Asset, object RawHandle) LoadAssetSync<T>(string key) where T : UnityEngine.Object
        {
            RequireReady();
            var handle = m_Package.LoadAssetSync<T>(key);
            try
            {
                EnsureSucceeded(handle, key);
                return ((T)handle.AssetObject, handle);
            }
            catch { handle.Release(); throw; }
        }
        public async Awaitable<(T Asset, object RawHandle)> LoadAssetAsync<T>(string key,
            CancellationToken cancellationToken = default) where T : UnityEngine.Object
        {
            await InitializeAsync(cancellationToken);
            var handle = m_Package.LoadAssetAsync<T>(key);
            try
            {
                while (!handle.IsDone) await Awaitable.NextFrameAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureSucceeded(handle, key);
                return ((T)handle.AssetObject, handle);
            }
            catch { handle.Release(); throw; }
        }
        public void ReleaseRawHandle(object rawHandle)
        {
            if (rawHandle is YooAssetHandle handle) handle.Release();
        }
        public async Awaitable<long> GetDownloadSizeAsync(string key, CancellationToken cancellationToken = default)
        {
            await InitializeAsync(cancellationToken);
            return m_Package.GetDownloadSize(key);
        }
        public async Awaitable DownloadDependenciesAsync(string key, CancellationToken cancellationToken = default)
        {
            await InitializeAsync(cancellationToken);
            var options = new BundleDownloaderOptions(m_Package.GetAssetInfo(key), true, 4, 3);
            var downloader = m_Package.CreateResourceDownloader(options);
            if (downloader.TotalDownloadCount == 0) return;
            downloader.StartDownload();
            try
            {
                while (!downloader.IsDone) await Awaitable.NextFrameAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (downloader.Status != EOperationStatus.Succeeded)
                    throw new InvalidOperationException($"YooAsset 下载失败：{key}，{downloader.Error}。");
            }
            catch { downloader.CancelDownload(); throw; }
        }
        // 外部包可被其他系统共享；不在这里销毁全局包。
        public void Dispose() => m_Disposed = true;
        private void Check()
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(YooAssetBackend));
        }
        private void RequireReady()
        {
            Check();
            if (!IsInitialized) throw new InvalidOperationException("同步加载前必须先初始化 YooAsset 包并加载清单。");
        }
        private static void EnsureSucceeded(YooAssetHandle handle, string key)
        {
            if (handle.Status != EOperationStatus.Succeeded || handle.AssetObject == null)
                throw new InvalidOperationException($"YooAsset 加载失败：{key}，{handle.Error}。");
        }
    }
}
#endif
