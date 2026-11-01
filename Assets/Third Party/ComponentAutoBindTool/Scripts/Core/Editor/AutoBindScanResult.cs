using System.Collections.Generic;
using UnityEngine;
using BindData = ComponentAutoBindTool.Core.ComponentAutoBindTool.BindData;

namespace ComponentAutoBindTool.Editor
{
    public sealed class AutoBindScanResult
    {
        private readonly List<BindData> _bindDatas = new List<BindData>();
        private readonly List<string> _errors = new List<string>();

        public IReadOnlyList<BindData> BindDatas => _bindDatas;
        public IReadOnlyList<string> Errors => _errors;
        public bool HasErrors => _errors.Count > 0;

        public void AddBindData(string name, Component bindCom)
        {
            _bindDatas.Add(new BindData(name, bindCom));
        }

        public void AddError(string message)
        {
            _errors.Add(message);
        }
    }
}
