using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AAGame.Assets;
using UnityEditor;
using UnityEngine;

/// <summary>后端配置的可校验磁盘归档；不引用任何可选 SDK 类型。</summary>
public static class BackendProfileStore
{
    public const string ProfileRoot = "ResourceBackendProfiles";
    private const string AddressablesKey = "com.unity.addressableassets";
    private static readonly string Project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    private static readonly string[] ConfigurationRoots =
    {
        "Assets/AAGame/Resources/AAGameDemo", "Assets/AddressableAssetsData", "Assets/BundleCollectorSetting.asset"
    };
    private static readonly string[] SnapshotRoots = ConfigurationRoots.Concat(new[]
    {
        ProfileRoot, "Packages/manifest.json", "Packages/packages-lock.json", "ProjectSettings/EditorBuildSettings.asset",
        "Assets/AAGame/Settings/AssetBackendSettings.asset"
    }).ToArray();

    [Serializable] private sealed class Entry { public string Path; public string Sha256; }
    [Serializable] private sealed class Record
    {
        public int Format = 2;
        public string AddressablesGuid;
        public string[] Roots;
        public List<Entry> Files = new();
    }

    public static void CaptureSnapshot(string directory)
    {
        string destination = SnapshotDirectory(directory);
        if (File.Exists(Path.Combine(destination, "manifest.json")))
            throw new IOException("事务快照已存在，不能覆盖：" + destination);
        WriteRecord(destination, SnapshotRoots, false);
    }

    public static void RestoreSnapshot(string directory)
    {
        string source = SnapshotDirectory(directory);
        var record = ReadRecord(source);
        Verify(source, record);
        if (!record.Roots.SequenceEqual(SnapshotRoots))
            throw new IOException("快照根目录与当前工具不匹配。");
        // 回滚前另存当前状态，保留切换中途可能产生的编辑，避免丢失恢复线索。
        string recovery = Absolute("Library/ProjectToolsBackendSwitch/Recoveries/" + Guid.NewGuid().ToString("N"));
        WriteRecord(recovery, SnapshotRoots, false);
        var recoveryRecord = ReadRecord(recovery);
        foreach (string root in SnapshotRoots)
            DeleteManaged(root, recoveryRecord.Files.Where(e => Belongs(e.Path, root)).ToArray());
        RestoreFiles(source, record);
        AssetDatabase.Refresh();
    }

    public static void RestoreSnapshotReferences(string directory)
    {
        // 此步骤由协调器在旧 SDK 依赖和程序集恢复后调用。
        var record = ReadRecord(SnapshotDirectory(directory));
        RestoreAddressablesReference(record.AddressablesGuid);
    }

    public static void ArchiveInactive(AssetBackendKind target)
    {
        RequireKind(target);
        foreach (AssetBackendKind kind in Enum.GetValues(typeof(AssetBackendKind)))
        {
            if (kind == target) continue;
            Archive(kind);
            CleanOutputs(kind);
        }
        AssetDatabase.Refresh();
    }

    public static void RestoreTarget(AssetBackendKind target)
    {
        RequireKind(target);
        if (target == AssetBackendKind.Resources) MigrateResourcesProfile();
        string folder = Absolute(ProfileRoot + "/" + target);
        if (!File.Exists(Path.Combine(folder, "manifest.json"))) return;
        var record = ReadRecord(folder);
        string root = RootFor(target);
        if (record.Roots.Length != 1 || record.Roots[0] != root)
            throw new IOException("归档后端根目录不匹配：" + target);
        Verify(folder, record);
        // 配置恢复不能覆盖重新创建或手工编辑的文件。
        foreach (var entry in record.Files)
        {
            string live = Absolute(entry.Path);
            if (File.Exists(live) && Hash(live) != entry.Sha256)
                throw new IOException("目标位置已有不同内容，请先处理冲突：" + entry.Path);
        }
        RestoreFiles(folder, record);
        AssetDatabase.Refresh();
        if (target == AssetBackendKind.Addressables)
            RestoreAddressablesReference(record.AddressablesGuid);
    }

    public static void RestoreMissingResources()
    {
        MigrateResourcesProfile();
        string folder = Absolute(ProfileRoot + "/Resources");
        if (!File.Exists(Path.Combine(folder, "manifest.json"))) return;
        var record = ReadRecord(folder);
        if (record.Roots.Length != 1 || record.Roots[0] != RootFor(AssetBackendKind.Resources))
            throw new IOException("Resources 归档根目录不匹配。");
        RestoreMissingFiles(folder, record);
        AssetDatabase.Refresh();
    }

    public static void SaveCurrentProfile(AssetBackendKind kind)
    {
        RequireKind(kind);
        string root = RootFor(kind);
        if (!Directory.Exists(Absolute(root)) && !File.Exists(Absolute(root)))
            throw new IOException("当前后端没有可保存的配置：" + kind);
        string folder = Absolute(ProfileRoot + "/" + kind);
        if (File.Exists(Path.Combine(folder, "manifest.json")))
        {
            Verify(folder, ReadRecord(folder));
            BackupProfile(folder);
        }
        WriteRecord(folder, new[] { root }, kind == AssetBackendKind.Addressables);
    }

    public static void MigrateResourcesProfile()
    {
        MigrateResourcesProfileAt(Absolute(ProfileRoot + "/Resources"), RootFor(AssetBackendKind.Resources));
    }

    private static void MigrateResourcesProfileAt(string folder, string root)
    {
        if (!File.Exists(Path.Combine(folder, "manifest.json"))) return;
        string originalManifestHash = Hash(Path.Combine(folder, "manifest.json"));
        var original = ReadRecord(folder);
        Verify(folder, original);
        if (original.Roots.Length != 1 || original.Roots[0] != root)
            throw new IOException("Resources 归档根目录不匹配。");
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [root + "/Data/DemoText.txt"] = root + "/Data_DemoText.txt"
        };
        foreach (string view in new[] { "StartMenuView", "MainRootMenuView", "LevelHudView", "LevelLoadingView" })
            mapping[root + "/UI/" + view + ".prefab"] = root + "/UI_" + view + ".prefab";
        foreach (var pair in mapping.ToArray()) mapping[pair.Key + ".meta"] = pair.Value + ".meta";
        var revised = new Record { Roots = original.Roots, AddressablesGuid = original.AddressablesGuid };
        foreach (var entry in original.Files)
            revised.Files.Add(new Entry
            {
                Path = mapping.TryGetValue(entry.Path, out var target) ? target : entry.Path,
                Sha256 = entry.Sha256
            });
        if (revised.Files.GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new IOException("Resources 归档中旧路径与统一 Key 路径冲突，停止迁移。");
        foreach (string child in new[] { "UI", "Data" })
        {
            string directory = root + "/" + child;
            if (!revised.Files.Any(entry => entry.Path.StartsWith(directory + "/", StringComparison.Ordinal)))
                revised.Files.RemoveAll(entry => entry.Path == directory + ".meta");
        }
        if (original.Format == 2 && original.Files.Select(entry => entry.Path).SequenceEqual(revised.Files.Select(entry => entry.Path)))
            return;
        string stage = Absolute("Library/ProjectToolsBackendSwitch/ProfileMigrations/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(stage, "files"));
        foreach (var entry in revised.Files)
        {
            var source = original.Files.First(item => item.Sha256 == entry.Sha256);
            File.Copy(StoragePath(folder, original, source), StoragePath(stage, revised, entry), true);
        }
        Verify(stage, revised);
        File.WriteAllText(Path.Combine(stage, "manifest.json"), JsonUtility.ToJson(revised, true));
        BackupProfile(folder);
        // 发布前再次核验旧档；只追加 payload，不删除旧 payload 或用户文件。
        Verify(folder, original);
        foreach (var entry in revised.Files)
        {
            string target = StoragePath(folder, revised, entry);
            if (File.Exists(target) && Hash(target) != entry.Sha256)
                throw new IOException("Resources 归档 payload 冲突：" + target);
            if (!File.Exists(target)) File.Copy(StoragePath(stage, revised, entry), target, false);
        }
        Verify(folder, revised);
        if (Hash(Path.Combine(folder, "manifest.json")) != originalManifestHash)
            throw new IOException("Resources 归档清单在迁移期间发生变化，停止发布。");
        string pending = Path.Combine(folder, "manifest.json.migrate-" + Guid.NewGuid().ToString("N"));
        File.Copy(Path.Combine(stage, "manifest.json"), pending);
        File.Replace(pending, Path.Combine(folder, "manifest.json"), null);
    }

    private static void BackupProfile(string folder)
    {
        string backup = Absolute("Library/ProjectToolsBackendSwitch/ProfileBackups/" + Guid.NewGuid().ToString("N"));
        WriteRecord(backup, new[] { Relative(Path.GetFullPath(folder)) }, false);
    }

    public static void CleanOutputs(AssetBackendKind kind)
    {
        RequireKind(kind);
        if (kind == AssetBackendKind.Addressables)
        {
            DeleteManaged("Library/com.unity.addressables/aa");
            // SDK 的当前平台内容状态是可重建的，其他缓存、报告及全局缓存保留。
            string states = Absolute("Library/com.unity.addressables/Android");
            if (Directory.Exists(states))
                foreach (var file in Directory.GetFiles(states, "addressables_content_state.bin", SearchOption.TopDirectoryOnly))
                    File.Delete(file);
        }
        if (kind == AssetBackendKind.YooAsset)
        {
            DeleteManaged("Assets/StreamingAssets/yoo/AAGameDemo");
            DeleteManaged("Library/AAGameContent/YooAsset");
        }
    }

    private static void Archive(AssetBackendKind kind)
    {
        string root = RootFor(kind);
        if (!File.Exists(Absolute(root)) && !Directory.Exists(Absolute(root)) && !File.Exists(Absolute(root) + ".meta")) return;
        string folder = Absolute(ProfileRoot + "/" + kind);
        // 若旧归档已存在，先核验其完整性，破损备份不能被新操作悄悄覆盖。
        if (File.Exists(Path.Combine(folder, "manifest.json"))) Verify(folder, ReadRecord(folder));
        var originals = Inventory(root).Select(path => new Entry { Path = Relative(path), Sha256 = Hash(path) }).ToArray();
        WriteRecord(folder, new[] { root }, kind == AssetBackendKind.Addressables);
        if (kind == AssetBackendKind.Addressables) EditorBuildSettings.RemoveConfigObject(AddressablesKey);
        DeleteManaged(root, originals);
    }

    private static void WriteRecord(string folder, string[] roots, bool omitBuildState)
    {
        var record = new Record { Roots = roots, AddressablesGuid = AddressablesGuid() };
        var files = roots.SelectMany(Inventory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string file in files)
        {
            string fileName = Path.GetFileName(file);
            if (omitBuildState && (fileName.Equals("addressables_content_state.bin", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("addressables_content_state.bin.meta", StringComparison.OrdinalIgnoreCase))) continue;
            string relative = Relative(file);
            var entry = new Entry { Path = relative, Sha256 = Hash(file) };
            // 扁平内容寻址避免“快照/归档/原路径”嵌套超过 Windows 路径长度；原路径仍在清单内。
            string output = StoragePath(folder, record, entry);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.Copy(file, output, true);
            record.Files.Add(entry);
        }
        Verify(folder, record);
        Directory.CreateDirectory(folder);
        string manifest = Path.Combine(folder, "manifest.json");
        string pending = manifest + ".pending";
        File.WriteAllText(pending, JsonUtility.ToJson(record, true));
        if (File.Exists(manifest)) File.Replace(pending, manifest, null);
        else File.Move(pending, manifest);
    }

    private static string AddressablesGuid()
    {
        return EditorBuildSettings.TryGetConfigObject<UnityEngine.Object>(AddressablesKey, out var value) && value != null
            ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value)) : "";
    }

    private static void RestoreAddressablesReference(string guid)
    {
        EditorBuildSettings.RemoveConfigObject(AddressablesKey);
        if (string.IsNullOrEmpty(guid)) return;
        string path = AssetDatabase.GUIDToAssetPath(guid);
        var value = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
        if (value == null) throw new IOException("Addressables 配置 GUID 未恢复：" + guid);
        EditorBuildSettings.AddConfigObject(AddressablesKey, value, true);
    }

    private static Record ReadRecord(string folder)
    {
        var record = JsonUtility.FromJson<Record>(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        if (record == null || (record.Format != 1 && record.Format != 2) || record.Roots == null || record.Files == null)
            throw new IOException("不支持或损坏的后端归档：" + folder);
        return record;
    }

    private static void Verify(string folder, Record record)
    {
        if (Directory.Exists(folder)) RejectLinks(folder);
        foreach (var entry in record.Files)
        {
            ValidateEntry(entry.Path, record.Roots);
            string stored = StoragePath(folder, record, entry);
            if (!stored.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(stored) || Hash(stored) != entry.Sha256)
                throw new IOException("归档 SHA-256 校验失败：" + entry.Path);
        }
    }

    private static void RestoreFiles(string folder, Record record)
    {
        Verify(folder, record);
        foreach (var entry in record.Files)
        {
            string destination = Absolute(entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(StoragePath(folder, record, entry), destination, true);
        }
    }

    private static void RestoreMissingFiles(string folder, Record record)
    {
        Verify(folder, record);
        foreach (var entry in record.Files)
        {
            string destination = Absolute(entry.Path);
            if (File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(StoragePath(folder, record, entry), destination, false);
        }
    }

    private static string StoragePath(string folder, Record record, Entry entry)
    {
        if (record.Format == 1) return Path.GetFullPath(Path.Combine(folder, "files", entry.Path));
        if (entry.Sha256 == null || entry.Sha256.Length != 64 || entry.Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new IOException("归档中的 SHA-256 格式无效：" + entry.Path);
        return Path.GetFullPath(Path.Combine(folder, "files", entry.Sha256.ToLowerInvariant()));
    }

    private static IEnumerable<string> Inventory(string relative)
    {
        string path = Absolute(relative);
        if (File.Exists(path)) yield return path;
        if (File.Exists(path + ".meta"))
        {
            if ((File.GetAttributes(path + ".meta") & FileAttributes.ReparsePoint) != 0)
                throw new IOException("归档元数据不能是链接：" + path + ".meta");
            yield return path + ".meta";
        }
        if (!Directory.Exists(path)) yield break;
        RejectLinks(path);
        foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("归档文件不能是链接：" + file);
            yield return file;
        }
    }

    private static void DeleteManaged(string relative, Entry[] expected = null)
    {
        bool allowed = SnapshotRoots.Contains(relative) || relative == "Library/com.unity.addressables/aa" ||
            relative == "Assets/StreamingAssets/yoo/AAGameDemo" || relative == "Library/AAGameContent/YooAsset";
        if (!allowed) throw new IOException("拒绝清理非托管目录：" + relative);
        string root = Absolute(relative);
        var inventory = expected ?? Inventory(relative).Select(path => new Entry { Path = Relative(path), Sha256 = Hash(path) }).ToArray();
        // 收集后重新校验，拒绝在归档期间被修改或新加入的文件。
        var current = Inventory(relative).ToArray();
        if (!current.OrderBy(p => p).SequenceEqual(inventory.Select(p => Absolute(p.Path)).OrderBy(p => p)) ||
            inventory.Any(file => !File.Exists(Absolute(file.Path)) || Hash(Absolute(file.Path)) != file.Sha256))
            throw new IOException("清理目标正在变化：" + relative);
        foreach (var file in inventory) File.Delete(Absolute(file.Path));
        if (Directory.Exists(root))
        {
            foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
                Directory.Delete(directory, false);
            Directory.Delete(root, false);
        }
    }

    private static void RejectLinks(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("归档目录不能是链接：" + root);
        foreach (string file in Directory.GetFiles(root))
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("归档文件不能是链接：" + file);
        foreach (string directory in Directory.GetDirectories(root)) RejectLinks(directory);
    }

    private static string RootFor(AssetBackendKind kind) => ConfigurationRoots[(int)kind];
    private static void RequireKind(AssetBackendKind kind)
    {
        if (!Enum.IsDefined(typeof(AssetBackendKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
    }
    private static string Relative(string absolute) => absolute.Substring(Project.Length + 1).Replace('\\', '/');
    private static string Absolute(string relative)
    {
        if (relative.Split('/', '\\').Any(segment => segment == "." || segment == ".."))
            throw new IOException("托管路径不能包含跳转段：" + relative);
        string result = Path.GetFullPath(Path.Combine(Project, relative));
        if (Path.IsPathRooted(relative) || !result.StartsWith(Project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("路径不在当前工程内：" + relative);
        for (string ancestor = result; ancestor.Length > Project.Length; ancestor = Path.GetDirectoryName(ancestor))
            if ((File.Exists(ancestor) || Directory.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("托管路径不能经过文件系统链接：" + relative);
        return result;
    }
    private static string SnapshotDirectory(string directory)
    {
        string value = Path.GetFullPath(Path.IsPathRooted(directory) ? directory : Path.Combine(Project, directory));
        if (!value.StartsWith(Path.Combine(Project, "Library") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("事务快照必须位于当前工程 Library 内。");
        return Absolute(Relative(value));
    }
    private static void ValidateEntry(string relative, string[] roots)
    {
        Absolute(relative);
        if (!roots.Any(root => Belongs(relative, root)))
            throw new IOException("归档文件越出托管范围：" + relative);
    }
    private static bool Belongs(string relative, string root) =>
        relative == root || relative == root + ".meta" || relative.StartsWith(root + "/", StringComparison.Ordinal);
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
