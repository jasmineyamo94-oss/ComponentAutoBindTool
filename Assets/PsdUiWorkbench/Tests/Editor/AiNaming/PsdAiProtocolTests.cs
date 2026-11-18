using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using PsdUiWorkbench.AiNaming;
using UnityEditor;
using UnityEngine;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdAiProtocolTests
    {
        private static string Envelope(string content, string finish = "stop") =>
            "{\"choices\":[{\"finish_reason\":" + PsdAiProtocol.Quote(finish) + ",\"message\":{\"content\":" + PsdAiProtocol.Quote(content) + "}}]}";
        private const string Valid = "{\"nodeKey\":\"n1\",\"suggestedName\":\"Confirm\",\"reason\":\"确认操作\",\"needsReview\":false}";

        [Test]
        public void ValidSuggestionBindsOnlyItsStableKey()
        {
            var result = PsdAiProtocol.ParseResponse(Envelope("{\"suggestions\":[" + Valid + "]}"), new[] { "n1", "n2" });
            Assert.That(result.Single().NodeKey, Is.EqualTo("n1"));
            Assert.That(result.Single().SuggestedName, Is.EqualTo("Confirm"));
        }

        [TestCase("unknown")]
        [TestCase("duplicate")]
        [TestCase("missing")]
        [TestCase("wrongType")]
        [TestCase("duplicateField")]
        public void InvalidBindingOrSchemaIsRejected(string mode)
        {
            string item = Valid;
            if (mode == "unknown") item = item.Replace("n1", "n9");
            if (mode == "duplicate") item += "," + Valid;
            if (mode == "missing") item = item.Replace(",\"needsReview\":false", "");
            if (mode == "wrongType") item = item.Replace("false", "\"false\"");
            if (mode == "duplicateField") item = item.Replace("\"nodeKey\":\"n1\"", "\"nodeKey\":\"n1\",\"nodeKey\":\"n1\"");
            Assert.Throws<PsdAiNamingException>(() => PsdAiProtocol.ParseResponse(Envelope("{\"suggestions\":[" + item + "]}"), new[] { "n1" }));
        }

        [Test]
        public void TruncatedResponseAndMarkdownAreRejectedWithoutEchoingContent()
        {
            var error = Assert.Throws<PsdAiNamingException>(() => PsdAiProtocol.ParseResponse(Envelope("secret", "length"), new[] { "n1" }));
            Assert.That(error.Message, Does.Not.Contain("secret"));
            Assert.Throws<PsdAiNamingException>(() => PsdAiProtocol.ParseResponse(Envelope("```json\n{}\n```"), new[] { "n1" }));
        }

        [Test]
        public void BodyLimitUsesUtf8AndLayerDataStaysOutsideSystemRules()
        {
            var settings = new PsdAiRequestSettings("deepseek-flash", PsdAiReasoningEffort.None, 30, "用户规则");
            var snapshot = new PsdAiNamingSnapshot("恶意图层指令", new[] { "n1" }, Array.Empty<PsdAiImage>());
            string body = Encoding.UTF8.GetString(PsdAiProtocol.BuildBody(snapshot, settings));
            Assert.That(body.IndexOf("恶意图层指令", StringComparison.Ordinal), Is.GreaterThan(body.IndexOf("\"role\":\"user\"", StringComparison.Ordinal)));
            var huge = new PsdAiNamingSnapshot(new string('图', PsdAiProtocol.MaxBodyBytes / 2), new[] { "n1" }, Array.Empty<PsdAiImage>());
            Assert.Throws<PsdAiNamingException>(() => PsdAiProtocol.BuildBody(huge, settings));
        }

        [Test]
        public void LegacyRequestKeepsDeepSeekThinkingAndMultimodalJsonContract()
        {
            var settings = new PsdAiRequestSettings("manual-vision-model", PsdAiReasoningEffort.High, 30, "");
            var snapshot = new PsdAiNamingSnapshot("{\"targets\":[]}", new[] { "n1" },
                new[] { new PsdAiImage("预览", new byte[] { 1, 2, 3 }) });

            string body = Encoding.UTF8.GetString(PsdAiProtocol.BuildBody(snapshot, settings));

            Assert.That(body, Does.Contain("\"thinking\":{\"type\":\"enabled\"}"));
            Assert.That(body, Does.Contain("\"reasoning_effort\":\"high\""));
            Assert.That(body, Does.Contain("\"response_format\":{\"type\":\"json_object\"}"));
            Assert.That(body, Does.Contain("\"type\":\"image_url\""));
            Assert.That(body, Does.Contain("data:image/png;base64,AQID"));
            Assert.That(body, Does.Contain("\"role\":\"system\""));
            Assert.That(body, Does.Contain("\"role\":\"user\""));
        }

        [TestCase(PsdAiReasoningEffort.None, "disabled")]
        [TestCase(PsdAiReasoningEffort.High, "enabled")]
        public void ZhipuUsesThinkingWithoutDeepSeekOnlyReasoningEffort(PsdAiReasoningEffort effort, string thinking)
        {
            var settings = new PsdAiRequestSettings(PsdAiProvider.Zhipu, "glm-4.6v", effort, 30, "");
            var snapshot = new PsdAiNamingSnapshot("{}", new[] { "n1" },
                new[] { new PsdAiImage("预览", new byte[] { 1, 2, 3 }) });

            string body = Encoding.UTF8.GetString(PsdAiProtocol.BuildBody(snapshot, settings));

            Assert.That(body, Does.Contain("\"thinking\":{\"type\":\"" + thinking + "\"}"));
            Assert.That(body, Does.Not.Contain("\"reasoning_effort\""));
            Assert.That(body, Does.Contain("\"model\":\"glm-4.6v\""));
            Assert.That(body, Does.Contain("data:image/png;base64,AQID"));
            Assert.That(body, Does.Not.Contain("\"response_format\""));
        }

        [TestCase("glm-5.3-flash")]
        [TestCase("glm-5.3-flashx")]
        public void ZhipuFlashUsesEnabledThinkingAndKeepsMultimodalRequest(string model)
        {
            var settings = new PsdAiRequestSettings(PsdAiProvider.Zhipu, model, PsdAiReasoningEffort.High, 30, "");
            var snapshot = new PsdAiNamingSnapshot("{}", new[] { "n1" },
                new[] { new PsdAiImage("预览", new byte[] { 1, 2, 3 }) });
            string body = Encoding.UTF8.GetString(PsdAiProtocol.BuildBody(snapshot, settings));
            Assert.That(body, Does.Contain("\"model\":\"" + model + "\""));
            Assert.That(body, Does.Contain("\"thinking\":{\"type\":\"enabled\"}"));
            Assert.That(body, Does.Not.Contain("\"type\":\"disabled\""));
            Assert.That(body, Does.Not.Contain("\"reasoning_effort\""));
            Assert.That(body, Does.Not.Contain("\"response_format\""));
            Assert.That(body, Does.Contain("data:image/png;base64,AQID"));
        }

        [TestCase("glm-5.3-flash", PsdAiReasoningEffort.None)]
        [TestCase("glm-5.3-flash", PsdAiReasoningEffort.Low)]
        [TestCase("glm-5.3-flash", PsdAiReasoningEffort.Max)]
        [TestCase("glm-5.3-flashx", PsdAiReasoningEffort.None)]
        [TestCase("glm-5.3-flashx", PsdAiReasoningEffort.Low)]
        [TestCase("glm-5.3-flashx", PsdAiReasoningEffort.Max)]
        public void SavedOrManualFlashSettingsCannotSendUnsupportedThinking(string model, PsdAiReasoningEffort effort)
        {
            var settings = new PsdAiRequestSettings(PsdAiProvider.Zhipu, model, effort, 30, "");
            var snapshot = new PsdAiNamingSnapshot("{}", Array.Empty<string>(), Array.Empty<PsdAiImage>());
            var error = Assert.Throws<PsdAiNamingException>(() => PsdAiProtocol.BuildBody(snapshot, settings));
            Assert.That(error.Message, Does.Contain("高（启用）"));
        }

        [TestCase(PsdAiProvider.DeepSeek, "glm-5.3-flash")]
        [TestCase(PsdAiProvider.DeepSeek, "glm-5.3-flashx")]
        [TestCase(PsdAiProvider.Zhipu, "glm-5.3-flash-custom")]
        [TestCase(PsdAiProvider.Zhipu, "custom-unknown-vision")]
        public void ThinkingRequirementIsNotInferredFromOtherProvidersOrModelPrefixes(PsdAiProvider provider, string model)
        {
            var settings = new PsdAiRequestSettings(provider, model, PsdAiReasoningEffort.None, 30, "");
            var snapshot = new PsdAiNamingSnapshot("{}", Array.Empty<string>(), Array.Empty<PsdAiImage>());
            string body = Encoding.UTF8.GetString(PsdAiProtocol.BuildBody(snapshot, settings));
            Assert.That(body, Does.Contain("\"thinking\":{\"type\":\"disabled\"}"));
        }

        [TestCase(PsdAiReasoningEffort.Low)]
        [TestCase(PsdAiReasoningEffort.Max)]
        public void ZhipuRejectsUnsupportedEffortBeforeBuildingRequest(PsdAiReasoningEffort effort)
        {
            var settings = new PsdAiRequestSettings(PsdAiProvider.Zhipu, "glm-4.6v", effort, 30, "");
            var snapshot = new PsdAiNamingSnapshot("{}", Array.Empty<string>(), Array.Empty<PsdAiImage>());
            Assert.Throws<PsdAiNamingException>(() => PsdAiProtocol.BuildBody(snapshot, settings));
        }

        [Test]
        public void ProviderEndpointsNeverSendZhipuRequestsToDeepSeek()
        {
            Assert.That(PsdAiModelCatalog.ChatCompletionsUrl(PsdAiProvider.DeepSeek),
                Is.EqualTo("https://api.deepseek.com/chat/completions"));
            Assert.That(PsdAiModelCatalog.ChatCompletionsUrl(PsdAiProvider.Zhipu),
                Is.EqualTo("https://open.bigmodel.cn/api/paas/v4/chat/completions"));
        }

        [TestCase("glm-4.6v", PsdAiReasoningEffort.None, 50, false)]
        [TestCase("glm-4.6v", PsdAiReasoningEffort.None, 51, true)]
        [TestCase("glm-4.6v-flash", PsdAiReasoningEffort.None, 50, false)]
        [TestCase("glm-4.6v-flash", PsdAiReasoningEffort.None, 51, true)]
        [TestCase("glm-4.6v-flashx", PsdAiReasoningEffort.None, 50, false)]
        [TestCase("glm-4.6v-flashx", PsdAiReasoningEffort.None, 51, true)]
        [TestCase("glm-5.3-flash", PsdAiReasoningEffort.High, 50, false)]
        [TestCase("glm-5.3-flash", PsdAiReasoningEffort.High, 51, true)]
        [TestCase("glm-5.3-flashx", PsdAiReasoningEffort.High, 50, false)]
        [TestCase("glm-5.3-flashx", PsdAiReasoningEffort.High, 51, true)]
        public void ZhipuImageCountLimitIsCheckedBeforeSending(string model, PsdAiReasoningEffort effort, int imageCount, bool rejected)
        {
            var settings = new PsdAiRequestSettings(PsdAiProvider.Zhipu, model, effort, 30, "");
            var images = Enumerable.Range(0, imageCount).Select(index => new PsdAiImage("预览", new byte[] { 1 }));
            var snapshot = new PsdAiNamingSnapshot("{}", new[] { "n1" }, images);
            if (rejected)
            {
                var error = Assert.Throws<PsdAiNamingException>(() => PsdAiProtocol.BuildBody(snapshot, settings));
                Assert.That(error.Message, Does.Contain("50"));
                Assert.That(error.Message, Does.Contain("不会自动拆批"));
            }
            else
            {
                string body = Encoding.UTF8.GetString(PsdAiProtocol.BuildBody(snapshot, settings));
                Assert.That(body.Split(new[] { "data:image/png;base64," }, StringSplitOptions.None).Length - 1,
                    Is.EqualTo(imageCount), "边界内图片必须全部保留，不能静默丢弃或拆批。");
            }
        }

        [TestCase("glm-4.6v", PsdAiReasoningEffort.None, 5 * 1024 * 1024 - 1, false)]
        [TestCase("glm-4.6v", PsdAiReasoningEffort.None, 5 * 1024 * 1024, true)]
        [TestCase("glm-4.6v-flash", PsdAiReasoningEffort.None, 5 * 1024 * 1024 - 1, false)]
        [TestCase("glm-4.6v-flash", PsdAiReasoningEffort.None, 5 * 1024 * 1024, true)]
        [TestCase("glm-4.6v-flashx", PsdAiReasoningEffort.None, 5 * 1024 * 1024 - 1, false)]
        [TestCase("glm-4.6v-flashx", PsdAiReasoningEffort.None, 5 * 1024 * 1024, true)]
        [TestCase("glm-5.3-flash", PsdAiReasoningEffort.High, 5 * 1024 * 1024 - 1, false)]
        [TestCase("glm-5.3-flash", PsdAiReasoningEffort.High, 5 * 1024 * 1024, true)]
        [TestCase("glm-5.3-flashx", PsdAiReasoningEffort.High, 5 * 1024 * 1024 - 1, false)]
        [TestCase("glm-5.3-flashx", PsdAiReasoningEffort.High, 5 * 1024 * 1024, true)]
        public void ZhipuSingleImageLimitUsesDecodedBytesRatherThanBase64Length(
            string model, PsdAiReasoningEffort effort, int imageBytes, bool rejected)
        {
            var settings = new PsdAiRequestSettings(PsdAiProvider.Zhipu, model, effort, 30, "");
            var image = new PsdAiImage("预览", new byte[imageBytes]);
            var snapshot = new PsdAiNamingSnapshot("{}", new[] { "n1" }, new[] { image });
            if (rejected)
            {
                var error = Assert.Throws<PsdAiNamingException>(() => PsdAiProtocol.BuildBody(snapshot, settings));
                Assert.That(error.Message, Does.Contain("5 MiB"));
                Assert.That(error.Message, Does.Contain("不会自动拆批"));
            }
            else
            {
                string body = Encoding.UTF8.GetString(PsdAiProtocol.BuildBody(snapshot, settings));
                Assert.That(body, Does.Contain(image.Base64Png));
            }
        }

        [TestCase(PsdAiProvider.DeepSeek, "manual-vision-model")]
        [TestCase(PsdAiProvider.DeepSeek, "glm-5.3-flash")]
        [TestCase(PsdAiProvider.DeepSeek, "glm-5.3-flashx")]
        [TestCase(PsdAiProvider.Zhipu, "custom-unknown-vision")]
        [TestCase(PsdAiProvider.Zhipu, "glm-5.3-flash-custom")]
        public void KnownZhipuImageCountLimitDoesNotApplyToOtherProvidersOrUnknownManualModels(PsdAiProvider provider, string model)
        {
            var settings = new PsdAiRequestSettings(provider, model, PsdAiReasoningEffort.None, 30, "");
            var images = Enumerable.Range(0, 51).Select(index => new PsdAiImage("预览", new byte[] { 1 }));
            var snapshot = new PsdAiNamingSnapshot("{}", new[] { "n1" }, images);
            string body = Encoding.UTF8.GetString(PsdAiProtocol.BuildBody(snapshot, settings));
            Assert.That(body.Split(new[] { "data:image/png;base64," }, StringSplitOptions.None).Length - 1, Is.EqualTo(51));
        }

        [TestCase(PsdAiProvider.DeepSeek, "glm-5.3-flash")]
        [TestCase(PsdAiProvider.DeepSeek, "glm-5.3-flashx")]
        [TestCase(PsdAiProvider.Zhipu, "custom-unknown-vision")]
        [TestCase(PsdAiProvider.Zhipu, "glm-5.3-flash-custom")]
        public void KnownZhipuSingleImageLimitDoesNotApplyToOtherProvidersOrUnknownManualModels(PsdAiProvider provider, string model)
        {
            var settings = new PsdAiRequestSettings(provider, model, PsdAiReasoningEffort.None, 30, "");
            var image = new PsdAiImage("预览", new byte[5 * 1024 * 1024]);
            var snapshot = new PsdAiNamingSnapshot("{}", new[] { "n1" }, new[] { image });
            string body = Encoding.UTF8.GetString(PsdAiProtocol.BuildBody(snapshot, settings));
            Assert.That(body, Does.Contain(image.Base64Png));
        }

        [Test]
        public void CreatingDefaultDoesNotSaveOtherDirtyAssets()
        {
            string target = PsdAiNamingSettings.DefaultAssetPath;
            if (File.Exists(target) || Directory.Exists(target) || File.Exists(target + ".meta") || Directory.Exists(target + ".meta"))
                Assert.Ignore("默认路径已有用户数据，不修改该路径。");
            string defaultFolder = Path.GetDirectoryName(target);
            bool madeDefaultFolder = !Directory.Exists(defaultFolder);
            string folder = "Assets/PsdAiSaveIsolation_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            string otherPath = folder + "/Other.asset";
            var other = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            PsdAiNamingSettings created = null;
            try
            {
                other.Rules = "初始值";
                AssetDatabase.CreateAsset(other, otherPath);
                byte[] originalBytes = File.ReadAllBytes(otherPath);
                other.Rules = "尚未保存的用户编辑";
                EditorUtility.SetDirty(other);
                created = PsdAiNamingSettings.CreateDefault();
                Assert.That(EditorUtility.IsDirty(other), Is.True);
                Assert.That(File.ReadAllBytes(otherPath), Is.EqualTo(originalBytes));
            }
            finally
            {
                if (created != null && AssetDatabase.LoadAssetAtPath<PsdAiNamingSettings>(target) == created)
                    AssetDatabase.DeleteAsset(target);
                AssetDatabase.DeleteAsset(folder);
                if (other != null && !EditorUtility.IsPersistent(other)) UnityEngine.Object.DestroyImmediate(other);
                if (madeDefaultFolder && Directory.Exists(defaultFolder) && !Directory.EnumerateFileSystemEntries(defaultFolder).Any())
                    AssetDatabase.DeleteAsset(defaultFolder.Replace('\\', '/'));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OrphanMetaOrDirectoryBlocksDefaultCreationWithoutOverwrite(bool directory)
        {
            string target = PsdAiNamingSettings.DefaultAssetPath;
            if (File.Exists(target) || Directory.Exists(target) || File.Exists(target + ".meta") || Directory.Exists(target + ".meta"))
                Assert.Ignore("默认路径已有用户数据，不修改该路径。");
            string parent = Path.GetDirectoryName(target);
            bool madeParent = !Directory.Exists(parent);
            Directory.CreateDirectory(parent);
            string marker = directory ? target : target + ".meta";
            try
            {
                if (directory) Directory.CreateDirectory(marker);
                else File.WriteAllText(marker, "测试占用，不得覆盖");
                Assert.Throws<PsdAiNamingException>(() => PsdAiNamingSettings.CreateDefault());
                if (directory) Assert.That(Directory.Exists(marker), Is.True);
                else Assert.That(File.ReadAllText(marker), Is.EqualTo("测试占用，不得覆盖"));
                Assert.That(File.Exists(target), Is.False);
            }
            finally
            {
                if (directory && Directory.Exists(marker)) Directory.Delete(marker);
                if (!directory && File.Exists(marker)) File.Delete(marker);
                if (madeParent && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            }
        }

        [TestCase("Confirm", true)]
        [TestCase("Reward2", true)]
        [TestCase("btn_Confirm", false)]
        [TestCase("BtnConfirm", false)]
        [TestCase("确认", false)]
        [TestCase("confirm", false)]
        public void NamesAreEnglishPascalCaseWithoutComponentPrefixes(string name, bool valid)
            => Assert.That(PsdAiProtocol.IsValidName(name), Is.EqualTo(valid));
    }
}
