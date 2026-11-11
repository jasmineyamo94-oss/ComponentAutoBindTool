using System;
using AAGame.Assets;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using Debug = UnityEngine.Debug;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace AAGame.Editor.EnvironmentSetup
{
    public enum InstallPreset { All, Addressables, YooAsset, Node, OpenUpm, AddressablesOnly, AndroidOnly }

    [FilePath("Library/AAGameEnvironment/Task.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class EnvironmentInstallState : ScriptableSingleton<EnvironmentInstallState>
    {
        public string[] Steps = Array.Empty<string>();
        public int Index;
        public bool Active;
        public bool Cancel;
        public string Status = "尚未检测";
        public string Error;
        public string Phase;
        public string Target;
        public string ResultFile;
        public int ProcessId;
        public long Deadline;
        public bool Reboot;
        public void Persist() { Save(true); }
    }

    [InitializeOnLoad]
    public static class EnvironmentInstaller
    {
        public const string AddressablesId = "com.unity.addressables";
        public const string AndroidId = "com.unity.addressables.android";
        public const string YooAssetId = "com.tuyoogame.yooasset";
        public const string PackageTrustNotice = "通过 OpenUPM 安装 YooAsset 时，Unity 可能显示第三方包信任确认。请从任务栏恢复正确的 Unity 编辑器窗口，核对来源后自行决定；本工具不代替或绕过该确认。";
        private static readonly string Project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static bool _tickRunning;
        private static bool _preparing;
        private static double _nextTick;
        private static SearchRequest _search;
        private static AddRequest _add;
        private static ListRequest _list;
        public static ToolStatus[] Tools { get; private set; } = Array.Empty<ToolStatus>();
        public static PackageInfo[] Packages { get; private set; } = Array.Empty<PackageInfo>();
        public static bool IsBusy => _preparing || EnvironmentInstallState.instance.Active;
        public static string Status => EnvironmentInstallState.instance.Status;
        public static string LastError => EnvironmentInstallState.instance.Error;
        public static string LogPath => Path.Combine(Project, "Library/AAGameEnvironment/install.log");
        public static event Action Changed;

        static EnvironmentInstaller()
        {
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode && IsBusy)
                {
                    EditorApplication.isPlaying = false;
                    Debug.LogWarning("环境检测或安装正在进行，请等待结束后进入运行模式");
                }
            };
        }

        public static bool IsSupportedNode(string version)
        {
            return Version.TryParse((version ?? "").Trim().TrimStart('v'), out var parsed) && parsed.Major >= 20;
        }

        public static string[] PlanSteps(InstallPreset preset, ToolStatus[] tools)
        {
            if (preset == InstallPreset.All)
                throw new InvalidOperationException("当前后端依赖由互斥切换协调器规划，不能生成共存安装清单。");
            bool Available(string name) => tools.Any(t => t.Name == name && t.Available && (name != "node" || IsSupportedNode(t.Version)));
            var steps = new List<string>();
            if (preset == InstallPreset.Node) return new[] { "NodeRepair" };
            if (preset == InstallPreset.YooAsset || preset == InstallPreset.OpenUpm)
            {
                if (!Available("node") || !Available("npm")) steps.Add(Available("node") ? "NodeRepair" : "Node");
                if (!Available("openupm")) steps.Add("OpenUpm");
            }
            if (preset == InstallPreset.Addressables)
            {
                steps.Add(AddressablesId);
                steps.Add(AndroidId);
            }
            if (preset == InstallPreset.AddressablesOnly) steps.Add(AddressablesId);
            if (preset == InstallPreset.AndroidOnly) steps.Add(AndroidId);
            if (preset == InstallPreset.YooAsset) steps.Add("YooAsset");
            return steps.ToArray();
        }

        public static async void Detect()
        {
            if (IsBusy || BackendSwitchCoordinator.IsBusy) return;
            _preparing = true;
            try
            {
                Tools = await EnvironmentProbe.ScanAsync();
                _list = Client.List(false, true);
                while (!_list.IsCompleted) await Task.Delay(100);
                if (_list.Status != StatusCode.Success) throw new IOException(_list.Error?.message);
                Packages = _list.Result.ToArray();
                EnvironmentInstallState.instance.Error = "";
                EnvironmentInstallState.instance.Status = "检测完成";
            }
            catch (Exception exception) { EnvironmentInstallState.instance.Error = "检测失败：" + exception.Message; }
            finally { _preparing = false; Changed?.Invoke(); }
        }

        public static void Start(InstallPreset preset, bool confirm = true)
        {
            if (IsBusy || BackendSwitchCoordinator.IsBusy || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (preset == InstallPreset.Node || preset == InstallPreset.OpenUpm)
            {
                StartInternal(preset, confirm);
                return;
            }
            var target = preset == InstallPreset.All ? DemoResourceSetup.EnsureSettings().Backend
                : preset == InstallPreset.YooAsset ? AssetBackendKind.YooAsset : AssetBackendKind.Addressables;
            BackendSwitchCoordinator.Start(target, confirm, preset != InstallPreset.All);
        }

        // 仅协调器内部使用：避免 SDK 安装再次进入完整切换流程。
        internal static void StartForSwitch(InstallPreset preset)
        {
            if (preset != InstallPreset.Addressables && preset != InstallPreset.YooAsset)
                throw new ArgumentException("切换内部安装仅支持目标 SDK。", nameof(preset));
            StartInternal(preset, false);
        }

        private static async void StartInternal(InstallPreset preset, bool confirm)
        {
            if (IsBusy || EditorApplication.isPlayingOrWillChangePlaymode) return;
            EnvironmentInstallState.instance.Error = "";
            EnvironmentInstallState.instance.Cancel = false;
            EnvironmentInstallState.instance.Persist();
            _preparing = true;
            try
            {
                Tools = await EnvironmentProbe.ScanAsync();
                if (EnvironmentInstallState.instance.Cancel)
                {
                    EnvironmentInstallState.instance.Status = "已取消环境准备，未启动后续安装";
                    EnvironmentInstallState.instance.Persist();
                    return;
                }
                var steps = PlanSteps(preset, Tools);
                if (steps.Length == 0) { EnvironmentInstallState.instance.Status = "所需工具已可用"; return; }
                var description = string.Join("\n", steps.Select(DisplayStep));
                if (steps.Any(s => s.StartsWith("Node", StringComparison.Ordinal)))
                {
                    var node = Tools.FirstOrDefault(t => t.Name == "node");
                    description += "\n\n当前 Node.js 路径：" + (node?.Path ?? "未发现") + "\n" + node?.Details;
                    description += "\n将安装独立 Node.js LTS；不会卸载版本管理器。多套安装可能改变命令解析路径，请核对上述路径。";
                }
                if (confirm && !EditorUtility.DisplayDialog("安装清单", description + "\n\n将安装或更新上述项目，可能需要 Windows 授权。已完成步骤可复用。", "开始安装", "取消")) return;
                var state = EnvironmentInstallState.instance;
                state.Steps = steps;
                state.Index = 0;
                state.Active = true;
                state.Error = "";
                state.Phase = "";
                state.Reboot = false;
                state.Status = "准备安装";
                state.Persist();
            }
            catch (Exception exception) { Fail(exception); }
            finally { _preparing = false; Changed?.Invoke(); }
        }

        public static void Cancel()
        {
            var state = EnvironmentInstallState.instance;
            state.Cancel = true;
            state.Status = "取消已请求：等待已启动操作结束后停止后续步骤";
            state.Persist();
        }

        public static void Retry()
        {
            var state = EnvironmentInstallState.instance;
            if (IsBusy || BackendSwitchCoordinator.IsBusy) return;
            bool yooTask = state.Steps.Any(step => step == "YooAsset" || step == YooAssetId);
            bool addressablesTask = state.Steps.Any(step => step == AddressablesId || step == AndroidId);
            if (yooTask || addressablesTask)
            {
                // SDK 任务必须保留互斥事务与回滚语义，不能直接重新激活旧安装队列。
                if (BackendSwitchCoordinator.RecoveryRequired || !string.IsNullOrEmpty(BackendSwitchCoordinator.LastError))
                    BackendSwitchCoordinator.Retry();
                else if (yooTask && addressablesTask)
                    throw new InvalidOperationException("旧共存安装任务不能独立重试，请重新选择单个目标资源后端。");
                else BackendSwitchCoordinator.Start(yooTask ? AssetBackendKind.YooAsset : AssetBackendKind.Addressables);
                return;
            }
            if (state.Index >= state.Steps.Length) return;
            state.Active = true;
            state.Cancel = false;
            state.Error = "";
            state.Reboot = false;
            state.Phase = "";
            state.Persist();
        }

        private static async void Update()
        {
            if (_tickRunning || _preparing || !EnvironmentInstallState.instance.Active || EditorApplication.isCompiling || EditorApplication.timeSinceStartup < _nextTick) return;
            _nextTick = EditorApplication.timeSinceStartup + 0.5;
            _tickRunning = true;
            try { await Tick(); }
            catch (Exception exception) { Fail(exception); }
            finally { _tickRunning = false; Changed?.Invoke(); }
        }

        private static async Task Tick()
        {
            var state = EnvironmentInstallState.instance;
            if (state.Index >= state.Steps.Length || (state.Cancel && state.Phase.Length == 0))
            {
                state.Active = false;
                state.Status = state.Cancel ? "已取消后续安装" : state.Reboot ? "安装已完成，Windows 要求重启；请保存工作后自行重启" : "全部步骤完成";
                state.Persist();
                return;
            }
            var step = state.Steps[state.Index];
            if (state.Phase.Length > 0 && state.Phase != "external" && DateTime.UtcNow.Ticks > state.Deadline)
                throw new TimeoutException("UPM 操作超时，请检查包管理器和网络后重试");
            if (state.Phase.Length == 0)
            {
                state.Status = "正在处理：" + DisplayStep(step);
                if (step.StartsWith("com.", StringComparison.Ordinal))
                {
                    _search = Client.Search(step, false);
                    state.Phase = "search";
                }
                else
                {
                    if (step == "YooAsset") Debug.LogWarning(PackageTrustNotice);
                    LaunchExternal(step, state);
                    state.Phase = "external";
                }
                state.Deadline = DateTime.UtcNow.AddMinutes(15).Ticks;
                state.Persist();
                return;
            }
            if (state.Phase == "external")
            {
                if (!File.Exists(state.ResultFile))
                {
                    try { using var process = Process.GetProcessById(state.ProcessId); if (!process.HasExited) return; }
                    catch (ArgumentException) { }
                    throw new IOException("安装进程已经结束，但未生成结果；请查看日志后重试");
                }
                var result = JsonUtility.FromJson<ExternalResult>(File.ReadAllText(state.ResultFile));
                if (!result.Success) throw new IOException(result.Error);
                state.Reboot |= result.Reboot;
                Tools = await EnvironmentProbe.ScanAsync();
                if (step == "YooAsset")
                {
                    // OpenUPM 已写入国际源和精确版本。使用可观察的 AddRequest 等待解析，
                    // 不再把 void Resolve 与下一帧的 List 并发提交给 UPM。
                    // 此处仍可能等待 Unity 自身的第三方包信任模态框，不能由本工具确认。
                    state.Status = "等待 YooAsset 安装与注册。" + PackageTrustNotice;
                    state.Target = YooAssetId + "@" + ManifestPackageVersion(YooAssetId);
                    state.Phase = "verify";
                    _list = null;
                    state.Persist();
                    _add = Client.Add(state.Target);
                }
                else
                {
                    var name = step == "OpenUpm" ? "openupm" : "node";
                    if (!Tools.Any(t => t.Name == name && t.Available)) throw new IOException("安装结束后工具仍不可用，请检查 PATH、权限和日志");
                    if (name == "node" && !Tools.Any(t => t.Name == name && t.Available && IsSupportedNode(t.Version)))
                        throw new IOException("PATH 优先使用的 Node.js 版本仍不满足要求；请查看检测出的多个路径或版本管理器，再重试");
                    if (name == "node" && !Tools.Any(t => t.Name == "npm" && t.Available)) throw new IOException("Node.js 已安装，但 npm 检测失败；请使用修复入口");
                    CompleteStep();
                }
                return;
            }
            if (state.Phase == "search")
            {
                if (_search == null) { _search = Client.Search(step, false); return; }
                if (!_search.IsCompleted) return;
                if (state.Cancel)
                {
                    state.Phase = "";
                    state.Persist();
                    return;
                }
                if (_search.Status != StatusCode.Success) throw new IOException("兼容版本查询失败：" + _search.Error?.message);
                var package = _search.Result.First(p => p.name == step);
                var versions = package.versions.compatible.Where(v => !v.Contains("-") && Version.TryParse(v, out _))
                    .OrderBy(v => new Version(v)).ToArray();
                var version = versions.Contains(package.versions.latestCompatible) ? package.versions.latestCompatible : versions.LastOrDefault();
                if (string.IsNullOrEmpty(version)) throw new IOException("当前 Unity 未找到兼容的正式版：" + step);
                state.Target = step + "@" + version;
                state.Phase = "verify";
                state.Persist();
                _add = Client.Add(state.Target);
                _list = null;
                return;
            }
            if (state.Phase == "verify")
            {
                if (_add != null)
                {
                    if (!_add.IsCompleted) return;
                    if (_add.Status != StatusCode.Success) throw new IOException("UPM 安装失败：" + _add.Error?.message);
                    _add = null;
                }
                if (step == "YooAsset")
                {
                    // 程序集重载会丢失 AddRequest；只观察已注册包，不再发起 UPM 查询或重复解析。
                    // 同时兼容更新前保存的未带版本事务，补齐 OpenUPM 已写入的精确目标。
                    if (!state.Target.Contains("@"))
                    {
                        state.Target = YooAssetId + "@" + ManifestPackageVersion(YooAssetId);
                        state.Persist();
                    }
                    Packages = PackageInfo.GetAllRegisteredPackages();
                }
                else
                {
                    if (_list == null) { _list = Client.List(false, true); return; }
                    if (!_list.IsCompleted) return;
                    if (_list.Status != StatusCode.Success) throw new IOException("UPM 检测失败：" + _list.Error?.message);
                    Packages = _list.Result.ToArray();
                    _list = null;
                }
                var parts = state.Target.Split('@');
                if (Packages.Any(p => p.name == parts[0] && (parts.Length == 1 || p.version == parts[1])))
                {
                    CompleteStep();
                    return;
                }
            }
            if (DateTime.UtcNow.Ticks > state.Deadline) throw new TimeoutException("UPM 操作超时，请检查包管理器和网络后重试");
        }

        private static string ManifestPackageVersion(string id)
        {
            var manifest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(Project, "Packages/manifest.json")));
            string version = (string)manifest["dependencies"]?[id];
            if (string.IsNullOrEmpty(version) || !Version.TryParse(version, out _))
                throw new IOException("OpenUPM 未写入有效的正式版本：" + id);
            return version;
        }

        private static void LaunchExternal(string step, EnvironmentInstallState state)
        {
            var folder = Path.Combine(Project, "Library/AAGameEnvironment");
            Directory.CreateDirectory(folder);
            state.ResultFile = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
            var script = Path.Combine(Project, "Assets/AAGame/Editor/Environment/InstallEnvironment.ps1");
            string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";
            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(script) + " -Step " + step + " -Project " + Quote(Project) + " -Result " + Quote(state.ResultFile) + " -Log " + Quote(LogPath),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.EnvironmentVariables["PATH"] = EnvironmentProbe.RefreshedPath();
            using var process = Process.Start(info);
            if (process == null) throw new IOException("无法启动后台安装器");
            state.ProcessId = process.Id;
        }

        private static void CompleteStep()
        {
            var state = EnvironmentInstallState.instance;
            state.Index++;
            state.Phase = "";
            if (state.Reboot)
            {
                state.Active = false;
                state.Status = "Windows 要求重启。请保存工作后自行重启，再点击重试继续剩余步骤";
            }
            state.Persist();
        }

        private static void Fail(Exception exception)
        {
            var state = EnvironmentInstallState.instance;
            state.Active = false;
            state.Error = "环境任务失败：" + exception.Message;
            if (state.Reboot) state.Error += "；Windows 要求重启，请保存工作后自行重启再重试";
            state.Status = state.Error;
            state.Persist();
            Debug.LogError(state.Error);
        }

        public static string DisplayStep(string step)
        {
            switch (step)
            {
                case "Node": return "安装 Node.js 最新 LTS（包含 npm）";
                case "NodeRepair": return "修复安装 Node.js/npm（可能更新已有 Node.js，请确认使用路径）";
                case "OpenUpm": return "安装 OpenUPM CLI 最新版";
                case "YooAsset": return "通过 OpenUPM 国际源安装 YooAsset 最新正式版。" + PackageTrustNotice;
                default: return "查询并安装当前 Unity 兼容最新正式版：" + step;
            }
        }

        [Serializable]
        private sealed class ExternalResult
        {
            public bool Success;
            public bool Reboot;
            public string Error;
        }
    }
}
