using System;
using System.Collections.Generic;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using UnityEditor;
using UnityEngine;

namespace PsdUiWorkbench.AiNaming.Tests
{
    public sealed class PsdAiLegacyMigrationTests
    {
        private static PsdSession Session(string guid = "document")
        {
            var session = new PsdSession();
            session.Refresh(new PsdDocumentSnapshot(guid, 100, 100, new[] {
                new PsdNode(1, null, "SourceA", false, true, true, false, "a", new PixelRect(0,0,10,10), 0),
                new PsdNode(2, null, "SourceB", false, true, true, false, "b", new PixelRect(20,0,10,10), 1) }));
            return session;
        }

        [Test]
        public void UnknownIdsAreReportedAndUnrelatedFieldsAreNotImported()
        {
            var session = Session();
            var plan = PsdAiLegacyMigration.BuildNameImport(session,
                "{\"layers\":[{\"id\":1,\"name\":\"Renamed\",\"visible\":false,\"export\":false},{\"id\":99,\"name\":\"Unknown\"}]}");
            Assert.That(plan.SkippedCount, Is.EqualTo(1));
            Assert.That(plan.Warnings.Count, Is.EqualTo(1));
            Assert.That(PsdAiLegacyMigration.ApplyNames(session, plan), Is.EqualTo(1));
            Assert.That(session.GetNodeName(1), Is.EqualTo("Renamed"));
            Assert.That(session.IsNodeEnabled(1), Is.True);
            Assert.That(session.IsNodeExportEnabled(1), Is.True);
        }

        [TestCase("{\"layers\":[{\"name\":\"Missing\"}]}")]
        [TestCase("{\"layers\":[{\"id\":1,\"name\":\"A\"},{\"id\":1,\"name\":\"B\"}]}")]
        [TestCase("{\"layers\":[{\"id\":1,\"name\":\" \"}]}")]
        public void InvalidBatchNeverChangesSession(string json)
        {
            var session = Session();
            Assert.Throws<ArgumentException>(() => PsdAiLegacyMigration.BuildNameImport(session, json));
            Assert.That(session.GetNodeName(1), Is.EqualTo("SourceA"));
        }

        [Test]
        public void ExistingOverridesAreProtectedAndSourceNamesAreSkipped()
        {
            var session = Session();
            session.SetNodeNames(new Dictionary<int,string> { [1] = "Workbench" });
            var plan = PsdAiLegacyMigration.BuildNameImport(session,
                "{\"layers\":[{\"id\":1,\"name\":\"Legacy\"},{\"id\":2,\"name\":\"SourceB\"}]}");
            Assert.That(plan.Changes.Count, Is.Zero);
            Assert.That(plan.SkippedCount, Is.EqualTo(2));
            Assert.That(plan.Warnings.Count, Is.EqualTo(1));
            var forced = PsdAiLegacyMigration.BuildNameImport(session, "{\"layers\":[{\"id\":1,\"name\":\"Legacy\"}]}", true);
            Assert.That(PsdAiLegacyMigration.ApplyNames(session, forced), Is.EqualTo(1));
        }

        [Test]
        public void ChangedDocumentRejectsPreviewEvenWithSameIds()
        {
            var session = Session();
            var plan = PsdAiLegacyMigration.BuildNameImport(session, "{\"layers\":[{\"id\":1,\"name\":\"Legacy\"}]}");
            session.Refresh(Session("other").Document);
            Assert.Throws<InvalidOperationException>(() => PsdAiLegacyMigration.ApplyNames(session, plan));
            Assert.That(session.GetNodeName(1), Is.EqualTo("SourceA"));
        }

        [Test]
        public void NameConflictDoesNotPartiallyCommit()
        {
            var session = Session();
            var plan = PsdAiLegacyMigration.BuildNameImport(session,
                "{\"layers\":[{\"id\":1,\"name\":\"Same\"},{\"id\":2,\"name\":\"Same\"}]}");
            Assert.Throws<InvalidOperationException>(() => PsdAiLegacyMigration.ApplyNames(session, plan));
            Assert.That(session.GetNodeName(1), Is.EqualTo("SourceA"));
            Assert.That(session.GetNodeName(2), Is.EqualTo("SourceB"));
        }

        [Test]
        public void YamlSupportsChineseEscapesQuotedFoldingAndLiteralRules()
        {
            var data = PsdAiLegacyMigration.ReadSettingsYaml("MonoBehaviour:\n  _model: \"中文\\u6A21\\u578B\"\n  _reasoningEffort: 2\n  _timeoutSeconds: 30\n  _rules: |-\n    第一行\n    第二行\n  _apiKey: secret-never-copy\n");
            Assert.That(data.Model, Is.EqualTo("中文模型"));
            Assert.That(data.Rules, Is.EqualTo("第一行\n第二行"));
            Assert.That(data.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.High));
            var folded = PsdAiLegacyMigration.ReadSettingsYaml("_model: 'it''s a\n  model'\n_rules: >-\n  中文规则\n  下一行\n");
            Assert.That(folded.Model, Is.EqualTo("it's a model"));
            Assert.That(folded.Rules, Is.EqualTo("中文规则 下一行"));
        }

        [Test]
        public void EarlySettingsMigrateWithCompatibleTimeoutAndReasoning()
        {
            var target = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            try
            {
                target.Model = "Before";
                target.TimeoutSeconds = 30;
                target.ReasoningEffort = PsdAiReasoningEffort.High;
                var data = PsdAiLegacyMigration.ReadSettingsYaml("_model: EarlyModel\n_rules: 早期规则");
                PsdAiLegacyMigration.ApplySettings(target, data);
                Assert.That(target.Model, Is.EqualTo("EarlyModel"));
                Assert.That(target.TimeoutSeconds, Is.EqualTo(120));
                Assert.That(target.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.None));
            }
            finally { Undo.ClearUndo(target); UnityEngine.Object.DestroyImmediate(target); }
        }

        [TestCase("_rules: unrelated")]
        [TestCase("_timeoutSeconds: 120")]
        [TestCase("_model:")]
        [TestCase("_model: '   '\n_rules: unrelated")]
        public void SettingsWithoutNonemptyModelAreRejected(string yaml)
        {
            Assert.Throws<ArgumentException>(() => PsdAiLegacyMigration.ReadSettingsYaml(yaml));
        }

        [TestCase(0)]
        [TestCase(9)]
        [TestCase(601)]
        [TestCase(3600)]
        public void TimeoutOutsideToolRangeIsRejected(int timeout)
        {
            Assert.Throws<ArgumentException>(() => PsdAiLegacyMigration.ReadSettingsYaml(
                "_model: Valid\n_timeoutSeconds: " + timeout));
        }

        [TestCase(10)]
        [TestCase(600)]
        public void TimeoutRangeEndpointsRemainImportable(int timeout)
        {
            var target = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            try
            {
                var data = PsdAiLegacyMigration.ReadSettingsYaml("_model: Valid\n_timeoutSeconds: " + timeout);
                PsdAiLegacyMigration.ApplySettings(target, data);
                Assert.That(target.TimeoutSeconds, Is.EqualTo(timeout));
            }
            finally { Undo.ClearUndo(target); UnityEngine.Object.DestroyImmediate(target); }
        }

        [Test]
        public void SecretsAreNeitherReturnedNorIncludedInErrors()
        {
            const string secret = "sensitive-test-sentinel";
            var data = PsdAiLegacyMigration.ReadSettingsYaml("_model: safe\n_apiKey: " + secret);
            Assert.That(data.Model, Is.EqualTo("safe"));
            Assert.That(data.Rules ?? "", Does.Not.Contain(secret));
            var error = Assert.Throws<ArgumentException>(() => PsdAiLegacyMigration.ReadSettingsYaml("_model: \"broken\n_apiKey: " + secret));
            Assert.That(error.Message, Does.Not.Contain(secret));
            foreach (var reference in typeof(PsdAiLegacyMigration).Assembly.GetReferencedAssemblies())
                Assert.That(reference.Name, Does.Not.Contain("PsdTools"));
        }

        [Test]
        public void SettingsMigrationCanBeUndoneAsOneOperation()
        {
            var target = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            try
            {
                target.Model = "Before";
                target.Rules = "Before rules";
                target.TimeoutSeconds = 17;
                target.ReasoningEffort = PsdAiReasoningEffort.Low;
                PsdAiLegacyMigration.ApplySettings(target, PsdAiLegacyMigration.ReadSettingsYaml(
                    "_model: After\n_rules: After rules\n_timeoutSeconds: 42\n_reasoningEffort: 3"));
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(target.Model, Is.EqualTo("Before"));
                Assert.That(target.Rules, Is.EqualTo("Before rules"));
                Assert.That(target.TimeoutSeconds, Is.EqualTo(17));
                Assert.That(target.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.Low));
            }
            finally { Undo.ClearUndo(target); UnityEngine.Object.DestroyImmediate(target); }
        }
    }
}
