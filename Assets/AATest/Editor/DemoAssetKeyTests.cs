using System;
using System.IO;
using System.Linq;
using AAGame.Assets;
using AAGame.Editor.Assets;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public sealed class DemoAssetKeyTests
    {
        [Test]
        public void DifferentExtensionsWithinDataGroupRejectDuplicateLogicalKey()
        {
            var unique = "KeyTest_" + Guid.NewGuid().ToString("N");
            var folder = DemoAssetKeySetup.DataRoot + "/" + unique;
            AssetDatabase.CreateFolder(DemoAssetKeySetup.DataRoot, unique);
            try
            {
                var text = folder + "/" + unique + ".txt";
                var json = folder + "/" + unique + ".json";
                File.WriteAllText(text, "唯一性测试");
                File.WriteAllText(json, "{}");
                AssetDatabase.ImportAsset(text);
                AssetDatabase.ImportAsset(json);
                var error = Assert.Throws<InvalidOperationException>(DemoAssetKeySetup.ValidateUniqueKeys);
                StringAssert.Contains(DemoAssetKeys.Data(unique), error.Message);
                StringAssert.Contains(text, error.Message);
                StringAssert.Contains(json, error.Message);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void SameFileNameAcrossUiAndDataHasDistinctKeys()
        {
            var unique = "KeyTest_" + Guid.NewGuid().ToString("N");
            var uiFolder = DemoAssetKeySetup.UiRoot + "/" + unique;
            var dataFolder = DemoAssetKeySetup.DataRoot + "/" + unique;
            AssetDatabase.CreateFolder(DemoAssetKeySetup.UiRoot, unique);
            AssetDatabase.CreateFolder(DemoAssetKeySetup.DataRoot, unique);
            var host = new GameObject(unique);
            try
            {
                var uiPath = uiFolder + "/" + unique + ".prefab";
                var dataPath = dataFolder + "/" + unique + ".txt";
                PrefabUtility.SaveAsPrefabAsset(host, uiPath);
                File.WriteAllText(dataPath, "同名资源位于不同分组");
                AssetDatabase.ImportAsset(dataPath);
                var entries = DemoAssetKeySetup.GetManagedAssets();
                Assert.That(entries.Single(entry => entry.Path == uiPath).Key, Is.EqualTo(DemoAssetKeys.Ui(unique)));
                Assert.That(entries.Single(entry => entry.Path == dataPath).Key, Is.EqualTo(DemoAssetKeys.Data(unique)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                AssetDatabase.DeleteAsset(uiFolder);
                AssetDatabase.DeleteAsset(dataFolder);
            }
        }
    }
}
