using System;
using System.Collections.Generic;
using System.Threading;
using ComponentAutoBindTool.AssetCore;
using UnityEngine;

namespace AAGame.Assets
{
    public static class AssetBackendRegistry
    {
        private static readonly Dictionary<AssetBackendKind, Func<AssetBackendSettings, IAssetBackend>> Factories = new();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => Factories.Clear();
        public static void Register(AssetBackendKind kind, Func<AssetBackendSettings, IAssetBackend> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            Factories[kind] = factory;
        }
        public static bool IsAvailable(AssetBackendKind kind) => kind == AssetBackendKind.Resources || Factories.ContainsKey(kind);
        public static IAssetBackend Create(AssetBackendSettings settings) => new DeferredBackend(settings);
        private static IAssetBackend CreateImmediate(AssetBackendSettings settings)
        {
            if (settings == null) throw new InvalidOperationException("Bootstrap 缺少资源后端配置。");
            if (settings.Backend == AssetBackendKind.Resources) return new ResourcesAssetBackend(settings.ResourcesPrefix);
            if (Factories.TryGetValue(settings.Backend, out var factory)) return factory(settings);
            throw new InvalidOperationException($"资源后端 {settings.Backend} 未安装或尚未注册，请打开环境检测与安装窗口。");
        }
        // Scope 构建不接触可选 SDK，让冷启动错误进入现有重试界面。
        private sealed class DeferredBackend : IAssetBackend
        {
            private readonly AssetBackendSettings _settings;
            private IAssetBackend _inner;
            private bool _disposed;
            public DeferredBackend(AssetBackendSettings settings) => _settings = settings;
            public bool IsInitialized => !_disposed && _inner != null && _inner.IsInitialized;
            private IAssetBackend Require()
            {
                if (_disposed) throw new ObjectDisposedException(nameof(DeferredBackend));
                return _inner ??= CreateImmediate(_settings);
            }
            public Awaitable InitializeAsync(CancellationToken token = default) => Require().InitializeAsync(token);
            public (T Asset, object RawHandle) LoadAssetSync<T>(string key) where T : UnityEngine.Object => Require().LoadAssetSync<T>(key);
            public Awaitable<(T Asset, object RawHandle)> LoadAssetAsync<T>(string key, CancellationToken token = default) where T : UnityEngine.Object => Require().LoadAssetAsync<T>(key, token);
            public void ReleaseRawHandle(object handle) => _inner?.ReleaseRawHandle(handle);
            public Awaitable<long> GetDownloadSizeAsync(string key, CancellationToken token = default) => Require().GetDownloadSizeAsync(key, token);
            public Awaitable DownloadDependenciesAsync(string key, CancellationToken token = default) => Require().DownloadDependenciesAsync(key, token);
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _inner?.Dispose();
            }
        }
    }
}
