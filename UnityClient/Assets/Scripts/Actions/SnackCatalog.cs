using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace EmbodiedNPC.Actions
{
    /// <summary>Single authored menu, shared with Backend. Static service configuration, never stock evidence.</summary>
    public static class SnackCatalog
    {
        [Serializable] public sealed class Entry
        {
            public string id, chinese, english, description, location;
        }
        private sealed class Document { public string[] quantity_ids; public Entry[] items; }
        private static readonly Document document = Load();
        private static Document Load()
        {
            var asset = Resources.Load<TextAsset>("CafeSnackCatalog");
            var value = asset == null ? null : JsonConvert.DeserializeObject<Document>(asset.text);
            if (value?.items == null || value.quantity_ids == null || value.items.Length == 0 ||
                value.items.Select(x => x.id).Distinct().Count() != value.items.Length ||
                value.items.Any(x => !ActionContractRules.IsIdentifier(x.id) || string.IsNullOrWhiteSpace(x.chinese) ||
                    string.IsNullOrWhiteSpace(x.english) || string.IsNullOrWhiteSpace(x.description) ||
                    (x.location != "showcase" && x.location != "rack")) ||
                value.quantity_ids.Length == 0 || value.quantity_ids.Any(x => !int.TryParse(x, out int n) || n < 1 || n > 4))
                throw new InvalidOperationException("CafeSnackCatalog is missing or invalid.");
            return value;
        }
        public static IReadOnlyList<string> Ids { get; } = Array.AsReadOnly(document.items.Select(x => x.id).ToArray());
        public static IReadOnlyList<string> QuantityIds { get; } = Array.AsReadOnly(document.quantity_ids);
        public static bool IsValid(string id) => document.items.Any(x => x.id == id);
        public static bool IsQuantity(string quantity) => document.quantity_ids.Contains(quantity);
        public static Entry Get(string id) => document.items.FirstOrDefault(x => x.id == id);
        public static string Name(string id, bool english = false) => Get(id) is { } item
            ? english ? item.english : item.chinese : english ? "snack" : "点心";
    }
}
