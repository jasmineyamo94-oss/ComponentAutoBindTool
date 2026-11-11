#if USE_YOOASSET
using System;
using System.Threading;
using ComponentAutoBindTool.AssetCore;
using UnityEngine;
using YooAsset;

namespace AAGame.Assets
{
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    public static class YooAssetBackendProvider
    {
        private static bool _stopping;
        static YooAssetBackendProvider()
        {
            RegisterLifetimeSignals();
            Register();
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetLifetime()
        {
            _stopping = false;
            RegisterLifetimeSignals();
        }
        private static void RegisterLifetimeSignals()
        {
            Application.quitting -= MarkStopping;
            Application.quitting += MarkStopping;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
        }
        private static void MarkStopping() => _stopping = true;
#if UNITY_EDITOR
        private static void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) _stopping = true;
            else if (state == UnityEditor.PlayModeStateChange.EnteredPlayMode) _stopping = false;
        }
#endif
        private static bool IsStopping => _stopping ||
            (Application.isPlaying && Application.exitCancellationToken.IsCancellationRequested);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Register() => AssetBackendRegistry.Register(AssetBackendKind.YooAsset,
            settings => new OwnedYooBackend(settings.YooPackageName));

        private sealed class OwnedYooBackend : IAssetBackend
        {
            private readonly ResourcePackage _package;
            private readonly YooAssetBackend _backend;
            private bool _disposed;
            public OwnedYooBackend(string packageName)
            {
                if (!YooAssets.IsInitialized) YooAssets.Initialize();
                if (YooAssets.ContainsPackage(packageName))
                    throw new InvalidOperationException($"YooAsset 资源包已由其他所有者占用：{packageName}。");
                _package = YooAssets.CreatePackage(packageName);
                _backend = new YooAssetBackend(_package, InitializePackage);
            }
            public bool IsInitialized => !_disposed && _backend.IsInitialized;
            public Awaitable InitializeAsync(CancellationToken token = default) => _backend.InitializeAsync(token);
            private static async Awaitable InitializePackage(ResourcePackage package, CancellationToken token)
            {
                if (package.InitializeStatus != EOperationStatus.Succeeded)
                {
                    var options = new OfflinePlayModeOptions
                    {
                        BuiltinFileSystemParameters = FileSystemParameters.CreateDefaultBuiltinFileSystemParameters(
                            Application.streamingAssetsPath + "/yoo/" + package.PackageName),
                        AutoUnloadBundleWhenUnused = true
                    };
                    var init = package.InitializePackageAsync(options);
                    await Wait(init, token);
                }
                var version = package.RequestPackageVersionAsync();
                await Wait(version, token);
                var manifest = package.LoadPackageManifestAsync(new LoadPackageManifestOptions(version.PackageVersion, 60));
                await Wait(manifest, token);
            }
            private static async Awaitable Wait(AsyncOperationBase operation, CancellationToken token)
            {
                // 已启动的原生操作必须观察结束，释放包时不会和初始化竞争。
                while (!operation.IsDone) await Awaitable.NextFrameAsync();
                token.ThrowIfCancellationRequested();
                if (operation.Status != EOperationStatus.Succeeded)
                    throw new InvalidOperationException($"YooAsset 离线初始化失败：{operation.Error}。");
            }
            public (T Asset, object RawHandle) LoadAssetSync<T>(string key) where T : UnityEngine.Object => _backend.LoadAssetSync<T>(key);
            public Awaitable<(T Asset, object RawHandle)> LoadAssetAsync<T>(string key, CancellationToken token = default) where T : UnityEngine.Object => _backend.LoadAssetAsync<T>(key, token);
            public void ReleaseRawHandle(object handle) => _backend.ReleaseRawHandle(handle);
            public Awaitable<long> GetDownloadSizeAsync(string key, CancellationToken token = default) => _backend.GetDownloadSizeAsync(key, token);
            public Awaitable DownloadDependenciesAsync(string key, CancellationToken token = default) => _backend.DownloadDependenciesAsync(key, token);
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _backend.Dispose();
                // 退出时 SDK 驱动器可能已在 OnApplicationQuit 中关闭调度器并销毁所有包。
                // 此时不能再向已停调度器提交操作；运行中的普通 Scope 释放仍走完整包销毁。
                if (IsStopping) return;
                if (!YooAssets.IsInitialized)
                    throw new InvalidOperationException("YooAsset 在正常资源释放前已被外部系统关闭，无法销毁所属资源包。");
                var destroy = _package.DestroyPackageAsync();
                destroy.Completed += operation =>
                {
                    // 正常释放开始后也可能立即退出；SDK 的全局收尾会接管并中止进行中的操作。
                    if (IsStopping) return;
                    if (!YooAssets.IsInitialized)
                    {
                        Debug.LogError("YooAsset 在资源包异步释放期间被外部系统关闭。");
                        return;
                    }
                    if (operation.Status == EOperationStatus.Succeeded) YooAssets.RemovePackage(_package.PackageName);
                    else Debug.LogError($"YooAsset 包释放失败：{operation.Error}");
                };
            }
        }
    }
}
#endif
