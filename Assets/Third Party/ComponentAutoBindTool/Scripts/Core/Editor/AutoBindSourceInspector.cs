using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor.Compilation;

namespace ComponentAutoBindTool.Editor
{
    internal static class AutoBindSourceInspector
    {
        public static void ValidateDeclarations(AutoBindBuildInput input, AutoBindValidationResult result)
        {
            string assemblyName = Path.GetFileNameWithoutExtension(CompilationPipeline.GetAssemblyNameFromScriptPath(input.ScriptPath));
            var assembly = CompilationPipeline.GetAssemblies(AssembliesType.Editor).FirstOrDefault(a => a.name == assemblyName);
            IEnumerable<string> files = assembly != null ? assembly.sourceFiles : new[] { input.ScriptPath };
            bool found = false;
            foreach (string path in files)
            {
                if (!File.Exists(path)) continue;
                if (!string.IsNullOrEmpty(input.OutputPath) &&
                    string.Equals(Path.GetFullPath(path), Path.GetFullPath(input.OutputPath), StringComparison.OrdinalIgnoreCase)) continue;
                string source = File.ReadAllText(path);
                foreach (string declaration in FindDeclarations(source, input.HostType))
                {
                    found = true;
                    if (path.EndsWith(".BindComponents.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        result.AddError("该宿主在其他位置已有生成文件，请先统一输出目录：" + path);
                        continue;
                    }
                    int open = declaration.IndexOf('{');
                    string header = declaration.Substring(0, open);
                    if (!Regex.IsMatch(header, @"\bpartial\b"))
                        result.AddError("目标脚本必须声明为 partial class：" + path);
                    foreach (string member in DirectMemberHeaders(declaration.Substring(open + 1)))
                    {
                        if (HasMemberConflict(member, source, input.HostType))
                            result.AddError("手写成员与 AutoBind 生成成员冲突：" + path + " → " + member.Trim());
                    }
                }
            }
            if (!found) result.AddError("无法定位目标脚本的 partial 类声明。");
        }

        private static bool HasMemberConflict(string member, string source, Type host)
        {
            string text = member.Replace("@", "").Trim();
            if (Regex.IsMatch(text, @"\bIAutoBindHost\s*\.\s*IsAutoBound\b")) return true;
            var getter = AutoBindCodeGenerator.FindBaseMethod(host, "GetGeneratedUIView");
            bool generatesGetter = getter != null && getter.ReturnType == typeof(object);
            bool Reserved(string name) => name == "view" || name == "UIView" ||
                name == "EnsureAutoBind" || (generatesGetter && name == "GetGeneratedUIView");
            var nestedType = Regex.Match(text, @"\b(class|struct|interface|enum|delegate)\s+(?:\S+\s+)?(\w+)");
            if (nestedType.Success) return Reserved(nestedType.Groups[2].Value);

            int parenthesis = text.IndexOf('(');
            string signature = parenthesis >= 0 ? text.Substring(0, parenthesis) : text;
            if (parenthesis >= 0 && !signature.Contains("="))
            {
                // 泛型方法和不同参数的重载不会与生成的非泛型方法冲突。
                var nameMatch = Regex.Match(signature, @"(\w+)\s*$");
                if (!nameMatch.Success) return false;
                string name = nameMatch.Groups[1].Value;
                if (name == "view" || name == "UIView") return true;
                int close = text.IndexOf(')', parenthesis);
                if (close < 0) return false;
                string parameters = text.Substring(parenthesis + 1, close - parenthesis - 1).Trim();
                if (name == "GetGeneratedUIView") return generatesGetter && parameters.Length == 0;
                if (name != "EnsureAutoBind" || parameters.Contains(",") ||
                    Regex.IsMatch(parameters, @"\b(ref|out|in)\b")) return false;
                var parameter = Regex.Match(parameters.Split('=')[0].Trim(), @"^(.+?)\s+(\w+)$");
                if (!parameter.Success) return false;
                string parameterType = Regex.Replace(parameter.Groups[1].Value, @"\s", "").Replace("global::", "");
                string aliasName = parameterType.Split('.')[0].Split(new[] { "::" }, StringSplitOptions.None)[0];
                var alias = Regex.Match(source, @"\busing\s+@?" + Regex.Escape(aliasName) + @"\s*=\s*([^;]+);");
                if (alias.Success)
                    parameterType = (Regex.Replace(alias.Groups[1].Value, @"\s", "") +
                        parameterType.Substring(aliasName.Length)).Replace("global::", "").Replace("::", ".");
                if (parameterType == "UnityEngine.GameObject") return true;
                if (parameterType != "GameObject") return false;
                // 利用已编译宿主消除同名 GameObject 类型造成的歧义。
                return host.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.DeclaredOnly).Any(method =>
                        !method.IsGenericMethod && (method.Name == name || method.Name.EndsWith("." + name, StringComparison.Ordinal)) &&
                        method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(UnityEngine.GameObject) &&
                        method.GetParameters()[0].Name == parameter.Groups[2].Value);
            }

            // 分别检查每个变量声明，不让第一个初始化表达式截断后续字段。
            int depth = 0;
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                char c = i == text.Length ? ',' : text[i];
                if (c == '(' || c == '[' || (c == '<' && IsTypeArgumentList(text.Substring(i)))) depth++;
                if (c == ')' || c == '>' || c == ']') depth = Math.Max(0, depth - 1);
                if (c != ',' || depth != 0) continue;
                string declarator = text.Substring(start, i - start).Split('=')[0].Trim();
                var name = Regex.Match(declarator, @"(\w+)\s*$");
                if (name.Success && Reserved(name.Groups[1].Value)) return true;
                start = i + 1;
            }
            return false;
        }

        private static bool IsTypeArgumentList(string text)
        {
            // 字段初始化中的比较/条件表达式不能把后续声明吞进“泛型参数”。
            return Regex.IsMatch(text, @"^<\s*(?:global\s*::\s*)?@?[\p{L}_](?:[\p{L}\p{N}_\s.,?<>\[\]]|::)*>");
        }

        // 保留源码长度与换行，字符串内的声明、注释和大括号不参与宿主识别。
        internal static string StripCommentsAndLiterals(string source)
        {
            char[] stripped = source.ToCharArray();
            int position = 0;
            while (position < source.Length)
            {
                int start = position;
                if (!SkipComment(source, ref position) && !SkipLiteral(source, ref position))
                {
                    position++;
                    continue;
                }
                for (int i = start; i < position; i++)
                    if (stripped[i] != '\r' && stripped[i] != '\n') stripped[i] = ' ';
            }
            return new string(stripped);
        }

        private static bool SkipComment(string source, ref int position)
        {
            if (position + 1 >= source.Length || source[position] != '/') return false;
            char next = source[position + 1];
            if (next != '/' && next != '*') return false;
            position += 2;
            if (next == '/')
            {
                while (position < source.Length && source[position] != '\n') position++;
            }
            else
            {
                while (position + 1 < source.Length && !(source[position] == '*' && source[position + 1] == '/')) position++;
                position = Math.Min(source.Length, position + 2);
            }
            return true;
        }

        private static bool SkipLiteral(string source, ref int position)
        {
            int quote = position;
            bool interpolated = false;
            int dollars = 0;
            bool verbatim = false;
            while (quote < source.Length && (source[quote] == '$' || source[quote] == '@'))
            {
                interpolated |= source[quote] == '$';
                if (source[quote] == '$') dollars++;
                verbatim |= source[quote] == '@';
                quote++;
            }
            if (quote >= source.Length || (source[quote] != '"' && (quote != position || source[quote] != '\''))) return false;
            char delimiter = source[quote];
            int count = 1;
            while (delimiter == '"' && quote + count < source.Length && source[quote + count] == '"') count++;
            position = quote + 1;
            if (count >= 3)
            {
                // 原始字符串支持任意长度引号分隔符；其内插表达式也整体屏蔽。
                position = quote + count;
                while (position < source.Length)
                {
                    if (interpolated && source[position] == '{')
                    {
                        int braces = 0;
                        while (position + braces < source.Length && source[position + braces] == '{') braces++;
                        position += braces;
                        if (braces >= dollars) SkipInterpolation(source, ref position, dollars);
                        continue;
                    }
                    int run = 0;
                    while (position + run < source.Length && source[position + run] == '"') run++;
                    position += run;
                    if (run >= count) return true;
                    if (run == 0) position++;
                }
                return true;
            }
            while (position < source.Length)
            {
                char current = source[position++];
                if (!verbatim && current == '\\') { position = Math.Min(source.Length, position + 1); continue; }
                if (current == delimiter)
                {
                    if (verbatim && position < source.Length && source[position] == '"') { position++; continue; }
                    return true;
                }
                if (!interpolated || current != '{') continue;
                if (position < source.Length && source[position] == '{') { position++; continue; }
                SkipInterpolation(source, ref position, 1);
            }
            return true;
        }

        private static void SkipInterpolation(string source, ref int position, int closingBraces)
        {
            int depth = 0;
            while (position < source.Length)
            {
                if (SkipComment(source, ref position) || SkipLiteral(source, ref position)) continue;
                char current = source[position++];
                if (current == '{') depth++;
                if (current != '}') continue;
                if (depth > 0) { depth--; continue; }
                int count = 1;
                while (count < closingBraces && position < source.Length && source[position] == '}')
                { position++; count++; }
                if (count == closingBraces) return;
            }
        }

        // 仅做声明级词法识别：忽略注释、字符串、特性和方法体，避免将局部变量当作字段。
        internal static IEnumerable<string> FindDeclarations(string source, Type host)
        {
            string stripped = StripCommentsAndLiterals(source);
            string[] tokens = Regex.Matches(stripped, @"@?[\p{L}_][\p{L}\p{N}_]*|[^\s]")
                .Cast<Match>().Select(m => m.Value).ToArray();
            string currentNamespace = "";
            int depth = 0;
            var namespaceScopes = new Dictionary<int, string>();
            int headerStart = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (token == "namespace")
                {
                    int j = i + 1;
                    while (j < tokens.Length && tokens[j] != "{" && tokens[j] != ";") j++;
                    string suffix = string.Concat(tokens.Skip(i + 1).Take(j - i - 1)).Replace("@", "");
                    string previous = currentNamespace;
                    currentNamespace = currentNamespace.Length == 0 ? suffix : currentNamespace + "." + suffix;
                    if (j < tokens.Length && tokens[j] == "{")
                    {
                        depth++;
                        namespaceScopes[depth] = previous;
                    }
                    i = j;
                    headerStart = i + 1;
                    continue;
                }
                if (token == "class" && i + 1 < tokens.Length && tokens[i + 1].TrimStart('@') == host.Name &&
                    currentNamespace == (host.Namespace ?? "") && depth == namespaceScopes.Count)
                {
                    int open = i + 2;
                    while (open < tokens.Length && tokens[open] != "{") open++;
                    int end = open + 1;
                    int nested = 1;
                    while (end < tokens.Length && nested > 0)
                    {
                        if (tokens[end] == "{") nested++;
                        if (tokens[end] == "}") nested--;
                        end++;
                    }
                    if (nested == 0) yield return string.Join(" ", tokens.Skip(headerStart).Take(end - headerStart));
                    i = end - 1;
                    headerStart = end;
                    continue;
                }
                if (token == "{") { depth++; headerStart = i + 1; }
                if (token == "}")
                {
                    if (namespaceScopes.TryGetValue(depth, out string previous))
                    {
                        currentNamespace = previous;
                        namespaceScopes.Remove(depth);
                    }
                    depth--;
                    headerStart = i + 1;
                }
                if (token == ";") headerStart = i + 1;
            }
        }

        private static IEnumerable<string> DirectMemberHeaders(string body)
        {
            int depth = 0;
            int attributes = 0;
            var header = new System.Text.StringBuilder();
            foreach (char c in body)
            {
                if (depth == 0 && c == '[') attributes++;
                if (depth == 0 && attributes > 0)
                {
                    if (c == ']') attributes--;
                    continue;
                }
                if (c == '{')
                {
                    if (depth++ == 0) { yield return header.ToString(); header.Clear(); }
                    continue;
                }
                if (c == '}') { depth--; if (depth < 0) yield break; continue; }
                if (depth != 0) continue;
                if (c == ';') { yield return header.ToString(); header.Clear(); }
                else header.Append(c);
            }
        }
    }
}
