using System;
using ComponentAutoBindTool.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public class AutoBindPathTests
    {
        // 注入路径规则验证词法行为；这些用例不代表 Unix Unity 实机验证。
        [TestCase("E:/Project/Assets", true, "Assets/UI", "E:/Project/Assets/UI")]
        [TestCase("E:/Project/Assets", true, "/Third Party/UI", "E:/Project/Assets/Third Party/UI")]
        [TestCase("E:/Project/Assets", true, "/tmp/Output", "E:/Project/Assets/tmp/Output")]
        [TestCase("E:/Project/Assets", true, "D:\\Output\\UI", "D:/Output/UI")]
        [TestCase("E:/Project/Assets", true, "\\\\server\\share\\Output", "//server/share/Output")]
        [TestCase("/work/Project/Assets", false, "Assets/UI", "/work/Project/Assets/UI")]
        [TestCase("/work/Project/Assets", false, "/tmp/Output", "/work/Project/Assets/tmp/Output")]
        [TestCase("/work/Project/Assets", false, "/Third Party/UI", "/work/Project/Assets/Third Party/UI")]
        [TestCase("/work/Project/Assets", false, "/work/Project/Assets/UI", "/work/Project/Assets/UI")]
        [TestCase("/work/Project/Assets", false, "/work/Project/AssetsBackup/UI", "/work/Project/AssetsBackup/UI")]
        [TestCase("/work/Project/Assets", false, "/work/Project/Assets/../Outside", "/work/Project/Outside")]
        [TestCase("/work/Project/Assets", false, "Assets/../Outside", "/work/Project/Outside")]
        [TestCase("/work/Project/Assets", false, "D:/Output", "D:/Output")]
        [TestCase("/work/Project/Assets", false, "Assets/UI:variant?*", "/work/Project/Assets/UI:variant?*")]
        [TestCase("//server/share/Project/Assets", true, "Assets/UI", "//server/share/Project/Assets/UI")]
        public void ConfigurationResolutionUsesInjectedRootAndPlatform(string assets, bool windows, string input, string expected)
        {
            Assert.That(AutoBindPathUtility.NormalizeCodePath(input, assets, windows), Is.EqualTo(expected));
        }

        [TestCase("E:/Project/Assets", true, "e:/project/assets/UI", true)]
        [TestCase("E:/Project/Assets", true, "E:/Project/AssetsBackup/UI", false)]
        [TestCase("E:/Project/Assets", true, "E:/Project/Assets/../Outside", false)]
        [TestCase("E:/Project/Assets", true, "Assets/UI", false)]
        [TestCase("E:/Project/Assets", true, "D:/Output", false)]
        [TestCase("/work/Project/Assets", false, "/work/Project/Assets/UI", true)]
        [TestCase("/work/Project/Assets", false, "/work/project/assets/UI", false)]
        [TestCase("/work/Project/Assets", false, "/work/Project/AssetsBackup/UI", false)]
        [TestCase("/work/Project/Assets", false, "/tmp/Output", false)]
        [TestCase("/work/Project/Assets", false, "/work/Project/Assets/../Outside", false)]
        [TestCase("//server/share/Project/Assets", true, "\\\\SERVER\\SHARE\\Project\\Assets\\UI", true)]
        [TestCase("//server/share/Project/Assets", true, "//server/other/Project/Assets/UI", false)]
        public void AbsoluteBoundaryUsesInjectedPlatformAndRejectsExternalPickerPaths(string assets, bool windows, string input, bool expected)
        {
            Assert.That(AutoBindPathUtility.IsPathUnderAssets(input, assets, windows), Is.EqualTo(expected));
        }

        [TestCase("E:/Project/Assets", true, "E:/Project/Assets/UI/../Generated", "Assets/Generated")]
        [TestCase("E:/Project/Assets", true, "E:/Project/Assets/../Outside", "E:/Project/Outside")]
        [TestCase("/work/Project/Assets", false, "/work/Project/Assets", "Assets")]
        [TestCase("/work/Project/Assets", false, "/work/Project/Assets/UI/../Generated", "Assets/Generated")]
        [TestCase("/work/Project/Assets", false, "/work/Project/Assets/../Outside", "/work/Project/Outside")]
        [TestCase("/work/Project/Assets", false, "/tmp/Output", "/tmp/Output")]
        [TestCase("//server/share/Project/Assets", true, "//server/share/Project/Assets/UI", "Assets/UI")]
        public void AssetConversionNeverReinterpretsAbsolutePaths(string assets, bool windows, string input, string expected)
        {
            Assert.That(AutoBindPathUtility.ToAssetRelativePath(input, assets, windows), Is.EqualTo(expected));
        }

        [TestCase("E:relative")]
        [TestCase("//server")]
        [TestCase("//server/../Output")]
        [TestCase("//server/sh?re/Output")]
        [TestCase("Assets/UI:stream")]
        [TestCase("Assets/Invalid?Name")]
        [TestCase("Assets/Invalid*Name")]
        [TestCase("Assets/Invalid|Name")]
        [TestCase("Assets/Invalid<Name")]
        [TestCase("Assets/Invalid>Name")]
        [TestCase("Assets/Invalid\"Name")]
        [TestCase("Assets/Bad\0Path")]
        public void InvalidConfigurationPathsAreRejected(string input)
        {
            Assert.Throws<ArgumentException>(() => AutoBindPathUtility.NormalizeCodePath(input, "E:/Project/Assets", true));
            Assert.That(AutoBindPathUtility.IsPathUnderAssets(input, "E:/Project/Assets", true), Is.False);
        }

        [TestCase("Assets/Generated")]
        [TestCase("Generated")]
        [TestCase("/tmp/Output")]
        public void AbsoluteBoundaryDoesNotInterpretConfigurationPaths(string path)
        {
            Assert.That(AutoBindPathUtility.IsPathUnderAssets(path), Is.False);
        }

        [TestCase("Assets/Generated")]
        [TestCase("Generated")]
        public void AssetRelativeConversionRequiresAnAbsolutePath(string path)
        {
            Assert.Throws<ArgumentException>(() => AutoBindPathUtility.ToAssetRelativePath(path));
        }

        [Test]
        public void ProjectAbsolutePathRemainsAbsoluteAndRejectsAssetsSibling()
        {
            string assets = Application.dataPath.Replace('\\', '/');
            string inside = assets + "/Generated";
            string sibling = assets + "Backup/Generated";

            Assert.That(AutoBindPathUtility.NormalizeCodePath(inside), Is.EqualTo(inside));
            Assert.That(AutoBindPathUtility.NormalizeCodePath(sibling), Is.EqualTo(sibling));
            Assert.That(AutoBindPathUtility.IsPathUnderAssets(inside), Is.True);
            Assert.That(AutoBindPathUtility.IsPathUnderAssets(sibling), Is.False);
            Assert.That(AutoBindPathUtility.ToAssetRelativePath(inside), Is.EqualTo("Assets/Generated"));
            Assert.That(AutoBindPathUtility.ToAssetRelativePath(sibling), Is.EqualTo(sibling));
        }

        [Test]
        public void ConfigurationAcceptsBroadLegacyLeadingSlash()
        {
            Assert.That(AutoBindPathUtility.NormalizeCodePath("/tmp/Output"),
                Is.EqualTo(Application.dataPath.Replace('\\', '/') + "/tmp/Output"));
        }

        [Test]
        public void NewSettingsFollowScriptDirectoryAndStoreAssetsRelativeDefault()
        {
            var setting = ScriptableObject.CreateInstance<AutoBindGlobalSetting>();
            try
            {
                Assert.That(setting.UseGlobalDefaultSavePath, Is.True);
                using (var serialized = new SerializedObject(setting))
                {
                    Assert.That(serialized.FindProperty("m_CodePath").stringValue,
                        Is.EqualTo("Assets/Third Party/ComponentAutoBindTool/Scripts/ViewBindComponents"));
                }
                Assert.That(AutoBindPathUtility.GetInspectorCodePath(setting, "Assets/Feature/Host.cs"),
                    Is.EqualTo("Assets/Feature"));
                Assert.That(setting.CodePath,
                    Is.EqualTo(Application.dataPath.Replace('\\', '/') + "/Third Party/ComponentAutoBindTool/Scripts/ViewBindComponents"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(setting);
            }
        }
    }
}
