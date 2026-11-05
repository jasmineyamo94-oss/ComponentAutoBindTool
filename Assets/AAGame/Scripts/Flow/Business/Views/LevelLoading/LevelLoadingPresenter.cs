using ComponentAutoBindTool.ViewCore;

public sealed class LevelLoadingPresenter
    : WindowPresenterBase<LevelLoadingView.UIView, EmptyViewArgs>
{
    private readonly LoadingOverlayService _loading;

    public LevelLoadingPresenter(LevelLoadingView.UIView view, LoadingOverlayService loading)
        : base(view) => _loading = loading;

    protected override void OnOpen(EmptyViewArgs args)
    {
        View.tMTxtTitle.text = "加载中";
    }

    protected override void OnWindowTick(float deltaTime)
    {
        View.tMTxtStatus.text = (_loading.Progress * 100).ToString("F0") + "%";
        var rect = View.imgProgress.rectTransform;
        rect.anchorMax = new UnityEngine.Vector2(.15f + .7f * _loading.Progress, .36f);
    }
}
