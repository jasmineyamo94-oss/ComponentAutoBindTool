using System;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

public interface ISceneLoader
{
    Awaitable<Scene> LoadAsync(string path, CancellationToken ct);
    Awaitable UnloadAsync(Scene scene);
}

public sealed class SceneLoaderService : ISceneLoader
{
    public async Awaitable<Scene> LoadAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var op = SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive);
        if (op == null)
            throw new InvalidOperationException("无法加载场景：" + path);
        while (!op.isDone)
            await Awaitable.NextFrameAsync();
        var scene = SceneManager.GetSceneByPath(path);
        if (ct.IsCancellationRequested)
        {
            await UnloadAsync(scene);
            ct.ThrowIfCancellationRequested();
        }
        return scene;
    }

    public async Awaitable UnloadAsync(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return;
        var op = SceneManager.UnloadSceneAsync(scene);
        if (op != null)
            while (!op.isDone)
                await Awaitable.NextFrameAsync();
    }
}
