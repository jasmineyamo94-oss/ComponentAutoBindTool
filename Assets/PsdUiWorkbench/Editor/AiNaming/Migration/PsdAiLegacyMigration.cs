using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using PsdUiWorkbench.Core;
using UnityEditor;

namespace PsdUiWorkbench.AiNaming
{
    public sealed class PsdAiLegacyNameImport
    {
        public IReadOnlyDictionary<int, string> Changes { get; }
        public IReadOnlyList<string> Warnings { get; }
        public int SkippedCount { get; }
        internal readonly PsdSession Session;
        internal readonly PsdDocumentSnapshot Document;
        internal readonly long Version;
        internal PsdAiLegacyNameImport(PsdSession session, Dictionary<int, string> changes, List<string> warnings, int skipped)
        {
            Session = session; Document = session.Document; Version = session.ContextVersion;
            Changes = new ReadOnlyDictionary<int, string>(changes);
            Warnings = warnings.AsReadOnly(); SkippedCount = skipped;
        }
    }

    public sealed class PsdAiLegacySettingsData
    {
        public string Model { get; internal set; } = "";
        public PsdAiReasoningEffort ReasoningEffort { get; internal set; }
        public int TimeoutSeconds { get; internal set; } = 120;
        public string Rules { get; internal set; } = "";
    }

    public static class PsdAiLegacyMigration
    {
        private const int MaxInput = 1024 * 1024;
        private static ArgumentException InvalidJson() => new ArgumentException("旧命名 JSON 无效：请检查 layers 数组、唯一整数 id 和非空 name。");
        private static ArgumentException InvalidYaml() => new ArgumentException("旧设置 YAML 无效或使用了不支持的格式，请检查四个命名设置字段。");

        public static PsdAiLegacyNameImport BuildNameImport(PsdSession session, string json, bool overwriteExisting = false)
        {
            if (session == null || session.Document == null) throw new ArgumentException("请先加载 Workbench 文档。");
            Dictionary<string, object> root;
            try { root = new JsonReader(json).Read() as Dictionary<string, object>; }
            catch (Exception) { throw InvalidJson(); }
            if (root == null || !root.TryGetValue("layers", out var value) || !(value is List<object> layers)) throw InvalidJson();
            var names = new Dictionary<int, string>();
            // 先检查整批结构，未知 ID 也必须参与重复和缺字段校验。
            foreach (var item in layers)
            {
                if (!(item is Dictionary<string, object> row) || !row.TryGetValue("id", out var idValue)
                    || !(idValue is Number number) || !int.TryParse(number.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int id)
                    || !row.TryGetValue("name", out var nameValue) || !(nameValue is string name)
                    || string.IsNullOrWhiteSpace(name) || names.ContainsKey(id)) throw InvalidJson();
                names.Add(id, name);
            }
            var changes = new Dictionary<int, string>();
            var warnings = new List<string>();
            int skipped = 0;
            var existing = session.CaptureNames().Overrides;
            foreach (var pair in names)
            {
                var node = session.Document.Find(pair.Key);
                if (node == null) { warnings.Add("已跳过未知节点 ID：" + pair.Key); skipped++; continue; }
                if (pair.Value == node.Name || pair.Value == session.GetNodeName(pair.Key)) { skipped++; continue; }
                if (!overwriteExisting && existing.ContainsKey(pair.Key))
                { warnings.Add("已保护 Workbench 名称覆盖，节点 ID：" + pair.Key); skipped++; continue; }
                changes.Add(pair.Key, pair.Value);
            }
            return new PsdAiLegacyNameImport(session, changes, warnings, skipped);
        }

        public static int ApplyNames(PsdSession session, PsdAiLegacyNameImport import)
        {
            if (session == null || import == null) throw new ArgumentNullException();
            if (!ReferenceEquals(session, import.Session) || !ReferenceEquals(session.Document, import.Document)
                || session.ContextVersion != import.Version)
                throw new InvalidOperationException("文档或会话状态已变化，请重新生成迁移预览。");
            if (import.Changes.Count == 0) return 0;
            // Session 执行真实整批冲突校验并原子提交，不能逐条调用。
            if (!session.SetNodeNames(import.Changes))
                throw new InvalidOperationException("名称整批校验或保存失败，未应用迁移名称。");
            return import.Changes.Count;
        }

        public static PsdAiLegacySettingsData ReadSettingsYaml(string yaml)
        {
            if (yaml == null || yaml.Length > MaxInput || yaml.IndexOf('\0') >= 0) throw InvalidYaml();
            var lines = yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var fields = new Dictionary<string, string>();
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].TrimStart();
                    int colon = trimmed.IndexOf(':');
                    if (colon < 0) continue;
                    string key = trimmed.Substring(0, colon);
                    // 不解析、不保留密钥或任何其他字段。
                    if (key != "_model" && key != "_reasoningEffort" && key != "_timeoutSeconds" && key != "_rules") continue;
                    if (fields.ContainsKey(key)) throw InvalidYaml();
                    int indent = lines[i].Length - trimmed.Length;
                    string scalar = trimmed.Substring(colon + 1).Trim();
                    if (scalar.StartsWith("|") || scalar.StartsWith(">"))
                    {
                        if (scalar != "|" && scalar != "|-" && scalar != ">" && scalar != ">-") throw InvalidYaml();
                        var block = new List<string>(); int blockIndent = -1;
                        while (i + 1 < lines.Length)
                        {
                            string next = lines[i + 1];
                            int spaces = next.Length - next.TrimStart(' ').Length;
                            if (next.Trim().Length != 0 && spaces <= indent) break;
                            if (next.TrimStart().StartsWith("_apiKey:", StringComparison.Ordinal)) throw InvalidYaml();
                            i++;
                            if (next.Trim().Length != 0 && blockIndent < 0) blockIndent = spaces;
                            if (next.Trim().Length != 0 && spaces < blockIndent) throw InvalidYaml();
                            block.Add(next.Trim().Length == 0 ? "" : next.Substring(blockIndent));
                        }
                        while (block.Count > 0 && block[block.Count - 1] == "") block.RemoveAt(block.Count - 1);
                        var text = new StringBuilder();
                        for (int n = 0; n < block.Count; n++)
                        {
                            if (n > 0) text.Append(scalar[0] == '>' && block[n - 1].Length > 0 && block[n].Length > 0 ? " " : "\n");
                            text.Append(block[n]);
                        }
                        scalar = text.ToString() + (scalar.EndsWith("-") || block.Count == 0 ? "" : "\n");
                    }
                    else
                    {
                        while (i + 1 < lines.Length)
                        {
                            string next = lines[i + 1];
                            if (next.Trim().Length == 0) break;
                            int spaces = next.Length - next.TrimStart(' ').Length;
                            if (spaces <= indent) break;
                            // 错缩进的敏感字段不能吞入命名设置。
                            string continuation = next.Trim();
                            if (continuation.StartsWith("_", StringComparison.Ordinal) && continuation.IndexOf(':') >= 0) throw InvalidYaml();
                            scalar += " " + continuation; i++;
                        }
                        scalar = DecodeScalar(scalar);
                    }
                    fields.Add(key, scalar);
                }
                var result = new PsdAiLegacySettingsData();
                if (!fields.TryGetValue("_model", out var model) || string.IsNullOrWhiteSpace(model)) throw InvalidYaml();
                result.Model = model;
                if (fields.TryGetValue("_rules", out var rules)) result.Rules = rules;
                if (fields.TryGetValue("_reasoningEffort", out var effort))
                {
                    if (!int.TryParse(effort, out int e) || e < 0 || e > 3) throw InvalidYaml();
                    result.ReasoningEffort = (PsdAiReasoningEffort)e;
                }
                if (fields.TryGetValue("_timeoutSeconds", out var timeout))
                {
                    if (!int.TryParse(timeout, out int seconds) || seconds < 10 || seconds > 600) throw InvalidYaml();
                    result.TimeoutSeconds = seconds;
                }
                return result;
            }
            catch (Exception) { throw InvalidYaml(); }
        }

        private static string DecodeScalar(string value)
        {
            if (value.StartsWith("\""))
            {
                // Unity 双引号标量的常见转义与 JSON 相同，额外支持 YAML 的常见转义。
                value = value.Replace("\\0", "\\u0000").Replace("\\a", "\\u0007").Replace("\\v", "\\u000b").Replace("\\e", "\\u001b");
                var decoded = new JsonReader(value).Read();
                if (!(decoded is string text)) throw InvalidYaml();
                return text;
            }
            if (value.StartsWith("'"))
            {
                if (value.Length < 2 || !value.EndsWith("'")) throw InvalidYaml();
                string inner = value.Substring(1, value.Length - 2);
                for (int i = 0; i < inner.Length; i++)
                    if (inner[i] == '\'' && (i + 1 >= inner.Length || inner[++i] != '\'')) throw InvalidYaml();
                return inner.Replace("''", "'");
            }
            if (value.StartsWith("!") || value.StartsWith("&") || value.StartsWith("*") || value.StartsWith("{") || value.StartsWith("[")) throw InvalidYaml();
            int comment = value.IndexOf(" #", StringComparison.Ordinal);
            return (comment < 0 ? value : value.Substring(0, comment)).TrimEnd();
        }

        public static void ApplySettings(PsdAiNamingSettings target, PsdAiLegacySettingsData data)
        {
            if (target == null || data == null) throw new ArgumentNullException();
            string model = target.Model, rules = target.Rules;
            int timeout = target.TimeoutSeconds;
            var effort = target.ReasoningEffort;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("迁移 AI 命名设置");
            Undo.RegisterCompleteObjectUndo(target, "迁移 AI 命名设置");
            try
            {
                target.Model = data.Model; target.ReasoningEffort = data.ReasoningEffort;
                target.TimeoutSeconds = data.TimeoutSeconds; target.Rules = data.Rules;
                EditorUtility.SetDirty(target);
                if (AssetDatabase.Contains(target)) AssetDatabase.SaveAssetIfDirty(target);
                Undo.CollapseUndoOperations(group);
            }
            catch (Exception)
            {
                target.Model = model; target.ReasoningEffort = effort;
                target.TimeoutSeconds = timeout; target.Rules = rules;
                Undo.RevertAllDownToGroup(group);
                throw new InvalidOperationException("迁移设置保存失败，已恢复目标设置。");
            }
        }

        private sealed class Number { internal string Text; }
        // 独立的有界 JSON 读取器：避免 JsonUtility 将缺失的 id 默认为 0。
        private sealed class JsonReader
        {
            private readonly string text;
            private int position;
            internal JsonReader(string text)
            {
                if (text == null || text.Length > MaxInput) throw InvalidJson();
                this.text = text;
            }
            internal object Read()
            {
                object value = Value(0); Space();
                if (position != text.Length) throw InvalidJson();
                return value;
            }
            private void Space() { while (position < text.Length && " \r\n\t".IndexOf(text[position]) >= 0) position++; }
            private bool Take(char c) { Space(); if (position >= text.Length || text[position] != c) return false; position++; return true; }
            private object Value(int depth)
            {
                if (depth > 64) throw InvalidJson();
                Space(); if (position >= text.Length) throw InvalidJson();
                if (text[position] == '"') return String();
                if (Take('{'))
                {
                    var map = new Dictionary<string, object>();
                    if (Take('}')) return map;
                    do
                    {
                        Space(); string key = String();
                        if (!Take(':') || map.ContainsKey(key)) throw InvalidJson();
                        map.Add(key, Value(depth + 1));
                        if (Take('}')) return map;
                    } while (Take(','));
                    throw InvalidJson();
                }
                if (Take('['))
                {
                    var list = new List<object>();
                    if (Take(']')) return list;
                    do { list.Add(Value(depth + 1)); if (Take(']')) return list; } while (Take(','));
                    throw InvalidJson();
                }
                foreach (string literal in new[] { "true", "false", "null" })
                    if (position + literal.Length <= text.Length && string.CompareOrdinal(text, position, literal, 0, literal.Length) == 0)
                    { position += literal.Length; return literal == "null" ? null : (object)(literal == "true"); }
                int start = position;
                if (text[position] == '-') position++;
                if (position >= text.Length || text[position] < '0' || text[position] > '9') throw InvalidJson();
                if (text[position++] != '0') while (position < text.Length && char.IsDigit(text[position])) position++;
                if (position < text.Length && text[position] == '.')
                { position++; int digits = position; while (position < text.Length && char.IsDigit(text[position])) position++; if (digits == position) throw InvalidJson(); }
                if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
                {
                    position++; if (position < text.Length && (text[position] == '+' || text[position] == '-')) position++;
                    int digits = position; while (position < text.Length && char.IsDigit(text[position])) position++;
                    if (digits == position) throw InvalidJson();
                }
                return new Number { Text = text.Substring(start, position - start) };
            }
            private string String()
            {
                if (position >= text.Length || text[position++] != '"') throw InvalidJson();
                var result = new StringBuilder();
                while (position < text.Length)
                {
                    char c = text[position++];
                    if (c == '"') return result.ToString();
                    if (c < 32) throw InvalidJson();
                    if (c != '\\') { result.Append(c); continue; }
                    if (position >= text.Length) throw InvalidJson();
                    c = text[position++];
                    switch (c)
                    {
                        case '"': case '\\': case '/': result.Append(c); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'u':
                            if (position + 4 > text.Length || !ushort.TryParse(text.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code)) throw InvalidJson();
                            result.Append((char)code); position += 4; break;
                        default: throw InvalidJson();
                    }
                }
                throw InvalidJson();
            }
        }
    }
}
