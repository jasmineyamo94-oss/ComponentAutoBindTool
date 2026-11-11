using System;
using System.Threading;
using System.IO;
using UnityEditor;
using AAGame.Assets;
using ComponentAutoBindTool.AssetCore;
using NUnit.Framework;
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public sealed class AssetBackendTests
    {
        private string _fixture;
        private string _prefix;

        [SetUp]
        public void CreateIndependentResource()
        {
            _prefix = "BackendFixture" + Guid.NewGuid().ToString("N");
            _fixture = "Assets/AATest/Temporary/" + _prefix;
            string data = _fixture + "/Resources/" + _prefix + "/Data";
            Directory.CreateDirectory(data);
            File.WriteAllText(data + "/DemoText.txt", "独立资源后端测试");
            AssetDatabase.Refresh();
        }

        [TearDown]
        public void RemoveIndependentResource() => AssetDatabase.DeleteAsset(_fixture);

        [Test]
        public void ResourcesLoadsNormalAssetWithLogicalKeyAndRejectsWrongType()
        {
            using var backend = new ResourcesAssetBackend(_prefix);
            var result = backend.LoadAssetSync<TextAsset>("Data/DemoText");
            Assert.That(result.Asset, Is.Not.Null);
            Assert.That(result.RawHandle, Is.Null);
            Assert.Throws<InvalidOperationException>(() => backend.LoadAssetSync<GameObject>("Data/DemoText"));
            Assert.Throws<InvalidOperationException>(() => backend.LoadAssetSync<TextAsset>("Data/不存在"));
            backend.ReleaseRawHandle(result.RawHandle);
            backend.ReleaseRawHandle(result.RawHandle);
            Assert.That(result.Asset.text, Is.Not.Empty);
        }

        [Test]
        public void ResourcesRejectsEscapingKeysAndDisposedUse()
        {
            var backend = new ResourcesAssetBackend(_prefix);
            Assert.Throws<ArgumentException>(() => backend.LoadAssetSync<TextAsset>("../Data/DemoText"));
            Assert.Throws<ArgumentException>(() => backend.LoadAssetSync<TextAsset>("/Data/DemoText"));
            backend.Dispose();
            backend.Dispose();
            Assert.That(backend.IsInitialized, Is.False);
            Assert.Throws<ObjectDisposedException>(() => backend.LoadAssetSync<TextAsset>("Data/DemoText"));
        }

        [Test]
        public void ResourcesCancellationAndOfflineDownloadSemantics()
        {
            using var backend = new ResourcesAssetBackend(_prefix);
            Assert.That(backend.GetDownloadSizeAsync("Data/DemoText").GetAwaiter().GetResult(), Is.Zero);
            backend.DownloadDependenciesAsync("Data/DemoText").GetAwaiter().GetResult();
            var canceled = new CancellationToken(true);
            Assert.Throws<OperationCanceledException>(() => backend.InitializeAsync(canceled).GetAwaiter().GetResult());
            Assert.Throws<OperationCanceledException>(() => backend.LoadAssetAsync<TextAsset>("Data/DemoText", canceled).GetAwaiter().GetResult());
            Assert.Throws<OperationCanceledException>(() => backend.DownloadDependenciesAsync("Data/DemoText", canceled).GetAwaiter().GetResult());
        }

        [Test]
        public void MissingBackendFailsAtInitializationAndCanBeRetried()
        {
            var settings = ScriptableObject.CreateInstance<AssetBackendSettings>();
            try
            {
                settings.Backend = (AssetBackendKind)999;
                using var backend = AssetBackendRegistry.Create(settings);
                Assert.That(backend.IsInitialized, Is.False);
                Assert.Throws<InvalidOperationException>(() => backend.InitializeAsync());
                settings.Backend = AssetBackendKind.Resources;
                backend.InitializeAsync().GetAwaiter().GetResult();
                Assert.That(backend.IsInitialized, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }
    }
}
