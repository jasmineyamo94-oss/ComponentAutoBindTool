using System;
using System.Threading;
using ComponentAutoBindTool.AssetCore;
using ComponentAutoBindTool.ViewCore;
using UnityEngine;
using VContainer;

public sealed class LoadingOverlayService : IDisposable
{
    private readonly SceneViewArchitecture _views;
    public float Progress { get; private set; }
    private int _id;

    public LoadingOverlayService(
        IObjectResolver resolver,
        AssetService assets,
        GlobalUIRootRegistry roots
    ) => _views = new SceneViewArchitecture(resolver, assets, roots.Overlay, 16000, "Overlay");

    public void Tick() => _views.Tick();

    public async Awaitable BeginAsync(CancellationToken ct)
    {
        Progress = 0;
        try
        {
            _id = await _views.Manager.ShowViewAsync(
                LevelLoadingRoutes.Main,
                cancellationToken: ct
            );
            if (_id == 0)
                throw new InvalidOperationException("Loading 开窗失败");
            await Awaitable.EndOfFrameAsync(ct);
            await Awaitable.NextFrameAsync(ct);
        }
        catch
        {
            End();
            throw;
        }
    }

    public void Report(float value) => Progress = Mathf.Clamp01(value);

    public void End()
    {
        if (_id != 0)
        {
            int id = _id;
            _id = 0;
            _views.Manager.CloseView(id);
        }
    }

    public void Dispose() => _views.Dispose();
}
