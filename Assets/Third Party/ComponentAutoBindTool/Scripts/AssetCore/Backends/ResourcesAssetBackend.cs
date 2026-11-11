#pragma warning disable CS1998
using System;
using System.Threading;
using UnityEngine;

namespace ComponentAutoBindTool.AssetCore
{
    /// <summary>Resources 的逻辑租约后端；实际回收由 Unity 在明确的清理点执行。</summary>
    public sealed class ResourcesAssetBackend : IAssetBackend
    {
        private readonly string _prefix;
        private bool _disposed;
        public ResourcesAssetBackend(string prefix = "") => _prefix = (prefix ?? "").Trim('/');
        public bool IsInitialized => !_disposed;
        public async Awaitable InitializeAsync(CancellationToken cancellationToken = default) => Check(cancellationToken);
        public (T Asset, object RawHandle) LoadAssetSync<T>(string key) where T : UnityEngine.Object
        {
            Check(default);
            var asset = Resources.Load<T>(Resolve(key));
            if (asset == null) throw Missing<T>(key);
            return (asset, null);
        }
        public async Awaitable<(T Asset, object RawHandle)> LoadAssetAsync<T>(string key,
            CancellationToken cancellationToken = default) where T : UnityEngine.Object
        {
            Check(cancellationToken);
            var request = Resources.LoadAsync<T>(Resolve(key));
            // 原生请求不可中断；等待结束后再传播取消，避免留下未观察的请求。
            while (!request.isDone) await Awaitable.NextFrameAsync();
            Check(cancellationToken);
            if (request.asset is not T asset || asset == null) throw Missing<T>(key);
            return (asset, null);
        }
        public void ReleaseRawHandle(object rawHandle) { }
        public async Awaitable<long> GetDownloadSizeAsync(string key, CancellationToken cancellationToken = default)
        {
            Check(cancellationToken);
            return 0;
        }
        public async Awaitable DownloadDependenciesAsync(string key, CancellationToken cancellationToken = default) => Check(cancellationToken);
        public void Dispose() => _disposed = true;
        private string Resolve(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.StartsWith("/") || key.Contains("..") || key.Contains("\\"))
                throw new ArgumentException("资源 Key 必须是 Resources 内的相对逻辑路径。", nameof(key));
            return string.IsNullOrEmpty(_prefix) ? key : _prefix + "/" + key;
        }
        private static Exception Missing<T>(string key) => new InvalidOperationException($"Resources 缺少资源或类型不匹配：{key} ({typeof(T).Name})。");
        private void Check(CancellationToken token)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ResourcesAssetBackend));
            token.ThrowIfCancellationRequested();
        }
    }
}
