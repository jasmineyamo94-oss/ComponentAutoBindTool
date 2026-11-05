using System;
using ComponentAutoBindTool.ViewCore;
using UnityEngine;

public sealed class LevelHudPresenter : WindowPresenterBase<LevelHudView.UIView, EmptyViewArgs>
{
    private readonly ISceneFlowCoordinator _flow;
    private readonly FlowState _state;
    private float _nextUi;
    private readonly LevelGameplaySession _session;

    public LevelHudPresenter(
        LevelHudView.UIView view,
        ISceneFlowCoordinator flow,
        FlowState state,
        LevelGameplaySession session
    )
        : base(view)
    {
        _flow = flow;
        _state = state;
        _session = session;
    }

    protected override void OnCreate()
    {
        View.btnPrimary.onClick.AddListener(Primary);
        View.btnBack.onClick.AddListener(Back);
    }

    protected override void OnDestroy()
    {
        View.btnPrimary.onClick.RemoveListener(Primary);
        View.btnBack.onClick.RemoveListener(Back);
    }

    protected override void OnOpen(EmptyViewArgs args) { }

    protected override void OnWindowTick(float deltaTime)
    {
        _session.Tick(deltaTime);
        if (Time.unscaledTime < _nextUi)
            return;
        _nextUi = Time.unscaledTime + .1f;
        View.tMTxtStatus.text =
            "关卡 "
            + _session.Request.LevelId
            + " · "
            + _session.Seconds.ToString("F1")
            + "s · 重开 "
            + _session.Restarts
            + "\n"
            + _state.Error;
    }

    private async void Primary()
    {
        try
        {
            await _flow.RestartLevelAsync(DestroyToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _state.Error = ex.Message;
            Debug.LogException(ex);
        }
    }

    private async void Back()
    {
        try
        {
            await _flow.ReturnToMainFromLevelAsync(DestroyToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _state.Error = ex.Message;
            Debug.LogException(ex);
        }
    }
}
