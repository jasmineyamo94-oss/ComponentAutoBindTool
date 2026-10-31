#pragma warning disable CS1998 // 离线后端刻意同步完成，保留与异步后端一致的接口。
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ComponentAutoBindTool.AssetCore
{
    /// <summary>已由场景、目录或测试持有的资源；不负责卸载这些外部对象。</summary>
    public sealed class DirectCatalogBackend : IAssetBackend
    {
        private readonly IReadOnlyDictionary<string, UnityEngine.Object> m_Assets;
        private bool m_Disposed;
        public DirectCatalogBackend(IReadOnlyDictionary<string, UnityEngine.Object> assets) =>
            m_Assets = assets ?? throw new ArgumentNullException(nameof(assets));
        public bool IsInitialized => !m_Disposed;
        public async Awaitable InitializeAsync(CancellationToken cancellationToken = default) => Check(cancellationToken);
        public (T Asset, object RawHandle) LoadAssetSync<T>(string key) where T : UnityEngine.Object
        {
            Check(default);
            if (!m_Assets.TryGetValue(key, out var asset) || asset is not T typed || typed == null)
                throw new InvalidOperationException($"离线资源目录缺少 {key} ({typeof(T).Name})。");
            return (typed, null);
        }
        public async Awaitable<(T Asset, object RawHandle)> LoadAssetAsync<T>(string key,
            CancellationToken cancellationToken = default) where T : UnityEngine.Object
        {
            Check(cancellationToken);
            return LoadAssetSync<T>(key);
        }
        public void ReleaseRawHandle(object rawHandle) { }
        public async Awaitable<long> GetDownloadSizeAsync(string key, CancellationToken cancellationToken = default)
        {
            Check(cancellationToken);
            return 0;
        }
        public async Awaitable DownloadDependenciesAsync(string key, CancellationToken cancellationToken = default) => Check(cancellationToken);
        public void Dispose() => m_Disposed = true;
        private void Check(CancellationToken token)
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(DirectCatalogBackend));
            token.ThrowIfCancellationRequested();
        }
    }
}
