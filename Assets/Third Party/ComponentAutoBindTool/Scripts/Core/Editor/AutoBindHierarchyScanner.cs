using System.Collections.Generic;
using ComponentAutoBindTool.Core;
using ComponentAutoBindTool.ViewCore;
using TMPro;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    internal static class AutoBindHierarchyScanner
    {
        public static AutoBindScanResult CollectBindings(Core.ComponentAutoBindTool target, AutoBindKeyMapSetting keyMapSetting)
        {
            var result = new AutoBindScanResult();
            if (target == null)
            {
                result.AddError("AutoBind 根节点为空。");
                return result;
            }

            if (keyMapSetting == null)
            {
                result.AddError("AutoBindKeyMapSetting 为空。");
                return result;
            }

            var childRoots = new List<Transform>();
            CollectChildRoots(target.transform, childRoots);

            for (int i = 0; i < childRoots.Count; i++)
            {
                CollectTransformBindings(childRoots[i], keyMapSetting, result);
            }

            return result;
        }

        private static void CollectChildRoots(Transform transform, List<Transform> childRoots)
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform currentChild = transform.GetChild(i);
                if (currentChild.name.Contains("NonRoot ==>"))
                {
                    continue;
                }

                // TMP 自动生成的 SubMeshUI 子对象，跳过
                if (currentChild.GetComponent<TMP_SubMeshUI>() != null)
                {
                    continue;
                }

                childRoots.Add(currentChild);
                // 父级可以绑定嵌套根节点，但其后代交由嵌套 AutoBind 自行扫描。
                if (currentChild.GetComponent<Core.ComponentAutoBindTool>() != null)
                {
                    continue;
                }

                CollectChildRoots(currentChild, childRoots);
            }
        }

        private static void CollectTransformBindings(Transform target, AutoBindKeyMapSetting keyMapSetting,
            AutoBindScanResult result)
        {
            string[] nameParts = target.name.Split('_');
            if (nameParts.Length <= 1)
            {
                return;
            }

            string fieldSuffix = nameParts[nameParts.Length - 1];
            for (int i = 0; i < nameParts.Length - 1; i++)
            {
                string prefix = nameParts[i];
                if (!keyMapSetting.TryGetComponentTypeName(prefix, out string componentTypeName))
                {
                    result.AddError($"{target.name}的命名中{prefix}不存在对应的组件类型，绑定失败");
                    return;
                }

                Component component = ResolveComponent(target, prefix, componentTypeName, result);
                if (component == null) continue;

                result.AddBindData($"{prefix}_{fieldSuffix}", component);
            }
        }

        private static Component ResolveComponent(Transform target, string prefix, string componentTypeName, AutoBindScanResult result)
        {
            if (prefix == "Widget" || componentTypeName == typeof(WidgetViewBase).FullName || componentTypeName == nameof(WidgetViewBase))
            {
                if (target.TryGetComponent<Core.ComponentAutoBindTool>(out var nestedTool) && nestedTool.m_targetScript != null)
                {
                    var selected = nestedTool.m_targetScript;
                    if (selected.gameObject != target.gameObject)
                    {
                        result.AddError($"{target.name}的嵌套 AutoBind 目标脚本必须挂载在当前节点上。");
                        return null;
                    }
                    if (selected is WidgetViewBase widget) return widget;
                    result.AddError($"{target.name}使用 Widget 前缀，但目标脚本未继承 WidgetViewBase；普通宿主请使用 Sub 前缀。");
                    return null;
                }
                var widgets = target.GetComponents<WidgetViewBase>();
                if (widgets.Length == 1) return widgets[0];
                if (widgets.Length == 0)
                {
                    result.AddError($"{target.name}上不存在继承自WidgetViewBase的组件");
                    return null;
                }

                result.AddError($"{target.name}上存在多个WidgetViewBase组件，请在同节点的AutoBind目标脚本中指定一个");
                return null;
            }

            if (prefix == "Sub" || componentTypeName == typeof(Core.ComponentAutoBindTool).FullName)
            {
                if (!target.TryGetComponent<Core.ComponentAutoBindTool>(out var nestedTool))
                {
                    result.AddError($"{target.name}使用 Sub 前缀，但节点上未挂载嵌套 AutoBind。");
                    return null;
                }
                var selected = nestedTool.m_targetScript;
                if (selected == null)
                {
                    result.AddError($"{target.name}使用 Sub 前缀，但嵌套 AutoBind 未指定目标脚本。");
                    return null;
                }
                if (selected.gameObject != target.gameObject || selected == nestedTool)
                {
                    result.AddError($"{target.name}的嵌套 AutoBind 目标必须是同节点的宿主脚本。");
                    return null;
                }
                return selected;
            }

            Component component = target.GetComponent(componentTypeName);
            if (component == null) result.AddError($"{target.name}上不存在{componentTypeName}的组件");
            return component;
        }

    }
}
