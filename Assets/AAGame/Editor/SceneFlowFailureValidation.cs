using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ComponentAutoBindTool.AssetCore;
using ComponentAutoBindTool.ViewCore;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VContainer;
using VContainer.Unity;

// 在真实 Play 环境注入加载故障，不向运行时服务增加调试开关。
public static class SceneFlowFailureValidation
{
    private sealed class FaultLoader : ISceneLoader
    {
        public readonly SceneLoaderService Inner = new();
        public string Mode;
        public CancellationTokenSource Cancellation;

        public async Awaitable<Scene> LoadAsync(string path, CancellationToken ct)
        {
            if (Mode == "load")
                throw new InvalidOperationException("验证：加载失败");
            var scene = await Inner.LoadAsync(path, ct);
            if (Mode == "scope")
                UnityEngine.Object.DestroyImmediate(LifetimeScope.Find<LevelSceneScope>(scene));
            if (Mode == "cancel")
                Cancellation.Cancel();
            return scene;
        }

        public async Awaitable UnloadAsync(Scene scene)
        {
            if (Mode == "unload" && scene.IsValid() && scene.name == "LevelScene")
                throw new InvalidOperationException("验证：卸载失败");
            await Inner.UnloadAsync(scene);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    private static async Task Consume(Awaitable operation) => await operation;

    private static async Awaitable ExpectFailure(Awaitable operation)
    {
        bool failed = false;
        try
        {
            await operation;
        }
        catch
        {
            failed = true;
        }
        Check(failed, "故障注入未触发异常");
    }

    public static async void Run()
    {
        var root = (GameRootLifetimeScope)LifetimeScope.Find<GameRootLifetimeScope>();
        var flow = root.Container.Resolve<SceneFlowCoordinator>();
        var state = root.Container.Resolve<FlowState>();
        var field = typeof(SceneFlowCoordinator).GetField(
            "_loader",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        var original = field.GetValue(flow);
        var faults = new FaultLoader();
        try
        {
            if (state.Stage == FlowStage.Start)
                await flow.EnterMainFromStartAsync();
            Check(state.Stage == FlowStage.Main, "验证必须从 Main 开始");
            field.SetValue(flow, faults);
            foreach (string mode in new[] { "load", "scope", "cancel" })
            {
                using var cancel = new CancellationTokenSource();
                faults.Mode = mode;
                faults.Cancellation = cancel;
                await ExpectFailure(
                    flow.EnterLevelFromMainAsync(new LevelLoadRequestContent(101), cancel.Token)
                );
                Check(
                    !flow.IsTransitioning
                        && state.Stage == FlowStage.Main
                        && !root.UIRoots.Main.IsSuspended,
                    "进入失败后未恢复 Main：" + mode
                );
                Check(
                    !SceneManager
                        .GetSceneByPath(SceneFlowCoordinator.Prefix + "LevelScene.unity")
                        .isLoaded,
                    "失败关卡未卸载"
                );
            }
            faults.Mode = "";
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ExpectFailure(
                    flow.EnterLevelFromMainAsync(new LevelLoadRequestContent(101), cancelled.Token)
                );
                Check(!flow.IsTransitioning, "Loading 取消未释放锁");
            }
            var first = Consume(flow.EnterLevelFromMainAsync(new LevelLoadRequestContent(111)));
            await flow.EnterLevelFromMainAsync(new LevelLoadRequestContent(222));
            await first;
            Check(
                LifetimeScope
                    .Find<LevelSceneScope>()
                    .Container.Resolve<LevelGameplaySession>()
                    .Request.LevelId == 111,
                "重复点击替换了当前请求"
            );
            faults.Mode = "unload";
            await ExpectFailure(flow.ReturnToMainFromLevelAsync());
            Check(
                !flow.IsTransitioning
                    && state.Stage == FlowStage.Main
                    && !root.UIRoots.Main.IsSuspended,
                "卸载失败未恢复 Main"
            );
            faults.Mode = "";
            await flow.EnterLevelFromMainAsync(new LevelLoadRequestContent(101));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ExpectFailure(flow.RestartLevelAsync(cancelled.Token));
                Check(state.Stage == FlowStage.Main && !flow.IsTransitioning, "重开取消未回退");
            }
            var manager = LifetimeScope.Find<MainSceneScope>().Container.Resolve<ViewManager>();
            int id = manager.ShowView(MainRootMenuRoutes.Main);
            var main = UnityEngine.Object.FindFirstObjectByType<MainRootMenuView>();
            var nested = new GameObject(
                "验证禁用的嵌套 Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(GraphicRaycaster)
            );
            nested.transform.SetParent(main.transform, false);
            nested.GetComponent<Canvas>().enabled = false;
            nested.GetComponent<GraphicRaycaster>().enabled = false;
            root.UIRoots.Main.Suspend();
            manager.ShowView(MainRootMenuRoutes.Main);
            root.UIRoots.Main.Resume();
            Check(
                main.GetComponent<Canvas>().enabled
                    && main.GetComponent<GraphicRaycaster>().enabled,
                "活动窗重复开窗恢复错误"
            );
            Check(
                !nested.GetComponent<Canvas>().enabled
                    && !nested.GetComponent<GraphicRaycaster>().enabled,
                "嵌套组件原始禁用状态丢失"
            );
            manager.CloseView(id);
            root.UIRoots.Main.Suspend();
            manager.ShowView(MainRootMenuRoutes.Main);
            Check(!main.GetComponent<GraphicRaycaster>().enabled, "挂起缓存窗接收射线");
            root.UIRoots.Main.Resume();
            Check(
                main.GetComponent<Canvas>().enabled
                    && main.GetComponent<GraphicRaycaster>().enabled,
                "缓存窗重开恢复错误"
            );
            UnityEngine.Object.Destroy(nested);
            using (
                var assets = new AssetService(
                    new DirectCatalogBackend(new Dictionary<string, UnityEngine.Object>())
                )
            )
            using (var loading = new LoadingOverlayService(root.Container, assets, root.UIRoots))
            {
                var broken = new SceneFlowCoordinator(
                    new SceneLoaderService(),
                    loading,
                    root.UIRoots,
                    state
                );
                await ExpectFailure(
                    broken.EnterLevelFromMainAsync(new LevelLoadRequestContent(101))
                );
                Check(!broken.IsTransitioning, "Loading 资源缺失未释放锁");
            }
            Directory.CreateDirectory("Temp/SceneFlow");
            File.WriteAllText(
                "Temp/SceneFlow/failures.txt",
                "PASS: load/scope/cancel/unload/restart/duplicate/loading/stage-active/stage-cache/nested-state"
            );
            Debug.Log("[SceneFlowFailureValidation] PASS");
        }
        catch (Exception error)
        {
            File.WriteAllText("Temp/SceneFlow/failures.txt", error.ToString());
            Debug.LogException(error);
        }
        finally
        {
            field.SetValue(flow, original);
        }
    }
}
