using System;
using UnityEngine;

namespace EmbodiedNPC.Memory
{
    /// <summary>One collision-resistant identity for a Unity runtime session.</summary>
    public static class ClientSessionIdentity
    {
        private static string current;

        public static string Current => current ??= Create();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize() => current = Create();

        private static string Create() => "session_" + Guid.NewGuid().ToString("N");
    }
}
