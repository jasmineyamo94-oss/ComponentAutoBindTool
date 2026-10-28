using System;
using System.Collections.Generic;
using Third_Party.ComponentAutoBindTool.Scripts.Core;
using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    internal static class AutoBindInspectorDrawer
    {
        private const float BindNameWidth = 150f;
        private const float DeleteButtonWidth = 22f;
        private const int MaxVisibleBindRows = 6;
        private static GUIStyle s_MiniHeaderStyle;
        private static GUIStyle s_TagStyle;
        private static GUIStyle s_DeleteButtonStyle;
        private static GUIStyle s_KeyMapBoxStyle;
        private static Vector2 s_BindListScrollPosition;

        public static void DrawActionSection(Action onAutoBindAndValidate, Action onGenerateCode, Action onClearAll)
        {
            EditorGUILayout.BeginHorizontal();
            if (DrawTintButton("重扫校验", GetPrimaryButtonColor(), GUILayout.Height(20)))
            {
                onAutoBindAndValidate?.Invoke();
            }

            if (DrawTintButton("生成代码", GetNeutralButtonColor(), GUILayout.Height(20)))
            {
                onGenerateCode?.Invoke();
            }

            if (DrawTintButton("清空", GetDangerButtonColor(), GUILayout.Height(20), GUILayout.Width(50)))
            {
                onClearAll?.Invoke();
            }
            EditorGUILayout.EndHorizontal();
        }

        public static void DrawSettingsSection(string namespaceValue, string classNameValue, string codePathValue)
        {
            DrawCompactInfoPair("NS", namespaceValue, "类", classNameValue);
            DrawCompactInfoLine("路径", codePathValue);
        }

        public static void DrawKeyMapTip(AutoBindKeyMapSetting keyMapSetting, ref bool isExpanded)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            isExpanded = EditorGUILayout.BeginFoldoutHeaderGroup(isExpanded, "组件映射表");

            if (isExpanded)
            {
                DrawKeyMapColumns(keyMapSetting);
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.EndVertical();
        }

        public static void DrawBindComponentFoldout(SerializedProperty bindDatas, ref bool isExpanded, Action drawContent)
        {
            int bindCount = bindDatas != null ? bindDatas.arraySize : 0;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            isExpanded = EditorGUILayout.BeginFoldoutHeaderGroup(isExpanded, $"绑定数据 ({bindCount})");

            if (isExpanded)
            {
                if (bindCount == 0)
                {
                    EditorGUILayout.LabelField("当前没有绑定数据。", EditorStyles.miniLabel);
                }
                else
                {
                    drawContent?.Invoke();
                }
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.EndVertical();
        }

        public static int DrawBindDataList(SerializedProperty bindDatas)
        {
            if (bindDatas == null || bindDatas.arraySize == 0)
            {
                return -1;
            }

            int deleteIndex = -1;
            HashSet<string> duplicateNames = CollectDuplicateNames(bindDatas);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("#", GetMiniHeaderStyle(), GUILayout.Width(18));
            EditorGUILayout.LabelField("绑定名", GetMiniHeaderStyle(), GUILayout.Width(BindNameWidth));
            EditorGUILayout.LabelField("组件", GetMiniHeaderStyle());
            EditorGUILayout.EndHorizontal();

            float rowHeight = EditorGUIUtility.singleLineHeight + 4f;
            float listHeight = Mathf.Min(bindDatas.arraySize, MaxVisibleBindRows) * rowHeight + 4f;
            s_BindListScrollPosition = EditorGUILayout.BeginScrollView(
                s_BindListScrollPosition,
                GUILayout.MaxHeight(listHeight));

            for (int i = 0; i < bindDatas.arraySize; i++)
            {
                SerializedProperty bindDataElement = bindDatas.GetArrayElementAtIndex(i);
                SerializedProperty nameProperty = bindDataElement.FindPropertyRelative("Name");
                SerializedProperty bindComProperty = bindDataElement.FindPropertyRelative("BindCom");
                bool hasMissingComponent = bindComProperty.objectReferenceValue == null;
                bool hasInvalidName = string.IsNullOrWhiteSpace(nameProperty.stringValue)
                    || duplicateNames.Contains(nameProperty.stringValue);

                Rect rowRect = EditorGUILayout.GetControlRect(false, rowHeight);
                DrawBindRowBackground(rowRect, i, hasMissingComponent, hasInvalidName);

                Rect indexRect = new Rect(rowRect.x + 2f, rowRect.y + 1f, 16f, EditorGUIUtility.singleLineHeight);
                Rect nameRect = new Rect(indexRect.xMax + 4f, rowRect.y + 1f, BindNameWidth, EditorGUIUtility.singleLineHeight);
                Rect deleteRect = new Rect(rowRect.xMax - DeleteButtonWidth, rowRect.y + 1f, DeleteButtonWidth, EditorGUIUtility.singleLineHeight);
                Rect objectRect = new Rect(nameRect.xMax + 4f, rowRect.y + 1f,
                    deleteRect.x - nameRect.xMax - 8f, EditorGUIUtility.singleLineHeight);

                EditorGUI.LabelField(indexRect, i.ToString(), EditorStyles.miniLabel);
                nameProperty.stringValue = EditorGUI.TextField(nameRect, nameProperty.stringValue);
                bindComProperty.objectReferenceValue =
                    EditorGUI.ObjectField(objectRect, bindComProperty.objectReferenceValue, typeof(Component), true);

                if (GUI.Button(deleteRect, "X", GetDeleteButtonStyle()))
                {
                    deleteIndex = i;
                }
            }

            EditorGUILayout.EndScrollView();
            return deleteIndex;
        }

        private static void DrawCompactInfoPair(string leftLabel, string leftValue, string rightLabel, string rightValue)
        {
            Rect rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            float halfWidth = (rect.width - 6f) * 0.5f;
            Rect leftRect = new Rect(rect.x, rect.y, halfWidth, rect.height);
            Rect rightRect = new Rect(rect.x + halfWidth + 6f, rect.y, halfWidth, rect.height);

            DrawCompactInfo(leftRect, leftLabel, leftValue);
            DrawCompactInfo(rightRect, rightLabel, rightValue);
        }

        private static void DrawCompactInfoLine(string label, string value)
        {
            DrawCompactInfo(EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight), label, value);
        }

        private static void DrawCompactInfo(Rect rect, string label, string value)
        {
            const float tagWidth = 32f;
            Rect tagRect = new Rect(rect.x, rect.y, tagWidth, rect.height);
            Rect valueRect = new Rect(rect.x + tagWidth, rect.y, rect.width - tagWidth, rect.height);

            DrawRectBadge(tagRect, label, GetInfoTagColor(), GetTagStyle());
            EditorGUI.LabelField(valueRect, new GUIContent(value ?? string.Empty, value ?? string.Empty), EditorStyles.miniLabel);
        }

        private static void DrawKeyMapColumns(AutoBindKeyMapSetting keyMapSetting)
        {
            var groupA = new List<string>();
            var groupB = new List<string>();
            bool addToGroupA = true;

            if (keyMapSetting != null)
            {
                foreach (KeyValuePair<string, string> item in keyMapSetting.GetAllComponentKeyMaps())
                {
                    string formattedItem = FormatKeyMapItem(item);
                    if (addToGroupA)
                    {
                        groupA.Add(formattedItem);
                    }
                    else
                    {
                        groupB.Add(formattedItem);
                    }

                    addToGroupA = !addToGroupA;
                }
            }

            EditorGUILayout.BeginVertical();
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical();
            GUILayout.Box(string.Join("\n", groupA.ToArray()), GetKeyMapBoxStyle());
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();
            GUILayout.Box(string.Join("\n", groupB.ToArray()), GetKeyMapBoxStyle());
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private static bool DrawTintButton(string text, Color color, params GUILayoutOption[] options)
        {
            Color oldContentColor = GUI.contentColor;
            GUI.contentColor = Color.Lerp(oldContentColor, color, 0.80f);
            bool clicked = GUILayout.Button(text, EditorStyles.miniButton, options);
            GUI.contentColor = oldContentColor;
            return clicked;
        }

        private static void DrawRectBadge(Rect rect, string text, Color color, GUIStyle style)
        {
            Color oldContentColor = GUI.contentColor;
            GUI.contentColor = Color.Lerp(oldContentColor, color, 0.9f);
            GUI.Label(rect, text, style);
            GUI.contentColor = oldContentColor;
        }

        private static void DrawBindRowBackground(Rect rowRect, int index, bool hasMissingComponent, bool hasInvalidName)
        {
            Rect insetRect = new Rect(rowRect.x, rowRect.y + 1f, rowRect.width, rowRect.height - 2f);
            Color backgroundColor = Color.clear;
            if (hasMissingComponent)
            {
                backgroundColor = GetDangerRowColor();
            }
            else if (hasInvalidName)
            {
                backgroundColor = GetWarningRowColor();
            }

            if (backgroundColor.a > 0.001f)
            {
                EditorGUI.DrawRect(insetRect, backgroundColor);
            }

            if (hasMissingComponent || hasInvalidName)
            {
                EditorGUI.DrawRect(
                    new Rect(insetRect.x, insetRect.y + 1f, 1f, insetRect.height - 2f),
                    hasMissingComponent ? GetDangerButtonColor() : GetWarningAccentColor());
            }

            EditorGUI.DrawRect(
                new Rect(insetRect.x + 4f, insetRect.yMax - 1f, insetRect.width - 8f, 1f),
                index % 2 == 0 ? GetSeparatorColorA() : GetSeparatorColorB());
        }

        private static HashSet<string> CollectDuplicateNames(SerializedProperty bindDatas)
        {
            var duplicateNames = new HashSet<string>();
            var uniqueNames = new HashSet<string>();
            for (int i = 0; i < bindDatas.arraySize; i++)
            {
                SerializedProperty bindDataElement = bindDatas.GetArrayElementAtIndex(i);
                SerializedProperty nameProperty = bindDataElement.FindPropertyRelative("Name");
                string fieldName = nameProperty.stringValue;
                if (string.IsNullOrWhiteSpace(fieldName))
                {
                    continue;
                }

                if (!uniqueNames.Add(fieldName))
                {
                    duplicateNames.Add(fieldName);
                }
            }

            return duplicateNames;
        }

        private static string FormatKeyMapItem(KeyValuePair<string, string> item)
        {
            string keyColor = ToHtmlColor(EditorGUIUtility.isProSkin
                ? new Color(0.68f, 0.85f, 0.95f)
                : new Color(0.14f, 0.39f, 0.54f));
            string valueColor = ToHtmlColor(EditorGUIUtility.isProSkin
                ? new Color(0.76f, 0.88f, 0.72f)
                : new Color(0.29f, 0.44f, 0.20f));
            string braceColor = ToHtmlColor(EditorGUIUtility.isProSkin
                ? new Color(0.72f, 0.72f, 0.72f)
                : new Color(0.42f, 0.42f, 0.42f));

            return
                $"<color=#{braceColor}>{{</color> <color=#{keyColor}>{item.Key}</color> <color=#{braceColor}>:</color> <color=#{valueColor}>{item.Value}</color> <color=#{braceColor}>}}</color>";
        }

        private static string ToHtmlColor(Color color)
        {
            return ColorUtility.ToHtmlStringRGB(color);
        }

        private static GUIStyle GetMiniHeaderStyle()
        {
            if (s_MiniHeaderStyle == null)
            {
                s_MiniHeaderStyle = new GUIStyle(EditorStyles.boldLabel);
                s_MiniHeaderStyle.fontSize = 11;
                s_MiniHeaderStyle.alignment = TextAnchor.MiddleCenter;
                s_MiniHeaderStyle.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.93f, 0.96f, 0.98f) : Color.white;
            }

            return s_MiniHeaderStyle;
        }

        private static GUIStyle GetTagStyle()
        {
            if (s_TagStyle == null)
            {
                s_TagStyle = new GUIStyle(EditorStyles.miniBoldLabel);
                s_TagStyle.alignment = TextAnchor.MiddleCenter;
                s_TagStyle.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.91f, 0.96f, 0.98f) : Color.white;
            }

            return s_TagStyle;
        }

        private static GUIStyle GetDeleteButtonStyle()
        {
            if (s_DeleteButtonStyle == null)
            {
                s_DeleteButtonStyle = new GUIStyle(EditorStyles.miniButton);
                s_DeleteButtonStyle.normal.textColor =
                    EditorGUIUtility.isProSkin ? new Color(0.88f, 0.74f, 0.74f) : new Color(0.58f, 0.28f, 0.28f);
            }

            return s_DeleteButtonStyle;
        }

        private static GUIStyle GetKeyMapBoxStyle()
        {
            if (s_KeyMapBoxStyle == null)
            {
                s_KeyMapBoxStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    richText = true,
                    padding = new RectOffset(8, 8, 6, 6)
                };
            }

            return s_KeyMapBoxStyle;
        }

        private static Color GetPrimaryAccentColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.44f, 0.60f, 0.66f)
                : new Color(0.36f, 0.52f, 0.58f);
        }

        private static Color GetInfoTagColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.72f, 0.80f, 0.82f)
                : new Color(0.38f, 0.44f, 0.47f);
        }

        private static Color GetPrimaryButtonColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.66f, 0.79f, 0.84f)
                : new Color(0.27f, 0.45f, 0.52f);
        }

        private static Color GetNeutralButtonColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.78f, 0.79f, 0.80f)
                : new Color(0.40f, 0.42f, 0.44f);
        }

        private static Color GetDangerButtonColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.84f, 0.72f, 0.72f)
                : new Color(0.60f, 0.40f, 0.40f);
        }

        private static Color GetWarningAccentColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.83f, 0.73f, 0.46f)
                : new Color(0.72f, 0.58f, 0.24f);
        }

        private static Color GetRowColorA()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.015f)
                : new Color(0f, 0f, 0f, 0.008f);
        }

        private static Color GetRowColorB()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.03f)
                : new Color(0f, 0f, 0f, 0.018f);
        }

        private static Color GetDangerRowColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.54f, 0.35f, 0.35f, 0.10f)
                : new Color(0.93f, 0.79f, 0.79f, 0.22f);
        }

        private static Color GetWarningRowColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.52f, 0.44f, 0.24f, 0.09f)
                : new Color(0.95f, 0.88f, 0.72f, 0.20f);
        }

        private static Color GetSeparatorColorA()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.04f)
                : new Color(0f, 0f, 0f, 0.05f);
        }

        private static Color GetSeparatorColorB()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.06f)
                : new Color(0f, 0f, 0f, 0.07f);
        }
    }
}
