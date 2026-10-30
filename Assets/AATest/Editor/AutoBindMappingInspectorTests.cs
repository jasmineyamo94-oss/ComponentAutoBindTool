using ComponentAutoBindTool.Core;
using ComponentAutoBindTool.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace ComponentAutoBindTool.Tests
{
    public class AutoBindMappingInspectorTests
    {
        [Test]
        public void ComponentTypeResolutionAcceptsFullAndLegacyShortNames()
        {
            Assert.That(AutoBindKeyMapSettingEditor.IsComponentTypeName(typeof(Button).FullName), Is.True);
            Assert.That(AutoBindKeyMapSettingEditor.IsComponentTypeName(nameof(Button)), Is.True);
            Assert.That(AutoBindKeyMapSettingEditor.IsComponentTypeName("System.String"), Is.False);
            Assert.That(AutoBindKeyMapSettingEditor.IsComponentTypeName("Removed.Example.Component"), Is.False);
            Assert.That(AutoBindKeyMapSettingEditor.IsComponentTypeName(""), Is.False);
        }

        [Test]
        public void CleanInvalidMappingsPreservesValidEntriesAndSupportsUndoRedo()
        {
            var setting = ScriptableObject.CreateInstance<AutoBindKeyMapSetting>();
            setting.extraComponentKeyMap = new GenericDictionary<string, string>();
            setting.extraComponentKeyMap.Add("Full", typeof(Button).FullName);
            setting.extraComponentKeyMap.Add("Legacy", nameof(Button));
            setting.extraComponentKeyMap.Add("Removed", "Removed.Example.Component");
            setting.extraComponentKeyMap.Add("NotComponent", "System.String");
            var serialized = new SerializedObject(setting);
            Undo.IncrementCurrentGroup();
            try
            {
                Assert.That(AutoBindKeyMapSettingEditor.RemoveInvalidMappings(serialized), Is.EqualTo(2));
                Undo.FlushUndoRecordObjects();
                AssertEntries(serialized, "Full", "Legacy");
                Undo.PerformUndo();
                AssertEntries(serialized, "Full", "Legacy", "Removed", "NotComponent");
                Undo.PerformRedo();
                AssertEntries(serialized, "Full", "Legacy");
                Assert.That(AutoBindKeyMapSettingEditor.RemoveInvalidMappings(serialized), Is.Zero);
            }
            finally
            {
                Undo.ClearUndo(setting);
                Object.DestroyImmediate(setting);
                Undo.IncrementCurrentGroup();
            }
        }

        private static void AssertEntries(SerializedObject serialized, params string[] keys)
        {
            serialized.Update();
            var list = serialized.FindProperty("extraComponentKeyMap").FindPropertyRelative("list");
            Assert.That(list.arraySize, Is.EqualTo(keys.Length));
            for (int i = 0; i < keys.Length; i++)
                Assert.That(list.GetArrayElementAtIndex(i).FindPropertyRelative("Key").stringValue, Is.EqualTo(keys[i]));
        }
    }
}
