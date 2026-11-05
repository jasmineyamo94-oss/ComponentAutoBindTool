using System;
using ComponentAutoBindTool.ViewCore;
using UnityEngine;

public sealed class MainRootMenuPresenter
    : WindowPresenterBase<MainRootMenuView.UIView, EmptyViewArgs>
{
    private readonly ISceneFlowCoordinator _flow;
    private readonly FlowState _state;
    private float _nextUi;

    public MainRootMenuPresenter(
        MainRootMenuView.UIView view,
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
        _state.MainSeconds += deltaTime;
        if (Time.unscaledTime < _nextUi)
            return;
        _nextUi = Time.unscaledTime + .1f;
        View.tMTxtStatus.text =
            "章节 101 · 主界面计时 "
            + _state.MainSeconds.ToString("F1")
            + "s"
            + "\n"
            + _state.Error;
    }

    private async void Primary()
    {
        try
        {
            await _flow.EnterLevelFromMainAsync(new LevelLoadRequestContent(101), DestroyToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _state.Error = ex.Message;
            Debug.LogException(ex);
        }
    }
}
