using System;
using System.Collections.Generic;

namespace ComponentAutoBindTool.Editor
{
    internal static class AutoBindFieldNameUtility
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>(
            ("abstract as base bool break byte case catch char checked class const continue decimal default delegate " +
             "do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int " +
             "interface internal is lock long namespace new null object operator out override params private protected " +
             "public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw " +
             "true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while __arglist __makeref __reftype __refvalue").Split(' '), StringComparer.Ordinal);

        public static string BuildFieldName(string bindName)
        {
            if (string.IsNullOrWhiteSpace(bindName))
            {
                return string.Empty;
            }

            string fieldName = bindName.Replace("_", string.Empty);
            if (fieldName.Length == 0)
            {
                return string.Empty;
            }

            char firstChar = fieldName[0];
            if (firstChar >= 'A' && firstChar <= 'Z')
            {
                fieldName = char.ToLowerInvariant(firstChar) + fieldName.Substring(1);
            }

            return fieldName;
        }

        public static bool IsValidIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier) || Keywords.Contains(identifier))
            {
                return false;
            }

            if (!IsIdentifierStartCharacter(identifier[0]))
            {
                return false;
            }

            for (int i = 1; i < identifier.Length; i++)
            {
                if (!IsIdentifierPartCharacter(identifier[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsIdentifierStartCharacter(char value)
        {
            return value == '_' || IsAsciiLetter(value);
        }

        private static bool IsIdentifierPartCharacter(char value)
        {
            return value == '_' || IsAsciiLetter(value) || (value >= '0' && value <= '9');
        }

        private static bool IsAsciiLetter(char value)
        {
            return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');
        }
    }
}
