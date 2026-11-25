// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Collections.Frozen;
using System.IO;
using eft_dma_radar.Silk.Misc.Data.TarkovMarket;

namespace eft_dma_radar.Silk.Misc.Data
{
    /// <summary>
    /// Loads and caches the Tarkov item database.
    /// On startup the embedded DEFAULT_DATA.json resource is used; a data.json disk cache
    /// takes priority when present and fresh. A background API refresh runs automatically
    /// when the cached data is older than 6 hours.
    /// Call <see cref="ModuleInit"/> once at startup.
    /// </summary>
    internal static class EftDataManager
    {
        private static readonly TimeSpan DataUpdateInterval = TimeSpan.FromHours(6);

        /// <summary>
        /// Each game mode has its own cache file (data-regular.json, data-pve.json,
        /// data-pvp-season.json), so switching modes never overwrites another mode's data and
        /// switching back is instant instead of a re-download.
        /// </summary>
        private static string DataFilePathFor(string gameMode) =>
            Path.Combine(AppContext.BaseDirectory, $"data-{gameMode}.json");

        /// <summary>The selected game mode (Settings), normalized to one of <see cref="TarkovMarketJob.AllGameModes"/>.</summary>
        public static string CurrentGameMode
        {
            get
            {
                var mode = SilkProgram.Config.TarkovApiGameMode;
                return Array.IndexOf(TarkovMarketJob.AllGameModes, mode) >= 0 ? mode : "pve";
            }
        }

        /// <summary>All items keyed by BSG ID (excludes static containers).</summary>
        public static FrozenDictionary<string, TarkovMarketItem> AllItems { get; private set; }
            = FrozenDictionary<string, TarkovMarketItem>.Empty;

        /// <summary>Static containers keyed by BSG ID.</summary>
        public static FrozenDictionary<string, TarkovMarketItem> AllContainers { get; private set; }
            = FrozenDictionary<string, TarkovMarketItem>.Empty;

        /// <summary>Quest/task data keyed by task ID.</summary>
        public static FrozenDictionary<string, TaskElement> TaskData { get; private set; }
            = FrozenDictionary<string, TaskElement>.Empty;

        /// <summary>Map data (extracts, transits) keyed by map nameId.</summary>
        public static FrozenDictionary<string, MapElement> MapData { get; private set; }
            = FrozenDictionary<string, MapElement>.Empty;

        /// <summary>Trader lookup — BSG trader id mapped to display name.</summary>
        public static FrozenDictionary<string, string> AllTraders { get; private set; }
            = FrozenDictionary<string, string>.Empty;

        /// <summary>Hideout craft recipes from tarkov.dev. Empty until a v2+ data refresh lands.</summary>
        public static IReadOnlyList<CraftElement> Crafts { get; private set; } = [];

        /// <summary>State of the tarkov.dev data download, for UI status display.</summary>
        public enum RefreshState { Idle, Refreshing, Succeeded, Failed }

        /// <summary>Current download state (see <see cref="TriggerRefresh"/>).</summary>
        public static RefreshState RefreshStatus { get; private set; } = RefreshState.Idle;

        /// <summary>Last download error message, when <see cref="RefreshStatus"/> is Failed.</summary>
        public static string RefreshError { get; private set; } = string.Empty;

        // Guards against overlapping refreshes (auto + manual, or double-click).
        private static int _refreshing;

        /// <summary>
        /// Force a tarkov.dev data download now, off the 6h/auto schedule. Used by the
        /// Hideout panel's "Download now" action when crafts/prices are missing. No-op
        /// (returns false) if a refresh is already running. Result lands in
        /// <see cref="RefreshStatus"/> / the static data properties.
        /// </summary>
        public static bool TriggerRefresh()
        {
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
                return false; // already in flight

            RefreshStatus = RefreshState.Refreshing;
            RefreshError = string.Empty;
            var mode = CurrentGameMode;
            _ = Task.Run(() => RefreshFromApiAsync(mode));
            return true;
        }

        private static async Task RefreshFromApiAsync(string gameMode)
        {
            try
            {
                Log.WriteLine($"[EftDataManager] Fetching market data from tarkov.dev (gameMode={gameMode})...");
                string json = await TarkovMarketJob.GetUpdatedMarketDataAsync(gameMode);
                if (string.IsNullOrEmpty(json))
                    throw new InvalidOperationException("Empty response from tarkov.dev.");

                var updated = JsonSerializer.Deserialize<DataRoot>(json, _jsonOpts);
                if (updated?.Items is not { Count: > 0 })
                    throw new InvalidOperationException("Downloaded data had no items.");

                // Only persist after we've confirmed it parses, so a bad payload
                // can't poison the disk cache. Always written to this mode's own file.
                await File.WriteAllTextAsync(DataFilePathFor(gameMode), json);

                // The user may have switched modes while this was downloading — then this
                // mode's data is cached for later but must not replace what's on screen.
                if (gameMode == CurrentGameMode)
                    ApplyData(updated);
                RefreshStatus = RefreshState.Succeeded;
                Log.WriteLine($"[EftDataManager] Market data updated for {gameMode} ({updated.Items.Count} items, {updated.Crafts.Count} crafts).");
            }
            catch (Exception ex)
            {
                RefreshStatus = RefreshState.Failed;
                RefreshError = ex.Message;
                Log.WriteLine($"[EftDataManager] Market data update failed for {gameMode}: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }

            // A mode switch during this download couldn't start its own refresh (single
            // flight) — catch up now if the newly selected mode's cache needs one.
            var current = CurrentGameMode;
            if (current != gameMode && IsCacheStale(current, ReadCache(current)))
                TriggerRefresh();
        }

        /// <summary>
        /// Switches the data set to <paramref name="gameMode"/>: loads that mode's own cache
        /// immediately (if any) and downloads it when missing or stale. Other modes' caches
        /// are left untouched.
        /// </summary>
        public static void SwitchGameMode(string gameMode)
        {
            if (Array.IndexOf(TarkovMarketJob.AllGameModes, gameMode) < 0)
                return;
            SilkProgram.Config.TarkovApiGameMode = gameMode;
            SilkProgram.Config.MarkDirty();

            var data = ReadCache(gameMode);
            if (data is not null)
            {
                ApplyData(data);
                Log.WriteLine($"[EftDataManager] Switched to {gameMode} — loaded its cached data.");
            }
            else
            {
                Log.WriteLine($"[EftDataManager] Switched to {gameMode} — no cached data yet, downloading.");
            }

            if (IsCacheStale(gameMode, data))
                TriggerRefresh();
        }

        /// <summary>Reads <paramref name="gameMode"/>'s cache file, or null if missing/unusable.</summary>
        private static DataRoot? ReadCache(string gameMode)
        {
            var path = DataFilePathFor(gameMode);
            if (!File.Exists(path))
                return null;
            try
            {
                using var fs = File.OpenRead(path);
                var data = JsonSerializer.Deserialize<DataRoot>(fs, _jsonOpts);
                return data?.Items is { Count: > 0 } ? data : null;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[EftDataManager] Cache {Path.GetFileName(path)} invalid: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// A mode's cache needs a download when it's missing, older than the update interval,
        /// or from an older data schema (new fields the UI depends on).
        /// </summary>
        private static bool IsCacheStale(string gameMode, DataRoot? cached)
        {
            var path = DataFilePathFor(gameMode);
            return cached is null
                || !File.Exists(path)
                || (DateTime.Now - File.GetLastWriteTime(path)) > DataUpdateInterval
                || cached.SchemaVersion < TarkovMarketJob.SchemaVersion;
        }

        /// <summary>
        /// Loads the item database at startup.
        /// Priority: disk cache → embedded resource.
        /// A background refresh is queued when the cache is stale.
        /// </summary>
        internal static void ModuleInit()
        {
            var mode = CurrentGameMode;

            // 1. Try this mode's disk cache
            DataRoot? cached = ReadCache(mode);
            DataRoot? data = cached;
            if (data is not null)
                Log.WriteLine($"[EftDataManager] Loaded {mode} data from disk cache.");

            // 2. Fall back to embedded resource
            if (data is null)
            {
                const string resourceName = "eft_dma_radar.Silk.DEFAULT_DATA.json";
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                    if (stream is null)
                    {
                        Log.WriteLine($"[EftDataManager] Embedded resource '{resourceName}' not found.");
                    }
                    else
                    {
                        data = JsonSerializer.Deserialize<DataRoot>(stream, _jsonOpts);
                        if (data?.Items is null || data.Items.Count == 0)
                            data = null;
                        else
                            Log.WriteLine("[EftDataManager] Loaded data from embedded resource.");
                    }
                }
                catch (Exception ex)
                {
                    Log.WriteLine($"[EftDataManager] Failed to load embedded data: {ex.Message}");
                }
            }

            if (data is not null)
                ApplyData(data);

            // 3. Schedule a background download when this mode's cache is missing or stale
            // (the embedded fallback data never counts as fresh).
            if (IsCacheStale(mode, cached))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000); // let the app finish starting
                    TriggerRefresh();
                });
            }
        }

        private static int _containerRefreshRunning;

        /// <summary>
        /// Fetches static-container definitions from tarkov.dev's REST data and merges them into
        /// <see cref="AllContainers"/> (existing entries kept). One fetch in flight at a time.
        /// </summary>
        private static void RefreshContainersInBackground()
        {
            if (Interlocked.Exchange(ref _containerRefreshRunning, 1) == 1)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var fetched = await TarkovContainerSource.FetchAsync().ConfigureAwait(false);
                    var merged = new Dictionary<string, TarkovMarketItem>(AllContainers, StringComparer.Ordinal);
                    foreach (var c in fetched)
                        merged.TryAdd(c.BsgId, c);
                    AllContainers = merged.ToFrozenDictionary(StringComparer.Ordinal);
                    Log.WriteLine($"[EftDataManager] Containers loaded from tarkov.dev REST data: {fetched.Count} fetched, {AllContainers.Count} total.");
                }
                catch (Exception ex)
                {
                    Log.WriteLine($"[EftDataManager] Container refresh failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _containerRefreshRunning, 0);
                }
            });
        }

        private static void ApplyData(DataRoot data)
        {
            var itemBuilder = new Dictionary<string, TarkovMarketItem>(data.Items.Count, StringComparer.Ordinal);
            var containerBuilder = new Dictionary<string, TarkovMarketItem>(64, StringComparer.Ordinal);
            foreach (var item in data.Items)
            {
                if (string.IsNullOrEmpty(item.BsgId))
                    continue;
                if (item.IsStaticContainer)
                    containerBuilder.TryAdd(item.BsgId, item);
                else
                    itemBuilder.TryAdd(item.BsgId, item);
            }
            // Containers also come from their own REST-sourced cache (TarkovContainerSource), so a
            // GraphQL outage or a data.json written without them doesn't hide every container.
            // Entries from the main data win; the REST set only fills gaps.
            foreach (var c in TarkovContainerSource.LoadCache())
                containerBuilder.TryAdd(c.BsgId, c);

            AllItems = itemBuilder.ToFrozenDictionary(StringComparer.Ordinal);
            AllContainers = containerBuilder.ToFrozenDictionary(StringComparer.Ordinal);
            Log.WriteLine($"[EftDataManager] {AllItems.Count} items, {AllContainers.Count} containers.");

            if (AllContainers.Count == 0)
                RefreshContainersInBackground();

            if (data.Tasks is { Count: > 0 })
            {
                var taskBuilder = new Dictionary<string, TaskElement>(data.Tasks.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var task in data.Tasks)
                    if (!string.IsNullOrEmpty(task.Id))
                        taskBuilder.TryAdd(task.Id, task);
                TaskData = taskBuilder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
                Log.WriteLine($"[EftDataManager] {TaskData.Count} tasks.");
            }

            if (data.Maps is { Count: > 0 })
            {
                var mapBuilder = new Dictionary<string, MapElement>(data.Maps.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var map in data.Maps)
                    if (!string.IsNullOrEmpty(map.NameId))
                        mapBuilder.TryAdd(map.NameId, map);
                MapData = mapBuilder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
                Log.WriteLine($"[EftDataManager] {MapData.Count} map configs.");
            }

            if (data.Traders is { Count: > 0 })
            {
                var traderBuilder = new Dictionary<string, string>(data.Traders.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var t in data.Traders)
                    if (!string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(t.Name))
                        traderBuilder.TryAdd(t.Id, t.Name);
                AllTraders = traderBuilder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
                Log.WriteLine($"[EftDataManager] {AllTraders.Count} traders.");
            }

            if (data.Crafts is { Count: > 0 })
            {
                Crafts = data.Crafts;
                Log.WriteLine($"[EftDataManager] {Crafts.Count} crafts.");
            }
        }

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private sealed class DataRoot
        {
            [JsonPropertyName("schemaVersion")]
            public int SchemaVersion { get; set; }

            [JsonPropertyName("gameMode")]
            public string GameMode { get; set; } = string.Empty;

            [JsonPropertyName("items")]
            public List<TarkovMarketItem> Items { get; set; } = [];

            [JsonPropertyName("crafts")]
            public List<CraftElement> Crafts { get; set; } = [];

            [JsonPropertyName("tasks")]
            public List<TaskElement> Tasks { get; set; } = [];

            [JsonPropertyName("maps")]
            public List<MapElement> Maps { get; set; } = [];

            [JsonPropertyName("traders")]
            public List<TraderEntry> Traders { get; set; } = [];
        }

        private sealed class TraderEntry
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
        }

        #region Map Data Models

        internal sealed class MapElement
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("nameId")]
            public string NameId { get; set; } = string.Empty;

            [JsonPropertyName("extracts")]
            public List<ExtractElement> Extracts { get; set; } = [];

            [JsonPropertyName("transits")]
            public List<TransitElement> Transits { get; set; } = [];
        }

        internal sealed class ExtractElement
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("faction")]
            public string Faction { get; set; } = string.Empty;

            [JsonPropertyName("position")]
            public MapPositionElement? Position { get; set; }
        }

        internal sealed class TransitElement
        {
            [JsonPropertyName("description")]
            public string Description { get; set; } = string.Empty;

            [JsonPropertyName("position")]
            public MapPositionElement? Position { get; set; }
        }

        internal sealed class MapPositionElement
        {
            [JsonPropertyName("x")]
            public float X { get; set; }

            [JsonPropertyName("y")]
            public float Y { get; set; }

            [JsonPropertyName("z")]
            public float Z { get; set; }

            public Vector3 ToVector3() => new(X, Y, Z);
        }

        #endregion
    }
}
