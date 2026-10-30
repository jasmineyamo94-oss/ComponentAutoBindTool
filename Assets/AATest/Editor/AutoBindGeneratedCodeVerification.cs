using System;
using System.IO;
using System.Reflection;
using ComponentAutoBindTool.Editor;
using ComponentAutoBindTool.ViewCore;
using ComponentAutoBindTool.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace ComponentAutoBindTool.Tests
{
    // 分两次 batchmode 执行：第一次生成，第二次在 Unity 真正编译产物后检查接口分派。
    // 请在验证副本运行，避免给正常项目留下临时测试场景与生成代码。
    public static class AutoBindGeneratedCodeVerification
    {
        private const string Output = "Assets/AATest/CompilationGenerated";
        public static void Prepare()
        {
            Directory.CreateDirectory(Output);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            var setting = ScriptableObject.CreateInstance<AutoBindGlobalSetting>();
            var data = new SerializedObject(setting);
            data.FindProperty("m_UseGlobalDefaultSavePath").boolValue = false;
            data.FindProperty("m_CodePath").stringValue = Output;
            data.ApplyModifiedPropertiesWithoutUndo();
            var keyMap = ScriptableObject.CreateInstance<AutoBindKeyMapSetting>();
            foreach (Type hostType in new[] { typeof(AutoBindPlainHost), typeof(AutoBindExplicitHost),
                         typeof(AutoBindProtectedHost), typeof(AutoBindVirtualHost) })
            {
                var root = new GameObject(hostType.Name);
                var host = (MonoBehaviour)root.AddComponent(hostType);
                var tool = root.AddComponent<Core.ComponentAutoBindTool>();
                tool.m_targetScript = host;
                var child = new GameObject("Btn_Value", typeof(RectTransform), typeof(Button));
                child.transform.SetParent(root.transform);
                EditorSceneManager.SaveScene(scene, Output + "/Verification.unity");
                var report = AutoBindBuildPipeline.GenerateCode(tool, setting, keyMap);
                if (!report.Succeeded) throw new InvalidOperationException(report.ValidationResult.BuildReport());
            }
            var parentObject = new GameObject("CascadeParent");
            var parent = parentObject.AddComponent<AutoBindCascadeParentHost>();
            var parentTool = parentObject.AddComponent<Core.ComponentAutoBindTool>();
            parentTool.m_targetScript = parent;
            var childObject = new GameObject("Sub_Child");
            childObject.transform.SetParent(parentObject.transform);
            var childHost = childObject.AddComponent<AutoBindCascadeChildHost>();
            var childTool = childObject.AddComponent<Core.ComponentAutoBindTool>();
            childTool.m_targetScript = childHost;
            var button = new GameObject("Btn_Value", typeof(RectTransform), typeof(Button));
            button.transform.SetParent(childObject.transform);
            EditorSceneManager.SaveScene(scene, Output + "/Verification.unity");
            foreach (var tool in new[] { childTool, parentTool })
            {
                var report = AutoBindBuildPipeline.GenerateCode(tool, setting, keyMap);
                if (!report.Succeeded) throw new InvalidOperationException(report.ValidationResult.BuildReport());
            }
            Debug.Log("四种宿主的真实编译验证产物已生成。");
        }

        public static void Verify()
        {
            EditorSceneManager.OpenScene(Output + "/Verification.unity");
            int count = 0;
            foreach (var tool in UnityEngine.Object.FindObjectsByType<Core.ComponentAutoBindTool>(FindObjectsSortMode.None))
            {
                if (tool.m_targetScript is AutoBindCascadeParentHost || tool.m_targetScript is AutoBindCascadeChildHost)
                    continue;
                var host = tool.m_targetScript as IAutoBindHost;
                if (host == null) throw new InvalidOperationException("生成宿主未实现 IAutoBindHost。");
                host.EnsureAutoBind(tool.gameObject);
                FieldInfo field = tool.m_targetScript.GetType().GetField("view", BindingFlags.Instance | BindingFlags.NonPublic);
                object view = field?.GetValue(tool.m_targetScript);
                object button = view?.GetType().GetField("btnValue")?.GetValue(view);
                if (!ReferenceEquals(button, tool.GetBindComponent<Button>(0, "btnValue")))
                    throw new InvalidOperationException("接口调用未执行生成绑定。");
                host.EnsureAutoBind(tool.gameObject);
                if (!ReferenceEquals(view, field.GetValue(tool.m_targetScript)))
                    throw new InvalidOperationException("绑定未保持幂等。");
                count++;
            }
            if (count != 4) throw new InvalidOperationException("验证宿主数量不符。");
            Debug.Log("AUTOBIND_COMPILED_HOSTS_PASS: 4");
            VerifyCascade();
            VerifyExampleIsUnchanged();
        }

        private static void VerifyCascade()
        {
            var parent = UnityEngine.Object.FindFirstObjectByType<AutoBindCascadeParentHost>();
            var child = parent.GetComponentInChildren<AutoBindCascadeChildHost>();
            var parentHost = parent as IAutoBindHost;
            if (parentHost == null || (object)child is not IAutoBindHost)
                throw new InvalidOperationException("级联验证宿主未实现生成接口。");
            FieldInfo childField = child.GetType().GetField("view", BindingFlags.Instance | BindingFlags.NonPublic);
            if (childField == null || childField.GetValue(child) != null)
                throw new InvalidOperationException("级联验证子宿主初始状态不符。");
            parentHost.EnsureAutoBind(parent.gameObject);
            object childView = childField.GetValue(child);
            var childTool = child.GetComponent<Core.ComponentAutoBindTool>();
            object button = childView?.GetType().GetField("btnValue")?.GetValue(childView);
            if (childView == null || !ReferenceEquals(button, childTool.GetBindComponent<Button>(0, "btnValue")))
                throw new InvalidOperationException("父宿主没有级联初始化子宿主。");
            parentHost.EnsureAutoBind(parent.gameObject);
            if (!ReferenceEquals(childView, childField.GetValue(child)))
                throw new InvalidOperationException("级联绑定未保持幂等。");
            Debug.Log("AUTOBIND_CASCADE_COMPILED_PASS: 父接口调用已初始化子宿主。");
        }

        private static void VerifyExampleIsUnchanged()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Example.Editor.AutoBindExampleSetup.PrefabPath);
            var setting = AutoBindEditorAssetLocator.LoadSingletonAsset<AutoBindGlobalSetting>();
            int count = 0;
            foreach (var tool in prefab.GetComponentsInChildren<Core.ComponentAutoBindTool>(true))
            {
                var input = new AutoBindBuildInput(tool, setting, tool.BindDatas);
                DateTime timestamp = File.GetLastWriteTimeUtc(input.OutputPath);
                var inspector = (ComponentAutoBindToolInspector)UnityEditor.Editor.CreateEditor(tool);
                try
                {
                    var report = inspector.RunPipeline(true);
                    if (!report.Succeeded || report.CodeFileUpdated || report.BindingsUpdated ||
                        File.GetLastWriteTimeUtc(input.OutputPath) != timestamp)
                        throw new InvalidOperationException("示例重复生成未保持不变：" + report.ValidationResult.BuildReport());
                    count++;
                }
                finally { UnityEngine.Object.DestroyImmediate(inspector); }
            }
            if (count != 4) throw new InvalidOperationException("示例宿主数量不符。");
            Debug.Log("AUTOBIND_EXAMPLE_NOOP_PASS: 4");
        }
    }
}
