// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

#pragma warning disable IDE0130
using UTF8String = eft_dma_radar.Silk.Misc.UTF8String;

namespace eft_dma_radar.Silk.Tarkov.Unity.IL2CPP
{
    public static partial class Il2CppDumper
    {
        // ── Constants ────────────────────────────────────────────────────────

        /// <summary>
        /// Cap on sites returned per signature scan. The broadest pattern
        /// (<c>48 89 05 ? ? ? ?</c>) matches thousands of sites in GameAssembly.dll, and at
        /// the old cap of 1024 the scan was silently truncated — the log for the
        /// 2022.3.43.16329911 build reported exactly <c>matches=1024</c>, i.e. the real
        /// table's site may never have been considered.
        /// </summary>
        private const int MaxSigMatches = 8192;

        // Depth estimation — used to pick between candidates that all pass validation.
        private const int DepthProbeStart = 1024;
        private const int DepthProbeMax = 262_144;
        private const int DepthProbeWindow = 8;
        private const int DepthProbeRequired = 3;
        private const int DepthProbeResolution = 512;
        private const int EarlyProbeCount = 16;
        private const int EarlyProbeRequired = 8;
        private const int MidProbeOffset = 5_000;
        private const int MidProbeCount = 8;
        private const int MidProbeRequired = 3;
        private const string GameAssemblyName = "GameAssembly.dll";
        private const string LogTag = "[Il2CppDumper]";

        // ── Signatures & TypeIndex map ──────────────────────────────────────

        // (Sig, RelOffset, InstrLen, Desc) — RelOffset/InstrLen used for RIP-relative decode
        private static readonly (string Sig, int RelOffset, int InstrLen, string Desc)[] TypeInfoTableSigs =
        [
            ("48 8B 05 ? ? ? ? ? ? ? ? ? ? ? 90 48 85 DB 75 ? 48 8D 2D ? ? ? ? 48 89 6C 24 ? 48 8B CD E8 ? ? ? ? 90 ? ? ? 48 85 DB 75 ? 8B CF", 3, 7, "read: mov rax,[rip+rel32] (table lookup)"),
            ("48 89 05 ? ? ? ? 48 8B 05 ? ? ? ? 8B 48", 3, 7, "write: mov [rip+rel32],rax (init store)"),
            ("48 89 05 ? ? ? ? 48 8B 05", 3, 7, "write: mov [rip+rel32],rax; mov rax,[rip+rel32] (minimal)"),
            ("48 89 05 ? ? ? ?", 3, 7, "write: mov [rip+rel32],rax; mov rax,[rip+rel32] (minimal)"),
        ];

        private static readonly (string Il2CppName, string FieldName)[] TypeIndexMap =
        [
            ("EFTHardSettings",       nameof(Offsets.Special.EFTHardSettings_TypeIndex)),
            ("GPUInstancerManager",   nameof(Offsets.Special.GPUInstancerManager_TypeIndex)),
            ("WeatherController",     nameof(Offsets.Special.WeatherController_TypeIndex)),
            ("GlobalConfiguration",   nameof(Offsets.Special.GlobalConfiguration_TypeIndex)),
            ("MatchingProgress",      nameof(Offsets.Special.MatchingProgress_TypeIndex)),
            ("MatchingProgressView",  nameof(Offsets.Special.MatchingProgressView_TypeIndex)),
            ("GamePlayerOwner",       nameof(Offsets.Special.GamePlayerOwner_TypeIndex)),
            ("TarkovApplication",     nameof(Offsets.Special.TarkovApplication_TypeIndex)),
            ("EFT.Hideout.HideoutArea",       nameof(Offsets.Special.HideoutArea_TypeIndex)),
            ("EFT.Hideout.HideoutController", nameof(Offsets.Special.HideoutController_TypeIndex)),
            ("BtrController",         nameof(Offsets.Special.BtrController_TypeIndex)),
            ("CameraManager",         nameof(Offsets.Special.CameraManager_TypeIndex)),
        ];

        // ── State ────────────────────────────────────────────────────────────

        private static readonly FieldInfo[] CachedTypeIndexFields =
            typeof(Offsets.Special).GetFields(BindingFlags.Public | BindingFlags.Static);

        private record struct SigScanResult(int Index, string Desc, string State, int Matches, int ValidMatches, ulong Rva);

        private static SigScanResult[] _lastSigResults = [];
        private static string _lastResolutionMode = "not run";

        // ── Resolution pipeline ──────────────────────────────────────────────

        private static bool ResolveTypeInfoTableRva(ulong gaBase, bool quiet = false)
        {
            var testedRvas = new HashSet<ulong>();
            var sigResults = new List<SigScanResult>(TypeInfoTableSigs.Length);

            for (int i = 0; i < TypeInfoTableSigs.Length; i++)
            {
                // The last signature ("48 89 05 ? ? ? ?") is a very broad 7-byte pattern with
                // thousands of raw matches in GameAssembly.dll. FindSignatures scans the module
                // in 16MB DMA reads and only stops once MaxSigMatches is reached
                // (VmmExtensions.FindSignatures) — collecting 8192 matches instead of a few
                // costs several extra large reads EVERY call. That's fine once, but this whole
                // method re-runs on every "IL2CPP not ready yet" startup retry (up to 30 times,
                // Il2CppDumper.cs), turning a several-second cost into several minutes.
                // Every build observed this session had sigs[0..2] alone reliably find the
                // correct table RVA (raw match counts of 1, 1, and 33) — this broad signature
                // has never once contributed the winning candidate, only occasionally a
                // shallower wrong one that EstimateTableDepth correctly discards anyway. Skip
                // it once an earlier signature already produced a validated candidate; keep it
                // as a genuine fallback for a hypothetical future build where the narrower
                // signatures stop matching entirely.
                bool isExpensiveBroadFallback = i == TypeInfoTableSigs.Length - 1;
                if (isExpensiveBroadFallback && testedRvas.Count > 0)
                {
                    sigResults.Add(new SigScanResult(i, TypeInfoTableSigs[i].Desc, "SKIPPED", 0, 0, 0));
                    continue;
                }

                var (sig, relOff, instrLen, desc) = TypeInfoTableSigs[i];
                var (_, scanResult) = TryResolveFromSignature(i, sig, relOff, instrLen, desc, gaBase, testedRvas);
                sigResults.Add(scanResult);
            }

            // Pick the DEEPEST table, not the first one that validates.
            //
            // ValidateTypeInfoTable only probes indices 0..16 and ~5000, which every
            // plausible candidate clears — so "first valid wins" was a lottery decided by
            // signature order and by which sites survived the match cap. On the
            // 2022.3.43.16329911 build that lottery picked 0x6FF7728, a table that runs out
            // after ~29.5k classes, over the real one at 0x6FF7A30 with ~46.8k. The
            // symptom was 78 classes "not found in type table" — including third-party
            // ones like TOD_Time and GPUInstancerRuntimeData that BSG never obfuscates,
            // which is what gives a truncated table away.
            //
            // Entry count is the discriminator: the real table is the biggest one.
            (ulong rva, int depth)? best = null;
            foreach (var rva in testedRvas)
            {
                int depth = EstimateTableDepth(gaBase, rva);
                Log.WriteLine($"{LogTag} TypeInfoTable candidate 0x{rva:X} — ~{depth} entries.");
                if (best is null || depth > best.Value.depth)
                    best = (rva, depth);
            }

            bool success;
            if (best.HasValue)
            {
                var prev = Offsets.Special.TypeInfoTableRva;
                Offsets.Special.TypeInfoTableRva = best.Value.rva;
                Log.WriteLine($"{LogTag} TypeInfoTable resolved: rva=0x{best.Value.rva:X}, " +
                              $"unique={testedRvas.Count}, depth=~{best.Value.depth}");
                if (prev != best.Value.rva)
                    Log.WriteLine($"{LogTag} TypeInfoTableRva UPDATED: 0x{prev:X} → 0x{best.Value.rva:X}");
                _lastResolutionMode = testedRvas.Count > 1 ? $"signature (best of {testedRvas.Count})" : "signature";
                success = true;
            }
            else if (Offsets.Special.TypeInfoTableRva != 0 && ValidateTypeInfoTable(gaBase, Offsets.Special.TypeInfoTableRva))
            {
                Log.WriteLine($"{LogTag} TypeInfoTable using fallback RVA: 0x{Offsets.Special.TypeInfoTableRva:X}");
                _lastResolutionMode = "fallback (hardcoded)";
                success = true;
            }
            else
            {
                if (!quiet)
                    Log.WriteLine($"{LogTag} WARNING: All TypeInfoTable resolution strategies failed — offsets may be stale!");
                _lastResolutionMode = "FAILED";
                success = false;
            }

            _lastSigResults = [.. sigResults];
            return success;
        }

        private static ((ulong rva, ulong sigAddr, string sig)?, SigScanResult) TryResolveFromSignature(
            int index, string sig, int relOff, int instrLen, string desc, ulong gaBase, HashSet<ulong> testedRvas)
        {
            ulong[] sigAddrs;
            try
            {
                sigAddrs = Memory.FindSignatures(sig, GameAssemblyName, MaxSigMatches);
            }
            catch (Exception ex)
            {
                Log.WriteLine($"{LogTag} TypeInfoTable sig[{index}] scan error: {ex.Message}");
                return (null, new SigScanResult(index, desc, "ERROR", 0, 0, 0));
            }

            if (sigAddrs.Length == 0)
                return (null, new SigScanResult(index, desc, "MISS", 0, 0, 0));

            // Resolve every site's RIP-relative target, then validate only the DISTINCT
            // RVAs. The broad signature matches thousands of sites but they collapse to a
            // handful of globals, so deduplicating first is what keeps a large match cap
            // affordable — validation is the expensive part, not the displacement read.
            //
            // The displacements are batched into one scatter: at one sequential DMA read
            // per site, an 8k cap would otherwise add a minute to startup.
            var candidateRvas = new List<ulong>();
            var seenHere = new HashSet<ulong>();
            try
            {
                using var scatter = Memory.GetScatter(VmmSharpEx.Options.VmmFlags.NOCACHE);
                foreach (var sigAddr in sigAddrs)
                    scatter.PrepareReadValue<int>(sigAddr + (ulong)relOff);
                scatter.Execute();

                foreach (var sigAddr in sigAddrs)
                {
                    if (!scatter.ReadValue<int>(sigAddr + (ulong)relOff, out int rel))
                        continue;

                    ulong globalVa = sigAddr + (ulong)instrLen + (ulong)(long)rel;
                    if (globalVa <= gaBase)
                        continue;

                    ulong rva = globalVa - gaBase;
                    if (seenHere.Add(rva))
                        candidateRvas.Add(rva);
                }
            }
            catch
            {
                // Scatter unavailable — fall back to per-site reads.
                foreach (var sigAddr in sigAddrs)
                {
                    var rva = ResolveRipRelativeRva(sigAddr, relOff, instrLen, gaBase);
                    if (rva != 0 && seenHere.Add(rva))
                        candidateRvas.Add(rva);
                }
            }

            // Validate every distinct candidate; the caller scores them and keeps the
            // deepest table rather than the first that happens to pass.
            ulong duplicateRva = 0, firstNewRva = 0;
            int validCount = 0;
            foreach (var rva in candidateRvas)
            {
                if (!ValidateTypeInfoTable(gaBase, rva))
                    continue;

                validCount++;
                if (testedRvas.Add(rva))
                {
                    if (firstNewRva == 0)
                        firstNewRva = rva;
                }
                else
                {
                    duplicateRva = rva; // valid RVA already contributed by a prior signature
                }
            }

            if (firstNewRva != 0)
                return ((firstNewRva, 0, sig), new SigScanResult(index, desc, "OK", sigAddrs.Length, validCount, firstNewRva));

            if (duplicateRva != 0)
                return (null, new SigScanResult(index, desc, "DUPLICATE", sigAddrs.Length, validCount, duplicateRva));

            return (null, new SigScanResult(index, desc, "INVALID", sigAddrs.Length, validCount, 0));
        }

        // ── User Diagnostic Report ───────────────────────────────────────────

        private static List<string> BuildUserReportBox(int classCount, int updated, int fallback, int skipped)
        {
            var gaBase = Memory.GameAssemblyBase;
            string gaText = eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(gaBase) ? $"0x{gaBase:X}" : "(not resolved)";

            int W = 56;
            const int SigTruncLen = 48;
            foreach (var r in _lastSigResults)
            {
                string rawSig = TypeInfoTableSigs[r.Index].Sig;
                int sigLen = Math.Min(rawSig.Length, SigTruncLen) + (rawSig.Length > SigTruncLen ? 3 : 0);
                int sigNeeded = 9 + sigLen;
                int statusNeeded = 4 + 7 + $"matches={r.Matches,-4} valid={r.ValidMatches,-4} rva=0x{r.Rva:X}".Length;
                if (sigNeeded > W) W = sigNeeded;
                if (statusNeeded > W) W = statusNeeded;
            }

            string Row(string text) => $"║  {text.PadRight(W - 2)}║";
            string Sep(string label) => $"╠── {label} {new string('─', W - 4 - label.Length)}╣";
            string Header(string text)
            {
                int pad = W - text.Length;
                int left = pad / 2;
                return $"║{new string(' ', left)}{text}{new string(' ', pad - left)}║";
            }

            var lines = new List<string>();
            lines.Add($"{LogTag} ╔{new string('═', W)}╗");
            lines.Add($"{LogTag} {Header("IL2CPP DIAGNOSTIC REPORT")}");
            lines.Add($"{LogTag} ╠{new string('═', W)}╣");
            lines.Add($"{LogTag} {Row("If you have issues, copy this entire block (╔ to ╚)")}");
            lines.Add($"{LogTag} {Row("and paste it when reporting to developers.")}");
            lines.Add($"{LogTag} ╠{new string('═', W)}╣");
            lines.Add($"{LogTag} {Row($"GameAssembly  : {gaText}")}");
            lines.Add($"{LogTag} {Row($"Resolution    : {_lastResolutionMode}")}");
            lines.Add($"{LogTag} {Row($"Table RVA     : 0x{Offsets.Special.TypeInfoTableRva:X}")}");
            lines.Add($"{LogTag} {Row($"Classes Found : {classCount}")}");

            int okCount = _lastSigResults.Count(r => r.State is "OK" or "DUPLICATE");
            lines.Add($"{LogTag} {Sep($"Signature Scan ({okCount}/{_lastSigResults.Length} OK)")}");
            foreach (var r in _lastSigResults)
            {
                string state = r.State is "OK" or "DUPLICATE" ? "OK" : r.State;
                string status = r.Rva != 0
                    ? $"  [{r.Index}] {state,-7} matches={r.Matches,-4} valid={r.ValidMatches,-4} rva=0x{r.Rva:X}"
                    : $"  [{r.Index}] {state,-7} matches={r.Matches,-4} valid={r.ValidMatches}";
                lines.Add($"{LogTag} {Row(status)}");
                string rawSig = TypeInfoTableSigs[r.Index].Sig;
                string sigLine = rawSig.Length > SigTruncLen ? rawSig[..SigTruncLen] + "..." : rawSig;
                lines.Add($"{LogTag} {Row($"       {sigLine}")}");
            }

            lines.Add($"{LogTag} {Sep("Offset Dump")}");
            lines.Add($"{LogTag} {Row($"Updated  : {updated}")}");
            lines.Add($"{LogTag} {Row($"Fallback : {fallback}")}");
            lines.Add($"{LogTag} {Row($"Skipped  : {skipped}")}");
            lines.Add($"{LogTag} {Sep("TypeIndex Values")}");
            foreach (var (il2cppName, fieldName) in TypeIndexMap)
            {
                var fi = GetTypeIndexField(fieldName);
                string val = fi is not null ? $"{fi.GetValue(null)}" : "(missing)";
                lines.Add($"{LogTag} {Row($"  {il2cppName + ":",-24} {val}")}");
            }

            lines.Add($"{LogTag} ╚{new string('═', W)}╝");
            return lines;
        }

        // ── RIP-relative decode ──────────────────────────────────────────────

        private static ulong ResolveRipRelativeRva(ulong sigAddr, int relOffset, int instrLen, ulong gaBase)
        {
            int rel;
            try { rel = Memory.ReadValue<int>(sigAddr + (ulong)relOffset, false); }
            catch { return 0; }

            ulong globalVa = sigAddr + (ulong)instrLen + (ulong)(long)rel;
            return globalVa > gaBase ? globalVa - gaBase : 0;
        }

        // ── TypeInfoTable validation ─────────────────────────────────────────

        private static bool ValidateTypeInfoTable(ulong gaBase, ulong rva)
        {
            ulong tablePtr;
            try { tablePtr = Memory.ReadPtr(gaBase + rva, false); }
            catch { return false; }

            return eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(tablePtr)
                && ProbeTableEntries(tablePtr, 0, EarlyProbeCount, EarlyProbeRequired)
                && ProbeTableEntries(tablePtr, MidProbeOffset, MidProbeCount, MidProbeRequired);
        }

        /// <summary>
        /// Approximates how many entries a candidate TypeInfoTable holds, by exponentially
        /// probing outward and then binary-searching the boundary. ~O(log n) reads.
        /// <para>
        /// This is the tiebreaker between candidates that all clear
        /// <see cref="ValidateTypeInfoTable"/>. A spurious match tends to be a shorter run
        /// of class pointers; the genuine table is the longest one in the module.
        /// </para>
        /// </summary>
        private static int EstimateTableDepth(ulong gaBase, ulong rva)
        {
            ulong tablePtr;
            try { tablePtr = Memory.ReadPtr(gaBase + rva, false); }
            catch { return 0; }

            if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(tablePtr))
                return 0;

            // Exponential probe for an index that is clearly past the end.
            int lo = 0, hi = DepthProbeStart;
            while (hi <= DepthProbeMax && HasClassesAt(tablePtr, hi))
            {
                lo = hi;
                hi *= 2;
            }

            if (lo == 0)
                return 0;
            if (hi > DepthProbeMax)
                return DepthProbeMax;

            // Narrow the boundary between the last good index and the first bad one.
            while (hi - lo > DepthProbeResolution)
            {
                int mid = lo + (hi - lo) / 2;
                if (HasClassesAt(tablePtr, mid))
                    lo = mid;
                else
                    hi = mid;
            }

            return lo;
        }

        /// <summary>
        /// True if the small window of entries starting at <paramref name="index"/> still
        /// looks like class pointers.
        /// </summary>
        private static bool HasClassesAt(ulong tablePtr, int index) =>
            ProbeTableEntries(tablePtr, index, DepthProbeWindow, DepthProbeRequired);

        private static bool ProbeTableEntries(ulong tablePtr, int startIndex, int count, int required)
        {
            ulong[] ptrs;
            try { ptrs = Memory.ReadArray<ulong>(tablePtr + (ulong)startIndex * 8, count, false); }
            catch { return false; }

            int valid = 0;
            foreach (var ptr in ptrs)
                if (IsValidClassPtr(ptr) && ++valid >= required)
                    return true;

            return false;
        }

        private static bool IsValidClassPtr(ulong ptr)
        {
            if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(ptr)) return false;
            try
            {
                var namePtr = Memory.ReadValue<ulong>(ptr + K_Name, false);
                if (!eft_dma_radar.Silk.Misc.Utils.IsValidVirtualAddress(namePtr)) return false;
                var name = ReadStr(namePtr);
                return !string.IsNullOrEmpty(name) && name.Length < MaxNameLen && IsPlausibleClassName(name);
            }
            catch { return false; }
        }

        private static bool IsPlausibleClassName(string name)
        {
            foreach (char c in name)
                if (c < 0x20 || (c > 0x7E && c < 0xA0)) return false;
            return true;
        }

        // ── TypeIndex resolution ─────────────────────────────────────────────

        private static void ResolveTypeIndices(
            Dictionary<string, int> nameToIndex,
            List<(string Name, string Namespace, ulong KlassPtr, int Index)> classes)
        {
            foreach (var (il2cppName, fieldName) in TypeIndexMap)
            {
                var fi = GetTypeIndexField(fieldName);
                if (fi is null) continue;

                // Namespace-qualified entry (e.g. "EFT.Hideout.HideoutController"):
                // search the raw classes list by namespace + short name.
                int dotIdx = il2cppName.LastIndexOf('.');
                if (dotIdx > 0)
                {
                    var ns = il2cppName[..dotIdx];
                    var shortName = il2cppName[(dotIdx + 1)..];
                    bool found = false;
                    foreach (var (cName, cNs, _, cIdx) in classes)
                    {
                        if (cName == shortName && cNs == ns)
                        {
                            UpdateTypeIndexField(fi, (uint)cIdx);
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                        Log.WriteLine($"{LogTag} WARN: '{il2cppName}' not found in type table — {fieldName} using fallback ({fi.GetValue(null) ?? 0u}).");
                }
                else if (nameToIndex.TryGetValue(il2cppName, out var index))
                {
                    UpdateTypeIndexField(fi, (uint)index);
                }
                else
                {
                    Log.WriteLine($"{LogTag} WARN: '{il2cppName}' not found in type table — {fieldName} using fallback ({fi.GetValue(null) ?? 0u}).");
                }
            }
        }

        private static FieldInfo? GetTypeIndexField(string fieldName) =>
            CachedTypeIndexFields.FirstOrDefault(f => f.Name == fieldName);

        private static void UpdateTypeIndexField(FieldInfo fi, uint newValue)
        {
            var previous = (uint)(fi.GetValue(null) ?? 0u);
            fi.SetValue(null, newValue);
            if (previous != newValue)
                Log.WriteLine($"{LogTag} {fi.Name} UPDATED: {previous} → {newValue}");
        }

        internal static void DebugDumpResolverState(int classCount, int updated, int fallback, int skipped) =>
            Log.WriteBlock(BuildUserReportBox(classCount, updated, fallback, skipped));

        /// <summary>
        /// Resolves an Il2CppClass pointer from the TypeInfoTable using a TypeIndex.
        /// Returns a valid klass pointer, or 0 on failure.
        /// Callers should cache the result.
        /// </summary>
        internal static ulong ResolveKlassByTypeIndex(uint typeIndex)
        {
            if (typeIndex == 0)
                return 0;

            var gaBase = Memory.GameAssemblyBase;
            if (!gaBase.IsValidVirtualAddress() || Offsets.Special.TypeInfoTableRva == 0)
                return 0;

            if (!Memory.TryReadPtr(gaBase + Offsets.Special.TypeInfoTableRva, out var tablePtr, false)
                || !tablePtr.IsValidVirtualAddress())
                return 0;

            return Memory.TryReadValue<ulong>(tablePtr + (ulong)typeIndex * 8, out var ptr, false)
                && ptr.IsValidVirtualAddress()
                ? ptr
                : 0;
        }

            }
        }