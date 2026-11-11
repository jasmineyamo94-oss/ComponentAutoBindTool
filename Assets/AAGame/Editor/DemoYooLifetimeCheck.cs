using System;
using System.IO;
using AAGame.Assets;
using ComponentAutoBindTool.AssetCore;
using UnityEditor;
using UnityEngine;

/// <summary>在 Resources 演示运行时，独立创建并释放 YooAsset 服务，验证普通释放路径。</summary>
public static class DemoYooLifetimeCheck
{
    public static async void Run()
    {
        const string result = "Library/AAGameValidation/yoo-owned-dispose.txt";
        AssetBackendSettings settings = null;
        AssetService service = null;
        try
        {
            if (!EditorApplication.isPlaying || DemoResourceSetup.EnsureSettings().Backend != AssetBackendKind.Resources)
                throw new InvalidOperationException("请使用 Resources 进入 Play 后验证独立 YooAsset 包释放。");
            var sdk = Type.GetType("YooAsset.YooAssets, YooAsset", true);
            bool Initialized() => (bool)sdk.GetProperty("IsInitialized").GetValue(null);
            bool Contains() => (bool)sdk.GetMethod("ContainsPackage").Invoke(null, new object[] { "AAGameDemo" });
            if (Initialized() && Contains())
                throw new InvalidOperationException("AAGameDemo 包已被其他所有者占用。");
            settings = ScriptableObject.CreateInstance<AssetBackendSettings>();
            settings.Backend = AssetBackendKind.YooAsset;
            service = new AssetService(AssetBackendRegistry.Create(settings));
            using (var handle = await service.LoadAssetAsync<TextAsset>(DemoAssetKeys.DemoText))
                if (handle.Asset == null) throw new InvalidOperationException("YooAsset 测试资源为空。");
            service.Dispose();
            service = null;
            float deadline = Time.realtimeSinceStartup + 15;
            while (Contains() && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync();
            if (!Initialized() || Contains())
                throw new InvalidOperationException("自有包未释放，或错误停止了全局 YooAsset 服务。");
            Directory.CreateDirectory(Path.GetDirectoryName(result));
            File.WriteAllText(result, "PASS：普通 Dispose 释放自有包，全局 YooAsset 服务继续运行。");
            Debug.Log("[DemoYooLifetimeCheck] PASS");
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(result));
            File.WriteAllText(result, "FAIL：" + error);
            Debug.LogException(error);
        }
        finally
        {
            service?.Dispose();
            if (settings != null) UnityEngine.Object.Destroy(settings);
        }
    }
}
