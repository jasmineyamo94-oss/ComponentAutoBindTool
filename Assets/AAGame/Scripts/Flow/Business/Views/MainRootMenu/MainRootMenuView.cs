using ComponentAutoBindTool.ViewCore;
using VContainer;

public partial class MainRootMenuView : FlowViewBase
{
    public override void ConfigureWindow(IContainerBuilder builder)
    {
        RegisterWindow<MainRootMenuPresenter, UIView>(builder, _view);
    }
}
