using System;
using System.Collections;
using System.IO;
using System.Linq;
using ComponentAutoBindTool.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Tests
{
    public sealed class TemplateCollisionView { }
    public sealed class OptionalCollisionArgs { }

    public class AutoBindViewTemplateTests
    {
        [Test]
        public void DefinitionUsesFixedNamesAndPerViewScriptDirectory()
        {
            var definition = AutoBindViewTemplateDefinition.Create("Inventory", "Assets/AAGame/UI/Prefabs", "Assets/AAGame/Scripts/UI", "Game.UI");
            Assert.That(definition.OutputPaths, Is.EqualTo(new[]
            {
                "Assets/AAGame/Scripts/UI/Inventory/InventoryView.cs",
                "Assets/AAGame/Scripts/UI/Inventory/InventoryPresenter.cs",
                "Assets/AAGame/Scripts/UI/Inventory/InventoryArgs.cs",
                "Assets/AAGame/Scripts/UI/Inventory/InventoryRoutes.cs",
                "Assets/AAGame/Scripts/UI/Inventory/InventoryView.BindComponents.cs",
                "Assets/AAGame/UI/Prefabs/InventoryView.prefab"
            }));
            Assert.That(definition.FullViewName, Is.EqualTo("Game.UI.InventoryView"));
            Assert.DoesNotThrow(definition.ValidateNew);
        }

        [Test]
        public void UncheckedArgsOmitTheFileAndUseTheBuiltInEmptyArgsContract()
        {
            var definition = AutoBindViewTemplateDefinition.Create("Inventory", "Assets/UI", "Assets/Scripts", "Game.UI", false);
            Assert.That(definition.UseArgs, Is.False);
            Assert.That(definition.OutputPaths.Count(), Is.EqualTo(5));
            Assert.That(definition.OutputPaths, Does.Not.Contain(definition.ArgsPath));
            Assert.That(definition.PresenterSource(), Does.Contain("EmptyViewArgs").And.Not.Contain("InventoryArgs"));
            Assert.That(definition.RoutesSource(), Does.Contain("EmptyViewArgs").And.Not.Contain("InventoryArgs"));
        }

        [Test]
        public void ExistingArgsTypeDoesNotBlockAWindowWhichDoesNotGenerateArgs()
        {
            var definition = AutoBindViewTemplateDefinition.Create("OptionalCollision", "Assets/UI", "Assets/Scripts", "ComponentAutoBindTool.Tests", false);
            Assert.DoesNotThrow(definition.ValidateNew);
            var withArgs = AutoBindViewTemplateDefinition.Create("OptionalCollision", "Assets/UI", "Assets/Scripts", "ComponentAutoBindTool.Tests");
            Assert.Throws<InvalidOperationException>(withArgs.ValidateNew);
        }

        [Test]
        public void ArgsChoiceSurvivesSerializationAndOlderTasksStillGenerateArgs()
        {
            var definition = AutoBindViewTemplateDefinition.Create("Inventory", "Assets/UI", "Assets/Scripts", "", false);
            var restored = JsonUtility.FromJson<AutoBindViewTemplateDefinition>(JsonUtility.ToJson(definition));
            Assert.That(restored.UseArgs, Is.False);
            Assert.That(restored.OutputPaths, Does.Not.Contain(restored.ArgsPath));
            var olderTask = JsonUtility.FromJson<AutoBindViewTemplateDefinition>("{\"BaseName\":\"Inventory\"}");
            Assert.That(olderTask.UseArgs, Is.True);
        }

        [TestCase("")]
        [TestCase("1Inventory")]
        [TestCase("class")]
        [TestCase("InventoryView")]
        [TestCase("A/B")]
        [TestCase("../Inventory")]
        [TestCase("CON")]
        [TestCase("LPT9")]
        public void InvalidBaseNameCannotProduceAssets(string name)
        {
            Assert.Throws<ArgumentException>(() => AutoBindViewTemplateDefinition.Create(name, "Assets/UI", "Assets/Scripts", ""));
        }

        [TestCase("../Outside")]
        [TestCase("Assets/../../Outside")]
        [TestCase("AssetsBackup/UI")]
        [TestCase("")]
        public void InvalidOutputFoldersAreRejected(string path)
        {
            Assert.Throws<ArgumentException>(() => AutoBindViewTemplateDefinition.NormalizeFolder(path));
        }

        [Test]
        public void AbsoluteAssetsPathNormalizesWithoutChangingTheOutputLocation()
        {
            Assert.That(AutoBindViewTemplateDefinition.NormalizeFolder(Application.dataPath + "/AAGame/UI/Prefabs"),
                Is.EqualTo("Assets/AAGame/UI/Prefabs"));
        }

        [Test]
        public void ExistingTypeAndEditorOutputAreRejected()
        {
            var duplicate = AutoBindViewTemplateDefinition.Create("TemplateCollision", "Assets/UI", "Assets/Scripts", "ComponentAutoBindTool.Tests");
            Assert.Throws<InvalidOperationException>(duplicate.ValidateNew);
            var editor = AutoBindViewTemplateDefinition.Create("Inventory", "Assets/UI", "Assets/AATest/Editor", "");
            Assert.Throws<ArgumentException>(editor.ValidateNew);
        }

        [Test]
        public void FileConflictIsDetectedWithoutChangingExistingContent()
        {
            string folder = "Assets/AATest/TemplateConflict" + Guid.NewGuid().ToString("N");
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                var definition = AutoBindViewTemplateDefinition.Create("Inventory", folder, folder, "");
                Directory.CreateDirectory(folder);
                File.WriteAllText(definition.PrefabPath, "existing");
                Assert.Throws<IOException>(definition.ValidateNew);
                Assert.That(File.ReadAllText(definition.PrefabPath), Is.EqualTo("existing"));
            }
            finally
            {
                FileUtil.DeleteFileOrDirectory(folder);
                AssetDatabase.AllowAutoRefresh();
            }
        }

        [Test]
        public void SettingsPersistBothNormalizedRootsAndRejectInvalidChanges()
        {
            const string path = "ProjectSettings/AutoBindViewTemplateSettings.asset";
            byte[] original = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var settings = AutoBindViewTemplateSettings.instance;
            string state = EditorJsonUtility.ToJson(settings);
            try
            {
                settings.Store("Assets/AAGame/UI/Prefabs", "Assets/AAGame/Scripts/UI");
                Assert.That(File.ReadAllText(path), Does.Contain("Assets/AAGame/UI/Prefabs"));
                string valid = File.ReadAllText(path);
                Assert.Throws<ArgumentException>(() => settings.Store("../Outside", "Assets/Scripts"));
                Assert.That(File.ReadAllText(path), Is.EqualTo(valid));
            }
            finally
            {
                EditorJsonUtility.FromJsonOverwrite(state, settings);
                if (original == null) File.Delete(path);
                else File.WriteAllBytes(path, original);
            }
        }

        [Test]
        public void FutureScriptPathUsesTheConfiguredRuntimeAssembly()
        {
            Assert.DoesNotThrow(() => AutoBindViewTemplateDefinition.ValidateAssembly(
                "Assets/AATest/Fixtures/AssemblyHosts/BrandNew/BrandNewView.cs"));
        }

        [Test]
        public void CompilerFailureStopsSerializedWaitingTaskWithoutDomainReload()
        {
            var job = AutoBindViewTemplateCreation.instance;
            string original = EditorJsonUtility.ToJson(job);
            try
            {
                var serialized = new SerializedObject(job);
                serialized.FindProperty("m_Phase").enumValueIndex = (int)AutoBindTemplatePhase.HostCompilation;
                serialized.FindProperty("m_ResumePhase").enumValueIndex = (int)AutoBindTemplatePhase.HostCompilation;
                serialized.FindProperty("m_Reloaded").boolValue = false;
                serialized.FindProperty("m_CompilationFinished").boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(job.Phase, Is.EqualTo(AutoBindTemplatePhase.HostCompilation));
                job.CompilationStarted();
                job.AssemblyFinished(new[] { new CompilerMessage { type = CompilerMessageType.Error, file = "Template.cs", line = 12, message = "测试编译错误" } });
                job.CompilationFinished();
                job.Tick();
                Assert.That(job.Phase, Is.EqualTo(AutoBindTemplatePhase.Failed));
                Assert.That(job.Message, Does.Contain("Template.cs:12").And.Contain("测试编译错误"));
            }
            finally
            {
                EditorJsonUtility.FromJsonOverwrite(original, job);
                var serialized = new SerializedObject(job);
                serialized.FindProperty("m_Phase").enumValueIndex = (int)AutoBindTemplatePhase.None;
                serialized.FindProperty("m_ResumePhase").enumValueIndex = (int)AutoBindTemplatePhase.None;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        [UnityTest]
        public IEnumerator ClearingCompileFailureStaysLockedAndShowsRecoverableError()
        {
            var job = AutoBindViewTemplateCreation.instance;
            string original = EditorJsonUtility.ToJson(job);
            try
            {
                var serialized = new SerializedObject(job);
                serialized.FindProperty("m_Phase").enumValueIndex = (int)AutoBindTemplatePhase.Clearing;
                serialized.FindProperty("m_ResumePhase").enumValueIndex = (int)AutoBindTemplatePhase.Clearing;
                serialized.FindProperty("m_Reloaded").boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(job.IsBusy, Is.True);
                Assert.That(job.CanCancel, Is.False);
                job.CompilationStarted();
                job.AssemblyFinished(new[] { new CompilerMessage { type = CompilerMessageType.Error, file = "Other.cs", line = 3, message = "外部类型引用错误" } });
                job.CompilationFinished();
                job.Tick();
                Assert.That(job.Phase, Is.EqualTo(AutoBindTemplatePhase.Failed));
                Assert.That(job.CanCancel, Is.False);
                Assert.That(job.Message, Does.Contain("文件已清除").And.Contain("Other.cs:3").And.Contain("备份"));
                yield return null;
            }
            finally
            {
                EditorJsonUtility.FromJsonOverwrite(original, job);
                var serialized = new SerializedObject(job);
                serialized.FindProperty("m_Phase").enumValueIndex = (int)AutoBindTemplatePhase.None;
                serialized.FindProperty("m_ResumePhase").enumValueIndex = (int)AutoBindTemplatePhase.None;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        [UnityTest]
        public IEnumerator BothWindowsShowReadOnlyNamesAndSeparateEditablePathSettings()
        {
            var main = ScriptableObject.CreateInstance<AutoBindViewTemplateWindow>();
            var settings = ScriptableObject.CreateInstance<AutoBindViewTemplateSettingsWindow>();
            try
            {
                main.Show(); settings.Show();
                yield return null;
                yield return null;
                var input = main.rootVisualElement.Q<TextField>("baseName");
                Assert.That(input, Is.Not.Null);
                input.value = "Inventory";
                var outputs = main.rootVisualElement.Query<TextField>().ToList().Where(field => field.name.StartsWith("output")).ToArray();
                Assert.That(outputs, Has.Length.EqualTo(6));
                Assert.That(outputs.All(field => field.isReadOnly), Is.True);
                Assert.That(outputs[0].value, Does.EndWith("Inventory/InventoryView.cs"), main.rootVisualElement.Q<HelpBox>("creationStatus").text);
                Assert.That(main.rootVisualElement.Q<Label>("outputName0").text, Is.EqualTo("InventoryView.cs"));
                var args = main.rootVisualElement.Q<Toggle>("useArgs");
                Assert.That(args.value, Is.True);
                args.value = false;
                Assert.That(outputs[2].value, Does.Contain("不生成").And.Contain("EmptyViewArgs"));
                Assert.That(outputs[3].value, Does.EndWith("Inventory/InventoryRoutes.cs"));
                args.value = true;
                Assert.That(outputs[2].value, Does.EndWith("Inventory/InventoryArgs.cs"));
                input.value = "Shop";
                Assert.That(outputs[0].value, Does.EndWith("Shop/ShopView.cs"));
                Assert.That(main.rootVisualElement.layout.width, Is.GreaterThan(100));
                Assert.That(settings.rootVisualElement.Q<TextField>("prefabRoot").isReadOnly, Is.False);
                Assert.That(settings.rootVisualElement.Q<TextField>("scriptRoot").isReadOnly, Is.False);
                Assert.That(main.rootVisualElement.Q<Button>("clearTemplate"), Is.Not.Null);
                Assert.That(main.rootVisualElement.Q<Button>("clearTemplate").enabledSelf, Is.False);
            }
            finally { main.Close(); settings.Close(); }
        }
    }
}
