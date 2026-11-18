using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using PsdUiWorkbench.Infrastructure;
using UnityEditor;
using UnityEngine;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdTextRejectionTests
    {
        private const string Battle = "Assets/Psd/战斗-主菜单界面.psd";
        private const int SupportedLayer = 42;
        private const int RejectedLayer = 43;

        [TestCase(false)]
        [TestCase(true)]
        public void NativeReaderIdentifiesUnsupportedEffectWithoutDroppingSupportedText(bool large)
        {
            using var stream = new MemoryStream(Fixture(large));
            var result = new PsdTextMetadataReader().Read(stream);

            Assert.That(result.Status, Is.EqualTo(PsdMetadataStatus.Partial), result.Diagnostic);
            Assert.That(result.Sources.Keys, Is.EquivalentTo(new[] { SupportedLayer }));
            Assert.That(result.RejectedLayers.Keys, Is.EquivalentTo(new[] { RejectedLayer }));
            Assert.That(result.RejectedLayers[RejectedLayer], Does.Contain("单色外描边"));
            Assert.That(result.Diagnostic, Does.Contain("跳过文本层 43"));
            Assert.That(result.Diagnostic, Does.Not.Contain("Image"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeReaderRejectsBrokenContainerInsteadOfReturningLayerRejections(bool large)
        {
            var bytes = Fixture(large);
            using var stream = new MemoryStream(bytes, 0, bytes.Length - 4);
            var result = new PsdTextMetadataReader().Read(stream);

            Assert.That(result.Status, Is.EqualTo(PsdMetadataStatus.Invalid));
            Assert.That(result.Sources, Is.Empty);
            Assert.That(result.RejectedLayers, Is.Empty);
        }

        [Test]
        public void CacheSerializationRoundTripPreservesRejectionReasonsWithoutParsingDiagnostic()
        {
            using var stream = new MemoryStream(Fixture(false));
            var original = new PsdTextMetadataReader().Read(stream);
            var rejected = original.RejectedLayers.ToDictionary(e => e.Key, e => e.Value);
            rejected.Add(44, "不支持“投影”\n额外原因：混合模式 / 50%");
            var result = new PsdTextMetadataResult(PsdMetadataStatus.Partial, "诊断不包含图层标识或拒绝原因。",
                original.Sources.ToDictionary(e => e.Key, e => e.Value), rejected);
            var cache = ScriptableObject.CreateInstance<PsdTextMetadataAsset>();
            var restored = ScriptableObject.CreateInstance<PsdTextMetadataAsset>();
            try
            {
                Initialize(cache, result);
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(cache), restored);
                var actual = restored.Read();

                Assert.That(restored.Version, Is.EqualTo(PsdTextMetadataAsset.CurrentVersion));
                Assert.That(actual.Status, Is.EqualTo(PsdMetadataStatus.Partial), actual.Diagnostic);
                Assert.That(actual.Diagnostic, Is.EqualTo(result.Diagnostic));
                Assert.That(actual.Sources.Keys, Is.EquivalentTo(new[] { SupportedLayer }));
                Assert.That(actual.Sources[SupportedLayer].Text, Is.EqualTo(original.Sources[SupportedLayer].Text));
                Assert.That(actual.RejectedLayers.Keys, Is.EquivalentTo(new[] { RejectedLayer, 44 }));
                Assert.That(actual.RejectedLayers[RejectedLayer], Is.EqualTo(original.RejectedLayers[RejectedLayer]));
                Assert.That(actual.RejectedLayers[44], Is.EqualTo("不支持“投影”\n额外原因：混合模式 / 50%"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(restored);
                UnityEngine.Object.DestroyImmediate(cache);
            }
        }

        [Test]
        public void VersionThreeCacheRequiresReimportInsteadOfReturningStaleTextOrRejections()
        {
            using var stream = new MemoryStream(Fixture(false));
            var cache = ScriptableObject.CreateInstance<PsdTextMetadataAsset>();
            try
            {
                Initialize(cache, new PsdTextMetadataReader().Read(stream));
                var serialized = new SerializedObject(cache);
                serialized.FindProperty("_version").intValue = 3;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var result = cache.Read();

                Assert.That(result.Status, Is.EqualTo(PsdMetadataStatus.Unsupported));
                Assert.That(result.Diagnostic, Does.Contain("重新导入"));
                Assert.That(result.Sources, Is.Empty);
                Assert.That(result.RejectedLayers, Is.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(cache); }
        }

        [Test]
        public void CacheRejectsPartialStateWithoutIdentifiableRejectedLayers()
        {
            var cache = ScriptableObject.CreateInstance<PsdTextMetadataAsset>();
            try
            {
                Initialize(cache, new PsdTextMetadataResult(PsdMetadataStatus.Partial, "图层 ID 无法关联。"));
                var result = cache.Read();
                Assert.That(result.Status, Is.EqualTo(PsdMetadataStatus.Invalid));
                Assert.That(result.Sources, Is.Empty);
                Assert.That(result.RejectedLayers, Is.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(cache); }
        }

        [TestCase(RejectedLayer, "拒绝原因", true)]
        [TestCase(SupportedLayer, "拒绝原因", false)]
        [TestCase(RejectedLayer, " ", false)]
        public void CacheRejectsAmbiguousOrIncompleteRejectionRecords(int layerId, string reason, bool duplicate)
        {
            using var stream = new MemoryStream(Fixture(false));
            var cache = ScriptableObject.CreateInstance<PsdTextMetadataAsset>();
            try
            {
                Initialize(cache, new PsdTextMetadataReader().Read(stream));
                var serialized = new SerializedObject(cache);
                var entries = serialized.FindProperty("_rejectedLayers");
                entries.arraySize = duplicate ? 2 : 1;
                for (int i = 0; i < entries.arraySize; i++)
                {
                    var entry = entries.GetArrayElementAtIndex(i);
                    entry.FindPropertyRelative("Id").intValue = layerId;
                    entry.FindPropertyRelative("Reason").stringValue = reason;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var result = cache.Read();

                Assert.That(result.Status, Is.EqualTo(PsdMetadataStatus.Invalid));
                Assert.That(result.Sources, Is.Empty);
                Assert.That(result.RejectedLayers, Is.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(cache); }
        }

        private static void Initialize(PsdTextMetadataAsset asset, PsdTextMetadataResult result)
        {
            // 测试跨程序集调用导入器使用的同一缓存初始化入口，不扩大生产 API。
            typeof(PsdTextMetadataAsset).GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(asset, new object[] { result });
        }

        private static byte[] Fixture(bool large)
        {
            // 复用已审计 PSD 的真实 TySh；仅增加活动投影效果，保留另一层完全可支持。
            var battle = File.ReadAllBytes(Battle);
            int offset = Find(battle, Encoding.ASCII.GetBytes("8BIMTySh"));
            int size = (battle[offset + 8] << 24) | (battle[offset + 9] << 16) | (battle[offset + 10] << 8) | battle[offset + 11];
            var text = new byte[size];
            Array.Copy(battle, offset + 12, text, 0, size);
            var effects = Bytes(s =>
            {
                Number(s, 0); Number(s, 16);
                Descriptor(s, "Lefx", 2);
                Identifier(s, "masterFXSwitch"); Key(s, "bool"); Number(s, 1, 1);
                Identifier(s, "DrSh"); Key(s, "Objc");
                Descriptor(s, "DrSh", 1);
                Identifier(s, "enab"); Key(s, "bool"); Number(s, 1, 1);
            });
            var records = Bytes(s =>
            {
                Number(s, 2, 2);
                for (int i = 0; i < 2; i++)
                {
                    Raw(s, new byte[16]); Number(s, 0, 2);
                    Key(s, "8BIM"); Key(s, "norm"); Raw(s, new byte[] { 255, 0, 0, 0 });
                    var extra = Bytes(e =>
                    {
                        Number(e, 0); Number(e, 0); Raw(e, new byte[4]);
                        Block(e, "TySh", text);
                        if (i == 1) Block(e, "lfx2", effects);
                        Block(e, "lyid", Bytes(id => Number(id, SupportedLayer + i)));
                    });
                    Number(s, extra.Length); Raw(s, extra);
                }
            });
            return Bytes(s =>
            {
                Key(s, "8BPS"); Number(s, large ? 2 : 1, 2); Raw(s, new byte[6]);
                Number(s, 3, 2); Number(s, 100); Number(s, 100); Number(s, 8, 2); Number(s, 3, 2);
                Number(s, 0); Number(s, 0);
                var mask = Bytes(m => { Number(m, records.Length, large ? 8 : 4); Raw(m, records); Number(m, 0); });
                Number(s, mask.Length, large ? 8 : 4); Raw(s, mask);
            });
        }

        private static int Find(byte[] bytes, byte[] target)
        {
            for (int i = 0; i <= bytes.Length - target.Length; i++)
            {
                int j = 0;
                while (j < target.Length && bytes[i + j] == target[j]) j++;
                if (j == target.Length) return i;
            }
            throw new InvalidDataException("验证样本缺少 TySh。");
        }

        private static byte[] Bytes(Action<Stream> write)
        {
            using var stream = new MemoryStream();
            write(stream);
            return stream.ToArray();
        }

        private static void Number(Stream stream, long value, int size = 4)
        {
            for (int i = size - 1; i >= 0; i--) stream.WriteByte((byte)((ulong)value >> (i * 8)));
        }

        private static void Raw(Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
        private static void Key(Stream stream, string key) => Raw(stream, Encoding.ASCII.GetBytes(key));
        private static void Identifier(Stream stream, string value) { Number(stream, value.Length); Key(stream, value); }
        private static void Descriptor(Stream stream, string type, int count) { Number(stream, 0); Identifier(stream, type); Number(stream, count); }

        private static void Block(Stream stream, string key, byte[] payload)
        {
            Key(stream, "8BIM"); Key(stream, key); Number(stream, payload.Length); Raw(stream, payload);
            if (payload.Length % 2 != 0) stream.WriteByte(0);
        }
    }
}
