#if USE_YOOASSET
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YooAsset;
using YooAsset.Editor;
namespace AAGame.Editor.Assets
{
    public static class YooAssetDemoBuild
    {
        [MenuItem("Tools/项目工具/资源后端/YooAsset/配置离线资源")]
        public static void Configure()
        {
            DemoAssetKeySetup.ValidateUniqueKeys();
            var settings = BundleCollectorSettingData.Setting;
            var package = settings.Packages.FirstOrDefault(p => p.PackageName == "AAGameDemo");
            if (package == null)
            {
                package = new BundleCollectorPackage { PackageName = "AAGameDemo" };
                settings.Packages.Add(package);
            }
            package.EnableAddressable = true;
            var paths = new[] { DemoAssetKeySetup.UiRoot, DemoAssetKeySetup.DataRoot };
            var names = new[] { "UI", "Data" };
            for (int i = 0; i < paths.Length; i++)
            {
                var path = paths[i];
                var matches = package.Groups.Where(g => g.GroupName == "AAGameDemo" || g.GroupName == names[i])
                    .SelectMany(g => g.Collectors.Where(c => c.CollectPath == path).Select(c => new { Group = g, Collector = c })).ToArray();
                if (matches.Length > 1) throw new InvalidOperationException("受管收集器重复，请先核对：" + path);
            }
            for (int i = 0; i < paths.Length; i++)
            {
                var path = paths[i];
                var source = package.Groups.FirstOrDefault(g => (g.GroupName == "AAGameDemo" || g.GroupName == names[i]) && g.Collectors.Any(c => c.CollectPath == path));
                var collector = source?.Collectors.First(c => c.CollectPath == path) ?? new BundleCollector
                {
                    CollectPath = path, CollectorGUID = AssetDatabase.AssetPathToGUID(path),
                    PackRuleName = nameof(PackDirectory), FilterRuleName = nameof(CollectAll)
                };
                var group = package.Groups.FirstOrDefault(g => g.GroupName == names[i]);
                if (group == null)
                {
                    group = new BundleCollectorGroup { GroupName = names[i] };
                    package.Groups.Add(group);
                }
                source?.Collectors.Remove(collector);
                collector.AddressRuleName = nameof(AddressByGroupAndFileName);
                collector.FilterRuleName = i == 0 ? nameof(CollectPrefab) : nameof(CollectAll);
                group.Collectors.Add(collector);
            }
            foreach (var old in package.Groups.Where(g => g.GroupName == "AAGameDemo" && g.Collectors.Count == 0).ToArray())
                package.Groups.Remove(old);
            BundleCollectorSettingData.SaveFile();
            AssetDatabase.SaveAssets();
        }
        [MenuItem("Tools/项目工具/资源后端/YooAsset/构建离线内容")]
        public static void Build()
        {
            Configure();
            var parameters = new ScriptableBuildParameters
            {
                BuildOutputRoot = "Library/AAGameContent/YooAsset", BundledFileRoot = "Assets/StreamingAssets/yoo",
                BuildPipeline = nameof(ScriptableBuildPipeline), BuildBundleType = (int)EBundleType.AssetBundle,
                BuildTarget = EditorUserBuildSettings.activeBuildTarget, PackageName = "AAGameDemo",
                PackageVersion = DateTime.UtcNow.ToString("yyyyMMddHHmmss"),
                VerifyBuildingResult = true, BundledCopyOption = EBundledCopyOption.ClearAndCopyAll,
                CompressOption = ECompressOption.LZ4, BuiltinShadersBundleName = "aagame-shaders", MonoScriptsBundleName = "aagame-scripts"
            };
            var result = new ScriptableBuildPipeline().Run(parameters, true);
            if (!result.Success) throw new InvalidOperationException(result.ErrorInfo);
            AssetDatabase.Refresh();
            Debug.Log("YooAsset 离线资源构建完成：" + result.OutputPackageDirectory);
        }
    }
}
#endif
