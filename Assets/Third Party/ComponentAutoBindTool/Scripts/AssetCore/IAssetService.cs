using System;
using System.Threading;
using UnityEngine;
using VContainer;

namespace ComponentAutoBindTool.AssetCore
{
    public interface IAssetHandle : IDisposable
    {
        bool IsReleased { get; }
        string Key { get; }
    }

    public interface IAssetReleaseScope : IDisposable
    {
        bool IsDisposed { get; }
        string ScopeName { get; }
        T Track<T>(T handle) where T : class, IAssetHandle;
    }

    /// <summary>所有入口在 Unity 主线程调用；每次返回的 Awaitable 只消费一次。</summary>
    public interface IAssetService : IDisposable
    {
        bool IsInitialized { get; }
        Awaitable InitializeAsync(CancellationToken cancellationToken = default);
        IAssetReleaseScope CreateReleaseScope(string scopeName = null);
        bool TryGetLoadedAsset<T>(string key, out T asset) where T : UnityEngine.Object;
        AssetHandle<T> LoadAssetSync<T>(string key, IAssetReleaseScope owner = null) where T : UnityEngine.Object;
        Awaitable<AssetHandle<T>> LoadAssetAsync<T>(string key, IAssetReleaseScope owner = null,
            CancellationToken cancellationToken = default) where T : UnityEngine.Object;
        AssetInstanceHandle InstantiateSync(string key, Transform parent = null, bool worldPositionStays = false,
            IObjectResolver resolver = null, IAssetReleaseScope owner = null);
        Awaitable<AssetInstanceHandle> InstantiateAsync(string key, Transform parent = null,
            bool worldPositionStays = false, IObjectResolver resolver = null, IAssetReleaseScope owner = null,
            CancellationToken cancellationToken = default);
        bool ReleaseInstance(GameObject instance);
        Awaitable<long> GetDownloadSizeAsync(string key, CancellationToken cancellationToken = default);
        Awaitable DownloadDependenciesAsync(string key, CancellationToken cancellationToken = default);
    }
}
