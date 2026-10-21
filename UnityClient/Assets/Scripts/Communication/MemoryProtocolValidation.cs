using System;
using System.Linq;

namespace EmbodiedNPC.Communication
{
    internal static class MemoryProtocolValidation
    {
        public static bool IsProtocolId(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= 160 &&
            value.All(character =>
                character >= 'A' && character <= 'Z' ||
                character >= 'a' && character <= 'z' ||
                character >= '0' && character <= '9' ||
                character == '_' || character == '-');

        public static bool IsOptionalProtocolId(string value) => string.IsNullOrEmpty(value) || IsProtocolId(value);

        public static bool IsEntityId(string value) =>
            !string.IsNullOrEmpty(value) && value.Length <= 120 && value[0] >= 'a' && value[0] <= 'z' &&
            value.Skip(1).All(character => char.IsLower(character) || char.IsDigit(character) || character == '_');

        public static bool IsOptionalEntityId(string value) => string.IsNullOrEmpty(value) || IsEntityId(value);
        public static bool IsUniqueProtocolIds(string[] values, bool requireOne) =>
            values != null && values.Length <= 32 && (!requireOne || values.Length > 0) &&
            values.All(IsProtocolId) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;

        public static bool IsUniqueBoundedStrings(string[] values) =>
            values != null && values.Length <= 32 &&
            values.All(value => value != null && value.Length <= 160) &&
            values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    }
}
