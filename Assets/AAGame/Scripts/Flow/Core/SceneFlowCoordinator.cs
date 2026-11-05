using System;
using System.Threading;
using ComponentAutoBindTool.ViewCore;
using UnityEngine;
using UnityEngine.SceneManagement;
using VContainer;
using VContainer.Unity;

public interface ISceneFlowCoordinator
{
    Awaitable EnterStartFromBootstrapAsync(CancellationToken ct = default);
    Awaitable EnterMainFromStartAsync(CancellationToken ct = default);
    Awaitable EnterLevelFromMainAsync(
        LevelLoadRequestContent request,
        CancellationToken ct = default
    );
    Awaitable ReturnToMainFromLevelAsync(CancellationToken ct = default);
    Awaitable RestartLevelAsync(CancellationToken ct = default);
}

public sealed class SceneFlowCoordinator : ISceneFlowCoordinator
{
    public const string Prefix = "Assets/AAGame/Scenes/";
    private readonly ISceneLoader _loader;
    private readonly LoadingOverlayService _loading;
    private readonly GlobalUIRootRegistry _roots;
    private readonly FlowState _state;
    private Scene _start,
        _main,
        _level;
    public bool IsTransitioning { get; private set; }

    public SceneFlowCoordinator(
        ISceneLoader loader,
        LoadingOverlayService loading,
        GlobalUIRootRegistry roots,
        FlowState state
    )
    {
        _loader = loader;
        _loading = loading;
        _roots = roots;
        _state = state;
    }

    private static T Scope<T>(Scene scene)
        where T : LifetimeScope =>
        LifetimeScope.Find<T>(scene) as T
        ?? throw new InvalidOperationException("场景缺少 Scope：" + scene.path);

    private static async Awaitable Show<T>(T scope, ViewRoute route, CancellationToken ct)
        where T : LifetimeScope
    {
        var manager = scope.Container.Resolve<ViewManager>();
        int id = route switch
        {
            ViewRoute<StartMenuView, EmptyViewArgs> r => await manager.ShowViewAsync(
                r,
                cancellationToken: ct
            ),
            ViewRoute<MainRootMenuView, EmptyViewArgs> r => await manager.ShowViewAsync(
                r,
                cancellationToken: ct
            ),
            ViewRoute<LevelHudView, EmptyViewArgs> r => await manager.ShowViewAsync(
                r,
                cancellationToken: ct
            ),
            _ => 0,
        };
        if (id == 0)
            throw new InvalidOperationException("开窗失败：" + route.Key);
    }

    public async Awaitable EnterStartFromBootstrapAsync(CancellationToken ct = default)
    {
        if (IsTransitioning || _state.Stage != FlowStage.Bootstrap)
            return;
        IsTransitioning = true;
        try
        {
            _start = await _loader.LoadAsync(Prefix + "StartScene.unity", ct);
            await Show(Scope<StartSceneScope>(_start), StartMenuRoutes.Main, ct);
            SceneManager.SetActiveScene(_start);
            _state.Stage = FlowStage.Start;
            _state.Error = "";
        }
        catch
        {
            await _loader.UnloadAsync(_start);
            throw;
        }
        finally
        {
            IsTransitioning = false;
        }
    }

    public async Awaitable EnterMainFromStartAsync(CancellationToken ct = default)
    {
        if (IsTransitioning || _state.Stage != FlowStage.Start)
            return;
        IsTransitioning = true;
        try
        {
            await _loading.BeginAsync(ct);
            _main = await _loader.LoadAsync(Prefix + "MainScene.unity", ct);
            await Show(Scope<MainSceneScope>(_main), MainRootMenuRoutes.Main, ct);
            await _loader.UnloadAsync(_start);
            SceneManager.SetActiveScene(_main);
            _state.Stage = FlowStage.Main;
            _state.Error = "";
        }
        catch
        {
            if (_start.isLoaded)
            {
                await _loader.UnloadAsync(_main);
                SceneManager.SetActiveScene(_start);
            }
            else if (_main.isLoaded)
            {
                // Start 已被卸载时，保留已准备成功的 Main 作为可用界面。
                SceneManager.SetActiveScene(_main);
                _state.Stage = FlowStage.Main;
            }
            throw;
        }
        finally
        {
            try
            {
                _loading.End();
            }
            finally
            {
                IsTransitioning = false;
            }
        }
    }

    public async Awaitable EnterLevelFromMainAsync(
        LevelLoadRequestContent request,
        CancellationToken ct = default
    )
    {
        if (IsTransitioning || _state.Stage != FlowStage.Main)
            return;
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        IsTransitioning = true;
        try
        {
            await _loading.BeginAsync(ct);
            _roots.Main.Suspend();
            _level = await _loader.LoadAsync(Prefix + "LevelScene.unity", ct);
            _loading.Report(.4f);
            var scope = Scope<LevelSceneScope>(_level);
            scope.Request = request;
            scope.Build();
            await Show(scope, LevelHudRoutes.Main, ct);
            _loading.Report(.8f);
            SceneManager.SetActiveScene(_level);
            _state.Stage = FlowStage.Level;
            _state.Error = "";
            _loading.Report(1);
            await Awaitable.NextFrameAsync(ct);
        }
        catch
        {
            await Recover();
            throw;
        }
        finally
        {
            try
            {
                _loading.End();
            }
            finally
            {
                IsTransitioning = false;
            }
        }
    }

    public async Awaitable ReturnToMainFromLevelAsync(CancellationToken ct = default)
    {
        if (IsTransitioning || _state.Stage != FlowStage.Level)
            return;
        IsTransitioning = true;
        try
        {
            await _loading.BeginAsync(ct);
            await Recover();
            _loading.Report(1);
            await Awaitable.NextFrameAsync();
        }
        finally
        {
            try
            {
                _loading.End();
            }
            finally
            {
                IsTransitioning = false;
            }
        }
    }

    private async Awaitable Recover()
    {
        try
        {
            try
            {
                await _loader.UnloadAsync(_level);
            }
            catch
            {
                // 适配器清理失败后，用原生卸载完成本地回收，再向调用方报告原异常。
                await new SceneLoaderService().UnloadAsync(_level);
                throw;
            }
        }
        finally
        {
            _roots.Main.Resume();
            if (_main.isLoaded)
            {
                SceneManager.SetActiveScene(_main);
                _state.Stage = FlowStage.Main;
            }
        }
    }

    public async Awaitable RestartLevelAsync(CancellationToken ct = default)
    {
        if (IsTransitioning || _state.Stage != FlowStage.Level)
            return;
        IsTransitioning = true;
        try
        {
            await _loading.BeginAsync(ct);
            ct.ThrowIfCancellationRequested();
            Scope<LevelSceneScope>(_level).Container.Resolve<LevelGameplaySession>().Restart();
            _loading.Report(1);
            await Awaitable.NextFrameAsync(ct);
        }
        catch
        {
            await Recover();
            throw;
        }
        finally
        {
            try
            {
                _loading.End();
            }
            finally
            {
                IsTransitioning = false;
            }
        }
    }
}

public sealed class GameColdBootEntryPoint : IStartable
{
    private readonly SceneFlowCoordinator _flow;
    private readonly FlowState _state;
    private readonly CancellationToken _token;

    public GameColdBootEntryPoint(SceneFlowCoordinator flow, FlowState state, LifetimeScope scope)
    {
        _flow = flow;
        _state = state;
        _token = scope.destroyCancellationToken;
    }

    public void Start() => StartObserved();

    private async void StartObserved()
    {
        try
        {
            await _flow.EnterStartFromBootstrapAsync(_token);
            string target = "";
#if UNITY_EDITOR
            target = UnityEditor.SessionState.GetString("AAGame.EditorStage", "");
            UnityEditor.SessionState.EraseString("AAGame.EditorStage");
#endif
            if (target == "MainScene" || target == "LevelScene")
                await _flow.EnterMainFromStartAsync(_token);
            if (target == "LevelScene")
                await _flow.EnterLevelFromMainAsync(new LevelLoadRequestContent(101), _token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _state.Error = ex.Message;
            Debug.LogException(ex);
        }
    }
}
