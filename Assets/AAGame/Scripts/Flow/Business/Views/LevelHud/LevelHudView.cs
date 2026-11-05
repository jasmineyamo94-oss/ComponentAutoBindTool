using ComponentAutoBindTool.ViewCore;
using VContainer;

public partial class LevelHudView : FlowViewBase
{
    public override void ConfigureWindow(IContainerBuilder builder)
    {
        RegisterWindow<LevelHudPresenter, UIView>(builder, _view);
    }
}
