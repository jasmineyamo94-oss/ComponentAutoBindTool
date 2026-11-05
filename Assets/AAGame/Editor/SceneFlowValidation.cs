using System;
using System.IO;
using ComponentAutoBindTool.ViewCore;
using UnityEngine;
using UnityEngine.EventSystems;
using VContainer;
using VContainer.Unity;

public static class SceneFlowValidation
{
    public static async void Run()
    {
        try
        {
            var root = (GameRootLifetimeScope)LifetimeScope.Find<GameRootLifetimeScope>();
            var flow = root.Container.Resolve<SceneFlowCoordinator>();
            var state = root.Container.Resolve<FlowState>();
            await flow.EnterMainFromStartAsync();
            var main = UnityEngine.Object.FindFirstObjectByType<MainRootMenuView>();
            int id = main.GetInstanceID();
            for (int i = 0; i < 3; i++)
            {
                float before = state.MainSeconds;
                await flow.EnterLevelFromMainAsync(new LevelLoadRequestContent(101 + i));
                if (!root.UIRoots.Main.IsSuspended)
                    throw new Exception("Main 未挂起");
                var session = LifetimeScope
                    .Find<LevelSceneScope>()
                    .Container.Resolve<LevelGameplaySession>();
                if (session.Request.LevelId != 101 + i)
                    throw new Exception("Scoped 请求注入错误");
                await flow.RestartLevelAsync();
                if (session.Restarts != 1)
                    throw new Exception("重开会话未复用");
                await Awaitable.WaitForSecondsAsync(.1f);
                if (state.MainSeconds <= before)
                    throw new Exception("Main 计时已停止");
                await flow.ReturnToMainFromLevelAsync();
                if (main == null || main.GetInstanceID() != id || root.UIRoots.Main.IsSuspended)
                    throw new Exception("Main 未无损恢复");
                if (ViewManagerDebugRegistry.ActiveManagers.Count != 2)
                    throw new Exception("关卡管理器泄漏");
            }
            if (
                UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length
                    != 1
                || UnityEngine
                    .Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None)
                    .Length != 1
            )
                throw new Exception("重复全局对象");
            Directory.CreateDirectory("Temp/SceneFlow");
            File.WriteAllText(
                "Temp/SceneFlow/result.txt",
                "PASS: 3 rounds, scoped requests, restart reuse, Main continuity, manager cleanup, unique globals"
            );
            Debug.Log("[SceneFlowValidation] PASS");
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory("Temp/SceneFlow");
            File.WriteAllText("Temp/SceneFlow/result.txt", ex.ToString());
            Debug.LogException(ex);
        }
    }
}
