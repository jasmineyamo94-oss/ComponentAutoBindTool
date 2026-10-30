using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using ComponentAutoBindTool.ViewCore;
using UnityEditor.Compilation;
using UnityEngine;
using BindData = ComponentAutoBindTool.Core.ComponentAutoBindTool.BindData;

namespace ComponentAutoBindTool.Editor
{
    internal static class AutoBindValidator
    {
        public static AutoBindValidationResult Validate(Core.ComponentAutoBindTool target, AutoBindGlobalSetting setting)
        {
            var result = new AutoBindValidationResult();
            if (target == null || setting == null)
            {
                result.AddError("AutoBind 目标或全局配置为空。");
                return result;
            }
            try
            {
                Validate(new AutoBindBuildInput(target, setting, target.BindDatas), result);
            }
            catch (Exception ex)
            {
                result.AddError("无法准备校验输入：" + ex.Message);
            }
            return result;
        }

        internal static void Validate(AutoBindBuildInput input, AutoBindValidationResult result)
        {
            ValidateHost(input, result);
            ValidatePath(input, result);
            ValidateBindings(input, result);
        }

        private static void ValidateHost(AutoBindBuildInput input, AutoBindValidationResult result)
        {
            Type type = input.HostType;
            if (type == null)
            {
                result.AddError("未指定目标脚本。");
                return;
            }
            if (input.Target.m_targetScript.gameObject != input.Target.gameObject)
                result.AddError("目标脚本必须挂在当前 AutoBind 根节点上。");
            if (type.IsAbstract || type.IsGenericType || type.IsNested)
                result.AddError("目标脚本必须是非抽象、非泛型、非嵌套的 MonoBehaviour。");
            if (string.IsNullOrEmpty(input.ScriptPath) || !File.Exists(input.ScriptPath))
            {
                result.AddError("无法读取目标脚本源码。");
                return;
            }

            AutoBindSourceInspector.ValidateDeclarations(input, result);
            MethodInfo ensure = AutoBindCodeGenerator.FindBaseMethod(type, "EnsureAutoBind", typeof(GameObject));
            if (ensure != null && !AutoBindCodeGenerator.CanOverride(ensure, typeof(void)))
                result.AddError("最近的基类 EnsureAutoBind 不是可访问、未封闭的 void 虚方法。");
            MethodInfo getter = AutoBindCodeGenerator.FindBaseMethod(type, "GetGeneratedUIView");
            bool requiresGetter = typeof(WidgetViewBase).IsAssignableFrom(type) ||
                                  (getter != null && getter.ReturnType == typeof(object));
            if (requiresGetter)
            {
                if (!AutoBindCodeGenerator.CanOverride(getter, typeof(object)))
                    result.AddError("GetGeneratedUIView 必须是可访问、未封闭的 object 虚方法。");
            }
        }

        private static void ValidatePath(AutoBindBuildInput input, AutoBindValidationResult result)
        {
            if (string.IsNullOrEmpty(input.OutputPath))
            {
                result.AddError("无法解析代码保存路径。");
                return;
            }
            if (!AutoBindPathUtility.IsPathUnderAssets(input.OutputPath))
            {
                result.AddError("代码保存路径必须位于 Assets 内：" + input.OutputPath);
                return;
            }
            string outputAssetPath = AutoBindPathUtility.ToAssetRelativePath(input.OutputPath);
            string sourceAssembly = CompilationPipeline.GetAssemblyNameFromScriptPath(input.ScriptPath);
            string outputAssembly = CompilationPipeline.GetAssemblyNameFromScriptPath(outputAssetPath);
            if (string.IsNullOrEmpty(sourceAssembly) || sourceAssembly != outputAssembly)
                result.AddError("生成文件必须与目标脚本位于同一程序集，请开启跟随目标脚本路径。");
            if (File.Exists(input.OutputPath))
            {
                string existing = File.ReadAllText(input.OutputPath);
                string marker = "// AutoBind host: " + input.HostType?.FullName + "\n";
                bool owned = existing.Replace("\r\n", "\n").Contains(marker);
                // 接纳此前生成器留下的无固定文件头产物。
                bool legacy = existing.Contains("//自动生成于：") &&
                    Regex.IsMatch(existing, @"\bpartial\s+class\s+" + Regex.Escape(input.HostType?.Name ?? "") + @"\b") &&
                    (string.IsNullOrEmpty(input.HostType?.Namespace)
                        ? !Regex.IsMatch(existing, @"\bnamespace\s+")
                        : Regex.IsMatch(existing, @"\bnamespace\s+" + Regex.Escape(input.HostType.Namespace) + @"\s*\{"));
                if (!owned && !legacy) result.AddError("输出文件已由其他宿主或手写代码占用：" + outputAssetPath);
            }
        }

        private static void ValidateBindings(AutoBindBuildInput input, AutoBindValidationResult result)
        {
            IReadOnlyList<BindData> bindings = input.Bindings;
            if (bindings == null || bindings.Count == 0)
            {
                result.AddWarning("当前没有绑定数据，生成的 UIView 将为空。");
                return;
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < bindings.Count; i++)
            {
                BindData binding = bindings[i];
                if (binding == null)
                {
                    result.AddError($"[{i}] 绑定记录为空。");
                    continue;
                }
                string name = AutoBindFieldNameUtility.BuildFieldName(binding.Name);
                if (!AutoBindFieldNameUtility.IsValidIdentifier(name) || name == "UIView")
                    result.AddError($"[{i}] 生成字段名非法或与类型名冲突：{name}");
                else if (!names.Add(name))
                    result.AddError($"[{i}] 生成字段名重复：{name}");
                if (binding.BindCom == null)
                {
                    result.AddError($"[{i}] 组件引用为空。");
                    continue;
                }
                Transform transform = binding.BindCom.transform;
                if (transform != input.Target.transform && !transform.IsChildOf(input.Target.transform))
                    result.AddError($"[{i}] 组件不属于当前 AutoBind 根节点。");
                Type type = binding.BindCom.GetType();
                if (type.ContainsGenericParameters || !type.IsVisible)
                    result.AddError($"[{i}] 组件类型不可被生成代码公开引用：{type.FullName}");
                if (binding.BindCom == input.Target.m_targetScript)
                    result.AddError($"[{i}] 不能将当前根节点的目标脚本自身作为绑定项。");
                if (binding.Name != null && binding.Name.StartsWith("Widget_", StringComparison.Ordinal) &&
                    binding.BindCom is not WidgetViewBase)
                    result.AddError($"[{i}] Widget 前缀绑定的组件必须继承 WidgetViewBase：{type.FullName}");
                if (binding.Name != null && binding.Name.StartsWith("Sub_", StringComparison.Ordinal))
                {
                    bool hasNestedTarget = binding.BindCom.TryGetComponent<Core.ComponentAutoBindTool>(out var nested) &&
                                           nested.m_targetScript == binding.BindCom && nested != binding.BindCom;
                    if (binding.BindCom.gameObject == input.Target.gameObject ||
                        (!(binding.BindCom is IAutoBindHost) && !hasNestedTarget))
                        result.AddError($"[{i}] Sub 前缀必须绑定嵌套 AutoBind 子宿主脚本：{type.FullName}");
                }
            }
        }

    }
}
