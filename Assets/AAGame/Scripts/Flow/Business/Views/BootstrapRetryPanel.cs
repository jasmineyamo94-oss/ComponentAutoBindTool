using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VContainer;
using VContainer.Unity;

public sealed class BootstrapRetryPanel : MonoBehaviour
{
    public Canvas Canvas;
    public TMP_Text Error;
    public Button Retry;
    private FlowState _state;
    private ISceneFlowCoordinator _flow;

    private void Start()
    {
        var resolver = LifetimeScope.Find<GameRootLifetimeScope>().Container;
        _state = resolver.Resolve<FlowState>();
        _flow = resolver.Resolve<ISceneFlowCoordinator>();
        Retry.onClick.AddListener(RetryStart);
    }

    private void Update()
    {
        if (_state == null)
            return;
        bool visible = _state.Stage == FlowStage.Bootstrap && !string.IsNullOrEmpty(_state.Error);
        Canvas.enabled = visible;
        if (visible && Error.text != _state.Error)
            Error.text = _state.Error;
    }

    private async void RetryStart()
    {
        try
        {
            _state.Error = "";
            await _flow.EnterStartFromBootstrapAsync(destroyCancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            _state.Error = error.Message;
            Debug.LogException(error);
        }
    }

    private void OnDestroy()
    {
        if (Retry != null)
            Retry.onClick.RemoveListener(RetryStart);
    }
}
