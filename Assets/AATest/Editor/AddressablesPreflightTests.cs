using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public sealed class AddressablesPreflightTests
    {
        private const string RuntimeDataKey = "AddressablesRuntimeDataPath";
        private string _root;
        private string _oldValue;
        private bool _hadValue;
        private string _validGuid;
        private string _wrongGuid;
        private MethodInfo _preflight;

        [SetUp]
        public void SetUp()
        {
            var provider = Type.GetType("AAGame.Assets.AddressablesBackendProvider, AAGame.Assets.Addressables");
            var settingsType = Type.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettings, Unity.Addressables.Editor");
            if (provider == null || settingsType == null)
                Assert.Ignore("未安装 Addressables，保留无 SDK 工程的编译与测试能力。");
            _preflight = provider.GetMethod("PreflightLocalContent", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(_preflight, Is.Not.Null);
            string sentinel = "不存在的会话值-" + Guid.NewGuid().ToString("N");
            _oldValue = SessionState.GetString(RuntimeDataKey, sentinel);
            _hadValue = _oldValue != sentinel;
            _root = "Assets/AATest/Temporary/GuidPreflight-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(_root);
            AssetDatabase.Refresh();
            var settings = ScriptableObject.CreateInstance(settingsType);
            AssetDatabase.CreateAsset(settings, _root + "/Settings.asset");
            _validGuid = AssetDatabase.AssetPathToGUID(_root + "/Settings.asset");
            File.WriteAllText(_root + "/WrongType.txt", "不是 AddressableAssetSettings");
            AssetDatabase.ImportAsset(_root + "/WrongType.txt");
            _wrongGuid = AssetDatabase.AssetPathToGUID(_root + "/WrongType.txt");
            Assert.That(_validGuid, Is.Not.Empty);
            Assert.That(_wrongGuid, Is.Not.Empty);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root == null) return;
            if (_hadValue) SessionState.SetString(RuntimeDataKey, _oldValue);
            else SessionState.EraseString(RuntimeDataKey);
            AssetDatabase.DeleteAsset(_root);
            _root = null;
        }

        private void Preflight(CancellationToken token = default)
        {
            Awaitable operation;
            try
            {
                operation = (Awaitable)_preflight.Invoke(null, new object[] { token });
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
            // 本夹具只使用本地同步读取分支，每个 Awaitable 消费一次，不触发 SDK 初始化。
            operation.GetAwaiter().GetResult();
        }

        [Test]
        public void ValidEditorSettingsGuidDoesNotRequireBuiltJson()
        {
            SessionState.SetString(RuntimeDataKey, "GUID:" + _validGuid);
            Assert.That(File.Exists(_root + "/settings.json"), Is.False);
            Assert.DoesNotThrow(() => Preflight());
        }

        [TestCase("missing")]
        [TestCase("empty")]
        [TestCase("wrong-type")]
        public void InvalidEditorSettingsGuidReportsSettingsError(string mode)
        {
            string guid = mode == "wrong-type" ? _wrongGuid : mode == "empty" ? "" : Guid.NewGuid().ToString("N");
            SessionState.SetString(RuntimeDataKey, "GUID:" + guid);
            var error = Assert.Throws<InvalidOperationException>(() => Preflight());
            Assert.That(error.Message, Does.Contain("编辑器设置资产无效"));
            SessionState.SetString(RuntimeDataKey, "GUID:" + _validGuid);
            Assert.DoesNotThrow(() => Preflight());
        }

        [Test]
        public void PreCanceledEditorGuidRequestIsObserved()
        {
            SessionState.SetString(RuntimeDataKey, "GUID:" + _validGuid);
            Assert.Throws<OperationCanceledException>(() => Preflight(new CancellationToken(true)));
        }

        [Test]
        public void ExistingBuildStillRejectsMissingSettingsFile()
        {
            string path = Path.GetFullPath(_root + "/settings.json");
            SessionState.SetString(RuntimeDataKey, path);
            var error = Assert.Throws<InvalidOperationException>(() => Preflight());
            Assert.That(error.Message, Does.Contain("本地内容不存在"));
        }

        [Test]
        public void ExistingBuildCanRetryAfterMissingCatalogIsRepaired()
        {
            string settingsPath = Path.GetFullPath(_root + "/settings.json");
            string catalogPath = Path.GetFullPath(_root + "/catalog.bin");
            // SDK ResourceManagerRuntimeData/ResourceLocationData 的真实序列化字段。
            var data = new RuntimeDataJson
            {
                m_CatalogLocations = new[] { new CatalogLocationJson { m_InternalId = catalogPath } }
            };
            File.WriteAllText(settingsPath, JsonUtility.ToJson(data));
            SessionState.SetString(RuntimeDataKey, settingsPath);
            var error = Assert.Throws<InvalidOperationException>(() => Preflight());
            Assert.That(error.Message, Does.Contain("本地内容不存在"));
            Assert.That(error.Message, Does.Contain("catalog.bin"));
            File.WriteAllText(catalogPath, "预检仅验证目录项存在，不运行 SDK Catalog 初始化");
            Assert.DoesNotThrow(() => Preflight());
        }

        [Serializable]
        private sealed class RuntimeDataJson { public CatalogLocationJson[] m_CatalogLocations; }
        [Serializable]
        private sealed class CatalogLocationJson { public string m_InternalId; }
    }
}
