using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace EmbodiedNPC.Memory
{
    internal static class ExperienceIds
    {
        public static string ProtocolId(string value, string prefix = "source")
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 160 && value.All(IsProtocolCharacter))
                return value;
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            var builder = new StringBuilder(prefix.Length + 65).Append(prefix).Append('_');
            foreach (byte item in hash) builder.Append(item.ToString("x2"));
            return builder.ToString();
        }

        private static bool IsProtocolCharacter(char value) =>
            char.IsLetterOrDigit(value) || value == '_' || value == '-';
    }
}
