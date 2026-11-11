using System;
using System.IO;
using System.Linq;
using System.Threading;
using AAGame.Assets;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityLogType = UnityEngine.LogType;
using UnityEngine.SceneManagement;
using VContainer;
using VContainer.Unity;

/// <summary>在普通编辑器逐一运行三种真实资源后端，跨 Play 重载保留验证进度。</summary>
[InitializeOnLoad]
public static class DemoBackendPlayChecks
{
    private const string Prefix = "AAGame.DemoBackendPlayChecks.";
    private const string Bootstrap = "Assets/AAGame/Scenes/BootstrapScene.unity";
    public const string EvidenceDirectory = "Library/AAGameValidation";
    private static readonly AssetBackendKind[] Backends =
        { AssetBackendKind.Resources, AssetBackendKind.Addressables, AssetBackendKind.YooAsset };
    private static bool _observing;
    private static CancellationTokenSource _cancellation;
    private static double _nextTick;

    public static bool IsRunning => SessionState.GetInt(Prefix + "Phase", 0) != 0;
    public static string Status => SessionState.GetString(Prefix + "Status", "尚未运行");

    static DemoBackendPlayChecks()
    {
        EditorApplication.update += Update;
        Application.logMessageReceived += ObserveLog;
    }

    private static void ObserveLog(string condition, string stackTrace, UnityLogType type)
    {
        if (!IsRunning || (type != UnityLogType.Error && type != UnityLogType.Exception && type != UnityLogType.Assert)) return;
        // 只观察，不重新输出或吞掉日志；SessionState 使退出 Play 的重载保留错误证据。
        SessionState.SetBool(Prefix + "Failed", true);
        string errors = SessionState.GetString(Prefix + "ObservedErrors", "");
        string observed = DateTime.UtcNow.ToString("O") + " phase=" + SessionState.GetInt(Prefix + "Phase", 0) +
            " " + type + ": " + condition + "\n" + stackTrace + "\n";
        SessionState.SetString(Prefix + "ObservedErrors", errors + observed);
    }

    [MenuItem("Tools/项目工具/资源后端/验证/依次运行三种后端三轮流程")]
    public static void Start()
    {
        if (IsRunning || BackendSwitchCoordinator.IsBusy || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("请等待已有验证结束并退出 Play。");
        if (Application.isBatchMode)
            throw new InvalidOperationException("三后端真实 UI 验证需要普通编辑器和 Game 视图，不能在 batch 模式运行。");
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i).isDirty)
                throw new InvalidOperationException("当前存在未保存的场景，验证不会替您保存或丢弃修改，请先保存。");
        var settings = Settings();
        if (EditorUtility.IsDirty(settings))
            throw new InvalidOperationException("资源后端配置存在未保存修改，请先保存。");
        var setup = new SavedSetup
        {
            Scenes = EditorSceneManager.GetSceneManagerSetup().Select(scene => new SavedScene
            {
                Path = scene.path, Loaded = scene.isLoaded, Active = scene.isActive
            }).ToArray()
        };
        if (setup.Scenes.Any(scene => string.IsNullOrEmpty(scene.Path)))
            throw new InvalidOperationException("请先保存未命名场景后运行验证。");
        SessionState.SetString(Prefix + "Scenes", JsonUtility.ToJson(setup));
        SessionState.SetInt(Prefix + "OriginalBackend", (int)settings.Backend);
        SessionState.SetBool(Prefix + "Background", Application.runInBackground);
        SessionState.SetString(Prefix + "PlayScene", AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene));
        SessionState.SetString(Prefix + "EditorStage", SessionState.GetString("AAGame.EditorStage", ""));
        SessionState.SetInt(Prefix + "Index", 0);
        SessionState.SetBool(Prefix + "Failed", false);
        SessionState.SetString(Prefix + "ObservedErrors", "");
        Directory.CreateDirectory(EvidenceDirectory);
        File.WriteAllText(EvidenceDirectory + "/three-backends.txt", DateTime.UtcNow.ToString("O") + " 开始三后端真实 Play 验证\n");
        SessionState.SetInt(Prefix + "Phase", 4);
        SetStatus("准备 Resources 后端");
    }

    private static AssetBackendSettings Settings()
    {
        var settings = AssetDatabase.LoadAssetAtPath<AssetBackendSettings>(DemoResourceSetup.SettingsPath);
        if (settings == null) throw new InvalidOperationException("缺少资源后端配置。");
        return settings;
    }

    private static void Update()
    {
        int phase = SessionState.GetInt(Prefix + "Phase", 0);
        if (phase == 0 || EditorApplication.timeSinceStartup < _nextTick) return;
        _nextTick = EditorApplication.timeSinceStartup + .2;
        try
        {
            if (phase == 5 || phase == 6)
            {
                long switchDeadline = long.Parse(SessionState.GetString(Prefix + "Deadline", "0"));
                if (DateTime.UtcNow.Ticks > switchDeadline) throw new TimeoutException("互斥后端切换超过 30 分钟。");
                if (BackendSwitchCoordinator.IsBusy || AAGame.Editor.EnvironmentSetup.EnvironmentInstaller.IsBusy || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                if (!string.IsNullOrEmpty(BackendSwitchCoordinator.LastError))
                    throw new InvalidOperationException("互斥切换未完成：" + BackendSwitchCoordinator.LastError);
                if (phase == 6) { FinishRestore(); return; }
                if (Settings().Backend != CurrentBackend()) throw new InvalidOperationException("目标后端未提交，不能运行验证。");
                BeginPlay();
                return;
            }
            if (phase == 3 || phase == 4)
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                    BackendSwitchCoordinator.IsBusy || AAGame.Editor.EnvironmentSetup.EnvironmentInstaller.IsBusy) return;
                int index = SessionState.GetInt(Prefix + "Index", 0);
                if (phase == 3)
                {
                    var exitErrors = SessionState.GetString(Prefix + "ObservedErrors", "");
                    if (!string.IsNullOrEmpty(exitErrors))
                    {
                        SessionState.SetBool(Prefix + "Failed", true);
                        WriteCurrentResult("FAIL：本轮运行或退出生命周期出现错误日志。\n" +
                            SessionState.GetString(Prefix + "CurrentFailure", "") + "\n" + exitErrors);
                    }
                    else if (SessionState.GetBool(Prefix + "CurrentPassed", false))
                    {
                        WriteCurrentResult("PASS：真实三轮流程完成，退出 Play 无错误日志。");
                    }
                    SessionState.SetInt(Prefix + "Index", ++index);
                }
                if (index >= Backends.Length) { Restore(); return; }
                Prepare(index);
                return;
            }
            long deadline = long.Parse(SessionState.GetString(Prefix + "Deadline", "0"));
            if (DateTime.UtcNow.Ticks > deadline) throw new TimeoutException("当前后端三轮验证超过 180 秒。");
            KeepGameAdvancing();
            if (!EditorApplication.isPlaying)
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    throw new InvalidOperationException("Play 在验证完成前退出。");
                return;
            }
            if (phase == 2)
            {
                if (!_observing) throw new InvalidOperationException("验证异步链期间发生意外程序集重载，不能将未观察完成的任务计为成功。");
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var root = LifetimeScope.Find<GameRootLifetimeScope>() as GameRootLifetimeScope;
            if (root == null || root.Container == null) return;
            var flow = root.Container.Resolve<SceneFlowCoordinator>();
            var state = root.Container.Resolve<FlowState>();
            if (!string.IsNullOrEmpty(state.Error)) throw new InvalidOperationException("冷启动失败：" + state.Error);
            if (flow.IsTransitioning || state.Stage != FlowStage.Start) return;
            SessionState.SetInt(Prefix + "Phase", 2);
            RunCurrent();
        }
        catch (Exception error)
        {
            if (phase >= 3)
            {
                SessionState.SetBool(Prefix + "Failed", true);
                if (phase == 6)
                {
                    Append("恢复原后端失败，请在环境窗口重试恢复：" + error);
                    SessionState.SetInt(Prefix + "Phase", 0);
                    SetStatus("FAIL：原后端恢复未完成，请检查切换任务");
                    return;
                }
                Append("恢复或准备阶段失败：" + error);
                try { Restore(); }
                catch (Exception restoreError)
                {
                    Append("恢复失败，需要手动处理：" + restoreError);
                    SessionState.SetInt(Prefix + "Phase", 0);
                    SetStatus("FAIL：恢复失败，查看 three-backends.txt");
                }
            }
            else FinishCurrent(error);
        }
    }

    private static void Prepare(int index)
    {
        SessionState.SetString(Prefix + "ObservedErrors", "");
        SessionState.SetBool(Prefix + "CurrentPassed", false);
        SessionState.SetString(Prefix + "CurrentFailure", "");
        SessionState.SetString(Prefix + "Deadline", DateTime.UtcNow.AddMinutes(30).Ticks.ToString());
        SessionState.SetInt(Prefix + "Phase", 5);
        SetStatus("准备互斥切换到 " + Backends[index]);
        BackendSwitchCoordinator.Start(Backends[index], false);
    }

    private static void BeginPlay()
    {
        EditorSceneManager.OpenScene(Bootstrap, OpenSceneMode.Single);
        EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(Bootstrap);
        SessionState.EraseString("AAGame.EditorStage");
        Application.runInBackground = true;
        SetStatus("运行 " + CurrentBackend() + "，等待 Start 就绪");
        Append("开始 " + CurrentBackend());
        SessionState.SetString(Prefix + "CurrentStart", DateTime.UtcNow.Ticks.ToString());
        SessionState.SetString(Prefix + "Deadline", DateTime.UtcNow.AddSeconds(180).Ticks.ToString());
        SessionState.SetInt(Prefix + "Phase", 1);
        KeepGameAdvancing();
        EditorApplication.isPlaying = true;
    }

    private static void KeepGameAdvancing()
    {
        Application.runInBackground = true;
        var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        if (type == null) throw new InvalidOperationException("当前编辑器没有 Game 视图。");
        var window = EditorWindow.GetWindow(type);
        window.Focus();
        window.Repaint();
        EditorApplication.QueuePlayerLoopUpdate();
    }

    private static async void RunCurrent()
    {
        _observing = true;
        _cancellation = new CancellationTokenSource();
        try
        {
            SetStatus("正在验证 " + CurrentBackend() + " 三轮流程");
            await ResourceBackendValidation.RunAsync(_cancellation.Token);
            FinishCurrent(ResourceBackendValidation.LastSucceeded ? null : new InvalidOperationException("验证没有报告通过。"));
        }
        catch (Exception error) { FinishCurrent(error); }
        finally
        {
            _observing = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private static AssetBackendKind CurrentBackend() => Backends[SessionState.GetInt(Prefix + "Index", 0)];

    private static void FinishCurrent(Exception error)
    {
        if (SessionState.GetInt(Prefix + "Phase", 0) >= 3 || !IsRunning) return;
        var backend = CurrentBackend();
        string observedErrors = SessionState.GetString(Prefix + "ObservedErrors", "");
        if (!string.IsNullOrEmpty(observedErrors))
            error = new InvalidOperationException("观察到错误日志：\n" + observedErrors, error);
        SessionState.SetBool(Prefix + "CurrentPassed", error == null);
        SessionState.SetString(Prefix + "CurrentFailure", error?.ToString() ?? "");
        WriteCurrentResult(error == null ? "待退出核验：真实三轮流程已完成。" : "FAIL：" + error);
        string detail = ResourceBackendValidation.ResultDirectory + "/" + backend + ".txt";
        if (File.Exists(detail) && File.GetLastWriteTimeUtc(detail).Ticks >= long.Parse(SessionState.GetString(Prefix + "CurrentStart", "0")))
            File.Copy(detail, EvidenceDirectory + "/play-" + backend + "-detail.txt", true);
        if (error != null) SessionState.SetBool(Prefix + "Failed", true);
        SetStatus((error == null ? "待退出核验：" : "FAIL：") + backend + "；退出 Play 后继续");
        SessionState.SetInt(Prefix + "Phase", 3);
        _cancellation?.Cancel();
        EditorApplication.isPlaying = false;
    }

    private static void Restore()
    {
        SessionState.SetString(Prefix + "Deadline", DateTime.UtcNow.AddMinutes(30).Ticks.ToString());
        SessionState.SetInt(Prefix + "Phase", 6);
        SetStatus("恢复原资源后端及其依赖、配置");
        BackendSwitchCoordinator.Start((AssetBackendKind)SessionState.GetInt(Prefix + "OriginalBackend", 0), false);
    }

    private static void FinishRestore()
    {
        EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(SessionState.GetString(Prefix + "PlayScene", ""));
        var saved = JsonUtility.FromJson<SavedSetup>(SessionState.GetString(Prefix + "Scenes", "{}"));
        if (saved?.Scenes != null && saved.Scenes.Length > 0)
            EditorSceneManager.RestoreSceneManagerSetup(saved.Scenes.Select(scene => new SceneSetup
                { path = scene.Path, isLoaded = scene.Loaded, isActive = scene.Active }).ToArray());
        Application.runInBackground = SessionState.GetBool(Prefix + "Background", false);
        var stage = SessionState.GetString(Prefix + "EditorStage", "");
        if (stage.Length == 0) SessionState.EraseString("AAGame.EditorStage");
        else SessionState.SetString("AAGame.EditorStage", stage);
        SessionState.SetInt(Prefix + "Phase", 0);
        SetStatus(SessionState.GetBool(Prefix + "Failed", false) ? "FAIL：至少一个后端未通过，已恢复原配置" : "PASS：三后端各三轮通过，已恢复原配置");
        Append(Status);
        Debug.Log("[DemoBackendPlayChecks] " + Status);
    }

    private static void SetStatus(string status) { SessionState.SetString(Prefix + "Status", status); }
    private static void WriteCurrentResult(string result)
    {
        Directory.CreateDirectory(EvidenceDirectory);
        string evidence = DateTime.UtcNow.ToString("O") + "\n" + result;
        File.WriteAllText(EvidenceDirectory + "/play-" + CurrentBackend() + ".txt", evidence);
        Append(CurrentBackend() + " " + evidence);
    }
    private static void Append(string message) { File.AppendAllText(EvidenceDirectory + "/three-backends.txt", DateTime.UtcNow.ToString("O") + " " + message + "\n"); }

    [Serializable]
    private sealed class SavedSetup { public SavedScene[] Scenes; }
    [Serializable]
    private sealed class SavedScene { public string Path; public bool Loaded; public bool Active; }
}
