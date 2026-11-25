// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Misc.Data.TarkovMarket
{
    /// <summary>
    /// Low-level client for json.tarkov.dev — a static REST/JSON mirror of Tarkov data,
    /// separate from the GraphQL API at api.tarkov.dev. Used for all game modes
    /// ("regular", "pve", "pvp-season"): the GraphQL <c>GameMode</c> enum has no season
    /// mode, and the GraphQL endpoint has been answering "GraphQL server unavailable"
    /// (HTTP 422) even for valid queries while this mirror stayed reliable.
    /// <para>
    /// Every text field in the base documents (item names, task descriptions, map/extract
    /// names, ...) is an unresolved locale token — e.g. an item's <c>name</c> field literally
    /// holds the string <c>"&lt;bsgId&gt; Name"</c>, not real text. Resolving it means fetching
    /// a second, per-language document at <c>{endpoint}_{lang}</c> — a flat
    /// <c>{token: translatedText}</c> dictionary — and looking the raw value up in it.
    /// There's no need to parse the base document's own <c>translations</c> JSONPath list to
    /// know which fields need this: applying <see cref="Resolve"/> to any string (translatable
    /// or not) is safe, since a value that's already-resolved plain text (e.g. some extract
    /// names come through pre-resolved) simply won't be a key in the dictionary and falls
    /// back to itself unchanged.
    /// </para>
    /// </summary>
    internal static class TarkovJsonApi
    {
        private const string BaseUrl = "https://json.tarkov.dev";

        /// <summary>
        /// Fetches <c>{gameMode}/{endpoint}</c> and returns its <c>data</c> node. The returned
        /// <see cref="JsonElement"/> is backed by its own document (via <c>.Clone()</c>) so it
        /// stays valid after this method returns.
        /// </summary>
        public static async Task<JsonElement> FetchDataAsync(string gameMode, string endpoint, CancellationToken ct)
        {
            using var response = await SilkProgram.HttpClient.GetAsync($"{BaseUrl}/{gameMode}/{endpoint}", ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"json.tarkov.dev {gameMode}/{endpoint} returned {(int)response.StatusCode} ({response.StatusCode}): " +
                    $"{(string.IsNullOrWhiteSpace(body) ? "<empty body>" : body)}");
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.GetProperty("data").Clone();
        }

        /// <summary>
        /// Fetches the flat <c>token → translated text</c> dictionary for
        /// <c>{gameMode}/{endpoint}_{lang}</c>.
        /// </summary>
        public static async Task<Dictionary<string, string>> FetchTranslationsAsync(
            string gameMode, string endpoint, string lang, CancellationToken ct)
        {
            using var response = await SilkProgram.HttpClient.GetAsync($"{BaseUrl}/{gameMode}/{endpoint}_{lang}", ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"json.tarkov.dev {gameMode}/{endpoint}_{lang} returned {(int)response.StatusCode} ({response.StatusCode}): " +
                    $"{(string.IsNullOrWhiteSpace(body) ? "<empty body>" : body)}");
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.GetProperty("data").EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    dict[prop.Name] = prop.Value.GetString() ?? "";
            }
            return dict;
        }

        /// <summary>
        /// Resolves a raw base-document value through a translation dictionary. Falls back to
        /// the raw value unchanged when it isn't a known token (already-resolved text, or a
        /// field the translation set doesn't cover) — safe to call on every string field.
        /// </summary>
        public static string Resolve(IReadOnlyDictionary<string, string> dict, string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return raw ?? "";
            return dict.TryGetValue(raw, out var resolved) ? resolved : raw;
        }

        // ── JsonElement helpers (all null/missing-safe) ─────────────────────────

        public static string GetString(this JsonElement el, string prop, string fallback = "")
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? fallback
                : fallback;

        public static int GetInt(this JsonElement el, string prop, int fallback = 0)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
                ? i
                : fallback;

        public static long GetLong(this JsonElement el, string prop, long fallback = 0)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)
                ? i
                : fallback;

        public static float GetFloat(this JsonElement el, string prop, float fallback = 0f)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
                ? (float)d
                : fallback;

        public static bool GetBool(this JsonElement el, string prop, bool fallback = false)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) &&
               (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
                ? v.GetBoolean()
                : fallback;

        /// <summary>Gets a nested object/array property, or <c>null</c> (default JsonElement) if absent/wrong kind.</summary>
        public static JsonElement? GetObj(this JsonElement el, string prop)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
               && (v.ValueKind == JsonValueKind.Object || v.ValueKind == JsonValueKind.Array)
                ? v
                : null;

        /// <summary>
        /// Gets a property regardless of its JSON kind (string, object, ...), or <c>null</c> if
        /// absent or explicit JSON null. Unlike <see cref="GetObj"/>, doesn't restrict to
        /// object/array — used where a field can be either a bare string or a wrapper object.
        /// </summary>
        public static JsonElement? PropertyOrNull(this JsonElement el, string prop)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null
                ? v
                : null;

        /// <summary>Gets a string property's raw value (unresolved token) — same as GetString but named for clarity at call sites.</summary>
        public static string GetToken(this JsonElement el, string prop) => el.GetString(prop);

        /// <summary>Enumerates an array property; empty if absent or not an array.</summary>
        public static IEnumerable<JsonElement> EnumerateArrayProp(this JsonElement el, string prop)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array)
                foreach (var item in v.EnumerateArray())
                    yield return item;
        }
    }
}

