using ComponentAutoBindTool.ViewCore;
using VContainer;

public partial class LevelLoadingView : FlowViewBase
{
    public override void ConfigureWindow(IContainerBuilder builder)
    {
        RegisterWindow<LevelLoadingPresenter, UIView>(builder, _view);
    }
}
