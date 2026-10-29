using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ComponentAutoBindTool.Core;
using ComponentAutoBindTool.Editor;
using ComponentAutoBindTool.Tests.AssemblyHosts;
using ComponentAutoBindTool.ViewCore;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Tests
{
    // 在验证副本分两次执行，确保断言检查的是 Unity 真正编译后的生成代码。
    public static class AutoBindAssemblyVerification
    {
        private const string Workspace = "Assets/AATest/AssemblyVerification";
        private const string ScenePath = Workspace + "/Verification.unity";
        private static readonly Type[] Hosts = { typeof(AssemblyPlainHost), typeof(AssemblyExplicitHost),
            typeof(AssemblyWindowHost), typeof(AssemblyWidgetHost) };

        public static void PrepareAll()
        {
            AutoBindGeneratedCodeVerification.Prepare();
            Prepare();
        }

        public static void VerifyAll()
        {
            AutoBindGeneratedCodeVerification.Verify();
            Verify();
        }

        public static void CompilePlayer()
        {
            var settings = new UnityEditor.Build.Player.ScriptCompilationSettings
            {
                target = BuildTarget.StandaloneWindows64,
                group = BuildTargetGroup.Standalone
            };
            string output = Path.GetFullPath("../BoundaryPlayerScripts");
            Directory.CreateDirectory(output);
            var result = UnityEditor.Build.Player.PlayerBuildInterface.CompilePlayerScripts(settings, output);
            if (result.assemblies == null || result.assemblies.Count == 0)
                throw new InvalidOperationException("Player 脚本编译失败。");
            string[] names = result.assemblies.Select(Path.GetFileNameWithoutExtension).ToArray();
            if (!names.Contains("ComponentAutoBindTool.Core") || !names.Contains("ComponentAutoBindTool.ViewCore") ||
                names.Contains("ComponentAutoBindTool.Editor")) throw new InvalidOperationException("Player 脚本输出程序集边界不符。");
            Debug.Log("AUTOBIND_PLAYER_COMPILE_PASS：Player 实际脚本编译成功，未包含 Editor 程序集。");
        }

        public static void Prepare()
        {
            Directory.CreateDirectory(Workspace);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            var setting = ScriptableObject.CreateInstance<AutoBindGlobalSetting>();
            var keyMap = ScriptableObject.CreateInstance<AutoBindKeyMapSetting>();
            try
            {
                if (!setting.UseGlobalDefaultSavePath) throw new InvalidOperationException("新配置没有默认跟随宿主目录。");
                foreach (Type type in Hosts)
                {
                    var root = new GameObject(type.Name);
                    var host = (MonoBehaviour)root.AddComponent(type);
                    var tool = root.GetComponent<Core.ComponentAutoBindTool>() ?? root.AddComponent<Core.ComponentAutoBindTool>();
                    tool.m_targetScript = host;
                    var child = new GameObject("Btn_Value", typeof(RectTransform), typeof(Button));
                    child.transform.SetParent(root.transform);
                    EditorSceneManager.SaveScene(scene, ScenePath);
                    var input = new AutoBindBuildInput(tool, setting, tool.BindDatas);
                    string targetAssembly = CompilationPipeline.GetAssemblyNameFromScriptPath(input.ScriptPath);
                    if (targetAssembly != "ComponentAutoBindTool.Tests.Hosts.dll" && targetAssembly != "ComponentAutoBindTool.Tests.Hosts")
                        throw new InvalidOperationException("编译夹具没有位于独立程序集。");

                    string snapshot = EditorJsonUtility.ToJson(tool);
                    var data = new SerializedObject(setting);
                    data.FindProperty("m_UseGlobalDefaultSavePath").boolValue = false;
                    data.FindProperty("m_CodePath").stringValue = Workspace;
                    data.ApplyModifiedPropertiesWithoutUndo();
                    var invalid = AutoBindBuildPipeline.GenerateCode(tool, setting, keyMap);
                    if (invalid.Succeeded || !invalid.ValidationResult.BuildReport().Contains("同一程序集") ||
                        snapshot != EditorJsonUtility.ToJson(tool) || File.Exists(Workspace + "/" + type.Name + ".BindComponents.cs"))
                        throw new InvalidOperationException("跨程序集生成未被安全拒绝。");
                    data.FindProperty("m_UseGlobalDefaultSavePath").boolValue = true;
                    data.ApplyModifiedPropertiesWithoutUndo();

                    var inspector = (ComponentAutoBindToolInspector)UnityEditor.Editor.CreateEditor(tool);
                    try
                    {
                        var report = inspector.RunPipeline(true, false, setting, keyMap);
                        if (!report.Succeeded) throw new InvalidOperationException(report.ValidationResult.BuildReport());
                    }
                    finally { Object.DestroyImmediate(inspector); }
                }
            }
            finally
            {
                Object.DestroyImmediate(setting);
                Object.DestroyImmediate(keyMap);
            }
            Debug.Log("AUTOBIND_ASSEMBLY_PREPARE_PASS：4 种独立程序集宿主已由 Inspector 生成。");
        }

        public static void Verify()
        {
            EditorSceneManager.OpenScene(ScenePath);
            int count = 0;
            foreach (var tool in Object.FindObjectsByType<Core.ComponentAutoBindTool>(FindObjectsSortMode.None))
            {
                var host = tool.m_targetScript;
                if (host.GetType().Assembly.GetName().Name != "ComponentAutoBindTool.Tests.Hosts")
                    throw new InvalidOperationException("宿主程序集不符。");
                var contract = host as IAutoBindHost;
                if (contract == null) throw new InvalidOperationException("宿主没有实现绑定契约。");
                contract.EnsureAutoBind(host.gameObject);
                var field = host.GetType().GetField("view", BindingFlags.Instance | BindingFlags.NonPublic);
                object view = field?.GetValue(host);
                if (view == null || !ReferenceEquals(view.GetType().GetField("btnValue")?.GetValue(view), tool.GetBindComponent<Button>(0, "btnValue")))
                    throw new InvalidOperationException("接口未执行生成绑定。");
                contract.EnsureAutoBind(host.gameObject);
                if (!ReferenceEquals(field.GetValue(host), view)) throw new InvalidOperationException("UIView 没有复用。");
                if (host is WidgetViewBase widget && (!ReferenceEquals(widget.GetGeneratedUIView(), view) ||
                    !ReferenceEquals(widget.GetGeneratedUIView(), view))) throw new InvalidOperationException("Widget getter 未返回缓存 UIView。");
                count++;
            }
            if (count != Hosts.Length) throw new InvalidOperationException("独立程序集验证宿主数量不符。");
            var playerAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            if (playerAssemblies.Any(a => a.name == "ComponentAutoBindTool.Editor") ||
                !playerAssemblies.Any(a => a.name == "ComponentAutoBindTool.Core") ||
                !playerAssemblies.Any(a => a.name == "ComponentAutoBindTool.ViewCore"))
                throw new InvalidOperationException("Player 程序集边界不符。");
            Debug.Log("AUTOBIND_ASSEMBLY_COMPILED_PASS：4 种宿主、接口分派、Widget 复用与 Player 程序集边界通过。");
        }
    }
}
