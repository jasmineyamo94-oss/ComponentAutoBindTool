using System;
using UnityEditor;
using UnityEngine;
using BindData = ComponentAutoBindTool.Core.ComponentAutoBindTool.BindData;

namespace ComponentAutoBindTool.Editor
{
    internal static class AutoBindSerializedBindingUtility
    {
        public static bool ApplyCandidate(AutoBindBuildInput input, bool recordUndo = true)
        {
            var serialized = new SerializedObject(input.Target);
            var bindings = serialized.FindProperty("BindDatas");
            bool same = bindings.arraySize == input.Bindings.Count;
            for (int i = 0; same && i < bindings.arraySize; i++)
            {
                var item = bindings.GetArrayElementAtIndex(i);
                same = item.FindPropertyRelative("Name").stringValue == input.Bindings[i].Name &&
                       item.FindPropertyRelative("BindCom").objectReferenceValue == input.Bindings[i].BindCom;
            }
            AutoBindTargetScriptSyncUtility.ApplyMetadata(input, serialized);
            if (!same) RebuildBindDatas(bindings, input.Bindings);
            var components = serialized.FindProperty("m_BindComs");
            var names = serialized.FindProperty("m_BindFieldNames");
            if (NeedsRuntimeBindingSync(bindings, components, names))
                SyncRuntimeBindingCaches(bindings, components, names);
            return recordUndo ? serialized.ApplyModifiedProperties() : serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        public static void AddBindData(SerializedProperty bindDatas, string name, Component bindCom)
        {
            int index = bindDatas.arraySize;
            bindDatas.InsertArrayElementAtIndex(index);

            SerializedProperty element = bindDatas.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("Name").stringValue = name;
            element.FindPropertyRelative("BindCom").objectReferenceValue = bindCom;
        }

        public static void RebuildBindDatas(SerializedProperty bindDatas, System.Collections.Generic.IReadOnlyList<BindData> bindDataList)
        {
            bindDatas.ClearArray();
            for (int i = 0; i < bindDataList.Count; i++)
            {
                BindData bindData = bindDataList[i];
                AddBindData(bindDatas, bindData.Name, bindData.BindCom);
            }
        }

        public static void SyncRuntimeBindingCaches(SerializedProperty bindDatas, SerializedProperty bindComs,
            SerializedProperty bindFieldNames)
        {
            bindComs.ClearArray();
            bindFieldNames.ClearArray();

            for (int i = 0; i < bindDatas.arraySize; i++)
            {
                SerializedProperty bindDataElement = bindDatas.GetArrayElementAtIndex(i);
                SerializedProperty bindComponentProperty = bindDataElement.FindPropertyRelative("BindCom");
                SerializedProperty bindNameProperty = bindDataElement.FindPropertyRelative("Name");

                bindComs.InsertArrayElementAtIndex(i);
                bindComs.GetArrayElementAtIndex(i).objectReferenceValue = bindComponentProperty.objectReferenceValue;

                bindFieldNames.InsertArrayElementAtIndex(i);
                bindFieldNames.GetArrayElementAtIndex(i).stringValue =
                    AutoBindFieldNameUtility.BuildFieldName(bindNameProperty.stringValue);
            }
        }

        public static bool NeedsRuntimeBindingSync(SerializedProperty bindDatas, SerializedProperty bindComs,
            SerializedProperty bindFieldNames)
        {
            if (bindDatas == null || bindComs == null || bindFieldNames == null)
            {
                return false;
            }

            if (bindDatas.arraySize != bindComs.arraySize || bindDatas.arraySize != bindFieldNames.arraySize)
            {
                return true;
            }

            for (int i = 0; i < bindDatas.arraySize; i++)
            {
                SerializedProperty bindDataElement = bindDatas.GetArrayElementAtIndex(i);
                SerializedProperty bindNameProperty = bindDataElement.FindPropertyRelative("Name");
                SerializedProperty bindComponentProperty = bindDataElement.FindPropertyRelative("BindCom");

                SerializedProperty bindFieldNameProperty = bindFieldNames.GetArrayElementAtIndex(i);
                SerializedProperty bindComponentCacheProperty = bindComs.GetArrayElementAtIndex(i);

                string generatedFieldName = AutoBindFieldNameUtility.BuildFieldName(bindNameProperty.stringValue);
                if (!string.Equals(generatedFieldName, bindFieldNameProperty.stringValue, StringComparison.Ordinal))
                {
                    return true;
                }

                if (bindComponentProperty.objectReferenceValue != bindComponentCacheProperty.objectReferenceValue)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
