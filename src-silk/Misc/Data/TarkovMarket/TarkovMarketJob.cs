// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Misc.Data.TarkovMarket
{
    using static TarkovDevCore;

    /// <summary>
    /// Fetches and serialises item/task/map data from the tarkov.dev GraphQL API
    /// into the same JSON shape that DEFAULT_DATA.json uses.
    /// </summary>
    internal static class TarkovMarketJob
    {
        /// <summary>
        /// Version of the data.json shape. Bump when the query gains fields the UI
        /// depends on, or when the query now pulls from a different data source —
        /// EftDataManager treats an older cached file as stale and re-fetches
        /// immediately instead of waiting out the 6h interval.
        /// v2: buyPrice/buyVendor on items, finishRewards on tasks, crafts list.
        /// v3: switched from PVP (default) to season game mode data.
        /// v4: gameMode query param changed pve -> season (new items added by the
        /// latest patch live under this mode) — a cache fetched under the old v3
        /// query still reports SchemaVersion=3, so without this bump it would never
        /// be seen as stale and users would keep the pre-patch item list until the
        /// 6h timer happened to land, showing new items as raw "Unknown &lt;bsgId&gt;"
        /// loot labels / raw "weapon_xxx" killfeed names instead of resolved names.
        /// v6: all data from json.tarkov.dev's REST API, per selectable game mode, cached in a
        /// separate file per mode, with static containers included. (A v5 data.json from an
        /// earlier REST build had no containers at all — every container in a raid vanished.)
        /// </summary>
        public const int SchemaVersion = 6;

        /// <summary>
        /// Game modes selectable in Settings, as json.tarkov.dev names them. "regular" is PvP;
        /// "pvp-season" only exists on the REST API (the GraphQL GameMode enum is regular/pve).
        /// </summary>
        public static readonly string[] AllGameModes = ["regular", "pve", "pvp-season"];

        /// <summary>Display labels for <see cref="AllGameModes"/>, same order.</summary>
        public static readonly string[] GameModeLabels = ["PvP", "PvE", "PvP Season"];

        public static async Task<string> GetUpdatedMarketDataAsync(string gameMode)
        {
            // REST for every mode. The GraphQL endpoint (api.tarkov.dev) has been answering
            // "GraphQL server unavailable. Try again later." (HTTP 422) for every query, its
            // GameMode enum has no season mode, and the old query hardcoded the invalid
            // "gameMode: season" — so it couldn't refresh at all.
            var result = await BuildJsonApiMarketDataAsync(gameMode);
            return JsonSerializer.Serialize(result);
        }

        #region json.tarkov.dev (REST) data build — used for all game modes

        /// <summary>
        /// Builds the <see cref="TarkovMarketData"/> document (the shape data.json files and
        /// DEFAULT_DATA.json use) from json.tarkov.dev's REST endpoints for one game mode.
        /// <para>
        /// Each endpoint's data is a base document full of unresolved locale tokens plus a
        /// separate <c>{endpoint}_en</c> document — a flat token→text dictionary. Every text
        /// field read here goes through <see cref="TarkovJsonApi.Resolve"/>, which is safe to
        /// call even on fields that turn out to already be plain text.
        /// </para>
        /// <para>
        /// Some fields have no verified equivalent on this API (e.g. per-item historical price
        /// points, which the old GraphQL path averaged for its flea price) and are approximated or
        /// left empty — see inline notes. Everything <see cref="EftDataManager"/> and the rest
        /// of the app actually reads (item name/price/category/icon, task objectives/zones,
        /// map extracts/transits, craft costs) is covered.
        /// </para>
        /// </summary>
        private static async Task<TarkovMarketData> BuildJsonApiMarketDataAsync(string gameMode)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            var ct = cts.Token;

            // Traders first — items/tasks/crafts all need trader-id → name to resolve their
            // own price/reward/requirement references.
            var (traders, traderNames) = await BuildJsonApiTradersAsync(gameMode, ct);

            // Maps next — tasks need map-id → {name, normalizedName} for objective/zone refs.
            // The same payload carries the static loot-container definitions.
            var (maps, mapRefs, containers) = await BuildJsonApiMapsAsync(gameMode, ct);

            var items = await BuildJsonApiItemsAsync(gameMode, traderNames, ct);
            items.AddRange(containers);
            var tasks = await BuildJsonApiTasksAsync(gameMode, traderNames, mapRefs, ct);
            var crafts = await BuildJsonApiCraftsAsync(gameMode, ct);

            return new TarkovMarketData
            {
                SchemaVersion = SchemaVersion,
                GameMode = gameMode,
                Items = items,
                Tasks = tasks,
                Maps = maps,
                Crafts = crafts,
                Traders = traders,
            };
        }

        private static async Task<(List<OutgoingTrader> traders, Dictionary<string, string> names)> BuildJsonApiTradersAsync(
            string gameMode, CancellationToken ct)
        {
            var tradersData = await TarkovJsonApi.FetchDataAsync(gameMode, "traders", ct);
            var tradersEn = await TarkovJsonApi.FetchTranslationsAsync(gameMode, "traders", "en", ct);

            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var outgoing = new List<OutgoingTrader>();

            if (tradersData.ValueKind == JsonValueKind.Object)
            {
                foreach (var tProp in tradersData.EnumerateObject())
                {
                    var name = TarkovJsonApi.Resolve(tradersEn, tProp.Value.GetToken("name"));
                    if (string.IsNullOrEmpty(name))
                        continue;

                    names[tProp.Name] = name;
                    outgoing.Add(new OutgoingTrader { Id = tProp.Name, Name = name });
                }
            }

            return (outgoing, names);
        }

        private static async Task<(List<OutgoingMap> maps, Dictionary<string, TaskElement.BasicRef> refs, List<OutgoingItem> containers)> BuildJsonApiMapsAsync(
            string gameMode, CancellationToken ct)
        {
            var mapsData = await TarkovJsonApi.FetchDataAsync(gameMode, "maps", ct);
            var mapsEn = await TarkovJsonApi.FetchTranslationsAsync(gameMode, "maps", "en", ct);

            var outgoing = new List<OutgoingMap>();
            var refs = new Dictionary<string, TaskElement.BasicRef>(StringComparer.Ordinal);

            // Static loot containers: data.lootContainers = { id: { id, name (locale token),
            // normalizedName } }. Emitted as "Static Container" items, which EftDataManager
            // routes into AllContainers — without them every container in a raid is dropped.
            var containers = new List<OutgoingItem>();
            if (mapsData.GetObj("lootContainers") is JsonElement lcObj && lcObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var cProp in lcObj.EnumerateObject())
                {
                    var name = TarkovJsonApi.Resolve(mapsEn, cProp.Value.GetToken("name"));
                    if (string.IsNullOrEmpty(name) || name.EndsWith(" Name", StringComparison.Ordinal))
                        name = cProp.Value.GetString("normalizedName", name);
                    if (string.IsNullOrEmpty(name))
                        continue;
                    containers.Add(new OutgoingItem
                    {
                        ID = cProp.Name,
                        Name = name,
                        ShortName = name,
                        TraderPrice = -1,
                        FleaPrice = -1,
                        Slots = 1,
                        Categories = ["Static Container"],
                    });
                }
            }

            if (mapsData.GetObj("maps") is JsonElement mapsObj && mapsObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var mProp in mapsObj.EnumerateObject())
                {
                    var m = mProp.Value;
                    var name = TarkovJsonApi.Resolve(mapsEn, m.GetToken("name"));
                    var normalizedName = m.GetString("normalizedName");
                    var nameId = m.GetString("nameId");

                    refs[mProp.Name] = new TaskElement.BasicRef { Id = mProp.Name, Name = name, NormalizedName = normalizedName };

                    var extracts = new List<OutgoingExtract>();
                    foreach (var ex in m.EnumerateArrayProp("extracts"))
                    {
                        extracts.Add(new OutgoingExtract
                        {
                            Name = TarkovJsonApi.Resolve(mapsEn, ex.GetToken("name")),
                            Faction = ex.GetString("faction"),
                            Position = ReadPosition(ex.GetObj("position")),
                        });
                    }

                    var transits = new List<OutgoingTransit>();
                    foreach (var tr in m.EnumerateArrayProp("transits"))
                    {
                        transits.Add(new OutgoingTransit
                        {
                            Description = TarkovJsonApi.Resolve(mapsEn, tr.GetToken("description")),
                            Position = ReadPosition(tr.GetObj("position")),
                        });
                    }

                    outgoing.Add(new OutgoingMap { Name = name, NameId = nameId, Extracts = extracts, Transits = transits });
                }
            }

            return (outgoing, refs, containers);
        }

        private static async Task<List<OutgoingItem>> BuildJsonApiItemsAsync(
            string gameMode, Dictionary<string, string> traderNames, CancellationToken ct)
        {
            var itemsData = await TarkovJsonApi.FetchDataAsync(gameMode, "items", ct);
            var itemsEn = await TarkovJsonApi.FetchTranslationsAsync(gameMode, "items", "en", ct);

            // Category id -> resolved display name (e.g. "Weapon", "Ammo"). An item's own
            // "categories" array already includes the full ancestor chain (leaf + parents),
            // matching what the GraphQL path's `categories { name }` returned — no need to
            // walk the "parent" chain ourselves.
            var categoryNames = new Dictionary<string, string>(StringComparer.Ordinal);
            if (itemsData.GetObj("itemCategories") is JsonElement catsEl && catsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var catProp in catsEl.EnumerateObject())
                    categoryNames[catProp.Name] = TarkovJsonApi.Resolve(itemsEn, catProp.Value.GetToken("name"));
            }

            var list = new List<OutgoingItem>();
            if (itemsData.GetObj("items") is JsonElement itemsObj && itemsObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var itemProp in itemsObj.EnumerateObject())
                {
                    var item = itemProp.Value;
                    var id = itemProp.Name;

                    var categories = new List<string>();
                    foreach (var catIdEl in item.EnumerateArrayProp("categories"))
                    {
                        if (catIdEl.ValueKind == JsonValueKind.String && catIdEl.GetString() is string catId
                            && categoryNames.TryGetValue(catId, out var catName))
                            categories.Add(catName);
                    }

                    // Trader sell/buy offers — unlike GraphQL's sellFor/buyFor, these arrays are
                    // trader-only; flea prices come from the item's own lastLowPrice/avg24hPrice.
                    long highestVendorPrice = 0;
                    string bestVendorName = "";
                    foreach (var offer in item.EnumerateArrayProp("sellToTrader"))
                    {
                        var priceRub = offer.GetLong("priceRUB");
                        var traderId = offer.GetString("trader");
                        if (priceRub > highestVendorPrice && !string.IsNullOrEmpty(traderId)
                            && traderNames.TryGetValue(traderId, out var tn))
                        {
                            highestVendorPrice = priceRub;
                            bestVendorName = tn;
                        }
                    }

                    long cheapestBuyPrice = 0;
                    string cheapestBuyVendor = "";
                    foreach (var offer in item.EnumerateArrayProp("buyFromTrader"))
                    {
                        var priceRub = offer.GetLong("priceRUB");
                        var traderId = offer.GetString("trader");
                        if (priceRub > 0 && !string.IsNullOrEmpty(traderId) && traderNames.TryGetValue(traderId, out var tn)
                            && (cheapestBuyPrice == 0 || priceRub < cheapestBuyPrice))
                        {
                            cheapestBuyPrice = priceRub;
                            var minLevel = offer.GetInt("minTraderLevel");
                            cheapestBuyVendor = minLevel > 1 ? $"{tn} LL{minLevel}" : tn;
                        }
                    }
                    var fleaLow = item.GetLong("lastLowPrice");
                    if (fleaLow > 0 && (cheapestBuyPrice == 0 || fleaLow < cheapestBuyPrice))
                    {
                        cheapestBuyPrice = fleaLow;
                        cheapestBuyVendor = "Flea";
                    }

                    // Optimal flea price: same tax-aware "avg24h beats base+tax" check as the
                    // GraphQL path, minus the historicalPrices fallback average — this API
                    // doesn't embed per-item price history (a separate endpoint per item id,
                    // infeasible to call for ~16k items on every refresh).
                    var basePrice = item.GetLong("basePrice");
                    var avg24h = item.GetLong("avg24hPrice");
                    long fleaPrice = 0;
                    if (basePrice > 0 && avg24h > 0 && FleaTax.Calculate(avg24h, basePrice) < avg24h)
                        fleaPrice = avg24h;

                    string? caliber = null;
                    if (item.GetObj("properties") is JsonElement props)
                    {
                        var c = props.GetString("caliber");
                        if (!string.IsNullOrEmpty(c)) caliber = c;
                    }

                    var iconLink = item.GetString("iconLink");

                    list.Add(new OutgoingItem
                    {
                        ID = id,
                        Name = TarkovJsonApi.Resolve(itemsEn, item.GetToken("name")),
                        ShortName = TarkovJsonApi.Resolve(itemsEn, item.GetToken("shortName")),
                        TraderPrice = highestVendorPrice,
                        BestTraderName = bestVendorName,
                        FleaPrice = fleaPrice,
                        Slots = Math.Max(1, item.GetInt("width", 1) * item.GetInt("height", 1)),
                        Categories = categories,
                        IconLink = iconLink,
                        IconLinkFallback = iconLink, // no separate fallback URL on this API
                        ImageLink = item.GetString("baseImageLink"),
                        Caliber = caliber,
                        BuyPrice = cheapestBuyPrice,
                        BuyVendor = cheapestBuyVendor,
                    });
                }
            }

            return list;
        }

        private static async Task<List<TaskElement>> BuildJsonApiTasksAsync(
            string gameMode, Dictionary<string, string> traderNames,
            Dictionary<string, TaskElement.BasicRef> mapRefs, CancellationToken ct)
        {
            var tasksData = await TarkovJsonApi.FetchDataAsync(gameMode, "tasks", ct);
            var tasksEn = await TarkovJsonApi.FetchTranslationsAsync(gameMode, "tasks", "en", ct);

            var list = new List<TaskElement>();
            if (tasksData.GetObj("tasks") is JsonElement tasksObj && tasksObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var tProp in tasksObj.EnumerateObject())
                {
                    var t = tProp.Value;

                    var traderId = t.GetString("trader");
                    TaskElement.TraderRef? traderRef = !string.IsNullOrEmpty(traderId) && traderNames.TryGetValue(traderId, out var trName)
                        ? new TaskElement.TraderRef { Name = trName }
                        : null;

                    var mapId = t.GetString("map");
                    TaskElement.BasicRef? mapRef = !string.IsNullOrEmpty(mapId) && mapRefs.TryGetValue(mapId, out var mr)
                        ? mr
                        : null;

                    var objectives = new List<TaskElement.ObjectiveElement>();
                    foreach (var o in t.EnumerateArrayProp("objectives"))
                        objectives.Add(BuildJsonApiObjective(o, tasksEn, mapRefs));

                    var taskRequirements = new List<TaskElement.TaskRequirementElement>();
                    foreach (var req in t.EnumerateArrayProp("taskRequirements"))
                    {
                        var reqTaskId = req.GetString("task");
                        if (string.IsNullOrEmpty(reqTaskId))
                            continue;
                        var statuses = new List<string>();
                        foreach (var s in req.EnumerateArrayProp("status"))
                            if (s.ValueKind == JsonValueKind.String && s.GetString() is string sv)
                                statuses.Add(sv);
                        taskRequirements.Add(new TaskElement.TaskRequirementElement
                        {
                            Task = new TaskElement.TaskRef { Id = reqTaskId },
                            Status = statuses,
                        });
                    }

                    list.Add(new TaskElement
                    {
                        Id = tProp.Name,
                        Name = TarkovJsonApi.Resolve(tasksEn, t.GetToken("name")),
                        NormalizedName = t.GetString("normalizedName"),
                        Trader = traderRef,
                        Map = mapRef,
                        KappaRequired = t.GetBool("kappaRequired"),
                        LightkeeperRequired = t.GetBool("lightkeeperRequired"),
                        MinPlayerLevel = t.GetInt("minPlayerLevel"),
                        Experience = t.GetInt("experience"),
                        WikiLink = t.GetString("wikiLink"),
                        FactionName = t.GetString("factionName"),
                        Objectives = objectives,
                        TaskRequirements = taskRequirements,
                        FinishRewards = BuildJsonApiRewards(t.GetObj("finishRewards"), traderNames),
                    });
                }
            }

            return list;
        }

        private static TaskElement.ObjectiveElement BuildJsonApiObjective(
            JsonElement o, Dictionary<string, string> tasksEn, Dictionary<string, TaskElement.BasicRef> mapRefs)
        {
            // The JSON API's objective "type" strings mostly match what QuestManager's
            // GetObjectiveType already expects from the GraphQL path (lowercased "giveitem",
            // "plantitem", "visit", ...) — "findItem" is the one mismatch (GraphQL used the
            // bare "find"), normalized here so the existing switch keeps working unmodified.
            var rawType = o.GetString("type");
            var type = rawType == "findItem" ? "find" : rawType;

            var maps = new List<TaskElement.BasicRef>();
            foreach (var mEl in o.EnumerateArrayProp("maps"))
                if (mEl.ValueKind == JsonValueKind.String && mEl.GetString() is string mid && mapRefs.TryGetValue(mid, out var mr))
                    maps.Add(mr);

            var zones = new List<TaskElement.ZoneElement>();
            foreach (var z in o.EnumerateArrayProp("zones"))
            {
                List<TaskElement.PositionElement>? outline = null;
                var outlinePts = z.EnumerateArrayProp("outline").ToList();
                if (outlinePts.Count > 0)
                {
                    outline = new List<TaskElement.PositionElement>(outlinePts.Count);
                    foreach (var pt in outlinePts)
                        outline.Add(new TaskElement.PositionElement { X = pt.GetFloat("x"), Y = pt.GetFloat("y"), Z = pt.GetFloat("z") });
                }

                var zoneMapId = z.GetString("map");
                TaskElement.BasicRef? zoneMap = !string.IsNullOrEmpty(zoneMapId) && mapRefs.TryGetValue(zoneMapId, out var zmr) ? zmr : null;

                zones.Add(new TaskElement.ZoneElement
                {
                    Id = z.GetString("id"),
                    Position = ReadZonePosition(z.GetObj("position")),
                    Outline = outline,
                    Map = zoneMap,
                });
            }

            // requiredKeys: array of arrays of bare item-id strings.
            var requiredKeys = new List<List<TaskElement.ItemRef>>();
            foreach (var group in o.EnumerateArrayProp("requiredKeys"))
            {
                var g = new List<TaskElement.ItemRef>();
                if (group.ValueKind == JsonValueKind.Array)
                    foreach (var k in group.EnumerateArray())
                        if (ExtractItemId(k) is string kid)
                            g.Add(new TaskElement.ItemRef { Id = kid });
                requiredKeys.Add(g);
            }

            var obj = new TaskElement.ObjectiveElement
            {
                Id = o.GetString("id"),
                Type = type,
                Optional = o.GetBool("optional"),
                Description = TarkovJsonApi.Resolve(tasksEn, o.GetToken("description")),
                Count = o.GetInt("count"),
                FoundInRaid = o.GetBool("foundInRaid"),
                Maps = maps,
                Zones = zones,
                RequiredKeys = requiredKeys,
            };

            // QuestManager only ever reads .Id off these three — never .Name/.ShortName — so a
            // bare id is all that's needed. "items" (giveItem/findItem) is an array; take the
            // first as the representative required item, matching the GraphQL path's single
            // `item` field for those objective types.
            foreach (var itemEl in o.EnumerateArrayProp("items"))
            {
                if (ExtractItemId(itemEl) is string firstItemId)
                {
                    obj.Item = new TaskElement.ItemRef { Id = firstItemId };
                    break;
                }
            }

            if (o.PropertyOrNull("questItem") is JsonElement qiEl && ExtractItemId(qiEl) is string qid)
                obj.QuestItem = new TaskElement.ItemRef { Id = qid };

            if (o.PropertyOrNull("markerItem") is JsonElement miEl && ExtractItemId(miEl) is string mid2)
                obj.MarkerItem = new TaskElement.ItemRef { Id = mid2 };

            return obj;
        }

        private static TaskElement.FinishRewardsElement? BuildJsonApiRewards(
            JsonElement? rewardsObj, Dictionary<string, string> traderNames)
        {
            if (rewardsObj is not JsonElement r || r.ValueKind != JsonValueKind.Object)
                return null;

            var items = new List<TaskElement.RewardItemElement>();
            foreach (var it in r.EnumerateArrayProp("items"))
            {
                var itemId = it.PropertyOrNull("item") is JsonElement ie ? ExtractItemId(ie) : ExtractItemId(it);
                if (itemId is null) continue;
                var count = it.ValueKind == JsonValueKind.Object && it.TryGetProperty("count", out var cEl)
                    && cEl.ValueKind == JsonValueKind.Number ? cEl.GetDouble() : 0;
                items.Add(new TaskElement.RewardItemElement { Item = new TaskElement.ItemRef { Id = itemId }, Count = count });
            }

            var standing = new List<TaskElement.TraderStandingElement>();
            foreach (var s in r.EnumerateArrayProp("traderStanding"))
            {
                var traderId = s.GetString("trader");
                if (string.IsNullOrEmpty(traderId) || !traderNames.TryGetValue(traderId, out var tn)) continue;
                standing.Add(new TaskElement.TraderStandingElement { Trader = new TaskElement.TraderRef { Name = tn }, Standing = s.GetFloat("standing") });
            }

            var offerUnlocks = new List<TaskElement.OfferUnlockElement>();
            foreach (var ou in r.EnumerateArrayProp("offerUnlock"))
            {
                var traderId = ou.GetString("trader");
                var itemId = ou.PropertyOrNull("item") is JsonElement oie ? ExtractItemId(oie) : null;
                TaskElement.TraderRef? tRef = !string.IsNullOrEmpty(traderId) && traderNames.TryGetValue(traderId, out var tn2)
                    ? new TaskElement.TraderRef { Name = tn2 } : null;
                offerUnlocks.Add(new TaskElement.OfferUnlockElement
                {
                    Trader = tRef,
                    Level = ou.GetInt("level"),
                    Item = itemId is not null ? new TaskElement.ItemRef { Id = itemId } : null,
                });
            }

            var traderUnlocks = new List<TaskElement.TraderRef>();
            foreach (var tu in r.EnumerateArrayProp("traderUnlock"))
            {
                var tuId = ExtractItemId(tu);
                if (tuId is not null && traderNames.TryGetValue(tuId, out var tun))
                    traderUnlocks.Add(new TaskElement.TraderRef { Name = tun });
            }

            return new TaskElement.FinishRewardsElement
            {
                Items = items,
                TraderStanding = standing,
                OfferUnlock = offerUnlocks,
                TraderUnlock = traderUnlocks,
            };
        }

        private static async Task<List<CraftElement>> BuildJsonApiCraftsAsync(string gameMode, CancellationToken ct)
        {
            var craftsData = await TarkovJsonApi.FetchDataAsync(gameMode, "crafts", ct);
            var hideoutData = await TarkovJsonApi.FetchDataAsync(gameMode, "hideout", ct);
            var hideoutEn = await TarkovJsonApi.FetchTranslationsAsync(gameMode, "hideout", "en", ct);

            var stationNames = new Dictionary<string, string>(StringComparer.Ordinal);
            var stationNormNames = new Dictionary<string, string>(StringComparer.Ordinal);
            if (hideoutData.ValueKind == JsonValueKind.Object)
            {
                foreach (var sProp in hideoutData.EnumerateObject())
                {
                    stationNames[sProp.Name] = TarkovJsonApi.Resolve(hideoutEn, sProp.Value.GetToken("name"));
                    stationNormNames[sProp.Name] = sProp.Value.GetString("normalizedName");
                }
            }

            var list = new List<CraftElement>();
            if (craftsData.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in craftsData.EnumerateArray())
                {
                    var stationId = c.GetString("station");
                    if (string.IsNullOrEmpty(stationId) || !stationNames.TryGetValue(stationId, out var stationName))
                        continue; // no known station — not usable by the Hideout panel

                    var required = new List<CraftElement.CraftItemElement>();
                    foreach (var ri in c.EnumerateArrayProp("requiredItems"))
                    {
                        var itemId = ri.PropertyOrNull("item") is JsonElement rie ? ExtractItemId(rie) : ExtractItemId(ri);
                        if (itemId is null) continue;
                        var count = ri.ValueKind == JsonValueKind.Object && ri.TryGetProperty("count", out var rc)
                            && rc.ValueKind == JsonValueKind.Number ? rc.GetDouble() : 0;
                        bool isTool = ri.ValueKind == JsonValueKind.Object && ri.TryGetProperty("attributes", out var attrs)
                            && attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("tool", out var toolEl)
                            && toolEl.ValueKind == JsonValueKind.True;

                        required.Add(new CraftElement.CraftItemElement
                        {
                            Item = new CraftElement.ItemRef { Id = itemId },
                            Count = count,
                            Attributes = isTool ? [new CraftElement.AttributeElement { Type = "tool", Value = "true" }] : null,
                        });
                    }

                    var reward = new List<CraftElement.CraftItemElement>();
                    if (c.TryGetProperty("productItem", out var prod) && prod.ValueKind == JsonValueKind.Object)
                    {
                        var itemId = prod.PropertyOrNull("item") is JsonElement pie ? ExtractItemId(pie) : ExtractItemId(prod);
                        if (itemId is not null)
                        {
                            var count = prod.TryGetProperty("count", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : 1;
                            reward.Add(new CraftElement.CraftItemElement { Item = new CraftElement.ItemRef { Id = itemId }, Count = count });
                        }
                    }

                    list.Add(new CraftElement
                    {
                        Station = new CraftElement.StationRef { Id = stationId, Name = stationName, NormalizedName = stationNormNames.GetValueOrDefault(stationId, "") },
                        Level = c.GetInt("level"),
                        DurationSeconds = c.GetInt("duration"),
                        RequiredItems = required,
                        RewardItems = reward,
                    });
                }
            }

            return list;
        }

        private static OutgoingPosition? ReadPosition(JsonElement? posObj) =>
            posObj is JsonElement p ? new OutgoingPosition { X = p.GetFloat("x"), Y = p.GetFloat("y"), Z = p.GetFloat("z") } : null;

        private static TaskElement.PositionElement? ReadZonePosition(JsonElement? posObj) =>
            posObj is JsonElement p ? new TaskElement.PositionElement { X = p.GetFloat("x"), Y = p.GetFloat("y"), Z = p.GetFloat("z") } : null;

        /// <summary>
        /// Extracts a bare item/trader id from either a raw string element or an object
        /// element carrying an "id"/"item" string property — the JSON API mixes both shapes
        /// across different fields (e.g. objectives' "items" is bare strings, but reward
        /// entries wrap the id in <c>{item: "...", count: N}</c>).
        /// </summary>
        private static string? ExtractItemId(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String)
                return el.GetString();
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    return idEl.GetString();
                if (el.TryGetProperty("item", out var itemEl))
                    return ExtractItemId(itemEl);
            }
            return null;
        }

        #endregion

        #region Outgoing JSON types

        private sealed class TarkovMarketData
        {
            [JsonPropertyName("schemaVersion")]
            public int SchemaVersion { get; set; }

            [JsonPropertyName("gameMode")]
            public string GameMode { get; set; } = "";

            [JsonPropertyName("items")]
            public List<OutgoingItem> Items { get; set; } = [];

            [JsonPropertyName("tasks")]
            public List<TaskElement> Tasks { get; set; } = [];

            [JsonPropertyName("maps")]
            public List<OutgoingMap> Maps { get; set; } = [];

            [JsonPropertyName("crafts")]
            public List<CraftElement> Crafts { get; set; } = [];

            [JsonPropertyName("traders")]
            public List<OutgoingTrader> Traders { get; set; } = [];
        }

        private sealed class OutgoingTrader
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
        }

        private sealed class OutgoingItem
        {
            [JsonPropertyName("bsgID")]
            public string ID { get; set; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("shortName")]
            public string ShortName { get; set; } = string.Empty;

            [JsonPropertyName("price")]
            public long TraderPrice { get; set; }

            [JsonPropertyName("traderName")]
            public string BestTraderName { get; set; } = string.Empty;

            [JsonPropertyName("fleaPrice")]
            public long FleaPrice { get; set; }

            [JsonPropertyName("slots")]
            public int Slots { get; set; }

            [JsonPropertyName("categories")]
            public List<string> Categories { get; set; } = [];

            [JsonPropertyName("iconLink")]
            public string? IconLink { get; set; }

            [JsonPropertyName("iconLinkFallback")]
            public string? IconLinkFallback { get; set; }

            [JsonPropertyName("imageLink")]
            public string? ImageLink { get; set; }

            [JsonPropertyName("caliber")]
            public string? Caliber { get; set; }

            [JsonPropertyName("buyPrice")]
            public long BuyPrice { get; set; }

            [JsonPropertyName("buyVendor")]
            public string BuyVendor { get; set; } = string.Empty;
        }

        private sealed class OutgoingMap
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("nameId")]
            public string NameId { get; set; } = string.Empty;

            [JsonPropertyName("extracts")]
            public List<OutgoingExtract> Extracts { get; set; } = [];

            [JsonPropertyName("transits")]
            public List<OutgoingTransit> Transits { get; set; } = [];
        }

        private sealed class OutgoingExtract
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("faction")]
            public string Faction { get; set; } = string.Empty;

            [JsonPropertyName("position")]
            public OutgoingPosition? Position { get; set; }
        }

        private sealed class OutgoingTransit
        {
            [JsonPropertyName("description")]
            public string Description { get; set; } = string.Empty;

            [JsonPropertyName("position")]
            public OutgoingPosition? Position { get; set; }
        }

        private sealed class OutgoingPosition
        {
            [JsonPropertyName("x")] public float X { get; set; }
            [JsonPropertyName("y")] public float Y { get; set; }
            [JsonPropertyName("z")] public float Z { get; set; }
        }

        #endregion
    }
}
