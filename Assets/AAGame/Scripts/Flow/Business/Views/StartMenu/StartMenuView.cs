using ComponentAutoBindTool.ViewCore;
using VContainer;

public partial class StartMenuView : FlowViewBase
{
    public override void ConfigureWindow(IContainerBuilder builder)
    {
        RegisterWindow<StartMenuPresenter, UIView>(builder, _view);
    }
}
