using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace ComponentAutoBindTool.Editor
{
    [Serializable]
    internal sealed class AutoBindViewTemplateDefinition
    {
        public string BaseName;
        public string Namespace;
        public string ScriptDirectory;
        public string PrefabPath;
        // 默认 false，已有的跨重载任务仍保持原来的专属 Args 行为。
        public bool WithoutArgs;
        internal bool IsInitialized => !string.IsNullOrEmpty(BaseName) && !string.IsNullOrEmpty(ScriptDirectory) && !string.IsNullOrEmpty(PrefabPath);
        internal bool UseArgs => !WithoutArgs;
        internal string ViewName => BaseName + "View";
        internal string PresenterName => BaseName + "Presenter";
        internal string ArgsName => BaseName + "Args";
        internal string ArgsTypeName => UseArgs ? ArgsName : "global::ComponentAutoBindTool.ViewCore.EmptyViewArgs";
        internal string RoutesName => BaseName + "Routes";
        internal string ViewPath => ScriptDirectory + "/" + ViewName + ".cs";
        internal string PresenterPath => ScriptDirectory + "/" + PresenterName + ".cs";
        internal string ArgsPath => ScriptDirectory + "/" + ArgsName + ".cs";
        internal string RoutesPath => ScriptDirectory + "/" + RoutesName + ".cs";
        internal string BindingPath => ScriptDirectory + "/" + ViewName + ".BindComponents.cs";
        internal string FullViewName => Qualify(ViewName);
        internal IEnumerable<string> OutputPaths => UseArgs
            ? new[] { ViewPath, PresenterPath, ArgsPath, RoutesPath, BindingPath, PrefabPath }
            : new[] { ViewPath, PresenterPath, RoutesPath, BindingPath, PrefabPath };
        private string Qualify(string name) => string.IsNullOrEmpty(Namespace) ? name : Namespace + "." + name;

        internal static AutoBindViewTemplateDefinition Create(string name, string prefabRoot, string scriptRoot, string nameSpace, bool useArgs = true)
        {
            string basis = ValidateBaseName(name);
            string ns = (nameSpace ?? "").Trim();
            if (ns.Length > 0 && ns.Split('.').Any(part => !AutoBindFieldNameUtility.IsValidIdentifier(part)))
                throw new ArgumentException("AutoBind 全局命名空间无效，请先修正全局配置。");
            string scripts = NormalizeFolder(scriptRoot);
            return new AutoBindViewTemplateDefinition
            {
                BaseName = basis, Namespace = ns, WithoutArgs = !useArgs,
                ScriptDirectory = scripts + "/" + basis,
                PrefabPath = NormalizeFolder(prefabRoot) + "/" + basis + "View.prefab"
            };
        }

        internal static string ValidateBaseName(string name)
        {
            string basis = (name ?? "").Trim();
            if (!AutoBindFieldNameUtility.IsValidIdentifier(basis))
                throw new ArgumentException("基础名须为合法 C# 标识符：使用英文字母、数字和下划线，且不能以数字开头或使用关键字。");
            if (basis.EndsWith("View", StringComparison.Ordinal))
                throw new ArgumentException("请输入不含 View 后缀的基础名，例如 Inventory。");
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(basis.ToUpperInvariant()) ||
                System.Text.RegularExpressions.Regex.IsMatch(basis, @"^(COM|LPT)[1-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new ArgumentException("基础名不能使用系统保留的文件夹名称。");
            return basis;
        }

        internal void ValidateNew()
        {
            ValidateAssembly(ViewPath);
            foreach (string path in OutputPaths)
                if (File.Exists(path) || Directory.Exists(path) || File.Exists(path + ".meta"))
                    throw new IOException("目标已存在，不会覆盖：" + path);
            foreach (string name in UseArgs ? new[] { ViewName, PresenterName, ArgsName, RoutesName } : new[] { ViewName, PresenterName, RoutesName })
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (assembly.GetType(Qualify(name), false) != null)
                        throw new InvalidOperationException("类型已存在：" + Qualify(name));
        }

        internal static string NormalizeFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("输出文件夹不能为空。");
            string input = path.Trim().Replace('\\', '/');
            if (!Path.IsPathRooted(input) && input != "Assets" && !input.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("请使用 Assets 相对路径或项目 Assets 内的绝对路径。");
            string full = Path.GetFullPath(input).Replace('\\', '/').TrimEnd('/');
            if (!AutoBindPathUtility.IsPathUnderAssets(full)) throw new ArgumentException("输出文件夹必须位于当前项目 Assets 内。");
            for (var directory = new DirectoryInfo(full); directory != null && AutoBindPathUtility.IsPathUnderAssets(directory.FullName); directory = directory.Parent)
            {
                if (File.Exists(directory.FullName)) throw new ArgumentException("输出路径中的节点是文件：" + directory.FullName);
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("输出路径不能经过链接或重解析目录。");
            }
            return AutoBindPathUtility.ToAssetRelativePath(full);
        }

        internal static void ValidateAssembly(string scriptPath)
        {
            if (scriptPath.Split('/').Any(part => string.Equals(part, "Editor", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("模板脚本必须放在运行时目录，不能位于 Editor 文件夹。");
            string name = Path.GetFileNameWithoutExtension(CompilationPipeline.GetAssemblyNameFromScriptPath(scriptPath));
            var assembly = CompilationPipeline.GetAssemblies(AssembliesType.Player).FirstOrDefault(item => item.name == name);
            HashSet<string> references;
            if (assembly != null) references = new HashSet<string>(assembly.assemblyReferences.Select(item => item.name));
            else
            {
                string asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromScriptPath(scriptPath);
                if (string.IsNullOrEmpty(asmdef) || !File.Exists(asmdef))
                    throw new ArgumentException("无法定位目标运行时程序集，请检查 asmdef 或 asmref 配置。");
                var definition = UnityEngine.JsonUtility.FromJson<AssemblyDefinition>(File.ReadAllText(asmdef));
                if (definition.includePlatforms?.SequenceEqual(new[] { "Editor" }) == true || definition.excludePlatforms?.Contains("Editor") == true)
                    throw new ArgumentException("目标程序集必须同时支持运行时和 Editor 中的宿主挂载。");
                references = new HashSet<string>();
                foreach (string reference in definition.references ?? Array.Empty<string>())
                {
                    if (!reference.StartsWith("GUID:", StringComparison.Ordinal)) { references.Add(reference); continue; }
                    string referencedPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyReference(reference);
                    if (!string.IsNullOrEmpty(referencedPath) && File.Exists(referencedPath))
                        references.Add(UnityEngine.JsonUtility.FromJson<AssemblyDefinition>(File.ReadAllText(referencedPath)).name);
                }
            }
            foreach (string required in new[] { "ComponentAutoBindTool.Core", "ComponentAutoBindTool.ViewCore", "VContainer" })
                if (!references.Contains(required)) throw new ArgumentException("目标程序集 " + name + " 缺少引用：" + required);
        }

        [Serializable]
        private sealed class AssemblyDefinition
        {
            public string name;
            public string[] references;
            public string[] includePlatforms;
            public string[] excludePlatforms;
        }

        private string Wrap(string content)
        {
            if (string.IsNullOrEmpty(Namespace)) return content + "\n";
            return "namespace " + Namespace + "\n{\n" +
                string.Join("\n", content.Split('\n').Select(line => line.Length == 0 ? "" : "    " + line)) + "\n}\n";
        }

        internal string ViewSource(bool complete) => "using ComponentAutoBindTool.ViewCore;\nusing VContainer;\n\n" + Wrap(
            "public partial class " + ViewName + " : global::ComponentAutoBindTool.ViewCore.ViewBase\n{\n" +
            "    public override void ConfigureWindow(global::VContainer.IContainerBuilder builder)\n    {\n" +
            (complete ? "        RegisterWindow<" + PresenterName + ", UIView>(builder, view);\n" : "") + "    }\n}");
        internal string ArgsSource() => "using ComponentAutoBindTool.ViewCore;\n\n" + Wrap("public sealed class " + ArgsName + " : global::ComponentAutoBindTool.ViewCore.IViewArgs\n{\n}");
        internal string RoutesSource() => "using ComponentAutoBindTool.ViewCore;\n\n" + Wrap(
            "public static class " + RoutesName + "\n{\n    public static readonly global::ComponentAutoBindTool.ViewCore.ViewRoute<" + ViewName + ", " + ArgsTypeName + "> Main = new(\"" + ViewName + "\");\n}");
        internal string PresenterSource() => "using ComponentAutoBindTool.ViewCore;\n\n" + Wrap(
            "public sealed class " + PresenterName + " : global::ComponentAutoBindTool.ViewCore.WindowPresenterBase<" + ViewName + ".UIView, " + ArgsTypeName + ">\n{\n" +
            "    public " + PresenterName + "(" + ViewName + ".UIView view) : base(view)\n    {\n    }\n\n" +
            "    protected override void OnOpen(" + ArgsTypeName + " args)\n    {\n    }\n}");
    }
}
