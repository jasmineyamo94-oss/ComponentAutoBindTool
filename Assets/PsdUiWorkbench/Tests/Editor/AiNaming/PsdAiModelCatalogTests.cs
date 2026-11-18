using System;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.AiNaming;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdAiModelCatalogTests
    {
        private const string Models = "{\"data\":[" +
            "{\"id\":\"vision\",\"input_modalities\":[\"text\",\"image\"]}," +
            "{\"id\":\"text-only\",\"input_modalities\":[\"text\"]}," +
            "{\"id\":\"unknown\"}]}";

        [Test]
        public void ChoicesIncludeEveryReturnedModelWithoutInventingImageSupport()
        {
            var models = PsdAiModelCatalog.ParseModels(Models);
            var choices = PsdAiModelCatalog.BuildModelChoices(models, "vision");
            Assert.That(choices, Is.EqualTo(new[] { "vision", "text-only", "unknown" }));
            Assert.That(models.Single(model => model.Id == "vision").SupportsImages, Is.True);
            Assert.That(models.Single(model => model.Id == "text-only").ImageCapabilityKnown, Is.True);
            Assert.That(models.Single(model => model.Id == "text-only").SupportsImages, Is.False);
            Assert.That(models.Single(model => model.Id == "unknown").ImageCapabilityKnown, Is.False);
            Assert.That(models.Single(model => model.Id == "unknown").SupportsImages, Is.False);
            Assert.That(PsdAiModelCatalog.FormatModelChoice(models, "text-only"), Does.Contain("不支持图片"));
            Assert.That(PsdAiModelCatalog.FormatModelChoice(models, "unknown"), Does.Contain("图片能力未知"));
        }

        [Test]
        public void ManualModelAbsentFromResponseRemainsSelectedWithoutChangingTheCatalog()
        {
            var models = PsdAiModelCatalog.ParseModels(Models);
            var choices = PsdAiModelCatalog.BuildModelChoices(models, "manual-custom");
            Assert.That(choices, Is.EqualTo(new[] { "vision", "text-only", "unknown", "manual-custom" }));
            Assert.That(models.Select(model => model.Id), Does.Not.Contain("manual-custom"));
            Assert.That(PsdAiModelCatalog.FormatModelChoice(models, "manual-custom"), Does.Contain("图片能力未知"));
            Assert.That(PsdAiModelCatalog.Validate(models, "manual-custom", PsdAiReasoningEffort.None), Is.Null);
        }

        [Test]
        public void VisibleTextOnlyModelIsStillRejectedForImageNaming()
        {
            var models = PsdAiModelCatalog.ParseModels(Models);
            Assert.That(PsdAiModelCatalog.Validate(models, "text-only", PsdAiReasoningEffort.None), Is.Not.Null);
            Assert.That(PsdAiModelCatalog.Validate(models, "vision", PsdAiReasoningEffort.None), Is.Null);
            Assert.That(PsdAiModelCatalog.Validate(models, "unknown", PsdAiReasoningEffort.None), Is.Null);
        }

        [Test]
        public void InvalidResponseCannotReplaceExistingCatalog()
        {
            var previous = PsdAiModelCatalog.Current;
            Assert.Throws<FormatException>(() => PsdAiModelCatalog.ParseModels("{\"data\":[]}"));
            Assert.Throws<FormatException>(() => PsdAiModelCatalog.ParseModels("{\"data\":[{\"id\":\"same\"},{\"id\":\"same\"}]}"));
            Assert.That(PsdAiModelCatalog.Current, Is.SameAs(previous));
        }

        [Test]
        public void MissingCredentialReportsFailureAndRetainsPreviousListAndSource()
        {
            var previous = PsdAiModelCatalog.Current;
            string previousSource = PsdAiModelCatalog.SourceSummary;
            if (PsdAiModelCatalog.IsRefreshing) Assert.Ignore("已有用户发起的刷新，不干扰其请求。");
            PsdAiModelCatalog.Refresh("", 120);
            Assert.That(PsdAiModelCatalog.IsRefreshing, Is.False);
            Assert.That(PsdAiModelCatalog.Status, Does.Contain("未刷新"));
            Assert.That(PsdAiModelCatalog.Current, Is.SameAs(previous));
            Assert.That(PsdAiModelCatalog.SourceSummary, Is.EqualTo(previousSource));
        }

        [TestCase("glm-5.3-flash")]
        [TestCase("glm-5.3-flashx")]
        public void ZhipuFlashModelsAdvertiseImagesAndRequireEnabledThinking(string id)
        {
            var models = PsdAiModelCatalog.GetModels(PsdAiProvider.Zhipu);
            var model = models.Single(entry => entry.Id == id);
            Assert.That(model.ImageCapabilityKnown && model.SupportsImages, Is.True);
            Assert.That(model.SupportsEffort(PsdAiReasoningEffort.None), Is.False);
            Assert.That(model.SupportsEffort(PsdAiReasoningEffort.High), Is.True);
            Assert.That(model.SupportsEffort(PsdAiReasoningEffort.Low), Is.False);
            Assert.That(model.SupportsEffort(PsdAiReasoningEffort.Max), Is.False);
            Assert.That(PsdAiModelCatalog.BuildModelChoices(models, id).Count(choice => choice == id), Is.EqualTo(1),
                "已有手动型号应与内置目录合并，而不是重复或替换用户选择。");
            Assert.That(PsdAiModelCatalog.FormatModelChoice(models, id), Does.Contain("仅支持启用思考"));
            Assert.That(PsdAiModelCatalog.Validate(PsdAiProvider.Zhipu, id, PsdAiReasoningEffort.None),
                Does.Contain("高（启用）"));
            Assert.That(PsdAiModelCatalog.Validate(PsdAiProvider.Zhipu, id, PsdAiReasoningEffort.High), Is.Null);
            Assert.That(PsdAiModelCatalog.Validate(PsdAiProvider.DeepSeek, id, PsdAiReasoningEffort.None), Is.Null,
                "智谱型号能力约束不能按名称泄漏到其他供应商。");
        }

        [TestCase("glm-4.6v")]
        [TestCase("glm-4.6v-flash")]
        [TestCase("glm-4.6v-flashx")]
        [TestCase("custom-unknown-vision")]
        [TestCase("glm-5.3-flash-custom")]
        public void LegacyAndUnknownZhipuModelsStillAllowDisabledThinking(string id)
        {
            Assert.That(PsdAiModelCatalog.Validate(PsdAiProvider.Zhipu, id, PsdAiReasoningEffort.None), Is.Null);
            Assert.That(PsdAiModelCatalog.Validate(PsdAiProvider.Zhipu, id, PsdAiReasoningEffort.High), Is.Null);
            Assert.That(PsdAiModelCatalog.Validate(PsdAiProvider.Zhipu, id, PsdAiReasoningEffort.Low), Is.Not.Null);
            Assert.That(PsdAiModelCatalog.Validate(PsdAiProvider.Zhipu, id, PsdAiReasoningEffort.Max), Is.Not.Null);
        }

        [Test]
        public void ZhipuCatalogDoesNotReplaceLegacyDeepSeekModelsOrApplyItsEffortRules()
        {
            var deepSeekModels = PsdAiModelCatalog.Current;
            var deepSeekIds = deepSeekModels.Select(model => model.Id).ToArray();
            var zhipuModels = PsdAiModelCatalog.GetModels(PsdAiProvider.Zhipu);

            Assert.That(zhipuModels.Any(model => model.Id == "glm-4.6v" && model.SupportsImages), Is.True);
            Assert.That(PsdAiModelCatalog.Validate(PsdAiProvider.Zhipu, "glm-4.6v", PsdAiReasoningEffort.High), Is.Null);
            Assert.That(PsdAiModelCatalog.Validate(PsdAiProvider.Zhipu, "glm-4.6v", PsdAiReasoningEffort.Low), Is.Not.Null);
            Assert.That(PsdAiModelCatalog.Validate(PsdAiProvider.DeepSeek, "custom-unknown-vision", PsdAiReasoningEffort.Low), Is.Null);
            Assert.That(PsdAiModelCatalog.Validate(PsdAiProvider.Zhipu, "custom-unknown-vision", PsdAiReasoningEffort.High), Is.Null);
            Assert.That(PsdAiModelCatalog.Validate(PsdAiProvider.Zhipu, "custom-unknown-vision", PsdAiReasoningEffort.Low), Is.Not.Null,
                "手工智谱型号仍须遵守供应商推理参数约束。");
            Assert.That(PsdAiModelCatalog.GetModels(PsdAiProvider.DeepSeek).Select(model => model.Id), Is.EqualTo(deepSeekIds));
            Assert.That(PsdAiModelCatalog.Current, Is.SameAs(deepSeekModels));
        }
    }
}
