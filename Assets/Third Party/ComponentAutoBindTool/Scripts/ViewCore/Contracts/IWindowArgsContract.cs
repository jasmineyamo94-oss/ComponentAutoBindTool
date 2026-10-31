using System;

namespace ComponentAutoBindTool.ViewCore
{
    /// <summary>Presenter 为类型化路由提供的参数契约。</summary>
    public interface IWindowArgsContract
    {
        Type ArgsType { get; }
    }
}
