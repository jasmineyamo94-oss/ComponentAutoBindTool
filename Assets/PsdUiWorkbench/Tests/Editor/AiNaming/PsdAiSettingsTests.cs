using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using PsdUiWorkbench.AiNaming;
using PsdUiWorkbench.Core;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdAiSettingsTests
    {
        public sealed class FocusHost : EditorWindow { }

        private sealed class ProcessCredentialScope : IDisposable
        {
            private const string PreferenceKey = "PsdUiWorkbench.AiNaming.LocalApiKey.v1";
            private const string EnvironmentKey = "DEEPSEEK_API_KEY";
            private readonly bool _hadLocalKey = EditorPrefs.HasKey(PreferenceKey);
            private readonly string _localKey = EditorPrefs.GetString(PreferenceKey, "");
            private readonly string _environmentKey = Environment.GetEnvironmentVariable(EnvironmentKey, EnvironmentVariableTarget.Process);

            public void Set(string local, string environment)
            {
                if (local == null) EditorPrefs.DeleteKey(PreferenceKey);
                else EditorPrefs.SetString(PreferenceKey, local);
                Environment.SetEnvironmentVariable(EnvironmentKey, environment, EnvironmentVariableTarget.Process);
            }

            public void Dispose()
            {
                try
                {
                    if (_hadLocalKey) EditorPrefs.SetString(PreferenceKey, _localKey);
                    else EditorPrefs.DeleteKey(PreferenceKey);
                }
                finally
                {
                    Environment.SetEnvironmentVariable(EnvironmentKey, _environmentKey, EnvironmentVariableTarget.Process);
                }
            }
        }

        private static void Click(VisualElement root, string name)
        {
            var button = root.Q<Button>(name);
            button.Focus();
            using (var submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = button;
                button.SendEvent(submit);
            }
        }

        private static IEnumerator Refocus(PsdAiNamingSettingsWindow window, FocusHost other)
        {
            other.Focus();
            yield return null;
            window.Focus();
            window.rootVisualElement.Q<Button>("saveSettings").Focus();
            yield return null;
            window.rootVisualElement.Q<TextField>("key").Focus();
            yield return null;
            yield return null;
        }

        private sealed class Client : IPsdAiNamingClient
        {
            public IEnumerator Analyze(PsdAiNamingSnapshot snapshot, PsdAiRequestSettings settings,
                Action<IReadOnlyList<PsdAiSuggestion>> completed)
            {
                while (true) yield return null;
            }
            public void Dispose() { }
        }

        private sealed class Preview : IPsdAiPreviewProvider
        {
            public PsdAiPreviewResult Capture(PsdDocumentSnapshot document, IReadOnlyList<int> targets,
                PsdVisibilityState visibility, Func<string, Sprite> resolve)
                => new PsdAiPreviewResult(Array.Empty<PsdAiImage>(), Array.Empty<int>());
        }

        private static PsdSession Session()
        {
            var session = new PsdSession();
            session.Refresh(new PsdDocumentSnapshot("fake", 100, 100, new[]
            {
                new PsdNode(1, null, "Node", false, false, true, false, "", new PixelRect(0, 0, 20, 20), 0)
            }));
            return session;
        }

        [TestCase(null, null, PsdAiCredentialSource.None, "")]
        [TestCase(" \t ", " \t ", PsdAiCredentialSource.None, "")]
        [TestCase(null, " environment-test-key ", PsdAiCredentialSource.Environment, "environment-test-key")]
        [TestCase(" \t ", " environment-test-key ", PsdAiCredentialSource.Environment, "environment-test-key")]
        [TestCase(" local-test-key ", null, PsdAiCredentialSource.Local, "local-test-key")]
        [TestCase(" local-test-key ", " \t ", PsdAiCredentialSource.Local, "local-test-key")]
        [TestCase(" local-test-key ", "environment-test-key", PsdAiCredentialSource.Local, "local-test-key")]
        public void CredentialSourceMatchesResolutionWhitespaceAndLocalPrecedence(string local, string environment,
            PsdAiCredentialSource expectedSource, string expectedKey)
        {
            var credentials = new ProcessCredentialScope();
            try
            {
                credentials.Set(local, environment);
                Assert.That(PsdAiCredentials.Source, Is.EqualTo(expectedSource));
                Assert.That(string.Equals(PsdAiCredentials.ResolveKey(), expectedKey, StringComparison.Ordinal),
                    Is.True, "解析结果应遵循本机优先、去除首尾空白与缺省空值语义，不输出凭据。");
            }
            finally
            {
                credentials.Dispose();
            }
        }

        [Test]
        public void LegacySerializedSettingsStillProduceDeepSeekRequests()
        {
            var settings = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            try
            {
                // 仅写入旧资产已有的序列化字段，不通过按供应商路由的公共 setter 构造测试数据。
                var serialized = new SerializedObject(settings);
                serialized.FindProperty("_model").stringValue = "legacy-custom-vision";
                serialized.FindProperty("_reasoningEffort").intValue = (int)PsdAiReasoningEffort.High;
                serialized.FindProperty("_timeoutSeconds").intValue = 45;
                serialized.FindProperty("_rules").stringValue = "保留旧规则";
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var snapshot = settings.Snapshot();
                Assert.That(snapshot.Provider, Is.EqualTo(PsdAiProvider.DeepSeek));
                Assert.That(snapshot.Model, Is.EqualTo("legacy-custom-vision"));
                Assert.That(snapshot.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.High));
                Assert.That(snapshot.TimeoutSeconds, Is.EqualTo(45));
                Assert.That(snapshot.Rules, Is.EqualTo("保留旧规则"));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void SwitchingProvidersPreservesIndependentModelsAndEffortsAndInFlightSnapshot()
        {
            var settings = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            try
            {
                settings.Provider = PsdAiProvider.DeepSeek;
                settings.Model = "deepseek-custom-vision";
                settings.ReasoningEffort = PsdAiReasoningEffort.Low;
                var deepSeekRequest = settings.Snapshot();

                settings.Provider = PsdAiProvider.Zhipu;
                settings.Model = "glm-4.6v";
                settings.ReasoningEffort = PsdAiReasoningEffort.High;
                var zhipuRequest = settings.Snapshot();

                settings.Provider = PsdAiProvider.DeepSeek;
                Assert.That(settings.Model, Is.EqualTo("deepseek-custom-vision"));
                Assert.That(settings.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.Low));
                settings.Model = "deepseek-changed";
                settings.Provider = PsdAiProvider.Zhipu;
                Assert.That(settings.Model, Is.EqualTo("glm-4.6v"));
                Assert.That(settings.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.High));
                Assert.That(deepSeekRequest.Provider, Is.EqualTo(PsdAiProvider.DeepSeek));
                Assert.That(deepSeekRequest.Model, Is.EqualTo("deepseek-custom-vision"));
                Assert.That(deepSeekRequest.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.Low));
                Assert.That(zhipuRequest.Provider, Is.EqualTo(PsdAiProvider.Zhipu));
                Assert.That(zhipuRequest.Model, Is.EqualTo("glm-4.6v"));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void ProviderCredentialsResolveIndependentlyAndLegacyApiStillTargetsDeepSeek()
        {
            const string zhipuPreference = "PsdUiWorkbench.AiNaming.Zhipu.LocalApiKey.v1";
            bool hadZhipuKey = EditorPrefs.HasKey(zhipuPreference);
            string zhipuKey = EditorPrefs.GetString(zhipuPreference, "");
            string zhipuEnvironment = Environment.GetEnvironmentVariable("ZHIPU_API_KEY", EnvironmentVariableTarget.Process);
            using (var deepSeek = new ProcessCredentialScope())
            {
                try
                {
                    deepSeek.Set("deepseek-local", "deepseek-environment");
                    EditorPrefs.DeleteKey(zhipuPreference);
                    Environment.SetEnvironmentVariable("ZHIPU_API_KEY", " zhipu-environment ", EnvironmentVariableTarget.Process);

                    Assert.That(PsdAiCredentials.GetSource(PsdAiProvider.Zhipu), Is.EqualTo(PsdAiCredentialSource.Environment));
                    Assert.That(PsdAiCredentials.ResolveKey(PsdAiProvider.Zhipu) == "zhipu-environment", Is.True);
                    PsdAiCredentials.SetLocalKey(PsdAiProvider.Zhipu, " zhipu-local ");
                    Assert.That(PsdAiCredentials.GetSource(PsdAiProvider.Zhipu), Is.EqualTo(PsdAiCredentialSource.Local));
                    Assert.That(PsdAiCredentials.ResolveKey(PsdAiProvider.Zhipu) == "zhipu-local", Is.True);
                    Assert.That(PsdAiCredentials.ResolveKey() == "deepseek-local", Is.True);
                    Assert.That(PsdAiCredentials.GetLocalKey() == PsdAiCredentials.GetLocalKey(PsdAiProvider.DeepSeek), Is.True);
                    Assert.That(PsdAiCredentials.Source, Is.EqualTo(PsdAiCredentials.GetSource(PsdAiProvider.DeepSeek)));

                    PsdAiCredentials.SetLocalKey("legacy-updated");
                    Assert.That(PsdAiCredentials.ResolveKey(PsdAiProvider.DeepSeek) == "legacy-updated", Is.True);
                    Assert.That(PsdAiCredentials.ResolveKey(PsdAiProvider.Zhipu) == "zhipu-local", Is.True);
                    PsdAiCredentials.SetLocalKey(PsdAiProvider.Zhipu, "");
                    Environment.SetEnvironmentVariable("ZHIPU_API_KEY", null, EnvironmentVariableTarget.Process);
                    Assert.That(PsdAiCredentials.GetSource(PsdAiProvider.Zhipu), Is.EqualTo(PsdAiCredentialSource.None));
                    Assert.That(string.IsNullOrEmpty(PsdAiCredentials.ResolveKey(PsdAiProvider.Zhipu)), Is.True,
                        "智谱不得回退到其他供应商的密钥。");
                }
                finally
                {
                    if (hadZhipuKey) EditorPrefs.SetString(zhipuPreference, zhipuKey);
                    else EditorPrefs.DeleteKey(zhipuPreference);
                    Environment.SetEnvironmentVariable("ZHIPU_API_KEY", zhipuEnvironment, EnvironmentVariableTarget.Process);
                }
            }
        }

        [UnityTest]
        public IEnumerator SavedCredentialIsExplicitWithEmptyInputAndBlankSavePreservesIt()
        {
            var credentials = new ProcessCredentialScope();
            var settings = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            string settingsBefore = EditorJsonUtility.ToJson(settings);
            bool wasDirty = EditorUtility.IsDirty(settings);
            PsdAiNamingSettingsWindow window = null;
            try
            {
                credentials.Set("saved-local-test-key", "environment-test-key");
                using (var controller = new PsdAiNamingController(Session(), _ => null, settings, new Client(), new Preview()))
                {
                    window = PsdAiNamingSettingsWindow.Open(controller);
                    yield return null;
                    yield return null;
                    var root = window.rootVisualElement;
                    var key = root.Q<TextField>("key");
                    Assert.That(key.value, Is.Empty);
                    Assert.That(key.isPasswordField, Is.True);
                    Assert.That(root.Q<Label>("credentialStatus").text, Is.EqualTo("本机Key已保存（无需重复输入）"));
                    Assert.That(root.Q<Label>("credentialDisclaimer").text, Does.Contain("未联网验证有效性"));
                    foreach (string blank in new[] { "", " \t " })
                    {
                        key.value = blank;
                        Click(root, "saveKey");
                        Assert.That(string.Equals(PsdAiCredentials.GetLocalKey(), "saved-local-test-key", StringComparison.Ordinal),
                            Is.True, "空白保存不能删除或替换已有本机凭据。");
                        Assert.That(root.Q<Label>("credentialStatus").text, Is.EqualTo("本机Key已保存（无需重复输入）"));
                    }
                    key.value = " replacement-local-test-key ";
                    Click(root, "saveKey");
                    Assert.That(key.value, Is.Empty, "保存完成后不回显新凭据。");
                    Assert.That(string.Equals(PsdAiCredentials.GetLocalKey(), "replacement-local-test-key", StringComparison.Ordinal), Is.True);
                    Assert.That(string.Equals(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"), "environment-test-key", StringComparison.Ordinal), Is.True);
                    Assert.That(root.Q<Label>("credentialStatus").text, Is.EqualTo("本机Key已保存（无需重复输入）"));
                    Assert.That(EditorJsonUtility.ToJson(settings), Is.EqualTo(settingsBefore),
                        "凭据操作不能将状态或凭据写入设置。");
                    Assert.That(EditorUtility.IsDirty(settings), Is.EqualTo(wasDirty));
                    Assert.That(AssetDatabase.Contains(settings), Is.False);
                    window.Close();
                    window = null;
                    key.value = "detached-test-key";
                    Click(root, "saveKey");
                    Assert.That(string.Equals(PsdAiCredentials.GetLocalKey(), "replacement-local-test-key", StringComparison.Ordinal),
                        Is.True, "关闭后的旧按钮不能再保存凭据。");
                }
            }
            finally
            {
                try
                {
                    if (window != null) window.Close();
                    UnityEngine.Object.DestroyImmediate(settings);
                }
                finally
                {
                    credentials.Dispose();
                }
            }
        }

        [UnityTest]
        public IEnumerator CredentialStatusRefreshesOnFocusWithoutPrefillingOrSavingSettings()
        {
            var credentials = new ProcessCredentialScope();
            var settings = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            string settingsBefore = EditorJsonUtility.ToJson(settings);
            bool wasDirty = EditorUtility.IsDirty(settings);
            PsdAiNamingSettingsWindow window = null;
            FocusHost other = null;
            try
            {
                credentials.Set(null, null);
                using (var controller = new PsdAiNamingController(Session(), _ => null, settings, new Client(), new Preview()))
                {
                    other = ScriptableObject.CreateInstance<FocusHost>();
                    other.ShowUtility();
                    window = PsdAiNamingSettingsWindow.Open(controller);
                    yield return null;
                    yield return null;
                    var root = window.rootVisualElement;
                    Assert.That(root.Q<Label>("credentialStatus").text, Is.EqualTo("未配置（请输入并保存）"));
                    credentials.Set(" \t ", "environment-test-key");
                    yield return Refocus(window, other);
                    Assert.That(root.Q<Label>("credentialStatus").text, Is.EqualTo("正在使用DEEPSEEK_API_KEY环境变量"));
                    Assert.That(root.Q<TextField>("key").value, Is.Empty);
                    Click(root, "saveKey");
                    Assert.That(PsdAiCredentials.Source, Is.EqualTo(PsdAiCredentialSource.Environment));
                    Assert.That(root.Q<Label>("credentialStatus").text, Is.EqualTo("正在使用DEEPSEEK_API_KEY环境变量"));
                    root.Q<TextField>("key").value = "new-local-test-key";
                    Click(root, "saveKey");
                    Assert.That(root.Q<TextField>("key").value, Is.Empty);
                    Assert.That(root.Q<Label>("credentialStatus").text, Is.EqualTo("本机Key已保存（无需重复输入）"));
                    credentials.Set(" \t ", " \t ");
                    yield return Refocus(window, other);
                    Assert.That(root.Q<Label>("credentialStatus").text, Is.EqualTo("未配置（请输入并保存）"));
                    Assert.That(root.Q<TextField>("key").value, Is.Empty);
                    Assert.That(EditorJsonUtility.ToJson(settings), Is.EqualTo(settingsBefore));
                    Assert.That(EditorUtility.IsDirty(settings), Is.EqualTo(wasDirty));
                    window.Close();
                    window = null;
                }
            }
            finally
            {
                try
                {
                    if (window != null) window.Close();
                    if (other != null) other.Close();
                    UnityEngine.Object.DestroyImmediate(settings);
                }
                finally
                {
                    credentials.Dispose();
                }
            }
        }

        [UnityTest]
        public IEnumerator SelectingFlashFromCatalogOrManualFieldEnablesThinkingWithoutChangingOtherProvider()
        {
            var settings = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            settings.Model = "deepseek-custom-vision";
            settings.ReasoningEffort = PsdAiReasoningEffort.Low;
            settings.Provider = PsdAiProvider.Zhipu;
            settings.Model = "glm-4.6v";
            settings.ReasoningEffort = PsdAiReasoningEffort.None;
            PsdAiNamingSettingsWindow window = null;
            int changes = 0;
            using (var controller = new PsdAiNamingController(Session(), _ => null, settings, new Client(), new Preview()))
            {
                try
                {
                    window = PsdAiNamingSettingsWindow.Open(controller, _ => changes++);
                    yield return null;
                    yield return null;
                    var root = window.rootVisualElement;
                    Assert.That(changes, Is.Zero, "显示和格式化型号不能作为用户编辑写回设置。");
                    foreach (string model in new[] { "glm-5.3-flash", "glm-5.3-flashx" })
                    {
                        root.Q<EnumField>("effort").value = PsdAiReasoningEffort.Low;
                        root.Q<DropdownField>("modelChoice").value = model;
                        Assert.That(settings.Model, Is.EqualTo(model));
                        Assert.That(settings.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.High));
                        Assert.That(root.Q<EnumField>("effort").value, Is.EqualTo(PsdAiReasoningEffort.High));
                        Assert.That(root.Q<Label>("notice").text, Does.Contain("已设为高"));
                        var snapshot = new PsdAiNamingSnapshot("{}", Array.Empty<string>(), Array.Empty<PsdAiImage>());
                        string body = Encoding.UTF8.GetString(PsdAiProtocol.BuildBody(snapshot, settings.Snapshot()));
                        Assert.That(body, Does.Contain("\"thinking\":{\"type\":\"enabled\"}"));

                        root.Q<TextField>("model").value = "custom-unknown-vision";
                        root.Q<EnumField>("effort").value = PsdAiReasoningEffort.Max;
                        root.Q<TextField>("model").value = model;
                        Assert.That(settings.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.High),
                            "手动输入已知型号也应消除不兼容的推理设置。");
                        root.Q<EnumField>("effort").value = PsdAiReasoningEffort.None;
                        Assert.That(root.Q<Label>("modelStatus").text, Does.Contain("请将推理设为高"));
                        root.Q<EnumField>("effort").value = PsdAiReasoningEffort.High;
                        Assert.That(PsdAiModelCatalog.Validate(settings.Provider, settings.Model, settings.ReasoningEffort), Is.Null);
                    }

                    root.Q<DropdownField>("modelChoice").value = "glm-4.6v";
                    root.Q<EnumField>("effort").value = PsdAiReasoningEffort.None;
                    Assert.That(PsdAiModelCatalog.Validate(settings.Provider, settings.Model, settings.ReasoningEffort), Is.Null);
                    int beforeSwitch = changes;
                    root.Q<EnumField>("provider").value = PsdAiProvider.DeepSeek;
                    Assert.That(settings.Model, Is.EqualTo("deepseek-custom-vision"));
                    Assert.That(settings.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.Low));
                    Assert.That(changes, Is.EqualTo(beforeSwitch + 1),
                        "切换供应商只能产生一次配置编辑，重建下拉选项不得递归写回显示文案。");
                    Assert.That(root.Q<TextField>("model").value, Is.EqualTo("deepseek-custom-vision"));
                    Assert.That(root.Q<DropdownField>("modelChoice").value, Is.EqualTo("deepseek-custom-vision"));
                    root.Q<TextField>("model").value = "glm-5.3-flash";
                    Assert.That(settings.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.Low),
                        "相同型号名称在其他供应商下不能触发智谱专属自动调整。");
                    root.Q<EnumField>("provider").value = PsdAiProvider.Zhipu;
                    Assert.That(settings.Model, Is.EqualTo("glm-4.6v"));
                    Assert.That(settings.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.None));
                    Assert.That(root.Q<DropdownField>("modelChoice").value, Is.EqualTo("glm-4.6v"));
                    root.Q<EnumField>("provider").value = PsdAiProvider.DeepSeek;
                    Assert.That(settings.Model, Is.EqualTo("glm-5.3-flash"));
                    Assert.That(settings.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.Low));
                }
                finally
                {
                    if (window != null) window.Close();
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            }
        }

        [UnityTest]
        public IEnumerator ExistingManualFlashSettingsArePreservedAndExplainHowToFixIncompatibleEffort()
        {
            var first = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            var second = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            first.Provider = second.Provider = PsdAiProvider.Zhipu;
            first.Model = "glm-5.3-flash";
            second.Model = "glm-5.3-flashx";
            first.ReasoningEffort = PsdAiReasoningEffort.None;
            second.ReasoningEffort = PsdAiReasoningEffort.Max;
            PsdAiNamingSettingsWindow window = null;
            using (var controller = new PsdAiNamingController(Session(), _ => null, first, new Client(), new Preview()))
            {
                int changes = 0;
                try
                {
                    window = PsdAiNamingSettingsWindow.Open(controller, _ => changes++);
                    yield return null;
                    yield return null;
                    var root = window.rootVisualElement;
                    Assert.That(first.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.None),
                        "打开已有资产不能静默改写用户选项。");
                    Assert.That(changes, Is.Zero);
                    Assert.That(root.Q<DropdownField>("modelChoice").value, Is.EqualTo("glm-5.3-flash"));
                    Assert.That(root.Q<Label>("modelStatus").text, Does.Contain("请将推理设为高"));
                    root.Q<ObjectField>("settingsAsset").value = second;
                    Assert.That(second.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.Max));
                    Assert.That(changes, Is.EqualTo(1));
                    Assert.That(root.Q<DropdownField>("modelChoice").value, Is.EqualTo("glm-5.3-flashx"));
                    Assert.That(root.Q<Label>("modelStatus").text, Does.Contain("请将推理设为高"));
                    root.Q<EnumField>("effort").value = PsdAiReasoningEffort.High;
                    var snapshot = new PsdAiNamingSnapshot("{}", Array.Empty<string>(), Array.Empty<PsdAiImage>());
                    string body = Encoding.UTF8.GetString(PsdAiProtocol.BuildBody(snapshot, second.Snapshot()));
                    Assert.That(body, Does.Contain("\"thinking\":{\"type\":\"enabled\"}"));
                    Assert.That(first.ReasoningEffort, Is.EqualTo(PsdAiReasoningEffort.None));
                }
                finally
                {
                    if (window != null) window.Close();
                    UnityEngine.Object.DestroyImmediate(first);
                    UnityEngine.Object.DestroyImmediate(second);
                }
            }
        }

        [UnityTest]
        public IEnumerator OpeningAndSwitchingSettingsDoesNotPersistOrRefreshAndClosingDetachesFields()
        {
            var first = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            var second = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            first.Model = "manual-first";
            second.Model = "manual-second";
            string previousStatus = PsdAiModelCatalog.Status;
            bool previouslyRefreshing = PsdAiModelCatalog.IsRefreshing;
            var defaultBefore = PsdAiNamingSettings.LoadDefault();
            PsdAiNamingSettingsWindow window = null;
            using (var controller = new PsdAiNamingController(Session(), _ => null, first, new Client(), new Preview()))
            {
                int changes = 0;
                int closes = 0;
                try
                {
                    window = PsdAiNamingSettingsWindow.Open(controller, settings => changes++, () => closes++);
                    yield return null;
                    yield return null;
                    var root = window.rootVisualElement;
                    Assert.That(root.Q<TextField>("key").value, Is.Empty);
                    Assert.That(root.Q<TextField>("model").value, Is.EqualTo("manual-first"));
                    Assert.That(root.Q<DropdownField>("modelChoice").value, Is.EqualTo("manual-first"));
                    Assert.That(root.Q<DropdownField>("modelChoice").choices, Does.Contain("manual-first"));
                    var assetField = root.Q<ObjectField>("settingsAsset");
                    assetField.value = second;
                    Assert.That(controller.Settings, Is.SameAs(second));
                    Assert.That(changes, Is.EqualTo(1));
                    Assert.That(root.Q<TextField>("model").value, Is.EqualTo("manual-second"));
                    Assert.That(AssetDatabase.Contains(first), Is.False);
                    Assert.That(AssetDatabase.Contains(second), Is.False);
                    Assert.That(PsdAiNamingSettings.LoadDefault(), Is.SameAs(defaultBefore));
                    Assert.That(PsdAiModelCatalog.Status, Is.EqualTo(previousStatus));
                    Assert.That(PsdAiModelCatalog.IsRefreshing, Is.EqualTo(previouslyRefreshing));
                    window.Close();
                    window = null;
                    assetField.value = first;
                    Assert.That(controller.Settings, Is.SameAs(second));
                    Assert.That(changes, Is.EqualTo(1), "关闭窗口后旧字段不能继续通知拥有者。");
                    Assert.That(closes, Is.EqualTo(1));
                }
                finally
                {
                    if (window != null) window.Close();
                    UnityEngine.Object.DestroyImmediate(first);
                    UnityEngine.Object.DestroyImmediate(second);
                }
            }
        }

        [UnityTest]
        public IEnumerator BusyControllerRejectsConfigChangesAndWindowCloseDoesNotCancelIt()
        {
            var settings = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            var other = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            settings.Model = "manual-busy";
            settings.Rules = "保留规则";
            PsdAiNamingSettingsWindow window = null;
            using (var controller = new PsdAiNamingController(Session(), _ => null, settings, new Client(), new Preview()))
            {
                try
                {
                    window = PsdAiNamingSettingsWindow.Open(controller);
                    yield return null;
                    yield return null;
                    controller.Start(new[] { 1 }, false);
                    Assert.That(controller.IsBusy, Is.True);
                    var root = window.rootVisualElement;
                    Assert.That(root.Q<VisualElement>("settingsContent").enabledSelf, Is.False);
                    root.Q<TextField>("model").value = "must-not-change";
                    root.Q<TextField>("rules").value = "must-not-change";
                    root.Q<ObjectField>("settingsAsset").value = other;
                    Assert.That(controller.Settings, Is.SameAs(settings));
                    Assert.That(settings.Model, Is.EqualTo("manual-busy"));
                    Assert.That(settings.Rules, Is.EqualTo("保留规则"));
                    window.Close();
                    window = null;
                    Assert.That(controller.IsBusy, Is.True, "设置窗口不拥有控制器，也不能取消命名任务。");
                    controller.Cancel();
                }
                finally
                {
                    if (window != null) window.Close();
                    UnityEngine.Object.DestroyImmediate(settings);
                    UnityEngine.Object.DestroyImmediate(other);
                }
            }
        }
    }
}
