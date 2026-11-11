using System;
using System.Collections.Generic;
using System.IO;
using AAGame.Assets;
using UnityEditor;

namespace AAGame.Editor.Assets
{
    public readonly struct DemoAssetEntry
    {
        public readonly string Path;
        public readonly string Key;
        public readonly bool IsUi;
        public DemoAssetEntry(string path, string key, bool isUi)
        {
            Path = path;
            Key = key;
            IsUi = isUi;
        }
    }

    /// <summary>仅扫描演示受管输入，不收集 Resources 副本或 SDK 构建输出。</summary>
    public static class DemoAssetKeySetup
    {
        public const string UiRoot = "Assets/AAGame/UI/Prefabs";
        public const string DataRoot = "Assets/AAGame/Content";
        public static void ValidateUniqueKeys() => GetManagedAssets();
        public static IReadOnlyList<DemoAssetEntry> GetManagedAssets()
        {
            var entries = new List<DemoAssetEntry>();
            Collect(UiRoot, "t:Prefab", true, entries);
            Collect(DataRoot, "", false, entries);
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (sources.TryGetValue(entry.Key, out var previous))
                    throw new InvalidOperationException($"演示资源 Key 重复：{entry.Key}。同组忽略扩展名及大小写，请重命名后重试。\n{previous}\n{entry.Path}");
                sources.Add(entry.Key, entry.Path);
            }
            entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
            return entries;
        }
        private static void Collect(string root, string filter, bool isUi, List<DemoAssetEntry> entries)
        {
            if (!AssetDatabase.IsValidFolder(root)) return;
            foreach (var guid in AssetDatabase.FindAssets(filter, new[] { root }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path)) continue;
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                entries.Add(new DemoAssetEntry(path, isUi ? DemoAssetKeys.Ui(name) : DemoAssetKeys.Data(name), isUi));
            }
        }
    }
}
