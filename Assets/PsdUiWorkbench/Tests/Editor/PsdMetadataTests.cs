using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using UnityEditor;
using UnityEditor.U2D.PSD;
using UnityEngine;
using UnityEngine.TestTools;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdMetadataTests
    {
        private const string Battle = "Assets/Psd/战斗-主菜单界面.psd";
        private sealed class PixelGuard : MemoryStream
        {
            public PixelGuard(byte[] data) : base(data) { }
            public override int ReadByte()
            {
                if (Position >= Length - 27 && Position < Length - 9) throw new InvalidOperationException("不应读取像素负载。");
                return base.ReadByte();
            }
        }
        [Test]
        public void ReaderSeeksOverPixels()
        {
            using var stream = new PixelGuard(Fixture(false));
            Assert.That(new PsdLayerMetadataReader().Read(stream).Status, Is.EqualTo(PsdMetadataStatus.Success));
        }
        private static byte[] Bytes(Action<MemoryStream> action)
        {
            using var s = new MemoryStream(); action(s); return s.ToArray();
        }
        private static void N(Stream s, long value, int bytes = 4)
        {
            for (int i = bytes - 1; i >= 0; i--) s.WriteByte((byte)((ulong)value >> (i * 8)));
        }
        private static void Raw(Stream s, byte[] b) => s.Write(b, 0, b.Length);
        private static void Key(Stream s, string key) => Raw(s, System.Text.Encoding.ASCII.GetBytes(key));
        private static void Block(Stream s, string key, byte[] data, bool large)
        {
            Key(s, "8BIM"); Key(s, key); N(s, data.Length, large && key == "Layr" ? 8 : 4); Raw(s, data);
            if (data.Length % 2 != 0) s.WriteByte(0);
        }
        private static byte[] Fixture(bool large, string textKey = "TySh", int depth = 8, bool global = false, bool duplicate = false, bool missing = false, bool odd = true)
        {
            var records = Bytes(s =>
            {
                N(s, 2, 2);
                for (int i = 0; i < 2; i++)
                {
                    N(s, 0); N(s, 0); N(s, 1); N(s, 1); N(s, 3, 2);
                    for (int c = 0; c < 3; c++) { N(s, c, 2); N(s, 3, large ? 8 : 4); }
                    Key(s, "8BIM"); Key(s, "norm"); Raw(s, new byte[] {255, 0, 0, 0});
                    var extra = Bytes(e =>
                    {
                        N(e, 0); N(e, 0); Raw(e, new byte[] {1, (byte)'x', 0, 0});
                        if (!missing || i != 1) Block(e, "lyid", Bytes(b => N(b, duplicate ? 1 : i + 1)), large);
                        Block(e, "zzzz", odd ? new byte[] {7} : new byte[] {7, 0}, large);
                        if (i == 0) Block(e, textKey, new byte[0], large);
                    });
                    N(s, extra.Length); Raw(s, extra);
                }
                Raw(s, new byte[18]);
            });
            return Bytes(s =>
            {
                Key(s, "8BPS"); N(s, large ? 2 : 1, 2); Raw(s, new byte[6]); N(s, 3, 2); N(s, 1); N(s, 1); N(s, depth, 2); N(s, 3, 2);
                N(s, 0); N(s, 0);
                var mask = Bytes(m =>
                {
                    N(m, global ? 0 : records.Length, large ? 8 : 4);
                    if (!global) Raw(m, records);
                    N(m, 0);
                    if (global)
                    {
                        Block(m, "Layr", records, large);
                        while (m.Length % 4 != 0) m.WriteByte(0);
                    }
                });
                N(s, mask.Length, large ? 8 : 4); Raw(s, mask); Raw(s, new byte[5]);
            });
        }
        [TestCase(false, "TySh", false)]
        [TestCase(true, "tySh", false)]
        [TestCase(false, "TySh", true)]
        [TestCase(true, "TySh", true)]
        public void ParsesPsdPsbAndGlobalLayers(bool large, string key, bool global)
        {
            using var s = new MemoryStream(Fixture(large, key, global: global));
            var result = new PsdLayerMetadataReader().Read(s);
            Assert.That(result.Status, Is.EqualTo(PsdMetadataStatus.Success), result.Diagnostic);
            Assert.That(result.Layers[1], Is.EqualTo(PsdLayerKind.Text));
            Assert.That(result.Layers[2], Is.EqualTo(PsdLayerKind.NonText));
        }
        [Test]
        public void InvalidUnsupportedAndAmbiguousDoNotBecomeNonText()
        {
            foreach (int depth in new[] {16, 32})
            {
                using var s = new MemoryStream(Fixture(false, depth: depth));
                Assert.That(new PsdLayerMetadataReader().Read(s).Status, Is.EqualTo(PsdMetadataStatus.Unsupported));
            }
            foreach (var data in new[] {Fixture(false, duplicate: true), Fixture(false, missing: true)})
            {
                using var s = new MemoryStream(data);
                var r = new PsdLayerMetadataReader().Read(s);
                Assert.That(r.Status, Is.EqualTo(PsdMetadataStatus.Partial));
                Assert.That(r.Layers.Values.All(k => k == PsdLayerKind.Unknown), Is.True);
            }
            var bytes = Fixture(true);
            for (int length = 0; length < bytes.Length - 6; length++)
            {
                using var s = new MemoryStream(bytes, 0, length);
                var r = new PsdLayerMetadataReader().Read(s);
                Assert.That(r.Status, Is.EqualTo(PsdMetadataStatus.Invalid), "截断位置：" + length);
            }
        }
        [Test]
        public void BattleReaderFindsTwentyTextLayers()
        {
            using var stream = File.OpenRead(Battle);
            var r = new PsdLayerMetadataReader().Read(stream);
            Assert.That(r.Status, Is.EqualTo(PsdMetadataStatus.Success), r.Diagnostic);
            Assert.That(r.Layers.Count, Is.EqualTo(69));
            Assert.That(r.Layers.Count(p => p.Value == PsdLayerKind.Text), Is.EqualTo(20));
            Assert.That(r.Layers[1091], Is.EqualTo(PsdLayerKind.Text));
            Assert.That(r.Layers[979], Is.EqualTo(PsdLayerKind.Text));
        }
        private static string SpriteIds(string path) => string.Join("\n", AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().Select(s =>
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(s, out string guid, out long local);
            using var so = new SerializedObject(s);
            return so.FindProperty("m_SpriteID").stringValue + ":" + guid + ":" + local;
        }).OrderBy(x => x));
        private static string Settings(string path)
        {
            return PsdImporterMigration.SettingsSignature((PSDImporter)AssetImporter.GetAtPath(path));
        }
        [UnityTest]
        public IEnumerator SwitchingPreservesSettingsSpritesReferencesAndMetadata()
        {
            string folder = "Assets/PsdUiWorkbench/Tests/Types_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/PsdUiWorkbench/Tests", Path.GetFileName(folder));
            string path = folder + "/copy.psd";
            try
            {
                Assert.That(AssetDatabase.CopyAsset(Battle, path), Is.True);
                var importer = (PSDImporter)AssetImporter.GetAtPath(path);
                importer.spritePixelsPerUnit = 123;
                importer.userData = "workbench-migration-test";
                importer.SaveAndReimport();
                string ids = SpriteIds(path), settings = Settings(path);
                var mainType = AssetDatabase.LoadMainAssetAtPath(path).GetType();
                var sprite = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().First();
                var reference = new SerializedObject(ScriptableObject.CreateInstance<SpriteReference>());
                reference.FindProperty("Sprite").objectReferenceValue = sprite;
                reference.ApplyModifiedPropertiesWithoutUndo();
                var holder = (SpriteReference)reference.targetObject;
                reference.Dispose();
                AssetDatabase.CreateAsset(holder, folder + "/reference.asset");
                string originalRef = EditorJsonUtility.ToJson(holder);
                PsdImporterMigration.Switch(path, true);
                yield return null;
                Assert.That(SpriteIds(path), Is.EqualTo(ids), "切换后 Sprite 引用标识变化。");
                Assert.That(Settings(path), Is.EqualTo(settings), "共同导入设置变化。");
                Assert.That(AssetImporter.GetAtPath(path).userData, Is.EqualTo("workbench-migration-test"));
                Assert.That(AssetDatabase.LoadMainAssetAtPath(path).GetType(), Is.EqualTo(mainType));
                Assert.That(EditorJsonUtility.ToJson(holder), Is.EqualTo(originalRef));
                Assert.That(holder.Sprite, Is.Not.Null);
                var metadata = AssetDatabase.LoadAllAssetsAtPath(path).OfType<PsdLayerMetadataAsset>().Single();
                var result = metadata.Read();
                Assert.That(result.Status, Is.EqualTo(PsdMetadataStatus.Success), result.Diagnostic);
                Assert.That(result.Layers.Count(p => p.Value == PsdLayerKind.Text), Is.EqualTo(20));
                var snapshot = new UnityPsdDocumentSource().Load(AssetDatabase.AssetPathToGUID(path));
                Assert.That(snapshot.Document, Is.Not.Null, snapshot.Diagnostic);
                Assert.That(snapshot.Document.Nodes.Count(n => n.SourceKind == PsdLayerKind.Text), Is.EqualTo(20));
                Assert.That(snapshot.Document.Find(947).SourceKind, Is.EqualTo(PsdLayerKind.Text), "未导入文本也应识别。");
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(metadata, out string guid, out long local);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var next = AssetDatabase.LoadAllAssetsAtPath(path).OfType<PsdLayerMetadataAsset>().Single();
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(next, out string nextGuid, out long nextLocal);
                Assert.That((nextGuid, nextLocal), Is.EqualTo((guid, local)));
                string renamed = folder + "/renamed.psd";
                Assert.That(AssetDatabase.MoveAsset(path, renamed), Is.Empty);
                path = renamed;
                Assert.That(AssetDatabase.LoadAllAssetsAtPath(path).OfType<PsdLayerMetadataAsset>().Single().Read().Layers[1091], Is.EqualTo(PsdLayerKind.Text));
                PsdImporterMigration.Switch(path, false);
                yield return null;
                Assert.That(SpriteIds(path), Is.EqualTo(ids));
                Assert.That(Settings(path), Is.EqualTo(settings));
                Assert.That(holder.Sprite, Is.Not.Null);
                Assert.That(AssetDatabase.LoadAllAssetsAtPath(path).OfType<PsdLayerMetadataAsset>(), Is.Empty);
                foreach (bool large in new[] {false, true})
                {
                    string tiny = folder + (large ? "/tiny.psb" : "/tiny.psd");
                    File.WriteAllBytes(tiny, Fixture(large, odd: false));
                    AssetDatabase.ImportAsset(tiny);
                    AssetDatabase.SetImporterOverride<WorkbenchPsdImporter>(tiny);
                    Assert.That(AssetDatabase.LoadMainAssetAtPath(tiny), Is.Not.Null);
                    Assert.That(AssetDatabase.LoadAllAssetsAtPath(tiny).OfType<PsdLayerMetadataAsset>().Single().Read().Status, Is.EqualTo(PsdMetadataStatus.Success));
                    File.WriteAllBytes(tiny, Fixture(large, textKey: "zzzz", odd: false));
                    AssetDatabase.ImportAsset(tiny, ImportAssetOptions.ForceUpdate);
                    Assert.That(AssetDatabase.LoadAllAssetsAtPath(tiny).OfType<PsdLayerMetadataAsset>().Single().Read().Layers.Values.Contains(PsdLayerKind.Text), Is.False, "源类型改变后必须更新缓存。");
                }
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}

