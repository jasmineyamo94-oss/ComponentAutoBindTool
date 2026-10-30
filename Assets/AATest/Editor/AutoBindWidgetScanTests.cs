using ComponentAutoBindTool.Core;
using ComponentAutoBindTool.Editor;
using ComponentAutoBindTool.Example;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Tests
{
    public sealed class AutoBindWidgetScanTests
    {
        private GameObject m_Root;
        private ComponentAutoBindTool.Core.ComponentAutoBindTool m_Tool;
        private AutoBindKeyMapSetting m_Map;

        [SetUp]
        public void SetUp()
        {
            m_Root = new GameObject("Root");
            m_Tool = m_Root.AddComponent<ComponentAutoBindTool.Core.ComponentAutoBindTool>();
            m_Tool.m_targetScript = m_Root.AddComponent<AutoBindPlainHost>();
            m_Map = ScriptableObject.CreateInstance<AutoBindKeyMapSetting>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(m_Root);
            Object.DestroyImmediate(m_Map);
        }

        private GameObject Child(string name)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(m_Root.transform);
            return child;
        }

        [Test]
        public void WidgetPrefixResolvesConcreteTypeAndPreservesNestedBoundaryAndMultiplePrefixes()
        {
            var child = Child("Rtf_Widget_Item");
            var widget = child.AddComponent<LabWidgetGroup>();
            var nested = new GameObject("Btn_Nested", typeof(RectTransform), typeof(UnityEngine.UI.Button));
            nested.transform.SetParent(child.transform);

            var scan = AutoBindHierarchyScanner.CollectBindings(m_Tool, m_Map);
            Assert.That(scan.Errors, Is.Empty);
            Assert.That(scan.BindDatas.Count, Is.EqualTo(2));
            Assert.That(scan.BindDatas[0].Name, Is.EqualTo("Rtf_Item"));
            Assert.That(scan.BindDatas[1].Name, Is.EqualTo("Widget_Item"));
            Assert.That(scan.BindDatas[1].BindCom, Is.SameAs(widget));
            var settings = ScriptableObject.CreateInstance<AutoBindGlobalSetting>();
            try
            {
                string code = AutoBindCodeGenerator.BuildCode(new AutoBindBuildInput(m_Tool, settings, scan.BindDatas));
                Assert.That(code, Does.Contain("global::@ComponentAutoBindTool.@Example.@LabWidgetGroup widgetItem"));
                Assert.That(code, Does.Not.Contain("cascadeHost"));
            }
            finally { Object.DestroyImmediate(settings); }
        }

        [Test]
        public void WidgetRejectsExplicitOrdinaryTargetEvenWithOneWidget()
        {
            var child = Child("Widget_Bad");
            child.AddComponent<LabWidgetGroup>();
            var nested = child.GetComponent<ComponentAutoBindTool.Core.ComponentAutoBindTool>();
            nested.m_targetScript = child.AddComponent<AutoBindPlainHost>();
            var scan = AutoBindHierarchyScanner.CollectBindings(m_Tool, m_Map);
            Assert.That(scan.HasErrors, Is.True);
            Assert.That(scan.Errors[0], Does.Contain("Sub"));
            Assert.That(scan.BindDatas, Is.Empty);
        }

        [Test]
        public void SubBindsNestedTargetAndGeneratesOneCascadeForAliases()
        {
            var child = Child("Sub_Header");
            var host = child.AddComponent<AutoBindPlainHost>();
            var nested = child.AddComponent<ComponentAutoBindTool.Core.ComponentAutoBindTool>();
            nested.m_targetScript = host;
            var inside = new GameObject("Unknown_Inside");
            inside.transform.SetParent(child.transform);
            var scan = AutoBindHierarchyScanner.CollectBindings(m_Tool, m_Map);
            Assert.That(scan.Errors, Is.Empty);
            Assert.That(scan.BindDatas.Count, Is.EqualTo(1));
            Assert.That(scan.BindDatas[0].BindCom, Is.SameAs(host));
            scan.AddBindData("Other_Header", host);
            var settings = ScriptableObject.CreateInstance<AutoBindGlobalSetting>();
            try
            {
                string code = AutoBindCodeGenerator.BuildCode(new AutoBindBuildInput(m_Tool, settings, scan.BindDatas));
                Assert.That(code, Does.Contain("(object)boundView.subHeader is not global::ComponentAutoBindTool.ViewCore.IAutoBindHost cascadeHost0"));
                Assert.That(code, Does.Contain("cascadeHost0.EnsureAutoBind(boundView.subHeader.gameObject)"));
                Assert.That(code, Does.Not.Contain("cascadeHost1"));
            }
            finally { Object.DestroyImmediate(settings); }
        }

        [Test]
        public void SubRejectsMissingOrForeignTarget()
        {
            var child = Child("Sub_Header");
            Assert.That(AutoBindHierarchyScanner.CollectBindings(m_Tool, m_Map).HasErrors, Is.True);
            var nested = child.AddComponent<ComponentAutoBindTool.Core.ComponentAutoBindTool>();
            Assert.That(AutoBindHierarchyScanner.CollectBindings(m_Tool, m_Map).HasErrors, Is.True);
            nested.m_targetScript = m_Tool.m_targetScript;
            Assert.That(AutoBindHierarchyScanner.CollectBindings(m_Tool, m_Map).HasErrors, Is.True);
            nested.m_targetScript = nested;
            Assert.That(AutoBindHierarchyScanner.CollectBindings(m_Tool, m_Map).HasErrors, Is.True);
        }

        [Test]
        public void ValidatorRejectsSelfAndInvalidWidgetAndSubBindings()
        {
            var ordinary = Child("Child").AddComponent<AutoBindPlainHost>();
            m_Tool.BindDatas.Add(new ComponentAutoBindTool.Core.ComponentAutoBindTool.BindData("Sub_Self", m_Tool.m_targetScript));
            m_Tool.BindDatas.Add(new ComponentAutoBindTool.Core.ComponentAutoBindTool.BindData("Widget_Bad", ordinary));
            m_Tool.BindDatas.Add(new ComponentAutoBindTool.Core.ComponentAutoBindTool.BindData("Sub_Bad", ordinary));
            var settings = ScriptableObject.CreateInstance<AutoBindGlobalSetting>();
            try
            {
                string report = AutoBindValidator.Validate(m_Tool, settings).BuildReport();
                Assert.That(report, Does.Contain("不能将当前根节点"));
                Assert.That(report, Does.Contain("Widget 前缀"));
                Assert.That(report, Does.Contain("Sub 前缀"));
            }
            finally { Object.DestroyImmediate(settings); }
        }

        [Test]
        public void MissingWidgetReportsError()
        {
            Child("Widget_Missing");
            var scan = AutoBindHierarchyScanner.CollectBindings(m_Tool, m_Map);
            Assert.That(scan.HasErrors, Is.True);
            Assert.That(scan.BindDatas, Is.Empty);
            Assert.That(scan.Errors[0], Does.Contain("WidgetViewBase"));
        }

        [Test]
        public void CustomComponentMappingAcceptsFullyQualifiedTypeName()
        {
            m_Map.extraComponentKeyMap = new GenericDictionary<string, string>();
            m_Map.extraComponentKeyMap.Add("Custom", typeof(AutoBindPlainHost).FullName);
            var host = Child("Custom_Item").AddComponent<AutoBindPlainHost>();

            var scan = AutoBindHierarchyScanner.CollectBindings(m_Tool, m_Map);
            Assert.That(scan.Errors, Is.Empty);
            Assert.That(scan.BindDatas[0].BindCom, Is.SameAs(host));
        }

        [Test]
        public void MultipleWidgetsRequireExplicitLocalTarget()
        {
            var child = Child("Widget_Item");
            child.AddComponent<LabWidgetGroup>();
            var selected = child.AddComponent<AutoBindExampleWidget>();
            var nestedTool = child.GetComponent<ComponentAutoBindTool.Core.ComponentAutoBindTool>();

            var ambiguous = AutoBindHierarchyScanner.CollectBindings(m_Tool, m_Map);
            Assert.That(ambiguous.HasErrors, Is.True);
            Assert.That(ambiguous.BindDatas, Is.Empty);

            nestedTool.m_targetScript = selected;
            var resolved = AutoBindHierarchyScanner.CollectBindings(m_Tool, m_Map);
            Assert.That(resolved.Errors, Is.Empty);
            Assert.That(resolved.BindDatas[0].BindCom, Is.SameAs(selected));

            var other = Child("Unbound").AddComponent<LabWidgetGroup>();
            nestedTool.m_targetScript = other;
            var invalid = AutoBindHierarchyScanner.CollectBindings(m_Tool, m_Map);
            Assert.That(invalid.HasErrors, Is.True);
            Assert.That(invalid.BindDatas, Is.Empty);
        }
    }
}
