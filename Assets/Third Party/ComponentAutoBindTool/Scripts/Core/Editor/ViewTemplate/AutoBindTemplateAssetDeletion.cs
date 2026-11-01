using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;

namespace ComponentAutoBindTool.Editor
{
    // 仅承接磁盘事务；调用者负责 AssetEditing 批次和任务状态，不包含任何场景或 Undo 操作。
    internal static class AutoBindTemplateAssetDeletion
    {
        [Serializable]
        internal sealed class Entry
        {
            public string Path, Hash, MetaHash;
            internal bool HasFile => !string.IsNullOrEmpty(Hash);
            internal bool HasMeta => !string.IsNullOrEmpty(MetaHash);
            internal bool Exists => HasFile || HasMeta;
        }

        [Serializable]
        internal sealed class Snapshot
        {
            public List<Entry> Entries = new();
            public string EmptyDirectory, DirectoryMetaHash;
            internal bool HasEmptyDirectory => !string.IsNullOrEmpty(EmptyDirectory);
            internal bool HasAnything => Entries.Any(entry => entry.Exists) || HasEmptyDirectory;
            internal bool DeletesScripts => Entries.Any(entry => entry.HasFile && entry.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
            internal IEnumerable<string> ExistingAssets => Entries.Where(entry => entry.Exists).Select(entry => entry.Path);
            internal IEnumerable<string> PhysicalFiles => Entries.SelectMany(entry =>
                (entry.HasFile ? new[] { entry.Path } : Array.Empty<string>()).Concat(
                    entry.HasMeta ? new[] { entry.Path + ".meta" } : Array.Empty<string>()))
                .Concat(HasEmptyDirectory && !string.IsNullOrEmpty(DirectoryMetaHash) ? new[] { EmptyDirectory + ".meta" } : Array.Empty<string>());

            internal void AssertUnchanged()
            {
                foreach (var entry in Entries)
                {
                    NormalizeFile(entry.Path);
                    if (Directory.Exists(entry.Path) || HashIfExists(entry.Path) != (entry.Hash ?? "") || HashIfExists(entry.Path + ".meta") != (entry.MetaHash ?? ""))
                        throw new IOException("确认清单后产物已变化，请刷新清单并重新确认：" + entry.Path);
                }
                if (!HasEmptyDirectory) return;
                AutoBindViewTemplateDefinition.NormalizeFolder(EmptyDirectory);
                var allowed = new HashSet<string>(PhysicalFiles, StringComparer.OrdinalIgnoreCase);
                bool changed = !Directory.Exists(EmptyDirectory) || HashIfExists(EmptyDirectory + ".meta") != (DirectoryMetaHash ?? "") ||
                    Directory.EnumerateFileSystemEntries(EmptyDirectory).Any(path => !allowed.Contains(path.Replace('\\', '/')));
                if (changed)
                    throw new IOException("脚本目录内容已变化，请刷新清单并重新确认：" + EmptyDirectory);
            }
        }

        internal static Snapshot Capture(IEnumerable<string> assets, string emptyDirectory = null)
        {
            var snapshot = new Snapshot();
            foreach (string path in assets.Select(NormalizeFile).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.Exists(path)) throw new IOException("目标位置是目录而非产物文件：" + path);
                snapshot.Entries.Add(new Entry { Path = path, Hash = HashIfExists(path), MetaHash = HashIfExists(path + ".meta") });
            }
            if (emptyDirectory != null)
            {
                string folder = AutoBindViewTemplateDefinition.NormalizeFolder(emptyDirectory);
                if (folder == "Assets") throw new ArgumentException("不能删除 Assets 根目录。");
                var allowed = new HashSet<string>(snapshot.PhysicalFiles, StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).All(path => allowed.Contains(path.Replace('\\', '/'))))
                {
                    snapshot.EmptyDirectory = folder;
                    snapshot.DirectoryMetaHash = HashIfExists(folder + ".meta");
                }
            }
            return snapshot;
        }

        internal static string NormalizeFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("产物路径不能为空。");
            string full = Path.GetFullPath(path.Trim());
            string folder = AutoBindViewTemplateDefinition.NormalizeFolder(Path.GetDirectoryName(full));
            string result = folder + "/" + Path.GetFileName(full);
            foreach (string candidate in new[] { result, result + ".meta" })
                if ((File.Exists(candidate) || Directory.Exists(candidate)) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("产物不能是链接或重解析文件：" + candidate);
            return result;
        }

        private static string HashIfExists(string path)
        {
            if (Directory.Exists(path)) throw new IOException("产物或元数据位置是目录：" + path);
            if (!File.Exists(path)) return "";
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("产物不能是链接文件：" + path);
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
        }

        internal static void Delete(Snapshot snapshot, out string backupPath)
        {
            backupPath = null;
            snapshot.AssertUnchanged();
            if (!snapshot.HasAnything) throw new InvalidOperationException("没有可删除的产物。");
            foreach (string path in snapshot.PhysicalFiles)
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) { }
            backupPath = Path.GetFullPath("Library/AutoBindViewTemplate/Cleared/" + Guid.NewGuid().ToString("N"));
            foreach (string path in snapshot.PhysicalFiles)
            {
                string saved = Path.Combine(backupPath, path);
                Directory.CreateDirectory(Path.GetDirectoryName(saved));
                File.Copy(path, saved);
            }
            // 空目录也需要一个可定位的备份根目录。
            Directory.CreateDirectory(backupPath);
            snapshot.AssertUnchanged();
            try
            {
                foreach (var entry in snapshot.Entries.AsEnumerable().Reverse())
                {
                    if (entry.HasFile)
                    {
                        if (!AssetDatabase.DeleteAsset(entry.Path)) throw new IOException("无法删除产物：" + entry.Path);
                    }
                    else if (entry.HasMeta) File.Delete(entry.Path + ".meta");
                    if (File.Exists(entry.Path) || File.Exists(entry.Path + ".meta")) throw new IOException("产物未被完整删除：" + entry.Path);
                }
                if (snapshot.HasEmptyDirectory)
                {
                    // 使用非递归删除，防止确认后外部进程加入的新文件被一并删除。
                    Directory.Delete(snapshot.EmptyDirectory, false);
                    if (!string.IsNullOrEmpty(snapshot.DirectoryMetaHash)) File.Delete(snapshot.EmptyDirectory + ".meta");
                }
            }
            catch (Exception failure)
            {
                var errors = new List<string>();
                if (snapshot.HasEmptyDirectory)
                    try { Directory.CreateDirectory(snapshot.EmptyDirectory); }
                    catch (Exception error) { errors.Add(error.Message); }
                foreach (string path in snapshot.PhysicalFiles)
                {
                    if (File.Exists(path)) continue;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.Copy(Path.Combine(backupPath, path), path);
                    }
                    catch (Exception error) { errors.Add(error.Message); }
                }
                // 先恢复文件夹和全部元数据，再导入，保留文件夹及文件 GUID。
                foreach (string path in (snapshot.HasEmptyDirectory ? new[] { snapshot.EmptyDirectory } : Array.Empty<string>()).Concat(snapshot.ExistingAssets))
                    try { AssetDatabase.ImportAsset(path); }
                    catch (Exception error) { errors.Add(error.Message); }
                throw new IOException("删除失败，已尝试恢复缺失文件。备份：" + backupPath + "\n" + failure.Message + "\n" + string.Join("\n", errors), failure);
            }
        }
    }
}
