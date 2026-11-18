using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace PsdUiWorkbench.AiNaming
{
    public sealed class PsdAiNamingSettingsWindow : EditorWindow
    {
        private PsdAiNamingController _controller;
        private Action<PsdAiNamingSettings> _settingsChanged;
        private Action _closed;
        private PsdAiNamingSettingsView _view;

        public static PsdAiNamingSettingsWindow Open(PsdAiNamingController controller,
            Action<PsdAiNamingSettings> settingsChanged = null, Action closed = null)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            var window = CreateInstance<PsdAiNamingSettingsWindow>();
            window.titleContent = new GUIContent("AI 设置");
            window.minSize = new Vector2(480, 460);
            window._controller = controller;
            window._settingsChanged = settingsChanged;
            window._closed = closed;
            window.ShowUtility();
            return window;
        }

        public void CreateGUI()
        {
            _view?.Dispose();
            _view = null;
            rootVisualElement.Clear();
            if (_controller == null)
            {
                rootVisualElement.Add(new Label("请从工作台的“AI 设置”按钮重新打开此窗口。"));
                return;
            }
            _view = new PsdAiNamingSettingsView(_controller, settings => _settingsChanged?.Invoke(settings));
            rootVisualElement.Add(_view);
        }

        private void OnFocus()
        {
            _view?.RefreshCredentialStatus();
        }

        private void OnDisable()
        {
            _view?.Dispose();
            _view = null;
            _controller = null;
            _settingsChanged = null;
            var closed = _closed;
            _closed = null;
            closed?.Invoke();
        }
    }

    internal sealed class PsdAiNamingSettingsView : VisualElement, IDisposable
    {
        private const string LayoutPath = "Assets/PsdUiWorkbench/Editor/AiNaming/Presentation/PsdAiNamingSettingsWindow.uxml";
        private readonly PsdAiNamingController _controller;
        private readonly List<Action> _unbind = new List<Action>();
        private Action<PsdAiNamingSettings> _settingsChanged;
        private bool _disposed;
        private bool _refreshing;
        private PsdAiProvider? _credentialProvider;

        public PsdAiNamingSettingsView(PsdAiNamingController controller, Action<PsdAiNamingSettings> settingsChanged)
        {
            _controller = controller;
            _settingsChanged = settingsChanged;
            AddToClassList("ai-settings-host");
            var layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            if (layout == null) throw new InvalidOperationException("AI 设置窗口布局缺失。");
            layout.CloneTree(this);
            var asset = this.Q<ObjectField>("settingsAsset");
            asset.objectType = typeof(PsdAiNamingSettings);
            asset.allowSceneObjects = false;
            Track(asset, e =>
            {
                if (e.newValue is PsdAiNamingSettings settings) SwitchSettings(settings);
                else Refresh();
            });
            this.Q<EnumField>("provider").Init(PsdAiProvider.DeepSeek);
            Track(this.Q<EnumField>("provider"), e =>
            {
                this.Q<TextField>("key").SetValueWithoutNotify("");
                Notice("");
                EditSettings(settings => settings.Provider = (PsdAiProvider)e.newValue);
            });
            this.Q<EnumField>("effort").Init(PsdAiReasoningEffort.None);
            Track(this.Q<TextField>("model"), e => SelectModel(e.newValue));
            Track(this.Q<DropdownField>("modelChoice"), e => SelectModel(e.newValue));
            Track(this.Q<EnumField>("effort"), e => EditSettings(settings =>
            {
                settings.ReasoningEffort = (PsdAiReasoningEffort)e.newValue;
                Notice("");
            }));
            Track(this.Q<IntegerField>("timeout"), e => EditSettings(settings => settings.TimeoutSeconds = Math.Max(10, Math.Min(600, e.newValue))));
            Track(this.Q<TextField>("rules"), e => EditSettings(settings => settings.Rules = e.newValue));
            // 不回显已存凭据；空白输入不会替换本机或环境变量中的 Key。
            this.Q<TextField>("key").isPasswordField = true;
            Click("createSettings", CreateDefaultSettings);
            Click("saveSettings", SaveSettings);
            Click("saveKey", () => Safe(() =>
            {
                var key = this.Q<TextField>("key");
                if (string.IsNullOrWhiteSpace(key.value))
                {
                    RefreshCredentialStatus();
                    Notice("未输入新的 Key，已有凭据保持不变。");
                    return;
                }
                PsdAiCredentials.SetLocalKey(_controller.Settings.Provider, key.value);
                key.SetValueWithoutNotify("");
                RefreshCredentialStatus();
                Notice("本机 Key 已保存，不会写入设置资产。");
            }));
            Click("refreshModels", () => Safe(() => PsdAiModelCatalog.Refresh(
                _controller.Settings.Provider, PsdAiCredentials.ResolveKey(_controller.Settings.Provider), _controller.Settings.TimeoutSeconds)));
            _controller.Changed += Refresh;
            PsdAiModelCatalog.Changed += Refresh;
            Undo.undoRedoPerformed += Refresh;
            EventCallback<FocusInEvent> onFocusIn = _ => RefreshCredentialStatus();
            RegisterCallback(onFocusIn);
            _unbind.Add(() => UnregisterCallback(onFocusIn));
            Refresh();
        }

        private void Click(string name, Action action)
        {
            var button = this.Q<Button>(name);
            Action guarded = () => { if (!_disposed && !_controller.IsBusy) action(); };
            button.clicked += guarded;
            _unbind.Add(() => button.clicked -= guarded);
        }

        private void Track<T>(BaseField<T> field, EventCallback<ChangeEvent<T>> callback)
        {
            EventCallback<ChangeEvent<T>> guarded = e =>
            {
                if (!_refreshing && ReferenceEquals(e.target, field)) callback(e);
            };
            field.RegisterValueChangedCallback(guarded);
            _unbind.Add(() => field.UnregisterValueChangedCallback(guarded));
        }

        private void Refresh()
        {
            if (_disposed || _refreshing) return;
            _refreshing = true;
            try { RefreshFields(); }
            finally { _refreshing = false; }
        }

        private void RefreshFields()
        {
            if (_disposed) return;
            var settings = _controller.Settings;
            this.Q<ObjectField>("settingsAsset").SetValueWithoutNotify(settings);
            this.Q<Label>("assetStatus").text = AssetDatabase.Contains(settings)
                ? "正在编辑已有设置；点击保存后仅保存此资产。"
                : "正在编辑临时设置；打开窗口不会创建资产，可创建默认设置或另存。";
            this.Q<EnumField>("provider").SetValueWithoutNotify(settings.Provider);
            this.Q<Label>("modelHelp").text = settings.Provider == PsdAiProvider.Zhipu
                ? "智谱 GLM-5.3-Flash/FlashX 仅支持高（启用思考）；选择这两个型号时自动设为高。已有不兼容设置请手动改为高；旧型号仍支持关闭／高。内置视觉型号每次最多 50 张图片，单张须小于 5 MiB；手工型号的图片和参数能力需自行核实，不发送 DeepSeek 专属推理强度参数。"
                : "列出服务返回的全部型号，不代表全部支持图片。已知不支持图片的型号不能用于命名；能力未知需自行核实。";
            this.Q<TextField>("model").SetValueWithoutNotify(settings.Model);
            this.Q<EnumField>("effort").SetValueWithoutNotify(settings.ReasoningEffort);
            this.Q<IntegerField>("timeout").SetValueWithoutNotify(settings.TimeoutSeconds);
            this.Q<TextField>("rules").SetValueWithoutNotify(settings.Rules);
            var models = PsdAiModelCatalog.GetModels(settings.Provider);
            var choices = this.Q<DropdownField>("modelChoice");
            choices.choices = PsdAiModelCatalog.BuildModelChoices(models, settings.Model);
            choices.formatListItemCallback = id => PsdAiModelCatalog.FormatModelChoice(models, id);
            choices.formatSelectedValueCallback = id => PsdAiModelCatalog.FormatModelChoice(models, id);
            choices.SetValueWithoutNotify(settings.Model ?? "");
            string validation = PsdAiModelCatalog.Validate(settings.Provider, settings.Model, settings.ReasoningEffort);
            this.Q<Label>("modelStatus").text = PsdAiModelCatalog.GetStatus(settings.Provider) + "\n" + PsdAiModelCatalog.GetSourceSummary(settings.Provider) +
                "\n当前型号：" + PsdAiModelCatalog.FormatModelChoice(models, settings.Model) +
                (string.IsNullOrEmpty(validation) ? "" : "\n" + validation);
            this.Q<VisualElement>("settingsContent").SetEnabled(!_controller.IsBusy);
            this.Q<Label>("busyStatus").EnableInClassList("ai-settings-hidden", !_controller.IsBusy);
            this.Q<Button>("refreshModels").SetEnabled(!_controller.IsBusy &&
                settings.Provider == PsdAiProvider.DeepSeek && !PsdAiModelCatalog.IsRefreshing);
            RefreshCredentialStatus();
        }

        public void RefreshCredentialStatus()
        {
            if (_disposed) return;
            var provider = _controller.Settings.Provider;
            if (_credentialProvider != provider)
            {
                this.Q<TextField>("key").SetValueWithoutNotify("");
                _credentialProvider = provider;
            }
            switch (PsdAiCredentials.GetSource(provider))
            {
                case PsdAiCredentialSource.Local:
                    this.Q<Label>("credentialStatus").text = provider == PsdAiProvider.DeepSeek
                        ? "本机Key已保存（无需重复输入）" : "智谱本机 Key 已保存（无需重复输入）";
                    break;
                case PsdAiCredentialSource.Environment:
                    this.Q<Label>("credentialStatus").text = provider == PsdAiProvider.DeepSeek
                        ? "正在使用DEEPSEEK_API_KEY环境变量" : "正在使用 ZHIPU_API_KEY 环境变量";
                    break;
                default:
                    this.Q<Label>("credentialStatus").text = "未配置（请输入并保存）";
                    break;
            }
        }

        private void SwitchSettings(PsdAiNamingSettings settings)
        {
            if (_disposed || _controller.IsBusy) { Refresh(); return; }
            _controller.SetSettings(settings);
            this.Q<TextField>("key").SetValueWithoutNotify("");
            Notice("");
            _settingsChanged?.Invoke(settings);
            Refresh();
        }

        private void SelectModel(string model)
        {
            EditSettings(settings =>
            {
                settings.Model = model;
                if (PsdAiModelCatalog.RequiresThinking(settings.Provider, model))
                {
                    settings.ReasoningEffort = PsdAiReasoningEffort.High;
                    Notice("该型号仅支持启用思考，推理已设为高（启用）。");
                }
                else Notice("");
            });
        }

        private void EditSettings(Action<PsdAiNamingSettings> change)
        {
            if (_disposed || _controller.IsBusy) { Refresh(); return; }
            var settings = _controller.Settings;
            Undo.RecordObject(settings, "修改 AI 命名设置");
            change(settings);
            EditorUtility.SetDirty(settings);
            _settingsChanged?.Invoke(settings);
            Refresh();
        }

        private void CreateDefaultSettings()
        {
            Safe(() =>
            {
                var existing = PsdAiNamingSettings.LoadDefault();
                if (existing != null)
                {
                    SwitchSettings(existing);
                    Notice("已切换至已有默认设置，未覆盖其内容。");
                    return;
                }
                var source = _controller.Settings;
                bool preserve = !AssetDatabase.Contains(source);
                var created = PsdAiNamingSettings.CreateDefault();
                if (preserve)
                {
                    EditorUtility.CopySerialized(source, created);
                    created.hideFlags = HideFlags.None;
                    EditorUtility.SetDirty(created);
                    AssetDatabase.SaveAssetIfDirty(created);
                }
                SwitchSettings(created);
                Notice("默认设置已创建并保存。");
            });
        }

        private void SaveSettings()
        {
            Safe(() =>
            {
                var settings = _controller.Settings;
                if (!AssetDatabase.Contains(settings))
                {
                    string path = EditorUtility.SaveFilePanelInProject("保存 AI 命名设置", "PsdAiNamingSettings", "asset", "仅保存当前设置资产。");
                    if (string.IsNullOrEmpty(path)) return;
                    if (AssetDatabase.LoadMainAssetAtPath(path) != null || File.Exists(path) || Directory.Exists(path) ||
                        File.Exists(path + ".meta") || Directory.Exists(path + ".meta"))
                    {
                        Notice("目标路径已存在，请选择新的资产路径；不会覆盖已有文件。");
                        return;
                    }
                    var copy = UnityEngine.Object.Instantiate(settings);
                    copy.hideFlags = HideFlags.None;
                    try { AssetDatabase.CreateAsset(copy, path); }
                    catch
                    {
                        if (!AssetDatabase.Contains(copy)) UnityEngine.Object.DestroyImmediate(copy);
                        throw;
                    }
                    SwitchSettings(copy);
                    settings = copy;
                }
                AssetDatabase.SaveAssetIfDirty(settings);
                Notice("当前设置已保存。");
            });
        }

        private void Notice(string text)
        {
            var notice = this.Q<Label>("notice");
            notice.text = text;
            notice.EnableInClassList("ai-settings-hidden", string.IsNullOrWhiteSpace(text));
        }

        private void Safe(Action action)
        {
            if (_disposed || _controller.IsBusy) return;
            try { action(); }
            catch (Exception)
            {
                Notice("操作未完成。请检查文件路径、设置或凭据后重试；未显示敏感内容。");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _controller.Changed -= Refresh;
            PsdAiModelCatalog.Changed -= Refresh;
            Undo.undoRedoPerformed -= Refresh;
            foreach (var unbind in _unbind) unbind();
            _unbind.Clear();
            this.Q<TextField>("key").SetValueWithoutNotify("");
            _settingsChanged = null;
            SetEnabled(false);
        }
    }
}
