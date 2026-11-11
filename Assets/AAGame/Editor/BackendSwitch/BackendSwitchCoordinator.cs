using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using AAGame.Assets;
using AAGame.Editor.EnvironmentSetup;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[Serializable]
internal sealed class BackendSwitchState
{
    private static BackendSwitchState _instance;
    public static BackendSwitchState instance => _instance ??= Load();
    private static string StatePath => Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "Library/ProjectToolsBackendSwitch/Task.json");
    public bool Active;
    public bool CancelRequested;
    public bool UpdateSdk;
    public bool RollingBack;
    public bool RecoveryRequired;
    public AssetBackendKind Target;
    public AssetBackendKind Original;
    public string Phase = "";
    public string Status = "尚未切换";
    public string Error = "";
    public string Snapshot = "";
    public long Deadline;
    public long RollbackPackagesMatchedAt;
    public string[] OriginalSdkPackages = Array.Empty<string>();
    public void Persist()
    {
        string path = StatePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonUtility.ToJson(this, true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static BackendSwitchState Load()
    {
        Exception failure = null;
        foreach (string file in new[] { StatePath, StatePath + ".bak" })
        {
            if (!File.Exists(file)) continue;
            try
            {
                string raw = File.ReadAllText(file);
                var json = JObject.Parse(raw);
                if (new[] { "Active", "Phase", "Snapshot", "Target", "Original" }.Any(key => json[key] == null))
                    throw new IOException("切换状态 JSON 缺少必要字段。");
                var loaded = JsonUtility.FromJson<BackendSwitchState>(raw);
                if (loaded == null) throw new IOException("状态 JSON 为空。");
                Validate(loaded);
                return loaded;
            }
            catch (Exception error) { failure = error; }
        }
        if (failure != null)
            return new BackendSwitchState { RecoveryRequired = true, Status = "切换状态文件损坏，请保留文件并恢复备份", Error = failure.Message };
        string legacy = Path.ChangeExtension(StatePath, ".asset");
        if (!File.Exists(legacy)) return new BackendSwitchState();
        try
        {
            // 旧 ScriptableSingleton 的 m_Script=fileID:0 无法可靠跨编辑器进程加载。
            // 这里只迁移确定的任务字段；原始 YAML 及事务快照均不删除。
            string yaml = File.ReadAllText(legacy);
            string Value(string key)
            {
                var match = Regex.Match(yaml, @"(?m)^  " + Regex.Escape(key) + @":[ \t]*([^\r\n]*)");
                if (!match.Success) return "";
                string value = match.Groups[1].Value.Trim();
                return value.StartsWith("\"") ? Newtonsoft.Json.JsonConvert.DeserializeObject<string>(value) : value;
            }
            bool Flag(string key) => Value(key) == "1";
            var state = new BackendSwitchState
            {
                Active = Flag("Active"), CancelRequested = Flag("CancelRequested"),
                UpdateSdk = Flag("UpdateSdk"), RollingBack = Flag("RollingBack"),
                RecoveryRequired = Flag("RecoveryRequired"),
                Target = (AssetBackendKind)int.Parse(Value("Target")), Original = (AssetBackendKind)int.Parse(Value("Original")),
                Phase = Value("Phase"), Snapshot = Value("Snapshot"),
                Deadline = long.Parse(Value("Deadline")), Status = "已从旧状态恢复：" + Value("Phase")
            };
            var packageList = Regex.Match(yaml, @"(?m)^  OriginalSdkPackages:\s*\r?\n((?:  - [^\r\n]*\r?\n?)*)");
            if (packageList.Success)
                state.OriginalSdkPackages = Regex.Matches(packageList.Groups[1].Value, @"(?m)^  - (.*)$")
                    .Cast<Match>().Select(m => m.Groups[1].Value.Trim()).ToArray();
            Validate(state);
            state.Persist();
            return state;
        }
        catch (Exception error)
        {
            return new BackendSwitchState { RecoveryRequired = true, Status = "旧切换状态迁移失败，原文件和快照已保留", Error = error.Message };
        }
    }

    private static void Validate(BackendSwitchState state)
    {
        if (!Enum.IsDefined(typeof(AssetBackendKind), state.Target) || !Enum.IsDefined(typeof(AssetBackendKind), state.Original))
            throw new IOException("状态包含未知后端。");
        state.OriginalSdkPackages ??= Array.Empty<string>();
        state.Status ??= "已加载切换状态";
        state.Error ??= "";
        state.Phase ??= "";
        state.Snapshot ??= "";
        if (state.Active && (string.IsNullOrEmpty(state.Phase) || string.IsNullOrEmpty(state.Snapshot)))
            throw new IOException("活动任务缺少阶段或回滚快照，拒绝覆盖。");
    }
}

/// <summary>项目后端切换事务；所有 SDK 特有类型只通过可选编辑器程序集调用。</summary>
[InitializeOnLoad]
public static class BackendSwitchCoordinator
{
    private const string Aa = "com.unity.addressables";
    private const string Android = "com.unity.addressables.android";
    private const string Yoo = "com.tuyoogame.yooasset";
    private static ListRequest _list;
    private static AddAndRemoveRequest _remove;
    private static double _nextTick;
    private static bool _executing;
    internal static Action<string> BeforeStage;

    public static bool IsBusy => BackendSwitchState.instance.Active;
    public static string Status => BackendSwitchState.instance.Status;
    public static string LastError => BackendSwitchState.instance.Error;
    public static bool RecoveryRequired => BackendSwitchState.instance.RecoveryRequired;
    internal static AssetBackendKind? PendingTarget => IsBusy || RecoveryRequired ? BackendSwitchState.instance.Target : (AssetBackendKind?)null;
    public static event Action Changed;

    static BackendSwitchCoordinator()
    {
        EditorApplication.update += Update;
        EditorApplication.playModeStateChanged += change =>
        {
            if (change == PlayModeStateChange.ExitingEditMode && (IsBusy || RecoveryRequired))
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("资源后端正在切换或等待恢复，请完成任务后再运行。");
            }
        };
    }

    public static void Start(AssetBackendKind target, bool confirm = true, bool updateSdk = false)
    {
        if (IsBusy || EnvironmentInstaller.IsBusy || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("已有安装、切换或运行任务，请等待结束。");
        if (RecoveryRequired)
            throw new InvalidOperationException("旧切换尚未恢复，请先点击重试恢复原后端。");
        if (!Enum.IsDefined(typeof(AssetBackendKind), target)) throw new ArgumentOutOfRangeException(nameof(target));
        var state = BackendSwitchState.instance;
        var settings = DemoResourceSetup.EnsureSettings();
        bool dirtyScenes = Enumerable.Range(0, SceneManager.sceneCount).Any(i => SceneManager.GetSceneAt(i).isDirty);
        bool dirtyAssets = RelatedAssets().Any(EditorUtility.IsDirty);
        if (dirtyScenes || dirtyAssets)
        {
            if (!confirm) throw new InvalidOperationException("存在未保存场景或相关配置，自动任务不会覆盖它们。");
            if (!EditorUtility.DisplayDialog("未保存的配置", "切换需要保存当前场景及资源后端相关配置。", "保存并继续", "取消")) return;
            if (!EditorSceneManager.SaveOpenScenes()) return;
            foreach (var asset in RelatedAssets()) AssetDatabase.SaveAssetIfDirty(asset);
        }
        if (confirm && !EditorUtility.DisplayDialog("切换资源后端", Preview(target, updateSdk), "开始切换", "取消")) return;
        state.Target = target;
        state.Original = settings.Backend;
        state.UpdateSdk = updateSdk;
        state.OriginalSdkPackages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
            .Where(p => new[] { Aa, Android, Yoo }.Contains(p.name)).Select(p => p.name + "@" + p.version).ToArray();
        state.Error = "";
        state.CancelRequested = false;
        state.RollingBack = false;
        state.Snapshot = "Library/ProjectToolsBackendSwitch/Transactions/" + Guid.NewGuid().ToString("N");
        // 快照失败时尚未改动任何后端，不能进入一个没有回滚点的任务。
        try { BackendProfileStore.CaptureSnapshot(state.Snapshot); }
        catch (Exception error)
        {
            state.Active = false;
            state.Phase = "";
            state.Status = "创建回滚快照失败，原后端未改动，可重试";
            state.Error = "快照失败：" + error.Message;
            state.Persist();
            Changed?.Invoke();
            throw;
        }
        state.Active = true;
        SetPhase("prepare", "检查目标依赖");
        if (confirm) EnvironmentSetupWindow.Open();
    }

    public static string Preview(AssetBackendKind target, bool updateSdk = false)
    {
        if (!Enum.IsDefined(typeof(AssetBackendKind), target)) throw new ArgumentOutOfRangeException(nameof(target));
        var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
        var selected = AssetDatabase.LoadAssetAtPath<AssetBackendSettings>(DemoResourceSetup.SettingsPath);
        if (!updateSdk && selected != null && selected.Backend == target && Exclusive(packages.Select(p => p.name).ToArray(), target) && Ready(target))
            return "目标后端：" + target + "\n当前依赖、配置和内容已通过幂等检查，本次无需安装、卸载、归档或重新构建。";
        string[] required = target == AssetBackendKind.Addressables ? new[] { Aa, Android }
            : target == AssetBackendKind.YooAsset ? new[] { Yoo } : Array.Empty<string>();
        bool needsInstall = updateSdk || !required.All(id => packages.Any(p => p.name == id));
        var text = new StringBuilder("目标后端：" + target + "；平台：" + EditorUserBuildSettings.activeBuildTarget + "\n\n依赖操作：");
        foreach (string id in required)
        {
            var installed = packages.FirstOrDefault(p => p.name == id);
            if (installed == null) text.Append("\n安装：").Append(id).Append("（动态查询兼容最新正式版）");
            else text.Append(needsInstall ? "\n重新查询并安装：" : "\n复用：").Append(id).Append("，当前 ").Append(installed.version);
        }
        var removals = packages.Where(p => new[] { Aa, Android, Yoo }.Contains(p.name) && !required.Contains(p.name)).ToArray();
        foreach (var package in removals) text.Append("\n卸载：").Append(package.name).Append("@").Append(package.version);
        if (required.Length == 0 && removals.Length == 0) text.Append("\n无 SDK 需要安装或卸载。");
        text.Append("\n\n配置与产物操作：");
        foreach (AssetBackendKind kind in Enum.GetValues(typeof(AssetBackendKind)))
        {
            string root = kind == AssetBackendKind.Resources ? DemoResourceSetup.ResourcesRoot.TrimEnd('/') :
                kind == AssetBackendKind.Addressables ? "Assets/AddressableAssetsData" : "Assets/BundleCollectorSetting.asset";
            bool live = File.Exists(root) || Directory.Exists(root) || File.Exists(root + ".meta");
            string profile = BackendProfileStore.ProfileRoot + "/" + kind;
            if (kind != target)
            {
                if (live) text.Append("\n校验归档：").Append(root).Append(" → ").Append(profile).Append("（含 .meta）");
                string[] outputs = kind == AssetBackendKind.Addressables
                    ? new[] { "Library/com.unity.addressables/aa", "Library/com.unity.addressables/Android/addressables_content_state.bin" }
                    : kind == AssetBackendKind.YooAsset
                        ? new[] { "Assets/StreamingAssets/yoo/AAGameDemo", "Library/AAGameContent/YooAsset" } : Array.Empty<string>();
                foreach (string output in outputs.Where(p => File.Exists(p) || Directory.Exists(p))) text.Append("\n清理：").Append(output);
            }
            else if (selected != null && selected.Backend == target && LiveConfigurationValid(target))
                text.Append("\n复用当前有效配置，不重放旧归档：").Append(root);
            else if (File.Exists(profile + "/manifest.json"))
            {
                text.Append("\n校验并恢复归档：").Append(profile).Append(" → ").Append(root);
                if (live) text.Append("（目标已有文件，不同内容将拒绝覆盖）");
            }
            else text.Append(live ? "\n保留目标配置：" : "\n新建目标配置：").Append(root);
        }
        text.Append(target == AssetBackendKind.Resources ? "\n同步 Resources 演示副本。" : "\n自动构建目标平台本地内容。");
        if (target != AssetBackendKind.YooAsset && JObject.Parse(File.ReadAllText("Packages/manifest.json"))["scopedRegistries"]?.ToString().Contains(Yoo) == true)
            text.Append("\n移除 OpenUPM 路由 com.tuyoogame.yooasset，保留其它路由。");
        text.Append("\n失败或取消后恢复原依赖及配置；全局缓存和基础工具保留。");
        if (target == AssetBackendKind.YooAsset && needsInstall) text.Append("\n\n").Append(EnvironmentInstaller.PackageTrustNotice);
        return text.ToString();
    }

    public static void Cancel()
    {
        if (!IsBusy) return;
        BackendSwitchState.instance.CancelRequested = true;
        BackendSwitchState.instance.Persist();
        if (EnvironmentInstaller.IsBusy) EnvironmentInstaller.Cancel();
    }

    public static void Retry()
    {
        var state = BackendSwitchState.instance;
        if (IsBusy || EnvironmentInstaller.IsBusy) return;
        if (!state.RecoveryRequired)
        {
            if (!string.IsNullOrEmpty(state.Error)) Start(state.Target, true, state.UpdateSdk);
            return;
        }
        state.Active = true;
        state.RollingBack = true;
        state.CancelRequested = false;
        SetPhase("rollback", "重试恢复原后端");
    }

    private static UnityEngine.Object[] RelatedAssets()
    {
        var paths = new[] { DemoResourceSetup.SettingsPath, "Assets/BundleCollectorSetting.asset" }
            .Concat(AssetDatabase.IsValidFolder("Assets/AddressableAssetsData")
                ? AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets/AddressableAssetsData" }).Select(AssetDatabase.GUIDToAssetPath)
                : Array.Empty<string>());
        return paths.Select(AssetDatabase.LoadMainAssetAtPath).Where(a => a != null).ToArray();
    }

    private static void SetPhase(string phase, string status)
    {
        var state = BackendSwitchState.instance;
        state.Phase = phase;
        state.Status = status;
        state.Deadline = DateTime.UtcNow.AddMinutes(20).Ticks;
        state.RollbackPackagesMatchedAt = 0;
        _list = null;
        _remove = null;
        state.Persist();
        Changed?.Invoke();
    }

    private static void Update()
    {
        if (!IsBusy || _executing || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.timeSinceStartup < _nextTick) return;
        _nextTick = EditorApplication.timeSinceStartup + .25;
        _executing = true;
        try { Tick(); }
        catch (Exception error)
        {
            var state = BackendSwitchState.instance;
            if (state.RollingBack)
            {
                state.Active = false;
                state.RecoveryRequired = true;
                state.Status = "恢复未完成，请检查网络或编译错误后重试";
                state.Error += "\n恢复失败：" + error.Message;
                state.Persist();
                Debug.LogError(state.Error);
            }
            else BeginRollback(error);
        }
        finally { _executing = false; Changed?.Invoke(); }
    }

    private static bool ReadPackages(out string[] installed)
    {
        installed = null;
        if (_list == null) { _list = Client.List(false, true); return false; }
        if (!_list.IsCompleted) return false;
        if (_list.Status != StatusCode.Success) throw new IOException("UPM 查询失败：" + _list.Error?.message);
        installed = _list.Result.Select(p => p.name).ToArray();
        _list = null;
        return true;
    }

    private static bool TargetPresent(string[] packages, AssetBackendKind target) =>
        target == AssetBackendKind.Resources || (target == AssetBackendKind.Addressables
            ? packages.Contains(Aa) && packages.Contains(Android) : packages.Contains(Yoo));

    private static bool Exclusive(string[] packages, AssetBackendKind target) =>
        TargetPresent(packages, target) && (target == AssetBackendKind.Addressables || (!packages.Contains(Aa) && !packages.Contains(Android))) &&
        (target == AssetBackendKind.YooAsset || !packages.Contains(Yoo));

    private static void Stage(string name)
    {
        BeforeStage?.Invoke(name);
        const string key = "ProjectTools.BackendSwitch.FaultStage";
        if (SessionState.GetString(key, "") != name) return;
        SessionState.EraseString(key);
        throw new InvalidOperationException("验证注入的切换故障：" + name);
    }

    private static void Tick()
    {
        var state = BackendSwitchState.instance;
        if (DateTime.UtcNow.Ticks > state.Deadline) throw new TimeoutException("后端切换阶段超时：" + state.Phase);
        if (state.CancelRequested && !state.RollingBack && !EnvironmentInstaller.IsBusy && _remove == null &&
            state.Phase != "installWait" && state.Phase != "removeWait")
        { BeginRollback(new OperationCanceledException("用户取消后端切换")); return; }
        switch (state.Phase)
        {
            case "prepare":
                if (!ReadPackages(out var packages)) return;
                if (!state.UpdateSdk && Exclusive(packages, state.Target) && state.Original == state.Target && Ready(state.Target))
                { Finish("已是目标后端，配置与内容均有效"); return; }
                if (state.Target != AssetBackendKind.Resources && (state.UpdateSdk || !TargetPresent(packages, state.Target)))
                {
                    SetPhase("installWait", "准备目标 SDK，等待安装完成");
                    EnvironmentInstaller.StartForSwitch(state.Target == AssetBackendKind.Addressables ? InstallPreset.Addressables : InstallPreset.YooAsset);
                }
                else SetPhase("archive", "归档非目标后端配置");
                return;
            case "installWait":
                if (EnvironmentInstaller.IsBusy) return;
                Stage("installWait");
                if (state.CancelRequested) { BeginRollback(new OperationCanceledException("用户取消后端切换")); return; }
                if (!string.IsNullOrEmpty(EnvironmentInstaller.LastError)) throw new IOException(EnvironmentInstaller.LastError);
                if (EnvironmentInstaller.Status.Contains("重启")) throw new IOException("环境安装要求重启，请保存工作并重启后重试切换。");
                if (!ReadPackages(out var installed)) return;
                if (!TargetPresent(installed, state.Target)) throw new IOException("目标 SDK 未完成安装。");
                SetPhase("archive", "归档非目标后端配置");
                return;
            case "archive":
                Stage("archive");
                BackendProfileStore.ArchiveInactive(state.Target);
                SetPhase("remove", "移除旧 SDK，重新解析共享依赖");
                return;
            case "remove":
                Stage("remove");
                // 在提交请求前持久化等待阶段，UPM 引起重载后通过实际包集合继续判断。
                var remove = state.Target == AssetBackendKind.Resources ? new[] { Aa, Android, Yoo } :
                    state.Target == AssetBackendKind.Addressables ? new[] { Yoo } : new[] { Aa, Android };
                if (!ReadPackages(out var beforeRemove)) return;
                remove = remove.Where(beforeRemove.Contains).ToArray();
                if (remove.Length == 0)
                {
                    if (state.Target != AssetBackendKind.YooAsset) RemoveYooRegistryScope();
                    SetPhase("restore", "恢复目标配置与 GUID");
                    return;
                }
                SetPhase("removeWait", "等待旧 SDK 卸载与程序集重载");
                _remove = Client.AddAndRemove(null, remove);
                return;
            case "removeWait":
                if (_remove != null)
                {
                    if (!_remove.IsCompleted) return;
                    if (_remove.Status != StatusCode.Success) throw new IOException("UPM 卸载失败：" + _remove.Error?.message);
                    _remove = null;
                }
                if (!ReadPackages(out var remaining)) return;
                Stage("removeWait");
                if (state.CancelRequested) { BeginRollback(new OperationCanceledException("用户取消后端切换")); return; }
                if (!Exclusive(remaining, state.Target)) throw new IOException("仍有其他包依赖旧 SDK，无法完成互斥卸载。请检查包依赖关系。");
                if (state.Target != AssetBackendKind.YooAsset) RemoveYooRegistryScope();
                SetPhase("restore", "恢复目标配置与 GUID");
                return;
            case "restore":
                Stage("restore");
                // 同后端的配置可能已因同步或显式更新而比历史归档更新。
                // 跨后端仍走归档模块的冲突检查，绝不借此覆盖另一份 live 配置。
                if (state.Original == AssetBackendKind.Resources && state.Target == AssetBackendKind.Resources)
                    BackendProfileStore.RestoreMissingResources();
                else if (state.Original != state.Target || !LiveConfigurationValid(state.Target))
                    BackendProfileStore.RestoreTarget(state.Target);
                SetPhase("build", "构建目标平台本地资源");
                AssetDatabase.Refresh();
                return;
            case "build":
                Stage("build");
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("目标后端存在编译错误。");
                if (state.Target == AssetBackendKind.Resources) DemoResourceSetup.SyncResources();
                else InvokeBuilder(state.Target, "Build");
                SetPhase("commit", "核验资源内容并提交后端选择");
                return;
            case "commit":
                Stage("commit");
                if (!ContentPresent(state.Target)) throw new IOException("构建结束但未找到当前平台可用内容。");
                var settings = DemoResourceSetup.EnsureSettings();
                settings.Backend = state.Target;
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssetIfDirty(settings);
                Directory.CreateDirectory("Library/ProjectToolsBackendSwitch");
                File.WriteAllText(ReadyPath(state.Target), Fingerprint(state.Target));
                Finish("切换完成：" + state.Target + "；可直接运行");
                return;
            case "rollback":
                if (state.Target != state.Original) BackendProfileStore.CleanOutputs(state.Target);
                BackendProfileStore.RestoreSnapshot(state.Snapshot);
                SetPhase("rollbackWait", "恢复原依赖并等待程序集重载");
                Client.Resolve();
                AssetDatabase.Refresh();
                return;
            case "rollbackWait":
                // Resolve 后只观察注册快照，避免并发提交 UPM List 请求。
                var restored = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Select(p => p.name).ToArray();
                if (!SnapshotPackagesMatch(restored))
                {
                    const string waiting = "等待 UPM 恢复原 SDK 版本与注册信息（阶段最多 20 分钟）";
                    if (state.RollbackPackagesMatchedAt != 0 || state.Status != waiting)
                    {
                        state.RollbackPackagesMatchedAt = 0;
                        state.Status = waiting;
                        state.Persist();
                    }
                    return;
                }
                if (state.RollbackPackagesMatchedAt == 0)
                {
                    state.RollbackPackagesMatchedAt = DateTime.UtcNow.Ticks;
                    state.Persist();
                    return;
                }
                // Resolve 后包列表可能先更新，程序集重载随后才发生，给予注册稳定窗口。
                if (DateTime.UtcNow.Ticks - state.RollbackPackagesMatchedAt < TimeSpan.FromSeconds(3).Ticks) return;
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("原后端存在编译错误，请修复后重试恢复。");
                BackendProfileStore.RestoreSnapshotReferences(state.Snapshot);
                SetPhase("rollbackBuild", "重新准备原后端本地资源");
                return;
            case "rollbackBuild":
                if (state.Original == AssetBackendKind.Resources) DemoResourceSetup.SyncResources();
                else InvokeBuilder(state.Original, "Build");
                if (!ContentPresent(state.Original)) throw new IOException("原后端资源重建未完成。");
                File.WriteAllText(ReadyPath(state.Original), Fingerprint(state.Original));
                Finish("切换未完成，已恢复原后端：" + state.Original);
                return;
            default: throw new InvalidOperationException("未知切换阶段：" + state.Phase);
        }
    }

    private static void InvokeBuilder(AssetBackendKind kind, string method)
    {
        string assembly = kind == AssetBackendKind.Addressables ? "AAGame.Assets.Addressables.Editor" : "AAGame.Assets.YooAsset.Editor";
        string typeName = "AAGame.Editor.Assets." + (kind == AssetBackendKind.Addressables ? "AddressablesDemoBuild" : "YooAssetDemoBuild");
        var type = Type.GetType(typeName + ", " + assembly, false);
        var operation = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
        if (operation == null) throw new InvalidOperationException("可选后端构建模块未加载或 SDK 版本不受当前适配器支持：" + kind);
        try { operation.Invoke(null, null); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }

    private static void RemoveYooRegistryScope()
    {
        const string path = "Packages/manifest.json";
        var manifest = JObject.Parse(File.ReadAllText(path));
        bool changed = false;
        if (manifest["scopedRegistries"] is JArray registries)
            foreach (var registry in registries.OfType<JObject>())
                if (registry["scopes"] is JArray scopes)
                    foreach (var scope in scopes.Where(s => s.Type == JTokenType.String && (string)s == Yoo).ToArray())
                    {
                        scope.Remove();
                        changed = true;
                    }
        // 同一国际源还服务 UniTask、VContainer，保留注册源和其它所有 scope。
        if (changed) File.WriteAllText(path, manifest.ToString(), new UTF8Encoding(false));
    }

    private static void BeginRollback(Exception error)
    {
        var state = BackendSwitchState.instance;
        state.Error = "切换未完成：" + error.Message;
        state.RollingBack = true;
        state.CancelRequested = false;
        state.RecoveryRequired = true;
        SetPhase("rollback", "准备恢复原依赖与配置");
    }

    private static bool SnapshotPackagesMatch(string[] actual)
    {
        // 快照中每个 SDK 的存在性均须恢复；原工程可能是改造前的双 SDK 状态。
        var expected = BackendSwitchState.instance.OriginalSdkPackages;
        var registered = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
        return new[] { Aa, Android, Yoo }.All(id => actual.Contains(id) == expected.Any(p => p.StartsWith(id + "@", StringComparison.Ordinal))) &&
            expected.All(p => registered.Any(r => r.name + "@" + r.version == p));
    }

    private static void Finish(string status)
    {
        var state = BackendSwitchState.instance;
        state.Active = false;
        state.RecoveryRequired = false;
        state.Phase = "";
        state.Status = status;
        state.Persist();
        Directory.CreateDirectory("Library/ProjectToolsBackendSwitch");
        File.WriteAllText("Library/ProjectToolsBackendSwitch/result.txt", DateTime.UtcNow.ToString("O") + "\n" + status + "\n" + state.Error);
        Debug.Log("[BackendSwitchCoordinator] " + status);
        Changed?.Invoke();
    }

    private static string ReadyPath(AssetBackendKind kind) => "Library/ProjectToolsBackendSwitch/Ready-" + kind + ".txt";
    private static bool LiveConfigurationValid(AssetBackendKind kind)
    {
        if (kind == AssetBackendKind.Resources) return ContentPresent(kind);
        string[] paths = kind == AssetBackendKind.Addressables
            ? new[] { "Assets/AddressableAssetsData/AddressableAssetSettings.asset", "Assets/AddressableAssetsData/DefaultObject.asset" }
            : new[] { "Assets/BundleCollectorSetting.asset" };
        return paths.All(path => File.Exists(path) && File.Exists(path + ".meta") && AssetDatabase.LoadMainAssetAtPath(path) != null);
    }
    public static bool Ready(AssetBackendKind kind) => ContentPresent(kind) && File.Exists(ReadyPath(kind)) && File.ReadAllText(ReadyPath(kind)) == Fingerprint(kind);
    private static bool ContentPresent(AssetBackendKind kind)
    {
        if (kind == AssetBackendKind.Resources)
            return DemoResourceSetup.ViewNames.All(n => File.Exists(DemoResourceSetup.ResourcesRoot + DemoAssetKeys.Ui(n) + ".prefab")) && File.Exists(DemoResourceSetup.ResourcesRoot + DemoAssetKeys.DemoText + ".txt");
        if (kind == AssetBackendKind.YooAsset)
            return Directory.Exists("Assets/StreamingAssets/yoo/AAGameDemo") && Directory.GetFiles("Assets/StreamingAssets/yoo/AAGameDemo", "*.version", SearchOption.AllDirectories).Length > 0;
        return new[] { "Library/com.unity.addressables/aa/" + EditorUserBuildSettings.activeBuildTarget,
            "Library/com.unity.addressables/" + EditorUserBuildSettings.activeBuildTarget }.Any(p => File.Exists(p + "/settings.json"));
    }

    private static string Fingerprint(AssetBackendKind kind)
    {
        var text = new StringBuilder(kind + ":" + EditorUserBuildSettings.activeBuildTarget + ":" + Application.unityVersion + ":keys=" + DemoAssetKeys.ConventionVersion);
        foreach (string root in new[] { DemoResourceSetup.SourcePrefabs.TrimEnd('/'), "Assets/AAGame/Content",
            kind == AssetBackendKind.Addressables ? "Assets/AddressableAssetsData" : "Assets/BundleCollectorSetting.asset" })
        {
            if (!root.EndsWith(".asset") && !AssetDatabase.IsValidFolder(root)) continue;
            if (root.EndsWith(".asset") && !File.Exists(root)) continue;
            foreach (string path in root.EndsWith(".asset") ? new[] { root } : AssetDatabase.FindAssets("", new[] { root }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p).ToArray())
                text.Append(path).Append(AssetDatabase.GetAssetDependencyHash(path));
        }
        text.Append(File.ReadAllText("Packages/manifest.json"));
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }
}
