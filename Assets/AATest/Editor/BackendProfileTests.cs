using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public sealed class BackendProfileTests
    {
        private string _root;
        private string _source;
        private string _archive;

        [SetUp]
        public void SetUp()
        {
            _root = "Library/AAGameValidation/ProfileTests-" + Guid.NewGuid().ToString("N");
            _source = _root + "/Source";
            _archive = Path.GetFullPath(_root + "/Archive");
            Directory.CreateDirectory(_source);
            File.WriteAllText(_source + "/data.asset", "资源配置");
            File.WriteAllText(_source + "/data.asset.meta", "guid: 1234567890abcdef1234567890abcdef\n");
            File.WriteAllText(_source + ".meta", "guid: abcdef1234567890abcdef1234567890\n");
        }

        [TearDown]
        public void TearDown()
        {
            string absolute = Path.GetFullPath(_root);
            string boundary = Path.GetFullPath("Library/AAGameValidation") + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                throw new IOException("测试目录越界，停止清理。");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }

        // 文件事务内核在独立 Library 夹中验证，避免测试改动真实 SDK 配置。
        private static object Invoke(string name, params object[] args)
        {
            try
            {
                return typeof(BackendProfileStore).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, args);
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        [Test]
        public void VerifiedArchivePreservesBytesAndFolderAndAssetGuids()
        {
            Invoke("WriteRecord", _archive, new[] { _source }, false);
            foreach (string relative in new[] { _source + ".meta", _source + "/data.asset", _source + "/data.asset.meta" })
                CollectionAssert.AreEqual(File.ReadAllBytes(relative), File.ReadAllBytes(Payload(relative)));
            Assert.That(File.Exists(_source + "/data.asset"), Is.True);
        }

        [Test]
        public void CorruptedArchiveFailsBeforeSourceIsTouched()
        {
            Invoke("WriteRecord", _archive, new[] { _source }, false);
            File.WriteAllText(Payload(_source + "/data.asset"), "损坏");
            var record = Invoke("ReadRecord", _archive);
            Assert.Throws<IOException>(() => Invoke("Verify", _archive, record));
            Assert.That(File.ReadAllText(_source + "/data.asset"), Is.EqualTo("资源配置"));
        }

        [Test]
        public void MissingFileRepairPreservesNewerLiveFilesAndRestoresMeta()
        {
            Invoke("WriteRecord", _archive, new[] { _source }, false);
            File.WriteAllText(_source + "/data.asset", "更新后的内容");
            File.Delete(_source + "/data.asset.meta");
            Invoke("RestoreMissingFiles", _archive, Invoke("ReadRecord", _archive));
            Assert.That(File.ReadAllText(_source + "/data.asset"), Is.EqualTo("更新后的内容"));
            Assert.That(File.ReadAllText(_source + "/data.asset.meta"), Does.Contain("1234567890abcdef1234567890abcdef"));
        }

        [Test]
        public void ArchiveRejectsPathTraversalEvenWhenItReturnsInsideProject()
        {
            Assert.Throws<IOException>(() => Invoke("ValidateEntry", _source + "/../Elsewhere.asset", new[] { _source }));
            Assert.Throws<IOException>(() => Invoke("ValidateEntry", "Packages/manifest.json", new[] { _source }));
        }

        [Test]
        public void SnapshotRejectsAssetsOrExternalTargets()
        {
            Assert.Throws<IOException>(() => BackendProfileStore.CaptureSnapshot("Assets/UnsafeSnapshot"));
            Assert.Throws<IOException>(() => BackendProfileStore.CaptureSnapshot(Path.GetTempPath()));
        }

        private string Payload(string source) => Path.Combine(_archive, "files", (string)Invoke("Hash", Path.GetFullPath(source)));

        [Test]
        public void FlatPayloadRoundTripsWhenLegacyNestedPathWouldExceedWindowsLimit()
        {
            string source = _source + "/" + new string('x', 75) + ".asset";
            File.WriteAllText(source, "长路径配置");
            Assert.That(Path.GetFullPath(Path.Combine(_archive, "files", source)).Length, Is.GreaterThan(260));
            Invoke("WriteRecord", _archive, new[] { _source }, false);
            Assert.That(File.Exists(Payload(source)), Is.True);
            File.Delete(source);
            var record = Invoke("ReadRecord", _archive);
            Invoke("RestoreFiles", _archive, record);
            Assert.That(File.ReadAllText(source), Is.EqualTo("长路径配置"));
        }

        [Test]
        public void ExistingFormatOneArchiveStillVerifiesAndRestores()
        {
            Invoke("WriteRecord", _archive, new[] { _source }, false);
            foreach (string source in new[] { _source + ".meta", _source + "/data.asset", _source + "/data.asset.meta" })
            {
                string stored = Path.Combine(_archive, "files", source);
                Directory.CreateDirectory(Path.GetDirectoryName(stored));
                File.Copy(source, stored, true);
            }
            string manifest = Path.Combine(_archive, "manifest.json");
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"Format\": 2", "\"Format\": 1"));
            foreach (string flat in Directory.GetFiles(Path.Combine(_archive, "files"), "*", SearchOption.TopDirectoryOnly)) File.Delete(flat);
            var record = Invoke("ReadRecord", _archive);
            File.Delete(_source + "/data.asset");
            Invoke("RestoreFiles", _archive, record);
            Assert.That(File.ReadAllText(_source + "/data.asset"), Is.EqualTo("资源配置"));
        }

        private void CreateResourcesArchive(bool otherResource = false, bool conflict = false, bool legacy = false)
        {
            Directory.CreateDirectory(_source + "/UI");
            Directory.CreateDirectory(_source + "/Data");
            foreach (string relative in new[] { "UI/StartMenuView.prefab", "UI/MainRootMenuView.prefab", "UI/LevelHudView.prefab", "UI/LevelLoadingView.prefab", "Data/DemoText.txt" })
            {
                File.WriteAllText(_source + "/" + relative, relative);
                File.WriteAllText(_source + "/" + relative + ".meta", "guid: " + Guid.NewGuid().ToString("N"));
            }
            File.WriteAllText(_source + "/UI.meta", "guid: 11111111111111111111111111111111");
            File.WriteAllText(_source + "/Data.meta", "guid: 22222222222222222222222222222222");
            if (otherResource) File.WriteAllText(_source + "/UI/Keep.txt", "保留其它资源");
            if (conflict) File.WriteAllText(_source + "/UI_StartMenuView.prefab", "冲突的新路径");
            Invoke("WriteRecord", _archive, new[] { _source }, false);
            if (!legacy) return;
            foreach (string source in Directory.GetFiles(_source, "*", SearchOption.AllDirectories).Concat(new[] { _source + ".meta" }))
            {
                string stored = Path.Combine(_archive, "files", source);
                Directory.CreateDirectory(Path.GetDirectoryName(stored));
                File.Copy(source, stored, true);
            }
            string manifest = Path.Combine(_archive, "manifest.json");
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"Format\": 2", "\"Format\": 1"));
            foreach (string flat in Directory.GetFiles(Path.Combine(_archive, "files"), "*", SearchOption.TopDirectoryOnly)) File.Delete(flat);
        }

        [Serializable] private sealed class ProfileView { public int Format; public EntryView[] Files; }
        [Serializable] private sealed class EntryView { public string Path; public string Sha256; }
        private ProfileView ReadProfile() => JsonUtility.FromJson<ProfileView>(File.ReadAllText(Path.Combine(_archive, "manifest.json")));

        [TestCase(false)]
        [TestCase(true)]
        public void ResourcesProfileMigratesKnownKeysWithoutChangingPayloadOrGuids(bool legacy)
        {
            CreateResourcesArchive(legacy: legacy);
            var before = ReadProfile();
            Invoke("MigrateResourcesProfileAt", _archive, _source);
            var after = ReadProfile();
            Assert.That(after.Format, Is.EqualTo(2));
            foreach (var entry in before.Files.Where(e => e.Path.StartsWith(_source + "/UI/") || e.Path.StartsWith(_source + "/Data/")))
            {
                string renamed = entry.Path.Replace(_source + "/UI/", _source + "/UI_").Replace(_source + "/Data/", _source + "/Data_");
                Assert.That(after.Files.Single(e => e.Path == renamed).Sha256, Is.EqualTo(entry.Sha256));
                Assert.That(File.Exists(Path.Combine(_archive, "files", entry.Sha256)), Is.True);
            }
            Assert.That(after.Files.Any(e => e.Path == _source + "/UI.meta" || e.Path == _source + "/Data.meta"), Is.False);
            Assert.That(after.Files.Any(e => e.Path == _source + ".meta"), Is.True);
            Invoke("Verify", _archive, Invoke("ReadRecord", _archive));
        }

        [Test]
        public void ResourcesProfileMigrationIsIdempotentAndKeepsUnrelatedResources()
        {
            CreateResourcesArchive(otherResource: true);
            var keep = ReadProfile().Files.Single(e => e.Path == _source + "/UI/Keep.txt").Sha256;
            Invoke("MigrateResourcesProfileAt", _archive, _source);
            byte[] manifest = File.ReadAllBytes(Path.Combine(_archive, "manifest.json"));
            var after = ReadProfile();
            Assert.That(after.Files.Single(e => e.Path == _source + "/UI/Keep.txt").Sha256, Is.EqualTo(keep));
            Assert.That(after.Files.Any(e => e.Path == _source + "/UI.meta"), Is.True);
            Assert.That(after.Files.Any(e => e.Path == _source + "/data.asset"), Is.True);
            Invoke("MigrateResourcesProfileAt", _archive, _source);
            CollectionAssert.AreEqual(manifest, File.ReadAllBytes(Path.Combine(_archive, "manifest.json")));
        }

        [Test]
        public void ResourcesProfileKeyConflictDoesNotPublishMigration()
        {
            CreateResourcesArchive(conflict: true);
            byte[] manifest = File.ReadAllBytes(Path.Combine(_archive, "manifest.json"));
            Assert.Throws<IOException>(() => Invoke("MigrateResourcesProfileAt", _archive, _source));
            CollectionAssert.AreEqual(manifest, File.ReadAllBytes(Path.Combine(_archive, "manifest.json")));
        }

        [Test]
        public void ResourcesProfileCorruptionIsRejectedBeforeMigration()
        {
            CreateResourcesArchive();
            byte[] manifest = File.ReadAllBytes(Path.Combine(_archive, "manifest.json"));
            File.WriteAllText(Payload(_source + "/UI/StartMenuView.prefab"), "损坏");
            Assert.Throws<IOException>(() => Invoke("MigrateResourcesProfileAt", _archive, _source));
            CollectionAssert.AreEqual(manifest, File.ReadAllBytes(Path.Combine(_archive, "manifest.json")));
            Assert.That(File.Exists(_source + "/UI/StartMenuView.prefab.meta"), Is.True);
        }
    }
}
