using ComponentAutoBindTool.ViewCore;
using VContainer;

namespace ComponentAutoBindTool.Tests.AssemblyHosts
{
    public partial class AssemblyWindowHost : ViewBase
    {
        // 编译夹具刻意不在首次生成前引用生成 UIView。
        public override void ConfigureWindow(IContainerBuilder builder) { }
    }
}
