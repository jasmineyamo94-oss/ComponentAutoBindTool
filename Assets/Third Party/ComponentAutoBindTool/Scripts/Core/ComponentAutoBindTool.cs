using System;
using System.Collections.Generic;
using UnityEngine;
using Component = UnityEngine.Component;

namespace ComponentAutoBindTool.Core
{
    /// <summary>
    /// 组件自动绑定工具
    /// </summary>
    public class ComponentAutoBindTool : MonoBehaviour, ISerializationCallbackReceiver
    {
#if UNITY_EDITOR
        [Serializable]
        public class BindData
        {
            public BindData()
            {
            }

            public BindData(string name, Component bindCom)
            {
                Name = name;
                BindCom = bindCom;
            }

            public string Name;
            public Component BindCom;
        }

        public List<BindData> BindDatas = new List<BindData>();

        [SerializeField] private string m_ClassName;

        [SerializeField] private string m_Namespace;

        public MonoBehaviour m_targetScript;

        [SerializeField] private string m_CodePath;

        public string ClassName
        {
            get { return m_ClassName; }
        }

        public string Namespace
        {
            get { return m_Namespace; }
        }

        public string CodePath
        {
            get { return m_CodePath; }
        }

#endif

        [SerializeField] public List<Component> m_BindComs = new List<Component>();
        [SerializeField] private List<string> m_BindFieldNames = new List<string>();
        [NonSerialized] private Dictionary<string, int> m_BindFieldIndexMap;
        [NonSerialized] private bool m_BindFieldIndexMapDirty = true;

        public T GetBindComponent<T>(int index) where T : Component
        {
            string fieldName = GetFieldName(index);
            return GetBindComponentInternal<T>(index, fieldName);
        }

        public T GetBindComponent<T>(int index, string fieldName) where T : Component
        {
            if (!string.IsNullOrWhiteSpace(fieldName))
            {
                int fieldIndex = FindBindIndex(fieldName);
                if (fieldIndex >= 0)
                {
                    return GetBindComponentInternal<T>(fieldIndex, fieldName);
                }

                Debug.LogError(BuildBindError(index, fieldName, typeof(T), "字段不存在，请重新生成并保存绑定数据"), this);
                return null;
            }

            Debug.LogError(BuildBindError(index, fieldName, typeof(T), "字段名为空，请使用纯索引重载或重新生成绑定"), this);
            return null;
        }

        public void OnBeforeSerialize()
        {
        }

        public void OnAfterDeserialize()
        {
            InvalidateRuntimeCaches();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            InvalidateRuntimeCaches();
        }
#endif

        private T GetBindComponentInternal<T>(int index, string requestedFieldName) where T : Component
        {
            if (index < 0 || index >= m_BindComs.Count)
            {
                Debug.LogError(BuildBindError(index, requestedFieldName, typeof(T),
                    $"索引超出范围，当前共 {m_BindComs.Count} 项绑定"));
                return null;
            }

            Component rawComponent = m_BindComs[index];
            if (rawComponent == null)
            {
                Debug.LogError(BuildBindError(index, requestedFieldName, typeof(T), "绑定组件为空引用"));
                return null;
            }

            T bindCom = rawComponent as T;

            if (bindCom == null)
            {
                Debug.LogError(BuildBindError(index, requestedFieldName, typeof(T),
                    $"类型不匹配，实际类型为 {rawComponent.GetType().FullName}"));
                return null;
            }

            return bindCom;
        }

        private int FindBindIndex(string fieldName)
        {
            EnsureBindFieldIndexMap();

            if (m_BindFieldIndexMap != null
                && m_BindFieldIndexMap.TryGetValue(fieldName, out int fieldIndex))
            {
                return fieldIndex;
            }

            return -1;
        }

        private void EnsureBindFieldIndexMap()
        {
            if (!m_BindFieldIndexMapDirty && m_BindFieldIndexMap != null)
            {
                return;
            }

            if (m_BindFieldIndexMap == null)
            {
                m_BindFieldIndexMap = new Dictionary<string, int>(m_BindFieldNames.Count, StringComparer.Ordinal);
            }
            else
            {
                m_BindFieldIndexMap.Clear();
            }

            for (int i = 0; i < m_BindFieldNames.Count; i++)
            {
                string fieldName = m_BindFieldNames[i];
                if (string.IsNullOrWhiteSpace(fieldName) || m_BindFieldIndexMap.ContainsKey(fieldName))
                {
                    continue;
                }

                m_BindFieldIndexMap.Add(fieldName, i);
            }

            m_BindFieldIndexMapDirty = false;
        }

        private void InvalidateRuntimeCaches()
        {
            m_BindFieldIndexMapDirty = true;
            m_BindFieldIndexMap = null;
        }

        private string GetFieldName(int index)
        {
            if (index >= 0 && index < m_BindFieldNames.Count && !string.IsNullOrWhiteSpace(m_BindFieldNames[index]))
            {
                return m_BindFieldNames[index];
            }

            return "<unknown>";
        }

        private string BuildBindError(int index, string requestedFieldName, Type expectedType, string reason)
        {
            string fieldName = !string.IsNullOrWhiteSpace(requestedFieldName)
                ? requestedFieldName
                : GetFieldName(index);

            return
                $"[{name}] AutoBind 获取绑定组件失败。Field={fieldName}, Index={index}, Expected={expectedType.FullName}, Reason={reason}";
        }
    }
}
