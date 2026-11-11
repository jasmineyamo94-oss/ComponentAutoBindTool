using AAGame.Assets;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComponentAutoBindTool.AssetCore;
using ComponentAutoBindTool.ViewCore;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using VContainer;
using VContainer.Unity;
using Object = UnityEngine.Object;

/// <summary>在选定后端的真实 Play 环境验证，结果写入 Temp，不修改演示资产。</summary>
public static class ResourceBackendValidation
{
    public const string ResultDirectory = "Temp/ResourceBackends";
    public static bool IsRunning { get; private set; }
    public static bool LastSucceeded { get; private set; }

    [MenuItem("Tools/项目工具/资源后端/验证/运行当前后端三轮流程")]
    public static async void Run()
    {
        if (IsRunning)
            return;
        try
        {
            await RunAsync();
        }
        catch (Exception error)
        {
            Debug.LogException(error);
        }
    }

    public static async Awaitable RunAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("资源后端验证正在执行。");
        if (!Application.isPlaying)
            throw new InvalidOperationException("请先选择后端，从 Bootstrap 进入 Play 并等待 Start 就绪。");
        IsRunning = true;
        LastSucceeded = false;
        string backend = "未解析";
        try
        {
            var root = LifetimeScope.Find<GameRootLifetimeScope>() as GameRootLifetimeScope;
            Check(root != null && root.Container != null, "根 Scope 尚未准备就绪。");
            backend = root.AssetSettings.Backend.ToString();
            var flow = root.Container.Resolve<SceneFlowCoordinator>();
            var state = root.Container.Resolve<FlowState>();
            var assets = root.Container.Resolve<IAssetService>();
            Check(!flow.IsTransitioning, "请等待当前切换结束后再验证。");
            if (state.Stage == FlowStage.Start)
                await flow.EnterMainFromStartAsync(cancellationToken);
            Check(state.Stage == FlowStage.Main, "验证需要 Start 或 Main 阶段。");

            await ValidateAssetsAsync(assets, cancellationToken);
            var main = Object.FindFirstObjectByType<MainRootMenuView>();
            Check(main != null, "主界面不存在。");
            int mainId = main.GetInstanceID();
            int managers = ViewManagerDebugCount();
            for (int round = 0; round < 3; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float before = state.MainSeconds;
                await flow.EnterLevelFromMainAsync(new LevelLoadRequestContent(101 + round), cancellationToken);
                Check(root.UIRoots.Main.IsSuspended, "Main 未挂起。");
                var scope = LifetimeScope.Find<LevelSceneScope>();
                var session = scope.Container.Resolve<LevelGameplaySession>();
                var hud = Object.FindFirstObjectByType<LevelHudView>();
                Check(hud != null && session.Request.LevelId == 101 + round, "关卡 HUD 或 Scoped 请求错误。");
                int hudId = hud.GetInstanceID();
                int restarts = session.Restarts;
                await flow.RestartLevelAsync(cancellationToken);
                Check(ReferenceEquals(scope, LifetimeScope.Find<LevelSceneScope>()), "重开重建了关卡 Scope。");
                Check(ReferenceEquals(session, scope.Container.Resolve<LevelGameplaySession>()), "重开重建了会话。");
                Check(hud != null && hud.GetInstanceID() == hudId && session.Restarts == restarts + 1,
                    "重开没有复用 HUD 或更新重开次数。");
                await Awaitable.WaitForSecondsAsync(.1f, cancellationToken);
                Check(state.MainSeconds > before, "Main 隐藏期间计时停止。");
                await flow.ReturnToMainFromLevelAsync(cancellationToken);
                await Awaitable.NextFrameAsync(cancellationToken);
                Check(main != null && main.GetInstanceID() == mainId && !root.UIRoots.Main.IsSuspended,
                    "返回后 Main 实例或状态错误。");
                Check(ViewManagerDebugCount() == managers, "关卡管理器未恢复基线。");
                Check(!assets.TryGetLoadedAsset<GameObject>(DemoAssetKeys.LevelHud, out _), "退关后 HUD Prefab 租约残留。");
                Check(!flow.IsTransitioning, "切换门禁残留。");
                Check(!Object.FindObjectsByType<LevelLoadingView>(FindObjectsSortMode.None)
                    .Any(view => view.gameObject.activeInHierarchy), "Loading 遮罩残留。");
            }
            Check(Object.FindObjectsByType<GameRootLifetimeScope>(FindObjectsSortMode.None).Length == 1,
                "根 Scope 重复。");
            Check(Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length == 1,
                "EventSystem 重复。");
            Check(Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Length == 1,
                "AudioListener 重复。");
            LastSucceeded = true;
            WriteResult(backend, "PASS：三轮流程、关卡请求、Main 计时与复用、Scope/HUD 重开复用、资源边界及租约释放。");
            Debug.Log("[ResourceBackendValidation] PASS：" + backend);
        }
        catch (Exception error)
        {
            WriteResult(backend, "FAIL：" + error);
            throw;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private static int ViewManagerDebugCount() => ViewManagerDebugRegistry.ActiveManagers.Count;

    private static async Awaitable ValidateAssetsAsync(IAssetService assets, CancellationToken token)
    {
        const string key = DemoAssetKeys.DemoText;
        using (var owner = assets.CreateReleaseScope("后端验证"))
        {
            var firstTask = Consume(assets.LoadAssetAsync<TextAsset>(key, owner, token));
            var secondTask = Consume(assets.LoadAssetAsync<TextAsset>(key, owner, token));
            var handles = await Task.WhenAll(firstTask, secondTask);
            Check(handles[0].Asset != null && handles[0].Asset.text.Length > 0, "普通文本资源为空。");
            Check(ReferenceEquals(handles[0].Asset, handles[1].Asset), "并发加载没有共享缓存。");
            handles[0].Dispose();
            handles[0].Dispose();
            Check(handles[1].Asset != null, "重复释放影响了其他调用者。");
        }
        Check(!assets.TryGetLoadedAsset<TextAsset>(key, out _), "资源作用域释放后缓存租约仍存在。");
        using (var canceled = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            canceled.Cancel();
            bool observed = false;
            try
            {
                using var unexpected = await assets.LoadAssetAsync<TextAsset>(key, cancellationToken: canceled.Token);
            }
            catch (OperationCanceledException)
            {
                observed = true;
            }
            Check(observed, "取消的调用没有报告取消。");
        }
        // SDK 可能记录预期资源错误；只在这两个被观察的负例期间抑制日志，随后恢复。
        bool logging = Debug.unityLogger.logEnabled;
        try
        {
            Debug.unityLogger.logEnabled = false;
            await ExpectLoadFailure<Texture2D>(assets, DemoAssetKeys.StartMenu, token);
            await ExpectLoadFailure<TextAsset>(assets, "Validation/MissingResource", token);
        }
        finally
        {
            Debug.unityLogger.logEnabled = logging;
        }
        using var retry = await assets.LoadAssetAsync<TextAsset>(key, cancellationToken: token);
        Check(retry.Asset != null, "负例之后资源无法重新加载。");
        Check(await assets.GetDownloadSizeAsync(key, token) == 0, "本地演示仍有待下载内容。");
        await assets.DownloadDependenciesAsync(key, token);
    }

    private static async Awaitable ExpectLoadFailure<T>(IAssetService assets, string key, CancellationToken token)
        where T : Object
    {
        bool failed = false;
        try
        {
            using var unexpected = await assets.LoadAssetAsync<T>(key, cancellationToken: token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            failed = true;
        }
        Check(failed, "错误的资源请求意外成功：" + key);
        Check(!assets.TryGetLoadedAsset<T>(key, out _), "失败的资源请求残留缓存：" + key);
    }

    private static async Task<AssetHandle<T>> Consume<T>(Awaitable<AssetHandle<T>> operation) where T : Object
        => await operation;

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void WriteResult(string backend, string result)
    {
        Directory.CreateDirectory(ResultDirectory);
        File.WriteAllText(Path.Combine(ResultDirectory, backend + ".txt"),
            DateTime.UtcNow.ToString("O") + "\n" + result);
    }

    [MenuItem("Tools/项目工具/资源后端/验证/编译当前目标 Player 脚本")]
    public static void CompilePlayer()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("请退出 Play 后编译 Player 脚本。");
        var target = EditorUserBuildSettings.activeBuildTarget;
        var settings = new UnityEditor.Build.Player.ScriptCompilationSettings
        {
            target = target,
            group = BuildPipeline.GetBuildTargetGroup(target)
        };
        string output = Path.GetFullPath(ResultDirectory + "/PlayerScripts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var result = UnityEditor.Build.Player.PlayerBuildInterface.CompilePlayerScripts(settings, output);
        Check(result.assemblies != null && result.assemblies.Count > 0, "Player 脚本编译失败。");
        string[] names = result.assemblies.Select(Path.GetFileNameWithoutExtension).ToArray();
        Check(names.Contains("ComponentAutoBindTool.AssetCore") && names.Contains("Assembly-CSharp"),
            "Player 输出缺少资源核心或业务程序集。");
        Check(!names.Contains("ComponentAutoBindTool.Editor") && !names.Contains("Assembly-CSharp-Editor"),
            "Player 输出包含 Editor 程序集。");
        Directory.CreateDirectory(ResultDirectory);
        File.WriteAllText(ResultDirectory + "/player-compile.txt", "PASS：" + target + "\n" + string.Join("\n", names));
        Debug.Log("[ResourceBackendValidation] Player 脚本编译通过：" + target);
    }

    // 隔离副本批处理入口：同时检查实际解析包集合，避免把选择 Resources 当成无 SDK 编译证明。
    public static void VerifyPackageMatrix()
    {
        try
        {
            VerifyPackageMatrixInternal();
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-resourceBackendMatrix") >= 0)
                EditorApplication.Exit(1);
            else
                throw;
        }
    }

    private static void VerifyPackageMatrixInternal()
    {
        string[] args = Environment.GetCommandLineArgs();
        int flag = Array.IndexOf(args, "-resourceBackendMatrix");
        Check(flag >= 0 && flag + 1 < args.Length, "缺少 -resourceBackendMatrix 参数。");
        string expected = args[flag + 1];
        Check(new[] { "None", "Addressables", "YooAsset", "Both" }.Contains(expected), "未知包组合参数。");
        Check(expected != "None" || !Application.isBatchMode,
            "None 的真实 UI 流程需要非 batch 编辑器渲染 EndOfFrame，请使用矩阵脚本启动。");
        string[] packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
            .Select(package => package.name).ToArray();
        bool addressables = packages.Contains("com.unity.addressables");
        bool yoo = packages.Contains("com.tuyoogame.yooasset");
        Check(addressables == (expected == "Addressables" || expected == "Both"), "Addressables 包集合不符。");
        Check(yoo == (expected == "YooAsset" || expected == "Both"), "YooAsset 包集合不符。");
        CompilePlayer();
        File.WriteAllText(ResultDirectory + "/package-matrix.txt", "PASS：" + expected + "\n" + string.Join("\n", packages));
        Debug.Log("[ResourceBackendValidation] 隔离包组合通过：" + expected);
        PersistMatrixEvidence();
        if (expected == "None")
            ResourceBackendMatrixPlay.Start();
        else
            EditorApplication.Exit(0);
    }

    public static void PersistMatrixEvidence()
    {
        // Unity 退出会清空 Temp；矩阵父进程需要读取持久证据。
        const string destination = "Library/AAGameValidation";
        Directory.CreateDirectory(destination);
        if (!Directory.Exists(ResultDirectory))
            return;
        foreach (string file in Directory.GetFiles(ResultDirectory, "*.txt"))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
    }
}
