using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AAGame.Assets;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VContainer;
using VContainer.Unity;

/// <summary>经过真实依赖切换覆盖六个方向，每个完成状态运行三轮 Flow。</summary>
[InitializeOnLoad]
public static class ExclusiveBackendValidation
{
    private const string Key = "ProjectTools.ExclusiveValidation.";
    private const string Output = "Library/AAGameValidation/exclusive-switches.txt";
    private static readonly AssetBackendKind[] Sequence = { AssetBackendKind.Resources,
        AssetBackendKind.Addressables, AssetBackendKind.Resources, AssetBackendKind.YooAsset,
        AssetBackendKind.Addressables, AssetBackendKind.YooAsset, AssetBackendKind.Resources,
        AssetBackendKind.YooAsset, AssetBackendKind.Resources };
    private static bool _runningFlow;
    private static double _next;
    private static Type Coordinator => Type.GetType("BackendSwitchCoordinator, Assembly-CSharp-Editor", true);
    private static bool Busy => (bool)Coordinator.GetProperty("IsBusy").GetValue(null);
    private static string Error => (string)Coordinator.GetProperty("LastError").GetValue(null);

    static ExclusiveBackendValidation() => EditorApplication.update += Tick;

    public static void Start()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || Busy || SessionState.GetInt(Key + "Phase", 0) != 0)
            throw new InvalidOperationException("请先结束已有操作。");
        Directory.CreateDirectory(Path.GetDirectoryName(Output));
        File.WriteAllText(Output, DateTime.UtcNow.ToString("O") + " 开始互斥六方向验证\n");
        SessionState.SetInt(Key + "Index", 0);
        SessionState.SetInt(Key + "Phase", 1);
        Deadline();
    }

    private static void Deadline() => SessionState.SetString(Key + "Deadline", DateTime.UtcNow.AddMinutes(20).Ticks.ToString());
    private static void Append(string message) => File.AppendAllText(Output, DateTime.UtcNow.ToString("O") + " " + message + "\n");

    private static void Tick()
    {
        int phase = SessionState.GetInt(Key + "Phase", 0);
        if (phase == 0 || EditorApplication.timeSinceStartup < _next) return;
        _next = EditorApplication.timeSinceStartup + .3;
        try
        {
            if (DateTime.UtcNow.Ticks > long.Parse(SessionState.GetString(Key + "Deadline", "0")))
                throw new TimeoutException("互斥切换验证超时。");
            int index = SessionState.GetInt(Key + "Index", 0);
            if (phase == 1)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating || Busy || AAGame.Editor.EnvironmentSetup.EnvironmentInstaller.IsBusy) return;
                if (index == Sequence.Length) { Append("PASS：六方向与各完成状态三轮 Flow，最终 Resources。"); SessionState.SetInt(Key + "Phase", 0); return; }
                SessionState.SetInt(Key + "Phase", 2);
                Deadline();
                Append("请求 " + Sequence[index]);
                Coordinator.GetMethod("Start", new[] { typeof(AssetBackendKind), typeof(bool), typeof(bool) })
                    .Invoke(null, new object[] { Sequence[index], false, false });
                return;
            }
            if (phase == 2)
            {
                if (Busy || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                if (!string.IsNullOrEmpty(Error)) throw new InvalidOperationException(Error);
                if (DemoResourceSetup.EnsureSettings().Backend != Sequence[index]) throw new InvalidOperationException("目标未提交。");
                var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Select(p => p.name).ToArray();
                bool aa = packages.Contains("com.unity.addressables"), yoo = packages.Contains("com.tuyoogame.yooasset");
                if (aa != (Sequence[index] == AssetBackendKind.Addressables) || yoo != (Sequence[index] == AssetBackendKind.YooAsset))
                    throw new InvalidOperationException("实际包集合不互斥。");
                Append("依赖互斥通过 " + Sequence[index]);
                EditorSceneManager.OpenScene("Assets/AAGame/Scenes/BootstrapScene.unity");
                EditorSceneManager.playModeStartScene = null;
                Application.runInBackground = true;
                SessionState.SetInt(Key + "Phase", 3);
                EditorApplication.isPlaying = true;
                return;
            }
            if (phase == 3)
            {
                if (!EditorApplication.isPlaying) return;
                Application.runInBackground = true;
                var game = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
                game.Focus(); game.Repaint(); EditorApplication.QueuePlayerLoopUpdate();
                var root = LifetimeScope.Find<GameRootLifetimeScope>() as GameRootLifetimeScope;
                if (root == null || root.Container == null) return;
                var state = root.Container.Resolve<FlowState>();
                if (!string.IsNullOrEmpty(state.Error)) throw new InvalidOperationException(state.Error);
                if (state.Stage != FlowStage.Start || root.Container.Resolve<SceneFlowCoordinator>().IsTransitioning) return;
                SessionState.SetInt(Key + "Phase", 4);
                RunFlow();
            }
            else if (phase == 4)
            {
                var game = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
                game.Focus(); game.Repaint(); EditorApplication.QueuePlayerLoopUpdate();
                if (!_runningFlow) throw new InvalidOperationException("运行验证时意外重载。");
            }
            else if (phase == 5 && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SessionState.SetInt(Key + "Index", index + 1);
                SessionState.SetInt(Key + "Phase", 1);
            }
        }
        catch (Exception error)
        {
            Append("FAIL：" + error);
            SessionState.SetInt(Key + "Phase", 0);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            Debug.LogException(error);
        }
    }

    private static async void RunFlow()
    {
        _runningFlow = true;
        try
        {
            await ResourceBackendValidation.RunAsync();
            Append("三轮 Flow 通过 " + Sequence[SessionState.GetInt(Key + "Index", 0)]);
            SessionState.SetInt(Key + "Phase", 5);
        }
        catch (Exception error) { Append("FAIL：" + error); SessionState.SetInt(Key + "Phase", 0); Debug.LogException(error); }
        finally { _runningFlow = false; EditorApplication.isPlaying = false; }
    }
}
