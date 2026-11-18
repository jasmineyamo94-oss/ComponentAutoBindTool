using System.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdTextReferenceTests
    {
        [Test]
        public void ActualTextProbeUsesPlainDefaultPlaceholdersAndPreservesSources()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("实际文字对照需要真实图形设备。");
            string defaultMaterial = EditorJsonUtility.ToJson(TMP_Settings.defaultFontAsset.material);
            float defaultSpacing = TMP_Settings.defaultFontAsset.boldSpacing;
            var report = PsdTextReferenceValidation.Measure(false);
            Assert.That(report.layers.Count, Is.EqualTo(20));
            Assert.That(report.layers.Count(l => l.measured), Is.EqualTo(13));
            Assert.That(report.layers.Count(l => l.placeholder), Is.EqualTo(7));
            var placeholders = report.layers.Where(l => l.placeholder).SelectMany(l => l.placeholderSpans).Where(s => s.placeholder).ToArray();
            Assert.That(placeholders.Length, Is.EqualTo(7));
            Assert.That(placeholders.All(s => s.fontSize == PsdTextReferenceValidation.PlaceholderSize && !s.bold && !s.outline && !s.shadow), Is.True);
            Assert.That(placeholders.All(s => s.actualFont == TMP_Settings.defaultFontAsset.faceInfo.familyName), Is.True);
            var mixed = report.layers.Single(l => l.id == 936).placeholderSpans;
            Assert.That(mixed.Select(s => s.placeholder), Is.EqualTo(new[] {false, true, false}));
            Assert.That(mixed.Select(s => s.outline), Is.EqualTo(new[] {true, false, true}));
            var energy = report.layers.Single(l => l.id == 1091);
            Assert.That(energy.fontSizeUi, Is.EqualTo(34).Within(0.0001f));
            Assert.That(energy.spriteId, Is.Not.Empty);
            Assert.That(energy.spriteLocalFileId, Is.Not.Zero);
            Assert.That(report.layers.Single(l => l.id == 947).sourceImported, Is.False);
            Assert.That(report.referenceCoverageProbeAlpha, Is.EqualTo(128f / 255).Within(0.006f));
            Assert.That(report.sourceUnchanged && report.metaUnchanged && report.fontUnchanged, Is.True);
            Assert.That(report.defaultFontUnchanged, Is.True);
            Assert.That(EditorJsonUtility.ToJson(TMP_Settings.defaultFontAsset.material), Is.EqualTo(defaultMaterial));
            Assert.That(TMP_Settings.defaultFontAsset.boldSpacing, Is.EqualTo(defaultSpacing));
            Assert.That(report.temporaryStateRestored, Is.True);
        }
    }
}
