using AAGame.Editor.EnvironmentSetup;
using NUnit.Framework;

namespace ComponentAutoBindTool.Tests
{
    public sealed class EnvironmentInstallerTests
    {
        private static ToolStatus Tool(string name, string version = "1.0.0")
        {
            return new ToolStatus { Name = name, Version = version, Path = "测试路径" };
        }

        [Test]
        public void MissingYooEnvironmentInstallsPrerequisitesInOrder()
        {
            CollectionAssert.AreEqual(new[] { "Node", "OpenUpm", "YooAsset" },
                EnvironmentInstaller.PlanSteps(InstallPreset.YooAsset, new ToolStatus[0]));
        }

        [Test]
        public void ExistingToolsAreReused()
        {
            var tools = new[] { Tool("node", "v24.14.0"), Tool("npm"), Tool("openupm") };
            CollectionAssert.AreEqual(new[] { "YooAsset" }, EnvironmentInstaller.PlanSteps(InstallPreset.YooAsset, tools));
            CollectionAssert.IsEmpty(EnvironmentInstaller.PlanSteps(InstallPreset.OpenUpm, tools));
        }

        [Test]
        public void AddressablesDoesNotRequireNodeOrOpenUpm()
        {
            CollectionAssert.AreEqual(new[] { EnvironmentInstaller.AddressablesId, EnvironmentInstaller.AndroidId },
                EnvironmentInstaller.PlanSteps(InstallPreset.Addressables, new ToolStatus[0]));
        }

        [Test]
        public void EachUnityPackageCanBeInstalledSeparately()
        {
            CollectionAssert.AreEqual(new[] { EnvironmentInstaller.AddressablesId },
                EnvironmentInstaller.PlanSteps(InstallPreset.AddressablesOnly, new ToolStatus[0]));
            CollectionAssert.AreEqual(new[] { EnvironmentInstaller.AndroidId },
                EnvironmentInstaller.PlanSteps(InstallPreset.AndroidOnly, new ToolStatus[0]));
        }

        [Test]
        public void MissingNpmOffersRepairInsteadOfAssumingNodeIsEnough()
        {
            CollectionAssert.AreEqual(new[] { "NodeRepair", "OpenUpm", "YooAsset" },
                EnvironmentInstaller.PlanSteps(InstallPreset.YooAsset, new[] { Tool("node", "v24.14.0") }));
        }

        [TestCase("v24.14.0", true)]
        [TestCase("v16.0.0", false)]
        [TestCase("命令失败", false)]
        public void NodeVersionMustBeParsedAndMeetSupportedBaseline(string version, bool expected)
        {
            Assert.That(EnvironmentInstaller.IsSupportedNode(version), Is.EqualTo(expected));
        }
    }
}
