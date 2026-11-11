using System;
using System.IO;
using AAGame.Assets;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AAGame.Editor.EnvironmentSetup
{
    public sealed class EnvironmentSetupWindow : EditorWindow
    {
        private Label _status;
        private VisualElement _tools;
        private VisualElement _actions;
        private Label _packages;
        private TextField _log;
        private bool _requestedEmptyToolsDetection;
        private Label _currentBackend;
        private EnumField _targetField;
        private Button _repairCurrent;
        private Button _updateCurrent;
        private HelpBox _resourcesUpdateHint;
        [SerializeField] private AssetBackendKind _targetBackend;
        [SerializeField] private AssetBackendKind _lastEffectiveBackend;
        [SerializeField] private bool _targetInitialized;
        [SerializeField] private bool _switchWasBusy;

        [MenuItem("Tools/项目工具/资源后端与环境")]
        public static void Open()
        {
            var window = GetWindow<EnvironmentSetupWindow>();
            window.titleContent = new GUIContent("资源后端与环境");
            window.minSize = new Vector2(660, 500);
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            var scroll = new ScrollView();
            scroll.style.paddingLeft = 12;
            scroll.style.paddingRight = 12;
            rootVisualElement.Add(scroll);
            scroll.Add(new HelpBox("项目只保留当前资源后端所需 SDK。切换时自动归档旧配置、卸载旧 SDK 并构建目标资源。基础工具只安装缺失项；系统可能请求 UAC 授权。", HelpBoxMessageType.Info));
            scroll.Add(new HelpBox(EnvironmentInstaller.PackageTrustNotice + "拒绝后若目标包缺失，切换会按失败流程恢复原后端。", HelpBoxMessageType.Info));
            _currentBackend = new Label { name = "currentBackend" };
            scroll.Add(_currentBackend);
            _actions = new VisualElement { name = "backendActions" };
            scroll.Add(_actions);
            if (!_targetInitialized)
            {
                _targetBackend = CurrentBackend();
                _lastEffectiveBackend = _targetBackend;
                _targetInitialized = true;
            }
            _targetField = new EnumField("目标后端", _targetBackend) { name = "targetBackend" };
            _targetField.RegisterValueChangedCallback(change =>
            {
                _targetBackend = (AssetBackendKind)change.newValue;
                EditorUtility.SetDirty(this);
            });
            _actions.Add(_targetField);
            AddAction("切换并准备资源", () => BeginOperation(_targetBackend, false), "switchBackend");
            _repairCurrent = AddAction("检查并修复当前后端", () => BeginOperation(CurrentBackend(), false), "repairCurrentBackend");
            _updateCurrent = AddAction("更新当前后端 SDK", () => BeginOperation(CurrentBackend(), true), "updateCurrentBackend");
            _resourcesUpdateHint = new HelpBox("Resources 是 Unity 内置能力，无独立 SDK 可更新；可使用检查并修复当前后端同步资源。", HelpBoxMessageType.Info) { name = "resourcesUpdateHint" };
            scroll.Add(_resourcesUpdateHint);
            _status = new Label { name = "taskStatus" };
            _status.style.whiteSpace = WhiteSpace.Normal;
            scroll.Add(_status);
            _tools = new VisualElement { name = "toolStatuses" };
            scroll.Add(_tools);
            _packages = new Label { name = "installedPackages" };
            _packages.style.whiteSpace = WhiteSpace.Normal;
            scroll.Add(_packages);
            AddAction("重新检测环境", EnvironmentInstaller.Detect, "detectEnvironment");
            AddAction("安装或修复 Node.js / npm", () => EnvironmentInstaller.Start(InstallPreset.Node), "installNode");
            AddAction("补齐 OpenUPM 所需环境", () => EnvironmentInstaller.Start(InstallPreset.OpenUpm), "installOpenUpm");
            var controls = new VisualElement();
            controls.style.flexDirection = FlexDirection.Row;
            controls.Add(new Button(() => { if (BackendSwitchCoordinator.IsBusy) BackendSwitchCoordinator.Cancel(); else EnvironmentInstaller.Cancel(); }) { text = "取消后续步骤", name = "cancelTask" });
            controls.Add(new Button(() =>
            {
                if (BackendSwitchCoordinator.IsBusy || EnvironmentInstaller.IsBusy) return;
                if (BackendSwitchCoordinator.RecoveryRequired) BackendSwitchCoordinator.Retry();
                else if (!string.IsNullOrEmpty(EnvironmentInstaller.LastError)) EnvironmentInstaller.Retry();
                else if (!string.IsNullOrEmpty(BackendSwitchCoordinator.LastError)) BackendSwitchCoordinator.Retry();
                else EnvironmentInstaller.Retry();
            }) { text = "重试失败步骤", name = "retryTask" });
            controls.Add(new Button(() => EditorUtility.RevealInFinder(EnvironmentInstaller.LogPath)) { text = "查看日志文件", name = "openLog" });
            scroll.Add(controls);
            _log = new TextField("安装日志") { multiline = true, isReadOnly = true, name = "installLog" };
            _log.style.minHeight = 160;
            scroll.Add(_log);
            rootVisualElement.schedule.Execute(Render).Every(500);
            Render();
            if (!EnvironmentInstaller.IsBusy && !BackendSwitchCoordinator.IsBusy && !BackendSwitchCoordinator.RecoveryRequired) EnvironmentInstaller.Detect();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("资源后端与环境");
            EnvironmentInstaller.Changed += Render;
        }
        private void OnDisable() { EnvironmentInstaller.Changed -= Render; }
        private Button AddAction(string text, Action action, string elementName)
        {
            var button = new Button(action) { text = text, name = elementName };
            _actions.Add(button);
            return button;
        }

        private void BeginOperation(AssetBackendKind target, bool update)
        {
            BackendSwitchCoordinator.Start(target, true, update);
            if (BackendSwitchCoordinator.IsBusy) Render();
        }

        private static AssetBackendKind CurrentBackend() =>
            AssetDatabase.LoadAssetAtPath<AssetBackendSettings>(DemoResourceSetup.SettingsPath)?.Backend ?? AssetBackendKind.Resources;

        private void Render()
        {
            if (_status == null) return;
            var settings = AssetDatabase.LoadAssetAtPath<AssetBackendSettings>(DemoResourceSetup.SettingsPath);
            var current = settings != null ? settings.Backend : AssetBackendKind.Resources;
            bool switching = BackendSwitchCoordinator.IsBusy || BackendSwitchCoordinator.RecoveryRequired;
            if (switching)
            {
                var pending = BackendSwitchCoordinator.PendingTarget;
                if (pending.HasValue) _targetBackend = pending.Value;
                _switchWasBusy = true;
            }
            else if (_switchWasBusy)
            {
                if (string.IsNullOrEmpty(BackendSwitchCoordinator.LastError)) _targetBackend = current;
                _switchWasBusy = false;
            }
            else if (_lastEffectiveBackend != current && _targetBackend == _lastEffectiveBackend)
                _targetBackend = current;
            _lastEffectiveBackend = current;
            _currentBackend.text = "当前生效后端：" + (settings != null ? current.ToString() : "未配置");
            _targetField.SetValueWithoutNotify(_targetBackend);
            _repairCurrent.SetEnabled(settings != null);
            _updateCurrent.SetEnabled(settings != null && current != AssetBackendKind.Resources);
            _resourcesUpdateHint.style.display = current == AssetBackendKind.Resources ? DisplayStyle.Flex : DisplayStyle.None;
            if (!_requestedEmptyToolsDetection && EnvironmentInstaller.Tools.Length == 0 &&
                !EnvironmentInstaller.IsBusy && !BackendSwitchCoordinator.IsBusy && !BackendSwitchCoordinator.RecoveryRequired &&
                string.IsNullOrEmpty(EnvironmentInstaller.LastError))
            {
                _requestedEmptyToolsDetection = true;
                EnvironmentInstaller.Detect();
            }
            if (BackendSwitchCoordinator.IsBusy || BackendSwitchCoordinator.RecoveryRequired)
                _status.text = BackendSwitchCoordinator.Status + "\n" + BackendSwitchCoordinator.LastError;
            else if (EnvironmentInstaller.IsBusy || !string.IsNullOrEmpty(EnvironmentInstaller.LastError))
                _status.text = EnvironmentInstaller.Status + "\n" + EnvironmentInstaller.LastError;
            else if (!string.IsNullOrEmpty(BackendSwitchCoordinator.LastError))
                _status.text = BackendSwitchCoordinator.Status + "\n" + BackendSwitchCoordinator.LastError;
            else _status.text = EnvironmentInstaller.Status;
            _actions.SetEnabled(!EnvironmentInstaller.IsBusy && !switching && !EditorApplication.isPlayingOrWillChangePlaymode);
            bool anyBusy = BackendSwitchCoordinator.IsBusy || EnvironmentInstaller.IsBusy;
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            rootVisualElement.Q<Button>("cancelTask").SetEnabled(anyBusy && !playing);
            rootVisualElement.Q<Button>("retryTask").SetEnabled(!anyBusy && !playing &&
                (BackendSwitchCoordinator.RecoveryRequired || !string.IsNullOrEmpty(BackendSwitchCoordinator.LastError) ||
                 !string.IsNullOrEmpty(EnvironmentInstaller.LastError) || EnvironmentInstaller.Status.Contains("取消")));
            _tools.Clear();
            foreach (var tool in EnvironmentInstaller.Tools)
            {
                var label = new Label(tool.Name + "：" + (tool.Available ? tool.Version : tool.Error) + "\n路径：" + tool.Path +
                    (tool.Candidates.Length > 1 ? "\n检测到多个路径：" + string.Join("；", tool.Candidates) : "") +
                    (string.IsNullOrEmpty(tool.Details) ? "" : "\n" + tool.Details));
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.marginBottom = 8;
                _tools.Add(label);
            }
            // 注册快照不会向 UPM 发起请求，程序集重载后也不会沿用旧缓存。
            var registeredPackages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            _packages.text = "项目依赖：";
            foreach (var id in new[] { EnvironmentInstaller.AddressablesId, EnvironmentInstaller.AndroidId, EnvironmentInstaller.YooAssetId })
            {
                var found = Array.Find(registeredPackages, p => p.name == id);
                _packages.text += "\n" + id + "：" + (found == null ? "未安装" : found.version + "\n" + found.resolvedPath);
            }
            if (File.Exists(EnvironmentInstaller.LogPath))
            {
                try
                {
                    using var stream = new FileStream(EnvironmentInstaller.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (stream.Length > 24000) stream.Seek(-24000, SeekOrigin.End);
                    using var reader = new StreamReader(stream);
                    _log.SetValueWithoutNotify(reader.ReadToEnd());
                }
                catch (IOException) { }
            }
        }
    }
}
