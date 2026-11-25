// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.IO;
using System.Text.Json;

namespace eft_dma_radar.Silk.Misc.Data.TarkovMarket
{
    /// <summary>
    /// Static loot-container definitions (BSG ID → display name) from tarkov.dev's REST data
    /// (json.tarkov.dev), independent of the GraphQL API.
    /// <para>
    /// The radar drops every container whose BSG ID isn't in <see cref="EftDataManager.AllContainers"/>.
    /// That table used to come only from the GraphQL refresh, so a GraphQL outage (it answers
    /// HTTP 422 "GraphQL server unavailable" for every query) or a cached data.json written
    /// without containers left it empty — and all ~1200 containers in a raid disappeared.
    /// Containers are game-mode independent and rarely change, so they are fetched from the
    /// REST endpoints and cached in their own file.
    /// </para>
    /// <para>
    /// Sources: <c>/regular/maps</c> has <c>data.lootContainers</c> = { id: { id, name, normalizedName } }
    /// where <c>name</c> is a locale token ("&lt;id&gt; Name"); <c>/regular/maps_en</c> maps those
    /// tokens to English text (e.g. "Duffle bag").
    /// </para>
    /// </summary>
    internal static class TarkovContainerSource
    {
        private const string MapsUrl = "https://json.tarkov.dev/regular/maps";
        private const string MapsLangUrl = "https://json.tarkov.dev/regular/maps_en";
        private const string CacheFileName = "containers.json";

        private static readonly string CachePath = Path.Combine(AppContext.BaseDirectory, CacheFileName);

        /// <summary>Containers from the local cache file, or an empty list.</summary>
        public static List<TarkovMarketItem> LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath))
                    return [];
                using var fs = File.OpenRead(CachePath);
                return JsonSerializer.Deserialize<List<TarkovMarketItem>>(fs) ?? [];
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[TarkovContainerSource] Cache load failed: {ex.Message}");
                return [];
            }
        }

        /// <summary>Downloads the container list and, on success, rewrites the cache file.</summary>
        public static async Task<List<TarkovMarketItem>> FetchAsync(CancellationToken ct = default)
        {
            using var mapsDoc = await GetJsonAsync(MapsUrl, ct).ConfigureAwait(false);
            using var langDoc = await GetJsonAsync(MapsLangUrl, ct).ConfigureAwait(false);

            if (!mapsDoc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("lootContainers", out var containers)
                || containers.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("maps payload has no data.lootContainers object");

            langDoc.RootElement.TryGetProperty("data", out var lang);

            var list = new List<TarkovMarketItem>();
            foreach (var prop in containers.EnumerateObject())
            {
                var c = prop.Value;
                string id = c.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? prop.Name : prop.Name;
                string token = c.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                string normalized = c.TryGetProperty("normalizedName", out var nn) ? nn.GetString() ?? string.Empty : string.Empty;

                // Resolve the locale token; fall back to the normalized name, then the raw token.
                string name = lang.ValueKind == JsonValueKind.Object && token.Length > 0
                              && lang.TryGetProperty(token, out var text) && text.GetString() is { Length: > 0 } resolved
                    ? resolved
                    : normalized.Length > 0 ? normalized : token;

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                    continue;

                list.Add(new TarkovMarketItem
                {
                    BsgId = id,
                    Name = name,
                    ShortName = name,
                    TraderPrice = -1,
                    FleaPrice = -1,
                    Slots = 1,
                    Categories = ["Static Container"],
                });
            }

            if (list.Count > 0)
            {
                try
                {
                    await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(list), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.WriteLine($"[TarkovContainerSource] Cache save failed: {ex.Message}");
                }
            }

            return list;
        }

        private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
        {
            using var response = await SilkProgram.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException($"GET {url} → {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
    }
}
