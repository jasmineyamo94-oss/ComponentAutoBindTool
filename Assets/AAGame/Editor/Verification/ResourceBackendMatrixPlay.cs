using System;
using System.IO;
using AAGame.Assets;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VContainer;
using VContainer.Unity;

/// <summary>只在隔离工程的 None 组合自动化启用；跨 Play 程序集重载保存进度。</summary>
[InitializeOnLoad]
public static class ResourceBackendMatrixPlay
{
    private const string Prefix = "AAGame.ResourceBackendMatrixPlay.";
    private const string Evidence = "Library/AAGameValidation/resources-flow.txt";
    private static bool _observingTask;

    static ResourceBackendMatrixPlay()
    {
        if (IsIsolatedRun())
            EditorApplication.update += Update;
    }

    private static bool IsIsolatedRun()
    {
        string[] args = Environment.GetCommandLineArgs();
        int flag = Array.IndexOf(args, "-resourceBackendMatrix");
        return flag >= 0 && flag + 1 < args.Length && args[flag + 1] == "None";
    }

    public static void Start()
    {
        if (!IsIsolatedRun())
            throw new InvalidOperationException("真实 Play 自动化仅允许 None 隔离包组合。");
        var settings = AssetDatabase.LoadAssetAtPath<AssetBackendSettings>(
            "Assets/AAGame/Settings/AssetBackendSettings.asset");
        if (settings == null)
            throw new InvalidOperationException("隔离副本缺少资源后端配置。");
        // 仅改新建隔离副本中的配置；不依赖复制时源工程最后选择的后端。
        settings.Backend = AssetBackendKind.Resources;
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
        EditorSceneManager.OpenScene("Assets/AAGame/Scenes/BootstrapScene.unity", OpenSceneMode.Single);
        EditorSceneManager.playModeStartScene = null;
        SessionState.EraseString("AAGame.EditorStage");
        SessionState.SetString(Prefix + "Deadline", DateTime.UtcNow.AddSeconds(180).Ticks.ToString());
        SessionState.SetInt(Prefix + "Phase", 1);
        Application.runInBackground = true;
        // batch 不执行 UI 渲染帧末；真实流程使用普通编辑器，并保持 Game 视图参与渲染。
        var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        if (gameViewType == null)
            throw new InvalidOperationException("当前 Unity 找不到 Game 视图。");
        EditorWindow.GetWindow(gameViewType).Focus();
        EditorApplication.isPlaying = true;
    }

    private static void Update()
    {
        int phase = SessionState.GetInt(Prefix + "Phase", 0);
        if (phase == 0)
            return;
        try
        {
            if (phase >= 3)
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    return;
                SessionState.SetInt(Prefix + "Phase", 0);
                EditorApplication.Exit(phase == 3 ? 0 : 1);
                return;
            }
            long deadline = long.Parse(SessionState.GetString(Prefix + "Deadline", "0"));
            if (DateTime.UtcNow.Ticks > deadline)
                throw new TimeoutException("无 SDK Resources 三轮 Play 验证超过 180 秒。");
            if (phase == 2)
            {
                if (!_observingTask)
                    throw new InvalidOperationException("验证运行期间发生意外程序集重载，不能将未观察完的任务视为通过。");
                return;
            }
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            Application.runInBackground = true;
            var root = LifetimeScope.Find<GameRootLifetimeScope>() as GameRootLifetimeScope;
            if (root == null || root.Container == null)
                return;
            var state = root.Container.Resolve<FlowState>();
            var flow = root.Container.Resolve<SceneFlowCoordinator>();
            if (!string.IsNullOrEmpty(state.Error))
                throw new InvalidOperationException("无 SDK Resources 冷启动失败：" + state.Error);
            if (flow.IsTransitioning || state.Stage != FlowStage.Start)
                return;
            SessionState.SetInt(Prefix + "Phase", 2);
            Run();
        }
        catch (Exception error)
        {
            Finish(error);
        }
    }

    private static async void Run()
    {
        _observingTask = true;
        try
        {
            await ResourceBackendValidation.RunAsync();
            Finish(null);
        }
        catch (Exception error)
        {
            Finish(error);
        }
        finally
        {
            _observingTask = false;
        }
    }

    private static void Finish(Exception error)
    {
        // 超时请求退出时，旧异步链可能随后结束；失败结果不能被迟到的成功覆盖。
        if (SessionState.GetInt(Prefix + "Phase", 0) >= 3)
            return;
        ResourceBackendValidation.PersistMatrixEvidence();
        Directory.CreateDirectory(Path.GetDirectoryName(Evidence));
        File.WriteAllText(Evidence, DateTime.UtcNow.ToString("O") + "\n" +
            (error == null ? "PASS：无 SDK Resources 真实 Play 三轮流程。" : "FAIL：" + error));
        SessionState.SetInt(Prefix + "Phase", error == null ? 3 : 4);
        if (error != null)
            Debug.LogException(error);
        else
            Debug.Log("[ResourceBackendMatrixPlay] PASS：无 SDK Resources 真实 Play 三轮流程。");
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            EditorApplication.isPlaying = false;
    }
}
