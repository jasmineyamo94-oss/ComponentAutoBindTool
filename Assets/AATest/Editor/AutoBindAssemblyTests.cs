using System.IO;
using System.Linq;
using ComponentAutoBindTool.Core;
using ComponentAutoBindTool.Editor;
using ComponentAutoBindTool.Tests.AssemblyHosts;
using ComponentAutoBindTool.ViewCore;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public class AutoBindAssemblyTests
    {
        [Test]
        public void RuntimeAssembliesAreIndependentAndEditorIsExcludedFromPlayer()
        {
            var core = typeof(Core.ComponentAutoBindTool).Assembly;
            var views = typeof(ViewBase).Assembly;
            var editor = typeof(AutoBindBuildPipeline).Assembly;
            Assert.That(core.GetName().Name, Is.EqualTo("ComponentAutoBindTool.Core"));
            Assert.That(views.GetName().Name, Is.EqualTo("ComponentAutoBindTool.ViewCore"));
            Assert.That(editor.GetName().Name, Is.EqualTo("ComponentAutoBindTool.Editor"));
            Assert.That(core.GetReferencedAssemblies().Select(a => a.Name), Does.Not.Contain(views.GetName().Name));
            Assert.That(views.GetReferencedAssemblies().Select(a => a.Name), Does.Contain(core.GetName().Name));
            foreach (var assembly in new[] { core, views, editor })
                Assert.That(assembly.GetReferencedAssemblies().Select(a => a.Name), Does.Not.Contain("Assembly-CSharp"));
            var players = CompilationPipeline.GetAssemblies(AssembliesType.Player).Select(a => a.name).ToArray();
            Assert.That(players, Does.Contain(core.GetName().Name));
            Assert.That(players, Does.Contain(views.GetName().Name));
            Assert.That(players, Does.Not.Contain(editor.GetName().Name));
        }

        [Test]
        public void MappingAssetRetainsItsScriptAndExtraMappingsAfterNamespaceMigration()
        {
            var setting = AssetDatabase.LoadAssetAtPath<AutoBindKeyMapSetting>(
                "Assets/Third Party/ComponentAutoBindTool/AutoBindKeyMapSetting.asset");
            Assert.That(setting, Is.Not.Null);
            Assert.That(setting.GetType().FullName, Is.EqualTo("ComponentAutoBindTool.Core.AutoBindKeyMapSetting"));
            Assert.That(setting.GetType().Assembly.GetName().Name, Is.EqualTo("ComponentAutoBindTool.Editor"));
            Assert.That(setting.TryGetComponentTypeName("Widget", out var widget), Is.True);
            Assert.That(widget, Is.EqualTo(typeof(WidgetViewBase).FullName));
            Assert.That(setting.extraComponentKeyMap.ContainsKey("Widget"), Is.False);
            Assert.That(setting.extraComponentKeyMap.ContainsKey("WidgetGroup"), Is.False);
        }

        [Test]
        public void FollowScriptPathMatchesIndependentAssemblyAndCrossAssemblyScanDoesNotCommit()
        {
            var root = new GameObject("独立程序集宿主");
            var setting = ScriptableObject.CreateInstance<AutoBindGlobalSetting>();
            var map = ScriptableObject.CreateInstance<AutoBindKeyMapSetting>();
            try
            {
                var tool = root.AddComponent<Core.ComponentAutoBindTool>();
                tool.m_targetScript = root.AddComponent<AssemblyPlainHost>();
                var input = new AutoBindBuildInput(tool, setting, tool.BindDatas);
                Assert.That(Path.GetDirectoryName(input.OutputPath)?.Replace('\\', '/'),
                    Does.EndWith("/Assets/AATest/Fixtures/AssemblyHosts"));
                Assert.That(CompilationPipeline.GetAssemblyNameFromScriptPath(input.ScriptPath),
                    Is.EqualTo(CompilationPipeline.GetAssemblyNameFromScriptPath(AutoBindPathUtility.ToAssetRelativePath(input.OutputPath))));

                string snapshot = EditorJsonUtility.ToJson(tool);
                var data = new SerializedObject(setting);
                data.FindProperty("m_UseGlobalDefaultSavePath").boolValue = false;
                data.FindProperty("m_CodePath").stringValue = "Assets/AATest/IndependentOutput";
                data.ApplyModifiedPropertiesWithoutUndo();
                var report = AutoBindBuildPipeline.RescanAndValidate(tool, setting, map);
                Assert.That(report.Succeeded, Is.False);
                Assert.That(report.ValidationResult.BuildReport(), Does.Contain("同一程序集"));
                Assert.That(EditorJsonUtility.ToJson(tool), Is.EqualTo(snapshot));
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(setting);
                Object.DestroyImmediate(map);
            }
        }
    }
}
