using System;
using System.Threading;
using UnityEngine;

namespace ComponentAutoBindTool.AssetCore
{
    /// <summary>只负责资源引擎操作；每个成功返回的 RawHandle 由服务释放一次。</summary>
    public interface IAssetBackend : IDisposable
    {
        bool IsInitialized { get; }
        Awaitable InitializeAsync(CancellationToken cancellationToken = default);
        (T Asset, object RawHandle) LoadAssetSync<T>(string key) where T : UnityEngine.Object;
        Awaitable<(T Asset, object RawHandle)> LoadAssetAsync<T>(string key,
            CancellationToken cancellationToken = default) where T : UnityEngine.Object;
        void ReleaseRawHandle(object rawHandle);
        Awaitable<long> GetDownloadSizeAsync(string key, CancellationToken cancellationToken = default);
        Awaitable DownloadDependenciesAsync(string key, CancellationToken cancellationToken = default);
    }
}
