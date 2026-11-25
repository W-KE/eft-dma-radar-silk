// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using UTF8String = eft_dma_radar.Silk.Misc.UTF8String;
using System.IO;
using eft_dma_radar.Silk.DMA.ScatterAPI;
using VmmSharpEx.Options;

namespace eft_dma_radar.Silk.Tarkov.Unity.IL2CPP
{
    public static partial class Il2CppDumper
    {
        // ── IL2CPP struct field offsets ──────────────────────────────────────────
        private const uint K_Name = 0x10;   // char*    Il2CppClass::name
        private const uint K_Fields = 0x80;   // FieldInfo*  (direct array)
        private const uint K_Methods = 0x98;   // MethodInfo** (array of pointers)
        private const uint K_MethodCount = 0x120;  // uint16
        private const uint K_FieldCount = 0x124;  // uint16

        private const int MaxClasses = 80_000;
        private const int MaxNameLen = 256;

        // ── Scatter-read raw structs ─────────────────────────────────────────────

        /// <summary>
        /// Contiguous name + namespace pointers read from Il2CppClass at offset 0x10.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ClassNamePtrs
        {
            public ulong NamePtr;      // 0x10  char* name
            public ulong NamespacePtr; // 0x18  char* namespaze
        }

        /// <summary>
        /// Raw FieldInfo entry (0x20 bytes stride). Only the fields we need are mapped.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 0x20)]
        private struct RawFieldInfo
        {
            [FieldOffset(0x00)] public ulong NamePtr; // char* name
            [FieldOffset(0x08)] public ulong TypePtr; // Il2CppType*
            [FieldOffset(0x18)] public int Offset;  // int32 offset (signed!)
        }

        /// <summary>
        /// Raw MethodInfo header. We read 0x20 bytes starting at the MethodInfo address
        /// to capture the method pointer (0x00) and name pointer (0x18).
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 0x20)]
        private struct RawMethodInfo
        {
            [FieldOffset(0x00)] public ulong MethodPointer; // void* methodPointer
            [FieldOffset(0x18)] public ulong NamePtr;       // char* name
        }

        // ── Run-once guard ────────────────────────────────────────────────────────

        /// <summary>
        /// Set to <c>true</c> after the first successful dump (live or from cache).
        /// Prevents re-running the expensive type-table scan on subsequent game
        /// restarts within the same process lifetime — the resolved offsets are
        /// already in memory and the TypeInfoTable may no longer be readable.
        /// </summary>
        private static volatile bool _dumped;

        // ── Entry point ──────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves IL2CPP offsets at runtime and applies them to
        /// <see cref="Offsets"/> via reflection. Hardcoded defaults in SDK.cs
        /// serve as fallback for any field that cannot be resolved.
        /// 
        /// Runs only once per process lifetime: after a successful dump the
        /// results are persisted to <c>il2cpp_offsets.json</c> next to the
        /// executable.  On subsequent calls (e.g. game restarts) the cache is
        /// loaded instead of re-reading the TypeInfoTable, which may no longer
        /// be accessible after the first ~10 minutes in-game.
        /// </summary>
        public static void Dump()
        {

            if (_dumped)
            {
                Log.WriteLine("[Il2CppDumper] Already dumped this session — skipping.");
                return;
            }

            Log.WriteLine("[Il2CppDumper] Dump starting...");

            var gaBase = Memory.GameAssemblyBase;
            if (gaBase == 0)
            {
                Log.WriteLine("[Il2CppDumper] ERROR: GameAssemblyBase is 0 — game not ready.");
                Log.WriteLine("IL2CPP dump failed: GameAssembly not found.");
                return;
            }

            // ── Fastest path: PE fingerprint match ──────────────────────────────
            // If the GameAssembly.dll binary hasn't changed since the last dump,
            // skip the expensive TypeInfoTableRva sig scan entirely and restore
            // all offsets directly from the cache file.
            if (TryFastLoadCache(gaBase))
            {
                _dumped = true;
                return;
            }

            // Live path from here on. Flush VMM's page-table / TLB / memory caches first:
            // NOCACHE reads only bypass the data cache, not stale address translations, so
            // pages the game mapped since the last refresh (type table chunks, metadata
            // strings) could otherwise fail to read and silently drop classes / fields.
            try
            {
                Memory.FullRefresh();
                Log.WriteLine("[Il2CppDumper] VMM caches refreshed before live dump.");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[Il2CppDumper] VMM refresh before dump failed: {ex.Message}");
            }

            // Dynamically resolve TypeInfoTableRva via sig scan (falls back to hardcoded).
            // We must do this even for the cache path so we have the RVA fingerprint
            // needed to validate whether the cache matches the current game build.
            // The IL2CPP runtime may not have populated the TypeInfoTable yet if the
            // radar started before the game, so retry with increasing delays.
            const int maxRvaRetries = 30;
            bool rvaResolved = false;

            // Same game binary as the cache (PE fingerprint match): the TypeInfoTable RVA can't
            // have changed, so use it directly when it validates. The signature scan reads the
            // whole GameAssembly module and, started mid-raid under DMA contention, took ~100 s
            // to re-find this exact same RVA.
            if (CachedRvaMatchesBinary
                && Offsets.Special.TypeInfoTableRva != 0
                && ValidateTypeInfoTable(gaBase, Offsets.Special.TypeInfoTableRva))
            {
                Log.WriteLine($"[Il2CppDumper] TypeInfoTable RVA 0x{Offsets.Special.TypeInfoTableRva:X} from the cache of this game binary validated — signature scan skipped.");
                _lastResolutionMode = "cache (same binary)";
                rvaResolved = true;
            }

            for (int rvaAttempt = 1; !rvaResolved && rvaAttempt <= maxRvaRetries; rvaAttempt++)
            {
                if (ResolveTypeInfoTableRva(gaBase, quiet: rvaAttempt < maxRvaRetries))
                {
                    rvaResolved = true;
                    break;
                }

                if (rvaAttempt < maxRvaRetries)
                {
                    int delay = rvaAttempt <= 10 ? 1000 : 2000;
                    Log.WriteLine($"[Il2CppDumper] TypeInfoTable not ready, retrying in {delay}ms... ({rvaAttempt}/{maxRvaRetries})");
                    Thread.Sleep(delay);
                }
            }

            if (!rvaResolved)
            {
                Log.WriteLine("[Il2CppDumper] TypeInfoTable resolution failed after all retries.");
                if (TryLoadCacheStale())
                {
                    _dumped = true;
                    Log.WriteLine("[Il2CppDumper] Using last cached offsets as fallback.");
                    return;
                }
                Log.WriteLine("[Il2CppDumper] No cache available — falling back to compiled-in offsets.");
                return;
            }

            // ── Fast path: load from cache ───────────────────────────────────────
            // If the cache was written against the same TypeInfoTableRva (i.e. the
            // same game binary), skip the expensive live memory read entirely.
            if (TryLoadCache(Offsets.Special.TypeInfoTableRva))
            {
                _dumped = true;
                Log.WriteLine("[Il2CppDumper] Offsets restored from cache — live dump skipped.");

                // Re-save so the PE fingerprint is persisted for the fast-path
                // (TryFastLoadCache) on the next startup. TryLoadCache accepts complete or
                // stable caches; re-save it unchanged so a stable one stays stable.
                SaveCache(LoadedUnresolvedCount, LoadedUnresolvedKeys, LoadedNonImprovingDumps);
                return;
            }

            // ── Live path: read TypeInfoTable from memory ────────────────────────

            // Resolve the type-info table pointer once — used by both paths.
            ulong tablePtr = 0;
            bool tableOk = false;
            try
            {
                tablePtr = Memory.ReadPtr(gaBase + Offsets.Special.TypeInfoTableRva, false);
                tableOk = eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(tablePtr);
                if (!tableOk)
                    Log.WriteLine("[Il2CppDumper] TypeInfoTable pointer is invalid.");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[Il2CppDumper] ReadPtr(TypeInfoTableRva) failed: {ex.Message}");
            }

            if (!tableOk)
            {
                if (TryLoadCacheStale())
                {
                    _dumped = true;
                    Log.WriteLine("[Il2CppDumper] Using last cached offsets as fallback.");
                    return;
                }
                Log.WriteLine("[Il2CppDumper] No cache available — falling back to compiled-in offsets.");
                return;
            }

            // Scan the full type table — needed for name lookups AND TypeIndex resolution.
            // Retry a few times for transient DMA failures during loading. Beyond 3 tries
            // the table is almost certainly stale/corrupt, not just slow — additional
            // attempts just delay startup, so we fall back instead.
            const int MinExpectedClasses = 1_000;
            const int maxRetries = 3;
            List<(string Name, string Namespace, ulong KlassPtr, int Index)> classes = [];

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                classes = ReadAllClassesFromTable(tablePtr);

                if (classes.Count >= MinExpectedClasses)
                    break;

                if (attempt < maxRetries)
                {
                    Log.WriteLine($"[Il2CppDumper] Only {classes.Count} classes found (expected ≥{MinExpectedClasses}), retrying... ({attempt}/{maxRetries})");
                    Thread.Sleep(1000);
                }
            }

            // Sanity gate: a healthy IL2CPP binary has tens of thousands of classes.
            // If we found very few, the table pointer is likely stale or corrupt.
            if (classes.Count < MinExpectedClasses)
            {
                Log.WriteLine($"[Il2CppDumper] Live dump failed: only {classes.Count} classes found (expected ≥{MinExpectedClasses}) after {maxRetries} attempts.");

                // Last-resort fallback: try the saved cache even if the RVA didn't match.
                // Better to use slightly stale offsets than fully default ones.
                if (TryLoadCacheStale())
                {
                    _dumped = true;
                    Log.WriteLine("[Il2CppDumper] Using last cached offsets as fallback.");
                    return;
                }

                Log.WriteLine("[Il2CppDumper] No cache available — falling back to compiled-in offsets.");
                return;
            }

            var nameLookup = new Dictionary<string, ulong>(classes.Count * 2, StringComparer.Ordinal);
            var nameToIndex = new Dictionary<string, int>(classes.Count * 2, StringComparer.Ordinal);

            // Dedup numbering: when multiple classes share the same sanitized base name,
            // the first is keyed as "World", the second as "World_2", third as "World_3", etc.
            // This matches the C++ AppSDK naming convention used by the schema.
            var baseNameSeen = new Dictionary<string, int>(classes.Count, StringComparer.Ordinal);

            // Namespace-qualified keys ("EFT.Player"). Short names are ambiguous — several
            // EFT classes share a short name across namespaces and the short-name lookup below
            // is first-wins in table order — so schema entries can name a class by its full
            // name to pin the right one. These keys can't collide with short / sanitized keys
            // (those never contain '.').
            var fullNameLookup = new Dictionary<string, ulong>(classes.Count, StringComparer.Ordinal);
            foreach (var (name, ns, ptr, _) in classes)
            {
                if (!string.IsNullOrEmpty(ns))
                    fullNameLookup.TryAdd($"{ns}.{name}", ptr);
            }

            foreach (var (name, _, ptr, idx) in classes)
            {
                var san = SanitizeName(name);

                // Index by raw name and sanitized name (first-wins via TryAdd).
                nameLookup.TryAdd(name, ptr);
                nameToIndex.TryAdd(name, idx);
                if (san != name)
                {
                    nameLookup.TryAdd(san, ptr);
                    nameToIndex.TryAdd(san, idx);
                }

                // Dedup numbering by sanitized base name:
                // First "World" → key "World", second → "World_2", third → "World_3", etc.
                if (baseNameSeen.TryGetValue(san, out int seen))
                {
                    int next = seen + 1;
                    baseNameSeen[san] = next;
                    var dedupKey = $"{san}_{next}";
                    nameLookup.TryAdd(dedupKey, ptr);
                    nameToIndex.TryAdd(dedupKey, idx);
                }
                else
                {
                    baseNameSeen[san] = 1;
                }
            }

            // Dynamically resolve TypeIndex values for known singleton classes.
            ResolveTypeIndices(nameToIndex, classes);

            // Build schema AFTER TypeIndex resolution so it picks up updated values.
            var schema = BuildSchema();

            // Reflection: locate nested types inside Offsets once.
            var offsetsType = typeof(Offsets);
            const BindingFlags bf = BindingFlags.Public | BindingFlags.Static;

            int updated = 0, fallback = 0, classesSkipped = 0;
            // Names of everything left unresolved — lets the cache tell "genuinely absent in this
            // game build" (same set every dump) from "unreadable this time" (set varies).
            var unresolvedKeys = new List<string>();

            foreach (var sc in schema)
            {
                ulong klassPtr;
                string resolvedVia;

                if (sc.TypeIndex.HasValue)
                {
                    klassPtr = ReadPtr(tablePtr + (ulong)sc.TypeIndex.Value * 8UL);
                    resolvedVia = $"TypeIndex={sc.TypeIndex.Value}";

                    if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(klassPtr))
                    {
                        Log.WriteLine($"[Il2CppDumper] SKIP '{sc.CsName}': TypeIndex={sc.TypeIndex.Value} resolved to invalid pointer.");
                        classesSkipped++;
                        unresolvedKeys.Add($"class {sc.CsName}:{sc.Il2CppName}");
                        continue;
                    }
                }
                else if (sc.ResolveViaChild is not null)
                {
                    // Generic parent resolution: find the concrete child class, then
                    // walk Il2CppClass::parent until we find the target class name.
                    if (!nameLookup.TryGetValue(sc.ResolveViaChild, out var childKlass))
                    {
                        Log.WriteLine($"[Il2CppDumper] SKIP '{sc.CsName}': child class '{sc.ResolveViaChild}' not found in type table.");
                        classesSkipped++;
                        unresolvedKeys.Add($"class {sc.CsName}:{sc.Il2CppName}");
                        continue;
                    }

                    klassPtr = 0;
                    ulong walkPtr = childKlass;
                    const int MaxParentDepth = 16;
                    for (int depth = 0; depth < MaxParentDepth && eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(walkPtr); depth++)
                    {
                        ulong parentPtr = ReadPtr(walkPtr + Offsets.Il2CppClass.Parent);
                        if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(parentPtr))
                            break;

                        ulong parentNamePtr = ReadPtr(parentPtr + K_Name);
                        string? parentName = ReadStr(parentNamePtr);

                        if (parentName != null && parentName == sc.Il2CppName)
                        {
                            klassPtr = parentPtr;
                            break;
                        }

                        walkPtr = parentPtr;
                    }

                    if (klassPtr == 0)
                    {
                        Log.WriteLine($"[Il2CppDumper] SKIP '{sc.CsName}': parent '{sc.Il2CppName}' not found in parent chain of '{sc.ResolveViaChild}'.");
                        classesSkipped++;
                        unresolvedKeys.Add($"class {sc.CsName}:{sc.Il2CppName}");
                        continue;
                    }

                    resolvedVia = $"child=\"{sc.ResolveViaChild}\"→parent=\"{sc.Il2CppName}\"";
                }
                else if (sc.Il2CppName.Contains('.'))
                {
                    // Namespace-qualified name: exact match first. If the class moved namespace
                    // in a game update, fall back to the short name (first-wins) and say so.
                    if (fullNameLookup.TryGetValue(sc.Il2CppName, out klassPtr))
                    {
                        resolvedVia = $"fullname=\"{sc.Il2CppName}\"";
                    }
                    else
                    {
                        var shortName = sc.Il2CppName[(sc.Il2CppName.LastIndexOf('.') + 1)..];
                        if (!nameLookup.TryGetValue(shortName, out klassPtr))
                        {
                            Log.WriteLine($"[Il2CppDumper] SKIP '{sc.Il2CppName}': not found in type table (neither full nor short name).");
                            classesSkipped++;
                            unresolvedKeys.Add($"class {sc.CsName}:{sc.Il2CppName}");
                            continue;
                        }
                        Log.WriteLine($"[Il2CppDumper] WARN: '{sc.Il2CppName}' not found by full name — using first class named '{shortName}' (namespace may have changed).");
                        resolvedVia = $"name=\"{shortName}\" (full-name miss)";
                    }
                }
                else
                {
                    if (!nameLookup.TryGetValue(sc.Il2CppName, out klassPtr))
                    {
                        Log.WriteLine($"[Il2CppDumper] SKIP '{sc.Il2CppName}': not found in type table.");
                        classesSkipped++;
                        unresolvedKeys.Add($"class {sc.CsName}:{sc.Il2CppName}");
                        continue;
                    }
                    resolvedVia = $"name=\"{sc.Il2CppName}\"";
                }

                // Find the target struct in Offsets via reflection.
                var nestedType = offsetsType.GetNestedType(sc.CsName, BindingFlags.Public | BindingFlags.NonPublic);
                if (nestedType is null)
                {
                    Log.WriteLine($"[Il2CppDumper] WARN: struct Offsets.{sc.CsName} not found via reflection — skipping.");
                    classesSkipped++;
                    unresolvedKeys.Add($"class {sc.CsName}:{sc.Il2CppName}");
                    continue;
                }

                var fieldMap = ReadClassFields(klassPtr);
                var methodMap = sc.Fields.Any(sf => sf.Kind == FieldKind.MethodRva)
                    ? ReadClassMethods(klassPtr, gaBase)
                    : null;

                foreach (var sf in sc.Fields)
                {
                    if (sf.Kind == FieldKind.MethodRva)
                    {
                        var methodName = sf.Il2CppName.EndsWith("_RVA", StringComparison.Ordinal)
                            ? sf.Il2CppName[..^4]
                            : sf.Il2CppName;

                        if (methodMap is not null && methodMap.TryGetValue(methodName, out var rva))
                        {
                            if (TrySetField(nestedType, sf.CsName, rva, bf))
                                updated++;
                            else
                            {
                                fallback++;
                                unresolvedKeys.Add($"{sc.CsName}.{sf.CsName}");
                            }
                        }
                        else
                        {
                            Log.WriteLine($"[Il2CppDumper] WARN: method '{methodName}' not found in '{sc.CsName}' — using fallback.");
                            fallback++;
                            unresolvedKeys.Add($"{sc.CsName}.{sf.CsName}");
                        }
                    }
                    else
                    {
                        int offset = 0;
                        bool found = false;
                        foreach (var candidate in FieldNameCandidates(sf.Il2CppName))
                        {
                            if (fieldMap.TryGetValue(candidate, out offset))
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                        {
                            Log.WriteLine($"[Il2CppDumper] WARN: field '{sf.Il2CppName}' not found in '{sc.CsName}' ({resolvedVia}, {fieldMap.Count} fields incl. inherited) — using fallback.");
                            fallback++;
                            unresolvedKeys.Add($"{sc.CsName}.{sf.CsName}");
                            continue;
                        }

                        // FieldInfo::offset is signed. Positive → uint, negative → int.
                        object value = offset >= 0 ? (object)(uint)offset : (object)offset;
                        if (TrySetField(nestedType, sf.CsName, value, bf))
                            updated++;
                        else
                        {
                            fallback++;
                            unresolvedKeys.Add($"{sc.CsName}.{sf.CsName}");
                        }
                    }
                }
            }

            DebugDumpResolverState(classes.Count, updated, fallback, classesSkipped);
            Log.WriteLine($"[Il2CppDumper] Done. {updated} offsets updated, {fallback} fallback, {classesSkipped} skipped.");

            if (fallback > 0 || classesSkipped > 0)
                Log.WriteLine($"IL2CPP dump partial: {fallback} fallback offset(s), {classesSkipped} class(es) skipped. Check logs.");

            // Persist to cache so future game restarts (where the TypeInfoTable may
            // no longer be readable) can skip the live dump entirely.
            _dumped = true;

            // Record how much was left unresolved, so an incomplete cache is re-dumped next
            // start instead of being served by the fast path as if it were complete. Never let
            // a worse dump replace a better cache for the same game binary + radar build
            // (e.g. a later run where part of the type table wasn't readable) — keep and apply
            // the better one instead.
            int unresolved = fallback + classesSkipped;
            // A dump that doesn't improve on this game build's cache (equal or more unresolved)
            // counts toward marking the cache stable; see OffsetCache.Stable.
            var previous = ExistingUnresolvedForCurrentBuild();
            if (previous is { } prev && prev.Unresolved < unresolved)
            {
                Log.WriteLine($"[Il2CppDumper] This dump left {unresolved} unresolved but the cached one for this build left {prev.Unresolved} — keeping and applying the cache.");
                // Re-save the kept cache under this radar build (MVID). Without this the cache
                // kept its old MVID, so every start saw "radar binary rebuilt", re-dumped (badly,
                // mid-raid) and kept it again — forever.
                if (TryLoadCacheStale())
                    SaveCache(LoadedUnresolvedCount, LoadedUnresolvedKeys, prev.NonImproving + 1);
            }
            else if (previous is { } same && same.Unresolved == unresolved)
            {
                SaveCache(unresolved, unresolvedKeys, same.NonImproving + 1);
            }
            else
            {
                SaveCache(unresolved, unresolvedKeys, 0);
            }
        }

        // ── Reflection helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Attempts to set a static field on a type via reflection.
        /// Handles uint/ulong/int type conversion automatically.
        /// For const fields (IsLiteral), skips silently (cannot set at runtime).
        /// For uint[] fields (deref chains), updates only the first element.
        /// </summary>
        private static bool TrySetField(Type type, string fieldName, object value, BindingFlags bf)
        {
            var fi = type.GetField(fieldName, bf);
            if (fi is null)
            {
                Log.WriteLine($"[Il2CppDumper] WARN: field '{fieldName}' not found on '{type.Name}' via reflection.");
                return false;
            }

            // const (literal) fields cannot be changed at runtime — skip silently.
            if (fi.IsLiteral)
                return true;

            try
            {
                // Convert the dumped value to the declared field type.
                var target = fi.FieldType;
                object converted;

                if (target == typeof(uint))
                    converted = Convert.ToUInt32(value);
                else if (target == typeof(ulong))
                    converted = Convert.ToUInt64(value);
                else if (target == typeof(int))
                    converted = Convert.ToInt32(value);
                else if (target == typeof(uint[]))
                {
                    // Deref-chain field: update only the first element with the dumped offset.
                    var arr = (uint[]?)fi.GetValue(null);
                    if (arr is not null && arr.Length > 0)
                    {
                        arr[0] = Convert.ToUInt32(value);
                        return true; // array is reference type — already mutated in place
                    }
                    return false;
                }
                else
                {
                    Log.WriteLine($"[Il2CppDumper] WARN: unsupported field type '{target}' for '{type.Name}.{fieldName}'.");
                    return false;
                }

                fi.SetValue(null, converted);
                return true;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[Il2CppDumper] ERROR: Failed to set '{type.Name}.{fieldName}': {ex.Message}");
                return false;
            }
        }

        // ── Memory helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Reads all IL2CppClass* entries from a pre-resolved type-info table pointer.
        /// Uses scatter reads to batch all DMA operations (2 scatter rounds instead of ~4 reads per class).
        /// Reads the pointer array in chunks to avoid oversized DMA reads during early loading.
        /// </summary>
        private static List<(string Name, string Namespace, ulong KlassPtr, int Index)> ReadAllClassesFromTable(ulong tablePtr)
        {
            var result = new List<(string, string, ulong, int)>(4096);

            // Step 1: Read all class pointers, one page (512 slots) per scatter entry.
            // Previously each 4096-slot chunk was a single ReadArray that threw if ANY of its
            // 8 pages was unreadable — and that ended the whole scan ("break"), silently losing
            // every class after it (one run only got 8192 of ~28k slots, so EFT.ProfileInfo
            // went missing and PlayerInfo.Side fell back to a stale default). Now an unreadable
            // page just leaves its 512 slots empty, and the scan only stops once a whole chunk
            // is empty (past the end of the table) — confirmed by a second empty chunk so a
            // single unreadable chunk can't end it early.
            const int slotsPerPage = PageSize / 8;
            const int chunkSize = 4096;
            var allPtrs = new List<ulong>(MaxClasses);
            int unreadablePages = 0, emptyChunks = 0;

            for (int offset = 0; offset < MaxClasses; offset += chunkSize)
            {
                int toRead = Math.Min(chunkSize, MaxClasses - offset);
                var chunk = new ulong[toRead];
                using (var s = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int p = 0; p < toRead; p += slotsPerPage)
                        s.PrepareReadArray<ulong>(tablePtr + (ulong)(offset + p) * 8, Math.Min(slotsPerPage, toRead - p));
                    TryExecute(s);
                    for (int p = 0; p < toRead; p += slotsPerPage)
                    {
                        var span = chunk.AsSpan(p, Math.Min(slotsPerPage, toRead - p));
                        if (!s.ReadSpan(tablePtr + (ulong)(offset + p) * 8, span))
                        {
                            span.Clear();
                            unreadablePages++;
                        }
                    }
                }

                bool hasValid = false;
                for (int i = 0; i < chunk.Length && !hasValid; i++)
                    hasValid = eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(chunk[i]);

                allPtrs.AddRange(chunk);

                if (hasValid)
                {
                    emptyChunks = 0;
                }
                else if (++emptyChunks >= 2)
                {
                    break; // two empty chunks in a row: past the end of the table
                }
            }

            if (unreadablePages > 0)
                Log.WriteLine($"[Il2CppDumper] Type table: {unreadablePages} page(s) of class pointers were unreadable (skipped, scan continued).");

            if (allPtrs.Count == 0)
                return result;

            var ptrs = allPtrs;

            // Collect indices of valid class pointers.
            var validIndices = new List<int>(ptrs.Count / 2);
            for (int i = 0; i < ptrs.Count; i++)
            {
                if (eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(ptrs[i]))
                    validIndices.Add(i);
            }

            if (validIndices.Count == 0)
                return result;

            // Step 2: Read name_ptr + namespace_ptr for every valid class (one 16-byte read each),
            // in batches of ScatterBatchSize (capped at 256 reads per scatter
            // — a single ~28k-read scatter dropped reads), with one retry round for whatever failed.
            var headers = new ClassNamePtrs[validIndices.Count];
            var headerOk = new bool[validIndices.Count];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int start = 0; start < validIndices.Count; start += ScatterBatchSize)
                {
                    int end = Math.Min(start + ScatterBatchSize, validIndices.Count);
                    using var s = Memory.GetScatter(VmmFlags.NOCACHE);
                    bool any = false;
                    for (int j = start; j < end; j++)
                    {
                        if (headerOk[j]) continue;
                        s.PrepareReadValue<ClassNamePtrs>(ptrs[validIndices[j]] + K_Name);
                        any = true;
                    }
                    if (!any) continue;
                    TryExecute(s);
                    for (int j = start; j < end; j++)
                    {
                        if (headerOk[j]) continue;
                        headerOk[j] = s.ReadValue(ptrs[validIndices[j]] + K_Name, out headers[j]);
                    }
                }
            }

            // Step 3: Read all name and namespace strings (page-boundary tolerant, batched, retried).
            var strAddrs = new ulong[validIndices.Count * 2];
            int headerFailures = 0;
            for (int j = 0; j < validIndices.Count; j++)
            {
                if (!headerOk[j])
                {
                    headerFailures++;
                    continue;
                }
                strAddrs[j * 2] = headers[j].NamePtr;
                strAddrs[j * 2 + 1] = headers[j].NamespacePtr;
            }

            var strs = ScatterReadNames(strAddrs);

            // Step 4: Build results.
            int unreadableNames = 0;
            for (int j = 0; j < validIndices.Count; j++)
            {
                int i = validIndices[j];

                string? name = strs[j * 2];
                if (string.IsNullOrEmpty(name))
                {
                    unreadableNames++;
                    continue;
                }

                result.Add((name, strs[j * 2 + 1] ?? string.Empty, ptrs[i], i));
            }

            Log.WriteLine($"[Il2CppDumper] Type table: {allPtrs.Count} slots read, {validIndices.Count} class pointers, " +
                          $"{result.Count} named, {unreadableNames} with unreadable name ({headerFailures} of them: class header unreadable).");

            return result;
        }

        private const ushort FIELD_ATTRIBUTE_STATIC = 0x10;
        private const ushort FIELD_ATTRIBUTE_LITERAL = 0x40;

        /// <summary>
        /// Reads the class's own fields, then walks <c>Il2CppClass::parent</c> and adds the
        /// <b>instance</b> fields declared on base classes (child wins on a name clash).
        /// Instance-field offsets are absolute within the object, so an inherited field's
        /// offset is valid for the derived class as-is. Static fields of base classes are
        /// skipped: their offsets index the <i>base</i> class's static storage, not this one's.
        /// </summary>
        private static Dictionary<string, int> ReadClassFields(ulong klassPtr)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            var visited = new HashSet<ulong>();
            ulong klass = klassPtr;

            for (int depth = 0; depth < 32 && eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(klass) && visited.Add(klass); depth++)
            {
                ReadDeclaredFields(klass, result, instanceOnly: depth > 0);
                klass = ReadPtr(klass + Offsets.Il2CppClass.Parent);
            }

            return result;
        }

        private static void ReadDeclaredFields(ulong klassPtr, Dictionary<string, int> result, bool instanceOnly)
        {
            if (!Memory.TryReadValue<ushort>(klassPtr + K_FieldCount, out var fieldCount, false)) return;
            if (fieldCount == 0 || fieldCount > 4096) return;

            var fieldsBase = ReadPtr(klassPtr + K_Fields);
            if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(fieldsBase)) return;

            // Bulk read the entire field array in one DMA operation.
            RawFieldInfo[] rawFields;
            try { rawFields = Memory.ReadArray<RawFieldInfo>(fieldsBase, fieldCount, false); }
            catch { return; }

            // For base classes we need each field's Il2CppType::attrs to tell static from instance.
            ScatterReadEntry<RawIl2CppType>?[]? typeEntries = null;
            if (instanceOnly)
            {
                typeEntries = new ScatterReadEntry<RawIl2CppType>?[rawFields.Length];
                var batch = new List<IScatterEntry>(rawFields.Length);
                for (int i = 0; i < rawFields.Length; i++)
                {
                    if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(rawFields[i].TypePtr)) continue;
                    typeEntries[i] = ScatterReadEntry<RawIl2CppType>.Get(rawFields[i].TypePtr, 0);
                    batch.Add(typeEntries[i]!);
                }
                if (batch.Count > 0)
                    Memory.ReadScatter([.. batch], false);
            }

            var nameAddrs = new ulong[rawFields.Length];
            for (int i = 0; i < rawFields.Length; i++)
                nameAddrs[i] = rawFields[i].NamePtr;
            var names = ScatterReadNames(nameAddrs);

            for (int i = 0; i < rawFields.Length; i++)
            {
                string? name = names[i];
                if (string.IsNullOrEmpty(name)) continue;

                if (typeEntries is not null)
                {
                    var te = typeEntries[i];
                    if (te is null || te.IsFailed) continue; // can't prove it's an instance field
                    var attrs = te.Result.Attrs;
                    if ((attrs & (FIELD_ATTRIBUTE_STATIC | FIELD_ATTRIBUTE_LITERAL)) != 0) continue;
                    if (rawFields[i].Offset < 0) continue;
                }

                result.TryAdd(name, rawFields[i].Offset);
            }
        }

        /// <summary>
        /// Name variants to try for a schema field, mirroring the IL2CPP naming conventions:
        /// <c>&lt;X&gt;k__BackingField</c> ↔ <c>_X_k__BackingField</c> ↔ <c>_X</c> ↔ <c>X</c>.
        /// The exact name is always tried first.
        /// </summary>
        private static IEnumerable<string> FieldNameCandidates(string name)
        {
            yield return name;

            var flipped = FlipBackingFieldConvention(name);
            if (flipped is not null)
                yield return flipped;

            const string suffix = ">k__BackingField";
            if (name.Length > suffix.Length + 1 && name[0] == '<' && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                var prop = name[1..^suffix.Length];
                yield return "_" + prop;
                yield return prop;
            }
            else if (name.Length > 1 && name[0] == '_' && !name.EndsWith("k__BackingField", StringComparison.Ordinal))
            {
                var prop = name[1..];
                yield return prop;
                yield return $"<{prop}>k__BackingField";
            }
        }

        private static Dictionary<string, ulong> ReadClassMethods(ulong klassPtr, ulong gaBase)
        {
            var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var methodCount = Memory.ReadValue<ushort>(klassPtr + K_MethodCount, false);
            if (methodCount == 0 || methodCount > 4096) return result;

            var methodsBase = ReadPtr(klassPtr + K_Methods);
            if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(methodsBase)) return result;

            ulong[] methodPtrs;
            try { methodPtrs = Memory.ReadArray<ulong>(methodsBase, methodCount, false); }
            catch { return result; }

            // Scatter read MethodPointer + NamePtr for all methods in one batch.
            var infoEntries = new ScatterReadEntry<RawMethodInfo>[methodPtrs.Length];
            var scatter1 = new List<IScatterEntry>(methodPtrs.Length);

            for (int i = 0; i < methodPtrs.Length; i++)
            {
                if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(methodPtrs[i])) continue;
                infoEntries[i] = ScatterReadEntry<RawMethodInfo>.Get(methodPtrs[i], 0);
                scatter1.Add(infoEntries[i]);
            }

            if (scatter1.Count > 0)
                Memory.ReadScatter([.. scatter1], false);

            // Read all method name strings (page-boundary tolerant, batched).
            var nameAddrs = new ulong[methodPtrs.Length];
            for (int i = 0; i < methodPtrs.Length; i++)
            {
                if (infoEntries[i] is null || infoEntries[i].IsFailed) continue;

                ref var info = ref infoEntries[i].Result;
                if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(info.MethodPointer) || info.MethodPointer < gaBase) continue;
                nameAddrs[i] = info.NamePtr;
            }

            var names = ScatterReadNames(nameAddrs);

            // Build results.
            for (int i = 0; i < methodPtrs.Length; i++)
            {
                string? name = names[i];
                if (string.IsNullOrEmpty(name)) continue;

                var rva = infoEntries[i].Result.MethodPointer - gaBase;
                result.TryAdd(name, rva);
            }

            return result;
        }

        // ── String / pointer helpers ─────────────────────────────────────────────

        /// <summary>
        /// Converts between the two IL2CPP backing field naming conventions:
        ///   "&lt;Name&gt;k__BackingField"  ↔  "_Name_k__BackingField"
        /// Returns null if the input is not a backing field name.
        /// </summary>
        private static string? FlipBackingFieldConvention(string name)
        {
            const string suffix = "k__BackingField";
            if (!name.EndsWith(suffix, StringComparison.Ordinal))
                return null;

            if (name.Length > suffix.Length + 2 && name[0] == '<')
            {
                // <Name>k__BackingField → _Name_k__BackingField
                var inner = name[1..name.IndexOf('>')];
                return $"_{inner}_{suffix}";
            }

            if (name.Length > suffix.Length + 2 && name[0] == '_')
            {
                // _Name_k__BackingField → <Name>k__BackingField
                var inner = name[1..^suffix.Length];
                if (inner.EndsWith('_'))
                    inner = inner[..^1];
                return $"<{inner}>{suffix}";
            }

            return null;
        }

        #region DumpClassFields

        /// <summary>
        /// Diagnostic helper: reads the IL2CPP klass pointer from an object instance,
        /// walks the entire inheritance chain, and logs every field with its offset,
        /// IL2CPP type name, field name, and live value read from the object instance.
        /// <para>
        /// Output format matches <c>DEBUG_OUTPUT_REFERENCE.md</c> §3:
        /// <code>
        /// ── Fields of 'label' @ 0xADDR (full hierarchy) ──
        ///   ┌ ClassName (klass=0xPTR, N field(s))
        ///   │  [0xOFFSET] type  name = value
        /// </code>
        /// </para>
        /// </summary>
        public static void DumpClassFields(ulong objectAddress, string? label = null)
        {
            try
            {
                if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(objectAddress))
                {
                    Log.WriteLine($"[Il2CppDumper] DumpClassFields: invalid object address 0x{objectAddress:X}");
                    return;
                }

                // Il2CppObject layout: first 8 bytes = klass pointer
                ulong klassPtr = ReadPtr(objectAddress);
                if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(klassPtr))
                {
                    Log.WriteLine($"[Il2CppDumper] DumpClassFields: invalid klass pointer at 0x{objectAddress:X}");
                    return;
                }

                // Read top-level class name for the header
                ulong topNamePtr = ReadPtr(klassPtr + K_Name);
                string topClassName = ReadStr(topNamePtr) ?? "<unknown>";
                var tag = label ?? topClassName;

                Log.WriteLine($"[Il2CppDumper] ── Fields of '{tag}' @ 0x{objectAddress:X} (full hierarchy) ──");

                // Walk the parent chain: klass → parent → parent → ... → null
                const int MaxDepth = 32;
                int depth = 0;
                ulong currentKlass = klassPtr;

                while (eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(currentKlass) && depth < MaxDepth)
                {
                    depth++;
                    DumpSingleClassFieldsWithValues(currentKlass, objectAddress);
                    currentKlass = ReadPtr(currentKlass + Offsets.Il2CppClass.Parent);
                }

                Log.WriteLine($"[Il2CppDumper] ── End of '{tag}' ({depth} class(es) in hierarchy) ──");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[Il2CppDumper] DumpClassFields error: {ex.Message}");
            }
        }

        /// <summary>
        /// Dumps all fields declared on a single Il2CppClass with their types and
        /// live values read from the object instance at <paramref name="objectAddress"/>.
        /// </summary>
        private static void DumpSingleClassFieldsWithValues(ulong klassPtr, ulong objectAddress)
        {
            // Read class name + namespace
            ulong namePtr = ReadPtr(klassPtr + K_Name);
            string className = ReadStr(namePtr) ?? "<unknown>";

            ulong nsPtr = ReadPtr(klassPtr + 0x18); // Il2CppClass::namespaze
            string ns = ReadStr(nsPtr) ?? string.Empty;
            string fullName = string.IsNullOrEmpty(ns) ? className : $"{ns}.{className}";

            var fieldCount = Memory.ReadValue<ushort>(klassPtr + K_FieldCount, false);

            Log.WriteLine($"[Il2CppDumper]   ┌ {fullName} (klass=0x{klassPtr:X}, {fieldCount} field(s))");

            if (fieldCount == 0 || fieldCount > 4096)
                return;

            ulong fieldsBase = ReadPtr(klassPtr + K_Fields);
            if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(fieldsBase))
            {
                Log.WriteLine($"[Il2CppDumper]   │  (fields pointer invalid)");
                return;
            }

            RawFieldInfoFull[] rawFields;
            try { rawFields = Memory.ReadArray<RawFieldInfoFull>(fieldsBase, fieldCount, false); }
            catch (Exception ex)
            {
                Log.WriteLine($"[Il2CppDumper]   │  (failed to read field array: {ex.Message})");
                return;
            }

            // Scatter read: field name strings + Il2CppType structs
            var nameEntries = new ScatterReadEntry<UTF8String>[rawFields.Length];
            var typeEntries = new ScatterReadEntry<RawIl2CppType>[rawFields.Length];
            var scatter = new List<IScatterEntry>(rawFields.Length * 2);

            for (int i = 0; i < rawFields.Length; i++)
            {
                if (eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(rawFields[i].NamePtr))
                {
                    nameEntries[i] = ScatterReadEntry<UTF8String>.Get(rawFields[i].NamePtr, MaxNameLen);
                    scatter.Add(nameEntries[i]);
                }
                if (eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(rawFields[i].TypePtr))
                {
                    typeEntries[i] = ScatterReadEntry<RawIl2CppType>.Get(rawFields[i].TypePtr, 0);
                    scatter.Add(typeEntries[i]);
                }
            }

            if (scatter.Count > 0)
                Memory.ReadScatter([.. scatter], false);

            for (int i = 0; i < rawFields.Length; i++)
            {
                string? name = nameEntries[i] is not null && !nameEntries[i].IsFailed
                    ? (string?)(UTF8String?)nameEntries[i].Result
                    : "<unreadable>";

                // Resolve type name
                string typeName = "?";
                byte typeEnum = 0;
                if (typeEntries[i] is not null && !typeEntries[i].IsFailed)
                {
                    ref var t = ref typeEntries[i].Result;
                    typeEnum = t.TypeEnum;
                    typeName = Il2CppTypeEnumName(typeEnum);
                }

                int offset = rawFields[i].Offset;

                // Read live value from the object instance
                string valueStr;
                if (offset < 0)
                {
                    // Static field — offset is into the static fields region, not the instance
                    valueStr = "(static)";
                }
                else
                {
                    valueStr = ReadFieldValueString(objectAddress, (uint)offset, typeEnum);
                }

                Log.WriteLine($"[Il2CppDumper]   │  [0x{(uint)offset:X}] {typeName,-12} {name} = {valueStr}");
            }
        }

        /// <summary>
        /// Reads a live field value from an object instance and formats it as a string.
        /// </summary>
        private static string ReadFieldValueString(ulong objectAddress, uint offset, byte typeEnum)
        {
            try
            {
                ulong addr = objectAddress + offset;
                return typeEnum switch
                {
                    0x02 => Memory.ReadValue<bool>(addr, false).ToString().ToLowerInvariant(),             // bool
                    0x03 => $"'{(char)Memory.ReadValue<ushort>(addr, false)}'",                             // char
                    0x04 => Memory.ReadValue<sbyte>(addr, false).ToString(),                                // sbyte
                    0x05 => $"0x{Memory.ReadValue<byte>(addr, false):X2}",                                  // byte
                    0x06 => Memory.ReadValue<short>(addr, false).ToString(),                                // short
                    0x07 => $"0x{Memory.ReadValue<ushort>(addr, false):X4}",                                // ushort
                    0x08 => Memory.ReadValue<int>(addr, false).ToString(),                                  // int
                    0x09 => $"0x{Memory.ReadValue<uint>(addr, false):X}",                                   // uint
                    0x0A => Memory.ReadValue<long>(addr, false).ToString(),                                 // long
                    0x0B => $"0x{Memory.ReadValue<ulong>(addr, false):X}",                                  // ulong
                    0x0C => Memory.ReadValue<float>(addr, false).ToString("G6"),                            // float
                    0x0D => Memory.ReadValue<double>(addr, false).ToString("G6"),                           // double
                    0x0E => ReadStringFieldValue(addr),                                                     // string
                    0x12 or 0x15 or 0x1D or 0x14 => ReadPointerFieldValue(addr),                           // class, generic<>, [], [,]
                    0x18 => $"0x{Memory.ReadValue<ulong>(addr, false):X}",                                  // IntPtr
                    _ => ReadPointerOrValueFieldValue(addr),                                                // valuetype, enum, unknown
                };
            }
            catch
            {
                return "<read failed>";
            }
        }

        /// <summary>Reads a string field: dereferences the pointer and reads the Unity string.</summary>
        private static string ReadStringFieldValue(ulong addr)
        {
            var ptr = ReadPtr(addr);
            if (ptr == 0) return "null";
            if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(ptr)) return $"<bad ptr 0x{ptr:X}>";
            try
            {
                var s = Memory.ReadUnityString(ptr, 128, false);
                return $"\"{s}\"";
            }
            catch
            {
                return $"0x{ptr:X}";
            }
        }

        /// <summary>Reads a pointer field (class, generic, array).</summary>
        private static string ReadPointerFieldValue(ulong addr)
        {
            var ptr = ReadPtr(addr);
            return ptr == 0 ? "null" : $"0x{ptr:X}";
        }

        /// <summary>Reads a field as a pointer first; if it looks like a small value, shows as int.</summary>
        private static string ReadPointerOrValueFieldValue(ulong addr)
        {
            var raw = Memory.ReadValue<ulong>(addr, false);
            // Heuristic: if the value fits in 32 bits and isn't a valid VA, show as int
            if (raw <= uint.MaxValue)
                return $"{(int)(uint)raw}";
            if (eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(raw))
                return $"0x{raw:X}";
            return $"0x{raw:X}";
        }

        #endregion

        // ── Name-string reads ───────────────────────────────────────────────────
        //
        // Class / namespace / field / method names are NUL-terminated C strings inside
        // global-metadata. A fixed-size read (MaxNameLen) that runs past the end of the
        // string's page fails as a whole whenever the *next* page isn't readable (not
        // resident / not mapped) — even though the string itself sits entirely in the
        // first page. That silently dropped random class and field names, which showed
        // up as "SKIP ... not found in type table" and "field ... not found" for classes
        // and fields that do exist. So: read only up to the end of the string's page
        // first, and only strings that genuinely cross the page boundary (no NUL in the
        // first part) get a second, full-length read.

        private const int PageSize = 0x1000;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PageClampedNameLen(ulong addr) =>
            (int)Math.Min((ulong)MaxNameLen, PageSize - (addr & (PageSize - 1)));

        private static string? DecodeName(ReadOnlySpan<byte> buf, out bool terminated)
        {
            int nul = buf.IndexOf((byte)0);
            terminated = nul >= 0;
            if (nul <= 0) return null; // empty, or no terminator in what we read
            return Encoding.UTF8.GetString(buf[..nul]);
        }

        /// <summary>
        /// Batched, page-boundary-tolerant read of NUL-terminated name strings.
        /// Invalid addresses and unreadable names come back as <c>null</c>.
        /// </summary>
        /// <summary>Max reads per scatter batch for the type-table walk — capped at 256 requests per scatter; 2048-read batches still dropped ~25% of names.</summary>
        private const int ScatterBatchSize = 256;

        /// <summary>
        /// VmmScatter.Execute throws when the whole batch fails; treat that as "every read in
        /// this batch failed" (subsequent ReadSpan/ReadValue calls then return false) rather
        /// than aborting the dump.
        /// </summary>
        private static void TryExecute(VmmSharpEx.Scatter.VmmScatter s)
        {
            try { s.Execute(); }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Debug, "il2cpp_scatter_exec", TimeSpan.FromSeconds(5),
                    $"[Il2CppDumper] Scatter batch failed: {ex.Message}");
            }
        }

        private static string?[] ScatterReadNames(IReadOnlyList<ulong> addrs)
        {
            var result = new string?[addrs.Count];
            if (addrs.Count == 0) return result;

            var pending = new List<int>(addrs.Count);
            for (int i = 0; i < addrs.Count; i++)
                if (eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(addrs[i]))
                    pending.Add(i);

            // Two passes: anything that failed the first time (transient scatter failure) is retried once.
            for (int pass = 0; pass < 2 && pending.Count > 0; pass++)
            {
                for (int start = 0; start < pending.Count; start += ScatterBatchSize)
                {
                    int end = Math.Min(start + ScatterBatchSize, pending.Count);
                    ReadNamesBatch(addrs, pending, start, end, result);
                }
                // Keep only the ones still unresolved for the retry pass. (This used to be
                // inverted — it dropped the failures and re-read the successes, and a re-read that
                // came back empty then wiped names that had been read fine.)
                pending.RemoveAll(i => result[i] is not null);
            }

            return result;
        }

        private static void ReadNamesBatch(IReadOnlyList<ulong> addrs, List<int> idx, int start, int end, string?[] result)
        {
            var crossing = new List<int>();
            Span<byte> buf = stackalloc byte[MaxNameLen];

            // Round 1: read each name only up to the end of its page.
            using (var s = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int k = start; k < end; k++)
                {
                    var a = addrs[idx[k]];
                    s.PrepareRead(a, (uint)PageClampedNameLen(a));
                }
                TryExecute(s);

                for (int k = start; k < end; k++)
                {
                    int i = idx[k];
                    var a = addrs[i];
                    int len = PageClampedNameLen(a);
                    var span = buf[..len];
                    span.Clear();
                    if (!s.ReadSpan(a, span)) continue;
                    var name = DecodeName(span, out bool terminated);
                    if (name is not null)
                        result[i] = name; // never overwrite a name with null
                    if (!terminated && len < MaxNameLen)
                        crossing.Add(i);
                }
            }

            // Round 2: names that run onto the next page — read the full length.
            if (crossing.Count > 0)
            {
                using var s = Memory.GetScatter(VmmFlags.NOCACHE);
                foreach (var i in crossing)
                    s.PrepareRead(addrs[i], (uint)MaxNameLen);
                TryExecute(s);
                foreach (var i in crossing)
                {
                    buf.Clear();
                    if (s.ReadSpan(addrs[i], buf) && DecodeName(buf, out _) is string full)
                        result[i] = full;
                }
            }
        }

        private static ulong ReadPtr(ulong addr)
        {
            if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(addr)) return 0;
            try { return Memory.ReadValue<ulong>(addr, false); }
            catch { return 0; }
        }

        private static string? ReadStr(ulong addr)
        {
            if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(addr)) return null;
            // Same page-boundary tolerance as ScatterReadNames: try the in-page part first.
            Span<byte> buf = stackalloc byte[MaxNameLen];
            int len = PageClampedNameLen(addr);
            if (Memory.TryReadBuffer(addr, buf[..len], false))
            {
                var name = DecodeName(buf[..len], out bool terminated);
                if (terminated || len >= MaxNameLen)
                    return name;
            }
            buf.Clear();
            return Memory.TryReadBuffer(addr, buf, false) ? DecodeName(buf, out _) : null;
        }

        /// <summary>
        /// Replaces non-alphanumeric/non-underscore characters with '_'.
        /// e.g. "World`2" → "World_2", "SlotView`2" → "SlotView_2"
        /// </summary>
        private static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new char[name.Length];
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                sb[i] = char.IsLetterOrDigit(c) || c == '_' ? c : '_';
            }
            return new string(sb);
        }

    }
}
