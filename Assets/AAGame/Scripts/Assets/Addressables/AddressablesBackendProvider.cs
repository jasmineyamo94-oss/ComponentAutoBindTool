#if USE_ADDRESSABLES
using System;
using System.IO;
using System.Threading;
using ComponentAutoBindTool.AssetCore;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.Networking;

namespace AAGame.Assets
{
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    public static class AddressablesBackendProvider
    {
        static AddressablesBackendProvider() => Register();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Register() => AssetBackendRegistry.Register(AssetBackendKind.Addressables,
            _ => new AddressablesAssetBackend(PreflightLocalContent));

        // SDK 内部启动后无法公开重置：先检查可修复的缺内容错误，保留原地重试能力。
        private static async Awaitable PreflightLocalContent(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var settingsPath = Addressables.RuntimePath + "/settings.json";
#if UNITY_EDITOR
            settingsPath = UnityEditor.SessionState.GetString(Addressables.kAddressablesRuntimeDataPath, settingsPath);
            if (settingsPath.StartsWith("GUID:", StringComparison.Ordinal))
            {
                var assetPath = UnityEditor.AssetDatabase.GUIDToAssetPath(settingsPath.Substring("GUID:".Length));
                var settingsType = Type.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettings, Unity.Addressables.Editor", false);
                var asset = string.IsNullOrEmpty(assetPath) ? null : UnityEditor.AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (settingsType == null || asset == null || !settingsType.IsInstanceOfType(asset))
                    throw new InvalidOperationException($"Addressables 编辑器设置资产无效：{settingsPath}。请检查 Play Mode Script 对应的 AddressableAssetSettings 后重试。");
                token.ThrowIfCancellationRequested();
                return;
            }
#endif
            settingsPath = Addressables.ResolveInternalId(settingsPath);
            var json = await Read(settingsPath, token);
            ResourceManagerRuntimeData data;
            try { data = JsonUtility.FromJson<ResourceManagerRuntimeData>(json); }
            catch (Exception error) { throw new InvalidOperationException("Addressables 本地 settings.json 无效，请重新构建本地内容。", error); }
            if (data == null || data.CatalogLocations == null || data.CatalogLocations.Count == 0)
                throw new InvalidOperationException("Addressables 本地内容缺少 Catalog，请构建后重试。");
            foreach (var location in data.CatalogLocations)
                await Read(Addressables.ResolveInternalId(location.InternalId), token);
        }
        private static async Awaitable<string> Read(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!path.Contains("://") && !path.StartsWith("jar:", StringComparison.Ordinal))
            {
                if (!File.Exists(path)) throw new InvalidOperationException($"Addressables 本地内容不存在：{path}。请先构建当前平台内容后重试。");
                return File.ReadAllText(path);
            }
            // Android 的 StreamingAssets 位于 APK 内，不能用 File.Exists 判断。
            using var request = UnityWebRequest.Get(path);
            var operation = request.SendWebRequest();
            while (!operation.isDone) await Awaitable.NextFrameAsync(token);
            token.ThrowIfCancellationRequested();
            if (request.result != UnityWebRequest.Result.Success)
                throw new InvalidOperationException($"Addressables 本地内容读取失败：{path}，{request.error}。");
            return request.downloadHandler.text;
        }
    }
}
#endif
