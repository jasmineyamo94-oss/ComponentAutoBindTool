using System;
using ComponentAutoBindTool.ViewCore;
using UnityEngine;

public sealed class StartMenuPresenter : WindowPresenterBase<StartMenuView.UIView, EmptyViewArgs>
{
    private readonly ISceneFlowCoordinator _flow;
    private readonly FlowState _state;
    private float _nextUi;

    public StartMenuPresenter(
        StartMenuView.UIView view,
        ISceneFlowCoordinator flow,
        FlowState state
    )
        : base(view)
    {
        _flow = flow;
        _state = state;
    }

    protected override void OnCreate()
    {
        View.btnPrimary.onClick.AddListener(Primary);
    }

    protected override void OnDestroy()
    {
        View.btnPrimary.onClick.RemoveListener(Primary);
    }

    protected override void OnOpen(EmptyViewArgs args) { }

    protected override void OnWindowTick(float deltaTime)
    {
        if (Time.unscaledTime < _nextUi)
            return;
        _nextUi = Time.unscaledTime + .1f;
        View.tMTxtStatus.text = "场景流程演示" + "\n" + _state.Error;
    }

    private async void Primary()
    {
        try
        {
            await _flow.EnterMainFromStartAsync(DestroyToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _state.Error = ex.Message;
            Debug.LogException(ex);
        }
    }
}
