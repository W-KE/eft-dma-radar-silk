// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using SilkUtils = eft_dma_radar.Silk.Misc.Utils;

namespace eft_dma_radar.Silk.Tarkov.Unity
{
    // ─────────────────────────────────────────────────────────────────────────────
    // IL2CPP Unity engine constants, layout structs, and GOM resolution.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// All Unity engine offsets — both IL2CPP object layout and native transform hierarchy.
    /// Update when game patches break functionality.
    /// </summary>
    internal static class UnityOffsets
    {
        // ── GameObject ──────────────────────────────────────────────────────
        // GO_Components/GO_Name/Comp_ObjectClass/Comp_GameObject have NO IL2CPP dump coverage
        // (native engine layout, not managed) and NO per-raid auto-detect the way
        // TransformAccess/TransformHierarchy get — they were hardcoded to values externally
        // supplied for the 16329911 UnityPlayer.dll rebuild. The game has since been observed
        // reverting to the original 13904407 build mid-session, and this set is NOT the same
        // for both: LootManager/ExfilManager (everything going through TransformChain) read
        // Vector3.Zero for every single object on 13904407 while player positions (a totally
        // different chain, _playerLookRaycastTransform) work fine — proving these 4 offsets
        // are still on the 16329911 values while the running build is 13904407.
        // SelectNativeOffsetsForVersion switches between the two known-good sets by matching
        // UnityPlayer.dll's FileVersion; an unrecognized version keeps whatever is already set.
        public static uint GO_ObjectClass   = 0x80;  // GameObject → ObjectClass (m_Object) — unconfirmed on either build, left as-is
        public static uint GO_Components    = 0x48;  // GameObject → ComponentArray
        public static uint GO_Name          = 0x78;  // GameObject → Name string pointer

        // ── Component ───────────────────────────────────────────────────────
        public static uint Comp_ObjectClass = 0x38;  // Component → ObjectClass (InteractiveClass)
        public static uint Comp_GameObject  = 0x48;  // Component → parent GameObject pointer

        /// <summary>
        /// Selects the GO_Components/GO_Name/Comp_ObjectClass/Comp_GameObject value set for
        /// <paramref name="fileVersion"/> (UnityPlayer.dll's FileVersion string). Only the two
        /// builds actually observed this session are known; anything else leaves the current
        /// values untouched (better to keep the last-known-good set than guess).
        /// </summary>
        internal static void SelectNativeOffsetsForVersion(string? fileVersion)
        {
            if (string.IsNullOrEmpty(fileVersion))
                return;

            (uint components, uint name, uint objClass, uint gameObject)? set = fileVersion switch
            {
                "2022.3.43.13904407" => (0x58u, 0x88u, 0x20u, 0x58u), // original build
                "2022.3.43.16329911" => (0x48u, 0x78u, 0x38u, 0x48u), // externally-supplied rebuild values
                _ => null,
            };

            if (set is not { } s)
            {
                Log.WriteLine($"[UnityOffsets] Unrecognized UnityPlayer.dll version '{fileVersion}' — " +
                    $"keeping current GameObject/Component offsets (Components=0x{GO_Components:X}, " +
                    $"Name=0x{GO_Name:X}, ObjClass=0x{Comp_ObjectClass:X}, GameObject=0x{Comp_GameObject:X}). " +
                    "LootManager/ExfilManager will read zero positions for everything if these are wrong for this build.");
                return;
            }

            if (GO_Components == s.components && GO_Name == s.name
                && Comp_ObjectClass == s.objClass && Comp_GameObject == s.gameObject)
                return; // already correct — avoid a no-op log line every raid

            GO_Components = s.components;
            GO_Name = s.name;
            Comp_ObjectClass = s.objClass;
            Comp_GameObject = s.gameObject;

            // TransformChain/SceneTransformChain bake these into fixed array slots at first
            // use — keep them in sync or a version switch mid-session (game update reverted,
            // relaunched) would leave stale values in the arrays despite the fields above
            // being correct.
            TransformChain[1] = Comp_GameObject;
            TransformChain[2] = GO_Components;
            TransformChain[4] = Comp_ObjectClass;
            SceneTransformChain[1] = Comp_GameObject;
            SceneTransformChain[2] = GO_Components;
            LevelSettings.LevelSettingsChain[0] = GO_Components;
            LevelSettings.LevelSettingsChain[2] = Comp_ObjectClass;

            Log.WriteLine($"[UnityOffsets] Selected GameObject/Component offsets for UnityPlayer.dll {fileVersion}: " +
                $"Components=0x{GO_Components:X}, Name=0x{GO_Name:X}, ObjClass=0x{Comp_ObjectClass:X}, GameObject=0x{Comp_GameObject:X}.");
        }

        // ── ObjectClass name chain ──────────────────────────────────────────
        public static readonly uint[] ObjClass_ToNamePtr = [0x0, 0x10];

        /// <summary>
        /// 6-element pointer chain: C# object → MonoBehaviour → GameObject → Components → Transform → ObjectClass → TransformInternal.
        /// Works for any MonoBehaviour-derived object (exfils, loot items, etc.).
        /// </summary>
        public static readonly uint[] TransformChain =
        [
            0x10,               // ObjectClass → MonoBehaviour
            Comp_GameObject,    // 0x58 — Component → GameObject
            GO_Components,      // 0x58 — GameObject → ComponentArray
            0x08,               // First component (Transform)
            Comp_ObjectClass,   // 0x20 — Transform → ObjectClass
            0x10,               // ObjectClass → TransformInternal
        ];

        /// <summary>
        /// 4-element variant of <see cref="TransformChain"/> for scene-placed MonoBehaviours
        /// (transits, BTR path stops, sniper zones). For these the first component (+0x08) is
        /// already the native <c>TransformInternal</c>; the managed Transform→ObjectClass→
        /// TransformInternal tail of the full chain returns null (<c>Comp_ObjectClass</c> at +0x20
        /// is null), so the full chain fails on them.
        /// </summary>
        public static readonly uint[] SceneTransformChain =
        [
            0x10,               // object → native MonoBehaviour (m_CachedPtr)
            Comp_GameObject,    // 0x58 — MonoBehaviour → GameObject
            GO_Components,      // 0x58 — GameObject → ComponentArray
            0x08,               // First component = native Transform (= TransformInternal)
        ];

        // ── ModuleBase (UnityPlayer.dll offsets) ────────────────────────────
        // Both are last-resort fallbacks only: GOM.GetAddr and CameraManager.ResolveAllCamerasAddr
        // validate the result (IsValidGomPtr / ValidateAllCamerasAddr) before ever trusting it,
        // so an update here is low-risk even if wrong — it just fails validation and falls
        // through to the signature scan, same as before this edit.
        public const uint GomFallback        = 0x1A46AF0;  // UnityPlayer.dll 16329911, was 0x1A233A0 (externally supplied)
        public const uint AllCameras         = 0x19EACC0;  // was 0x19F3080 (externally supplied, unverified against this codebase's history)

        // ── ObjectClass helpers ──────────────────────────────────────────────
        public static class ObjectClass
        {
            /// <summary>ObjectClass + 0x10 → MonoBehaviour.</summary>
            public const uint MonoBehaviourOffset = 0x10;
        }

        // ── Camera struct offsets (sig-scanned at runtime, fallback values) ──
        public static class Camera
        {
            /// <summary>
            /// Camera + offset → 4×4 ViewProjection matrix (Matrix4x4).
            /// Cross-checked against this repo's own main branch (confirmed working on
            /// 11.0.0.46657) — our own auto-detect never converged on a working candidate
            /// within ±0x2000 of the stale cached 0x98/0x128 across an entire session of
            /// testing, so this default matters: it's what the search starts validating
            /// from before ever needing to widen out.
            /// </summary>
            public static uint ViewMatrix = 0x334;

            /// <summary>Camera + offset → Field of View (float, degrees).</summary>
            public static uint FOV = 0x1A8;

            /// <summary>Camera + offset → Aspect Ratio (float).</summary>
            public static uint AspectRatio = 0x518;

            /// <summary>IsAdded offset after +0x10 dereference (DEC 2025).</summary>
            public const uint DerefIsAddedOffset = 0x35;
        }

        // ── Il2Cpp generic List<T> layout ────────────────────────────────────
        public static class List
        {
            /// <summary>Offset from List base to _items (the backing array pointer).</summary>
            public const uint ArrOffset = 0x10;

            /// <summary>Offset from the array base to the first element (element[0]).</summary>
            public const uint ArrStartOffset = 0x20;
        }

        // ── IL2CPP Managed List<T> ──────────────────────────────────────────
        public static class ManagedList
        {
            public const uint ItemsPtr = 0x10;  // Pointer to items array
            public const uint Count = 0x18;     // Count of items (_size)
        }

        // ── IL2CPP Managed Array header ─────────────────────────────────────
        public static class ManagedArray
        {
            public const uint FirstElement = 0x20;  // First element (after header)
            public const int ElementSize = 0x8;     // Size of pointer element
        }

        // ── MongoID struct layout ───────────────────────────────────────────
        public static class MongoID
        {
            public const uint TimeStamp = 0x00;     // uint
            public const uint Counter = 0x08;       // ulong
            public const uint StringID = 0x10;      // string pointer
        }

        // ── HashSet<T> with inline MongoID storage ──────────────────────────
        public static class IL2CPPHashSet
        {
            public const uint Entries = 0x18;           // Pointer to entries array
            public const uint Count = 0x1C;             // int — count
            public const int EntrySize = 0x20;          // 32 bytes per entry
            public const uint EntryValueOffset = 0x08;  // MongoID value starts here

            // Entry layout:
            //   +0x00: hashCode (int) + next (int)
            //   +0x08: MongoID value (struct inline)
            //     +0x00: _timeStamp (uint)
            //     +0x08: _counter (ulong)
            //     +0x10: _stringID (string pointer)
        }

        /// <summary>
        /// TransformInternal native layout: <c>HierarchyOffset</c> → pointer to
        /// TransformHierarchy, <c>IndexOffset</c> (immediately after, 8-byte aligned) → int
        /// index into the hierarchy's parallel arrays. This is the single most load-bearing
        /// pair of offsets in the radar — every world-space position (players, loot, exfils,
        /// doors, grenades, the BTR) reads through <see cref="Unity.ReadWorldPosition"/> /
        /// <see cref="Unity.ReadWorldPose"/>, both keyed off these two fields.
        /// <para>
        /// Unlike the IL2CPP game classes (dumped fresh every session from the TypeInfoTable)
        /// this is native Unity engine layout with no dumper schema to source it from, and
        /// unlike Camera.ViewMatrix/FOV/AspectRatio it had no sig-scan behind it either — a
        /// straight hardcoded const. When BSG rebuilds UnityPlayer.dll (a version bump, not
        /// necessarily an engine bump) this can silently shift, and every downstream read
        /// starts returning garbage while looking structurally valid (non-null pointers all
        /// the way down, only the final int is nonsense). <see cref="TryAutoDetect"/> exists
        /// because there is nothing else that can catch this.
        /// </para>
        /// </summary>
        public static class TransformAccess
        {
            /// <summary>TransformInternal + offset → pointer to TransformHierarchy.</summary>
            public static uint HierarchyOffset = 0x70;

            /// <summary>TransformInternal + offset → int index into the hierarchy arrays.</summary>
            public static uint IndexOffset = 0x78;

            /// <summary>True once <see cref="TryAutoDetect"/> has found a pair that validates.</summary>
            public static bool Confirmed { get; private set; }

            /// <summary>How far past the last known-good HierarchyOffset to search.</summary>
            private const uint ProbeRange = 0x400;
            private const uint ProbeStep = 0x8;
            private const int MaxIndexValue = 150_000;

            /// <summary>Resets <see cref="Confirmed"/> so a game restart re-probes. Call on game stop.</summary>
            internal static void Reset() => Confirmed = false;

            /// <summary>
            /// Confirms the current (HierarchyOffset, IndexOffset) pair against every supplied
            /// TransformInternal pointer, and — only if that fails — searches for a pair that
            /// does validate. Assumes Index sits immediately after Hierarchy (both fields
            /// shift together when something earlier in the native struct is resized), which
            /// turns a two-dimensional search into a one-dimensional one.
            /// <para>
            /// Requiring every supplied pointer to agree (not just one) is what makes this
            /// safe to trust: a wrong offset landing on plausible-looking garbage in ONE
            /// object is plausible, doing so consistently across several unrelated objects
            /// is not.
            /// </para>
            /// </summary>
            /// <param name="transformInternals">
            /// Several distinct, currently-live TransformInternal pointers (e.g. skeleton
            /// bones of a player known to be moving). At least 3 recommended.
            /// </param>
            internal static bool TryAutoDetect(ReadOnlySpan<ulong> transformInternals)
            {
                if (transformInternals.Length == 0)
                    return false;

                if (Confirmed || ValidatesAgainstAll(HierarchyOffset, IndexOffset, transformInternals))
                {
                    Confirmed = true;
                    return true;
                }

                Log.WriteLine($"[TransformAccess] Current offsets (H=0x{HierarchyOffset:X}, I=0x{IndexOffset:X}) " +
                              $"failed against {transformInternals.Length} live transform(s) — searching for a replacement.");

                // Search around the CURRENT value, not a fixed origin, so a second consecutive
                // game update that shifts things again keeps working from wherever the last
                // confirmed value ended up.
                uint origin = HierarchyOffset;
                uint lo = origin > ProbeRange ? origin - ProbeRange : 0;
                uint hi = origin + ProbeRange;

                for (uint h = lo; h <= hi; h += ProbeStep)
                {
                    uint i = h + 0x8;
                    if (!ValidatesAgainstAll(h, i, transformInternals))
                        continue;

                    Log.WriteLine($"[TransformAccess] Auto-detected new offsets: H=0x{HierarchyOffset:X}→0x{h:X}, " +
                                  $"I=0x{IndexOffset:X}→0x{i:X} (confirmed against {transformInternals.Length} transforms).");
                    HierarchyOffset = h;
                    IndexOffset = i;
                    Confirmed = true;
                    return true;
                }

                Log.Write(AppLogLevel.Warning,
                    $"[TransformAccess] Auto-detect FAILED — no offset pair in ±0x{ProbeRange:X} of 0x{origin:X} " +
                    $"validated against {transformInternals.Length} transform(s). World positions will not read correctly.");
                return false;
            }

            private static bool ValidatesAgainstAll(uint hierarchyOff, uint indexOff, ReadOnlySpan<ulong> transformInternals)
            {
                int firstIndex = -1;
                bool sawDifferentIndex = false;

                foreach (var ti in transformInternals)
                {
                    if (!Memory.TryReadPtr(ti + hierarchyOff, out var hierarchy, false) || !hierarchy.IsValidVirtualAddress())
                        return false;
                    if (!Memory.TryReadValue<int>(ti + indexOff, out var index, false))
                        return false;
                    if (index < 0 || index > MaxIndexValue)
                        return false;

                    // IsValidVirtualAddress is a loose numeric-range check spanning almost the
                    // entire 47-bit user address space (0x100000..0x7FFFFFFFFFFF) — wide enough
                    // that a plain small int/flags/counter field lands inside it by pure chance.
                    // That is exactly what happened here: H=0x8 "confirmed" against 3 bones with
                    // a value like 0xFFEC2D6A (~4GB, nowhere near this process's real heap
                    // addresses in the 0x1D-trillion range), and every TransformHierarchy probe
                    // built on top of it necessarily found nothing, because there was no real
                    // TransformHierarchy object there to find. Requiring an actual successful
                    // read THROUGH the candidate — not just that its value looks address-shaped —
                    // is what a plain int field cannot fake.
                    if (!Memory.TryReadValue<ulong>(hierarchy, out _, false))
                        return false;

                    if (firstIndex == -1)
                        firstIndex = index;
                    else if (index != firstIndex)
                        sawDifferentIndex = true;
                }

                // I=0x40 "confirmed" against 3 distinct skeleton bones that ALL read index=0 —
                // every subsequent TransformHierarchy search built on that index correctly found
                // nothing (every candidate pair was asked to explain position at parent-index 0
                // for every bone, which isn't how a skeleton hierarchy works). Real bones almost
                // never share the exact same hierarchy slot; a wrong IndexOffset landing on a
                // zeroed padding/flags field does exactly that. Reject anything where every
                // sampled bone reads the same index — it's evidence of the wrong field, not a
                // real hierarchy index, the same way TryReadValue<ulong>(hierarchy, ...) above
                // catches a wrong HierarchyOffset.
                if (transformInternals.Length >= 2 && !sawDifferentIndex)
                    return false;

                return true;
            }
        }

        /// <summary>
        /// TransformHierarchy native layout: pointers to the parallel vertices (TRS) and
        /// indices (parent-index) arrays that <see cref="TrsX.ComputeWorldPosition"/> walks.
        /// Same class of problem as <see cref="TransformAccess"/> — hardcoded native layout,
        /// no dumper schema, no sig scan — except Vertices/Indices are NOT assumed adjacent
        /// (the gap between them changed from 0x28 to something else across the 16329911
        /// rebuild), so both axes are searched independently rather than as a pair with a
        /// fixed relative offset.
        /// <para>
        /// An externally supplied guess (V=0x48, I=0x8) was tried directly first — in-raid
        /// logs showed <c>TryInitTransform</c> failing at the 'verticesAddr' step on every
        /// retry, proving that guess wrong for V (I was never reached, so it's untested).
        /// <see cref="TryAutoDetect"/> replaces "trust the guess" with "prove it": a cheap
        /// pointer-validity pass narrows each axis to a handful of candidates, then only
        /// that (small) cross-product is checked by actually walking the parent chain and
        /// requiring a finite, non-zero, map-scale position.
        /// </para>
        /// </summary>
        public static class TransformHierarchy
        {
            /// <summary>
            /// TransformHierarchy + offset → pointer to indices array (int[]).
            /// Cross-checked against a verified working third-party client's own
            /// UnityOffsets (Hierarchy_IndicesOffset) on 2022.3.43.13904407 — matches this
            /// value, not the 0x28 our own auto-detect had been converging on. That
            /// auto-detected 0x28 was a false positive: its "root" showed up as an index
            /// parenting itself, which a reference implementation of the same walk treats
            /// as CORRUPT data (cycle → reject the read), not a legitimate "stop here"
            /// sentinel — the real sentinel is a plain -1. Reading the true indices array
            /// at 0x40 should produce genuine -1 termination and make the self-loop
            /// safety net below purely defensive.
            /// </summary>
            public static uint IndicesOffset = 0x40;

            /// <summary>
            /// TransformHierarchy + offset → pointer to vertices array (TrsX[]).
            /// Matches the same verified third-party client's Hierarchy_VerticesOffset —
            /// same value our own auto-detect already converges on independently.
            /// </summary>
            public static uint VerticesOffset = 0x68;

            /// <summary>True once <see cref="TryAutoDetect"/> has found a pair that validates.</summary>
            public static bool Confirmed { get; private set; }

            // Widened from an initial ±0x400: that range found ZERO pointer-shaped candidates
            // on the 16329911 build, even though the Hierarchy pointer feeding this search was
            // itself confirmed by TransformAccess. TransformAccess's own Hierarchy/Index pair
            // shifted by 0x68 (104 bytes) in the same rebuild, so a shift larger than ±0x400
            // for a DIFFERENT pair of fields in a a different (if related) struct is plausible,
            // not surprising.
            private const uint ProbeRange = 0x1000;
            private const uint ProbeStep = 0x8;
            private const int MaxParentHops = 32;
            private const float MaxMapCoord = 50_000f; // Tarkov maps are a few km across at most

            // A candidate at V=0x0/I=0x10 passed the old "pos != Vector3.Zero" check by reading
            // garbage TrsX data whose Translation happened to be a subnormal float like
            // 8.56E-43 — technically nonzero, but every player/exfil/AI on the 2D map collapsed
            // onto the same point because that's indistinguishable from 0 at map scale. No real
            // in-raid position sits within a meter of exact (0,0,0), so require actual
            // map-scale magnitude, not just bitwise inequality with zero.
            private const float MinMapCoordSq = 1f; // 1 map unit²

            /// <summary>Resets <see cref="Confirmed"/> so a game restart re-probes. Call on game stop.</summary>
            internal static void Reset() => Confirmed = false;

            /// <inheritdoc cref="TransformHierarchy"/>
            /// <param name="transformInternals">
            /// Several distinct, currently-live TransformInternal pointers (e.g. skeleton
            /// bones of a player known to be moving) — the same set passed to
            /// <see cref="TransformAccess.TryAutoDetect"/>, which must run first so
            /// <see cref="TransformAccess.HierarchyOffset"/>/<c>IndexOffset</c> are correct.
            /// </param>
            internal static bool TryAutoDetect(ReadOnlySpan<ulong> transformInternals)
            {
                if (transformInternals.Length == 0)
                    return false;

                var buffer = new (ulong hierarchy, int index)[transformInternals.Length];
                int n = 0;
                foreach (var ti in transformInternals)
                {
                    if (!Memory.TryReadPtr(ti + TransformAccess.HierarchyOffset, out var h, false) || !h.IsValidVirtualAddress())
                        continue;
                    if (!Memory.TryReadValue<int>(ti + TransformAccess.IndexOffset, out var idx, false) || idx < 0)
                        continue;
                    buffer[n++] = (h, idx);
                }
                if (n == 0)
                    return false;

                var hierarchies = buffer.AsSpan(0, n);

                if (Confirmed || ValidatesAgainstAll(VerticesOffset, IndicesOffset, hierarchies))
                {
                    Confirmed = true;
                    return true;
                }

                Log.WriteLine($"[TransformHierarchy] Current offsets (V=0x{VerticesOffset:X}, I=0x{IndicesOffset:X}) " +
                              $"failed against {n} live transform(s) — searching for a replacement.");

                var vCandidates = FindPointerCandidates(VerticesOffset, hierarchies);
                var iCandidates = FindPointerCandidates(IndicesOffset, hierarchies);

                foreach (var v in vCandidates)
                {
                    foreach (var i in iCandidates)
                    {
                        if (v == i) continue; // can't both be the same field
                        if (!ValidatesAgainstAll(v, i, hierarchies))
                            continue;

                        Log.WriteLine($"[TransformHierarchy] Auto-detected new offsets: V=0x{VerticesOffset:X}→0x{v:X}, " +
                                      $"I=0x{IndicesOffset:X}→0x{i:X} (confirmed against {n} transforms).");
                        VerticesOffset = v;
                        IndicesOffset = i;
                        Confirmed = true;
                        return true;
                    }
                }

                Log.Write(AppLogLevel.Warning,
                    $"[TransformHierarchy] Auto-detect FAILED — {vCandidates.Count} vertices / {iCandidates.Count} " +
                    $"indices pointer candidate(s), {vCandidates.Count * iCandidates.Count} pair(s) tried, none " +
                    $"produced a sane position across {n} transform(s). World positions will not read correctly.");

                // A large candidate count on BOTH axes with zero surviving pairs (as opposed
                // to zero candidates on either axis) points at a different suspect: the
                // `index` value itself. It comes from TransformAccess.IndexOffset, which —
                // unlike HierarchyOffset — has no "does this actually read as something real"
                // check behind it, only a plausible-range check. If it's reading the wrong
                // int field, the (Vertices, Indices) pair could be exactly right and every
                // walk would still land on garbage, because it starts from the wrong slot.
                //
                // Dump raw bytes AND the index each hierarchy resolved to whenever nothing
                // validated overall — not just when the axis-level pre-filter found nothing —
                // so this is diagnosable in one round instead of narrowing it down by trial.
                Log.WriteLine("[TransformHierarchy] Indices in use (from TransformAccess.IndexOffset) — " +
                              "suspect if these don't look like plausible bone/hierarchy slots: " +
                              string.Join(", ", hierarchies.ToArray().Select(h => h.index)));
                DumpHierarchyBytes(hierarchies[0].hierarchy);

                return false;
            }

            private static void DumpHierarchyBytes(ulong hierarchy)
            {
                const int dumpLen = 0x200;
                Span<byte> raw = stackalloc byte[dumpLen];
                if (!Memory.TryReadBuffer(hierarchy, raw, false))
                {
                    Log.WriteLine($"[TransformHierarchy] Raw dump failed — could not read 0x{dumpLen:X} bytes @ 0x{hierarchy:X}.");
                    return;
                }

                Log.WriteLine($"[TransformHierarchy] Raw dump @ 0x{hierarchy:X} (offset: qword [valid VA?]):");
                for (int off = 0; off < dumpLen; off += 8)
                {
                    ulong qword = BitConverter.ToUInt64(raw.Slice(off, 8));
                    string flag = qword.IsValidVirtualAddress() ? "PTR?" : "    ";
                    Log.WriteLine($"[TransformHierarchy]   +0x{off:X3}: 0x{qword:X16} {flag}");
                }
            }

            /// <summary>
            /// Offsets within ±<see cref="ProbeRange"/> of <paramref name="origin"/> where every
            /// supplied hierarchy pointer dereferences to SOME non-null virtual address. Cheap
            /// first-pass filter — narrows each axis before the expensive position walk below.
            /// <para>
            /// Batched through one scatter round-trip per hierarchy rather than one DMA read
            /// per (hierarchy, offset) pair — at ±0x1000 that is ~1025 candidates, and doing
            /// them sequentially is what made one full auto-detect take close to three minutes
            /// in practice.
            /// </para>
            /// </summary>
            private static List<uint> FindPointerCandidates(uint origin, ReadOnlySpan<(ulong hierarchy, int index)> hierarchies)
            {
                uint lo = origin > ProbeRange ? origin - ProbeRange : 0;
                uint hi = origin + ProbeRange;

                using var scatter = Memory.GetScatter(VmmSharpEx.Options.VmmFlags.NOCACHE);
                foreach (var (hierarchy, _) in hierarchies)
                    for (uint off = lo; off <= hi; off += ProbeStep)
                        scatter.PrepareReadValue<ulong>(hierarchy + off);
                scatter.Execute();

                var result = new List<uint>();
                for (uint off = lo; off <= hi; off += ProbeStep)
                {
                    bool allValid = true;
                    foreach (var (hierarchy, _) in hierarchies)
                    {
                        if (!scatter.ReadValue<ulong>(hierarchy + off, out var ptr) || !ptr.IsValidVirtualAddress())
                        {
                            allValid = false;
                            break;
                        }
                    }
                    if (allValid)
                        result.Add(off);
                }
                return result;
            }

            /// <summary>
            /// True if, for every supplied (hierarchy, index) pair, walking the parent chain
            /// under this candidate (V, I) pair produces a finite, non-zero, map-scale
            /// position — i.e. real TRS data, not garbage that merely happened to look like a
            /// valid pointer. Reads individual elements as it walks (rather than materializing
            /// full arrays), since <c>index</c> can be in the tens of thousands.
            /// </summary>
            private static bool ValidatesAgainstAll(uint verticesOff, uint indicesOff,
                ReadOnlySpan<(ulong hierarchy, int index)> hierarchies)
            {
                foreach (var (hierarchy, index) in hierarchies)
                {
                    if (!Memory.TryReadPtr(hierarchy + verticesOff, out var verticesPtr, false) || !verticesPtr.IsValidVirtualAddress())
                        return false;
                    if (!Memory.TryReadPtr(hierarchy + indicesOff, out var indicesPtr, false) || !indicesPtr.IsValidVirtualAddress())
                        return false;

                    if (!TryWalkPosition(verticesPtr, indicesPtr, index, out var pos))
                        return false;

                    if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z))
                        return false;
                    if (pos.LengthSquared() < MinMapCoordSq)
                        return false;
                    if (MathF.Abs(pos.X) > MaxMapCoord || MathF.Abs(pos.Y) > MaxMapCoord || MathF.Abs(pos.Z) > MaxMapCoord)
                        return false;
                }
                return true;
            }

            private static bool TryWalkPosition(ulong verticesPtr, ulong indicesPtr, int index, out Vector3 pos)
            {
                pos = Vector3.Zero;
                try
                {
                    const ulong trsXSize = 48;

                    if (!Memory.TryReadValue<TrsX>(verticesPtr + (ulong)index * trsXSize, out var self, false))
                        return false;

                    pos = self.T;
                    int parent = ReadParentIndex(indicesPtr, index);
                    int iter = 0;

                    while (parent >= 0 && parent < 200_000 && iter++ < MaxParentHops)
                    {
                        if (!Memory.TryReadValue<TrsX>(verticesPtr + (ulong)parent * trsXSize, out var p, false))
                            return false;

                        pos = Vector3.Transform(pos, p.Q);
                        pos *= p.S;
                        pos += p.T;

                        // Root sentinel: the engine marks "no parent" by pointing a node at
                        // ITSELF, not -1. Without this check this validation walk re-applied
                        // the root's own T/S up to MaxParentHops times — landing under
                        // MaxMapCoord purely because that hop cap (32) is far smaller than
                        // production's (4000), which is exactly why a bad candidate could
                        // "validate" here and still blow up (Y≈131,560) the first time a real
                        // per-player read used the production hop cap. See
                        // TrsX.ComputeWorldPosition for the matching production-path fix.
                        int nextParent = ReadParentIndex(indicesPtr, parent);
                        if (nextParent == parent)
                            break;
                        parent = nextParent;
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private static int ReadParentIndex(ulong indicesPtr, int index) =>
                Memory.TryReadValue<int>(indicesPtr + (ulong)index * 4, out var v, false) ? v : -1;
        }

        // ── Unity Animator ────────────────────────────────────────────────────
        public static class UnityAnimator
        {
            /// <summary>Animator.m_Speed field offset.</summary>
            public const uint Speed = 0x4B0;
        }

        // ── LevelSettings pointer chain ──────────────────────────────────────
        public static class LevelSettings
        {
            /// <summary>
            /// Chain from a "---Custom_levelsettings---" GameObject to the managed LevelSettings instance.
            /// GO → ComponentArray → second component (0x18) → ObjectClass.
            /// </summary>
            public static readonly uint[] LevelSettingsChain =
            [
                GO_Components,      // 0x58 — GameObject → ComponentArray
                0x18,               // Second component (LevelSettings)
                Comp_ObjectClass,   // 0x20 — Component → ObjectClass
            ];
        }

        /// <summary>
        /// Reads the world-space position of a Unity transform from its <c>TransformInternal</c> pointer.
        /// Shared helper used by <c>Exfil</c>, <c>BtrTracker</c>, and any other code that holds a
        /// resolved <c>TransformInternal</c> (end of the standard 6-step <see cref="TransformChain"/>).
        /// Returns <see cref="Vector3.Zero"/> on any read failure.
        /// </summary>
        internal static Vector3 ReadWorldPosition(ulong transformInternal)
        {
            try
            {
                var hierarchy = Memory.ReadValue<ulong>(transformInternal + TransformAccess.HierarchyOffset);
                if (!SilkUtils.IsValidVirtualAddress(hierarchy))
                    return Vector3.Zero;

                var index = Memory.ReadValue<int>(transformInternal + TransformAccess.IndexOffset);
                if (index < 0 || index > 150_000)
                    return Vector3.Zero;

                var verticesPtr = Memory.ReadValue<ulong>(hierarchy + TransformHierarchy.VerticesOffset);
                var indicesPtr  = Memory.ReadValue<ulong>(hierarchy + TransformHierarchy.IndicesOffset);
                if (!SilkUtils.IsValidVirtualAddress(verticesPtr) || !SilkUtils.IsValidVirtualAddress(indicesPtr))
                    return Vector3.Zero;

                int count    = index + 1;
                var vertices = Memory.ReadArray<TrsX>(verticesPtr, count);
                var indices  = Memory.ReadArray<int>(indicesPtr, count);

                if (vertices.Length < count || indices.Length < count)
                    return Vector3.Zero;

                return TrsX.ComputeWorldPosition(vertices, indices, index);
            }
            catch
            {
                return Vector3.Zero;
            }
        }

        /// <summary>
        /// Reads the world-space position AND rotation of a Unity transform from its
        /// <c>TransformInternal</c> pointer in a single hierarchy read (used for the weapon fireport,
        /// where the muzzle forward direction = rotation · +Z). Returns false on any read failure.
        /// </summary>
        internal static bool ReadWorldPose(ulong transformInternal, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.Zero;
            rotation = Quaternion.Identity;
            try
            {
                var hierarchy = Memory.ReadValue<ulong>(transformInternal + TransformAccess.HierarchyOffset);
                if (!SilkUtils.IsValidVirtualAddress(hierarchy))
                    return false;

                var index = Memory.ReadValue<int>(transformInternal + TransformAccess.IndexOffset);
                if (index < 0 || index > 150_000)
                    return false;

                var verticesPtr = Memory.ReadValue<ulong>(hierarchy + TransformHierarchy.VerticesOffset);
                var indicesPtr  = Memory.ReadValue<ulong>(hierarchy + TransformHierarchy.IndicesOffset);
                if (!SilkUtils.IsValidVirtualAddress(verticesPtr) || !SilkUtils.IsValidVirtualAddress(indicesPtr))
                    return false;

                int count    = index + 1;
                var vertices = Memory.ReadArray<TrsX>(verticesPtr, count);
                var indices  = Memory.ReadArray<int>(indicesPtr, count);
                if (vertices.Length < count || indices.Length < count)
                    return false;

                position = TrsX.ComputeWorldPosition(vertices, indices, index);
                rotation = TrsX.ComputeWorldRotation(vertices, indices, index);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// TRS element in a Unity TransformHierarchy vertices array.
    /// Layout: Translation(Vector3) + pad(float) + Rotation(Quaternion) + Scale(Vector3) + pad(float) = 48 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct TrsX
    {
        public readonly Vector3 T;        // translation (12 bytes)
        private readonly float _pad0;     // padding (4 bytes)
        public readonly Quaternion Q;     // rotation (16 bytes)
        public readonly Vector3 S;        // scale (12 bytes)
        private readonly float _pad1;     // padding (4 bytes)

        /// <summary>
        /// Walks the transform hierarchy from pre-read vertex/index data and returns the world position.
        /// Pure math — no DMA reads. Returns the raw computed position; callers validate as needed.
        /// </summary>
        internal static Vector3 ComputeWorldPosition(
            ReadOnlySpan<TrsX> vertices,
            ReadOnlySpan<int> parentIndices,
            int index,
            int maxIterations = 4096)
        {
            var pos = vertices[index].T;
            int parent = parentIndices[index];
            int iter = 0;

            while (parent >= 0 && parent < vertices.Length && iter++ < maxIterations)
            {
                ref readonly var p = ref vertices[parent];
                pos = Vector3.Transform(pos, p.Q);
                pos *= p.S;
                pos += p.T;

                // The engine marks the hierarchy root by pointing a node's parent index at
                // ITSELF (not -1). Missing this made the walk treat the root as "just another
                // parent" and keep re-applying its own T/S every iteration until maxIterations
                // — confirmed live: index 0 pointed to itself, and 4000 iterations of its
                // ~32-unit translation landed Y at ~131,560 (vs. the correct, sane position
                // produced by applying the root exactly once). Stop as soon as the parent we
                // just applied is discovered to be its own parent.
                int nextParent = parentIndices[parent];
                if (nextParent == parent)
                    break;
                parent = nextParent;
            }

            return pos;
        }

        /// <summary>
        /// Walks the transform hierarchy and returns the accumulated world-space rotation quaternion.
        /// </summary>
        internal static Quaternion ComputeWorldRotation(
            ReadOnlySpan<TrsX> vertices,
            ReadOnlySpan<int> parentIndices,
            int index,
            int maxIterations = 4096)
        {
            var rot = vertices[index].Q;
            int parent = parentIndices[index];
            int iter = 0;

            while (parent >= 0 && parent < vertices.Length && iter++ < maxIterations)
            {
                rot = Quaternion.Multiply(vertices[parent].Q, rot);

                // Same self-referencing root sentinel as ComputeWorldPosition above.
                int nextParent = parentIndices[parent];
                if (nextParent == parent)
                    break;
                parent = nextParent;
            }

            return rot;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors the IL2CPP LinkedListObject struct (Sequential, Pack=8).
    /// [0x00] PreviousObjectLink
    /// [0x08] NextObjectLink
    /// [0x10] ThisObject (the actual GameObject ptr)
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal readonly struct LinkedListObject
    {
        public readonly ulong PreviousObjectLink;
        public readonly ulong NextObjectLink;
        public readonly ulong ThisObject;
    }

    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// IL2CPP ComponentArray layout — embedded inside a GameObject at offset <see cref="UnityOffsets.GO_Components"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal readonly struct ComponentArray
    {
        public readonly ulong ArrayBase;
        public readonly ulong MemLabelId;
        public readonly ulong Size;
        public readonly ulong Capacity;

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal readonly struct Entry
        {
            [FieldOffset(0x8)]
            public readonly ulong Component;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //
    // The old [FieldOffset]-marshalled GameObject struct was removed: GO_ObjectClass/
    // GO_Components/GO_Name are version-specific and mutable (see
    // UnityOffsets.SelectNativeOffsetsForVersion), and FieldOffset requires a compile-time
    // constant. Callers read ComponentArray directly at gameObject + UnityOffsets.GO_Components
    // instead (see GetComponentByKlassPtr / GetComponentByClassName below).

    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors the IL2CPP GameObjectManager struct.
    /// [0x20] LastActiveNode  — pointer to last LinkedListObject
    /// [0x28] ActiveNodes     — pointer to first LinkedListObject
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal readonly struct GOM
    {
        private const int MaxWalkNodes = 100_000;

        [FieldOffset(0x20)] public readonly ulong LastActiveNode;
        [FieldOffset(0x28)] public readonly ulong ActiveNodes;

        // ── Name cache ───────────────────────────────────────────────────────
        private static readonly Dictionary<string, ulong> _nameCache = new();
        private static readonly Lock _cacheLock = new();

        public static void ClearCache()
        {
            lock (_cacheLock)
                _nameCache.Clear();
        }

        // ── Cached resolved addresses ────────────────────────────────────────
        private static ulong _cachedGomAddr;

        /// <summary>Reads the GOM struct from a resolved GOM address.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static GOM Get(ulong gomAddress)
            => Memory.ReadValue<GOM>(gomAddress, false);

        // ── GOM address resolution ────────────────────────────────────────────

        // ── Direct signatures: mov [rip+rel32] / mov reg,[rip+rel32] ─────
        // These reference the GOM global directly via a RIP-relative operand.
        // (Sig, RelOffset, InstrLen, Desc)
        private static readonly (string Sig, int RelOff, int InstrLen, string Desc)[] GomDirectSigs =
        [
            // mov [rip+rel32], rax — GOM init store
            ("48 89 05 ? ? ? ? 48 83 C4 ? C3 33 C9", 3, 7, "mov [rip+rel32],rax (GOM init store)"),
            // mov [rip+rel32], rbp — GOM store variant
            ("48 89 2D ? ? ? ? 48 8B 6C 24 ? 48 83 C4 ? 5E C3 33 ED", 3, 7, "mov [rip+rel32],rbp (GOM store)"),
            // mov rsi, [rip+rel32] — GOM read
            ("48 8B 35 ? ? ? ? 48 85 F6 0F 84 ? ? ? ? 8B 46", 3, 7, "mov rsi,[rip+rel32] (GOM read)"),
            // mov rdx, [rip+rel32] — GOM read variant
            ("48 8B 15 ? ? ? ? 48 83 C2 ? 48 3B DA", 3, 7, "mov rdx,[rip+rel32] (GOM read)"),
            // mov rcx, [rip+rel32] — GOM read variant
            ("48 8B 0D ? ? ? ? 4C 8D 4C 24 ? 4C 8D 44 24 ? 89 44 24", 3, 7, "mov rcx,[rip+rel32] (GOM read)"),
        ];

        // ── Call-site signatures: E8 rel32 → sub_180A40AB0 (GOM getter) ──
        // These are call-site patterns that invoke the GOM getter function
        // (sub_180A40AB0: mov rax, cs:qword_181A233A0; retn).
        // We resolve the E8 target, then read the getter body to find the global.
        // (Sig, RelOffset, InstrLen, Desc)
        private static readonly (string Sig, int RelOff, int InstrLen, string Desc)[] GomCallSiteSigs =
        [
            ("E8 ? ? ? ? 4C 8D 45 ? 89 5D ? 48 8D 55", 1, 5, "call GomGetter (variant 1)"),
            ("E8 ? ? ? ? 8B 48 ? ? ? ? ? ? ? ? 48 8D 77", 1, 5, "call GomGetter (variant 2)"),
            ("E8 ? ? ? ? 48 8B 58 ? 48 8D 78 ? 48 3B DF 74 ? ? ? ? 48 8B 53", 1, 5, "call GomGetter (variant 3)"),
            ("E8 ? ? ? ? 8B 48 ? ? ? ? ? ? ? ? 48 8D 6F", 1, 5, "call GomGetter (variant 4)"),
            ("E8 ? ? ? ? 48 8B 58 ? 48 8D 78 ? 48 3B DF 74 ? 66 66 66 0F 1F 84 00", 1, 5, "call GomGetter (variant 5)"),
            ("E8 ? ? ? ? 4C 8D 44 24 ? C7 44 24 ? ? ? ? ? 48 8D 54 24 ? 48 8B C8", 1, 5, "call GomGetter (variant 6)"),
            ("E8 ? ? ? ? 4C 8D 44 24 ? 89 5C 24 ? 48 8D 54 24", 1, 5, "call GomGetter (variant 7)"),
        ];

        // ── Broad signatures: short patterns that may match many sites ─────
        // Generic mov [rip+rel32] store patterns — extremely patch-resilient.
        // Will match 100+ locations, so we use FindSignatures (multi-match)
        // and validate each candidate with IsValidGomPtr().
        // (Sig, RelOffset, InstrLen, Desc)
        private static readonly (string Sig, int RelOff, int InstrLen, string Desc)[] GomBroadSigs =
        [
            // mov [rip+rel32],reg; add rsp,imm8 — generic store before epilogue
            ("48 89 05 ? ? ? ? 48 83 C4", 3, 7, "mov [rip+rel32],rax; add rsp (broad)"),
        ];

        private const int BroadSigMaxMatches = 256;

        /// <summary>
        /// How many sites to test per direct / call-site signature. These patterns are
        /// short enough to match several unrelated sequences, and which one comes first
        /// shifts between UnityPlayer builds — so every match is tried, not just the
        /// first. See <see cref="GetAddr"/>.
        /// </summary>
        private const int SigMaxMatches = 64;

        /// <summary>
        /// Resolves the GameObjectManager global from UnityPlayer.dll.
        /// <para>
        /// EVERY candidate is confirmed with <see cref="IsValidGomPtr"/> before being
        /// accepted or cached. Phases 1, 2 and 4 used to accept anything that merely read
        /// back as a valid pointer, which is how the 2022.3.43.16329911 update killed the
        /// radar outright: the generic phase-1 pattern's first match landed on a different
        /// global, that address was cached permanently, and every GOM consumer — including
        /// LocalGameWorld's "GameWorld" lookup — silently found nothing.
        /// </para>
        /// </summary>
        public static ulong GetAddr(ulong unityBase)
        {
            if (SilkUtils.IsValidVirtualAddress(_cachedGomAddr))
                return _cachedGomAddr;

            int tested = 0, rejected = 0;

            // Phase 1: direct mov [rip+rel32] signatures — read the GOM global directly.
            foreach (var (sig, relOff, instrLen, desc) in GomDirectSigs)
            {
                foreach (var addr in FindSites(sig))
                {
                    try
                    {
                        int rva = Memory.ReadValue<int>(addr + (ulong)relOff, false);
                        if (!Memory.TryReadPtr(addr + (ulong)instrLen + (ulong)rva, out var ptr, false))
                            continue;

                        tested++;
                        if (!IsValidGomPtr(ptr))
                        {
                            rejected++;
                            LogRejected("direct", desc, addr, ptr);
                            continue;
                        }

                        Log.WriteLine($"[GOM] Located via direct sig: {desc} @ 0x{addr:X} → 0x{ptr:X} (validated)");
                        _cachedGomAddr = ptr;
                        return ptr;
                    }
                    catch { }
                }
            }

            // Phase 2: E8 call-site signatures — resolve the call target, then read the
            // getter body for the global it loads.
            foreach (var (sig, relOff, instrLen, desc) in GomCallSiteSigs)
            {
                foreach (var callAddr in FindSites(sig))
                {
                    try
                    {
                        int callRel = Memory.ReadValue<int>(callAddr + (ulong)relOff, false);
                        ulong targetFunc = callAddr + (ulong)instrLen + (ulong)callRel;

                        if (!SilkUtils.IsValidVirtualAddress(targetFunc))
                            continue;

                        if (!TryResolveGetterGlobal(targetFunc, out var globalPtr))
                            continue;

                        tested++;
                        if (!IsValidGomPtr(globalPtr))
                        {
                            rejected++;
                            LogRejected("call-site", desc, callAddr, globalPtr);
                            continue;
                        }

                        Log.WriteLine($"[GOM] Located via call-site sig: {desc} @ 0x{callAddr:X} → 0x{globalPtr:X} (validated)");
                        _cachedGomAddr = globalPtr;
                        return globalPtr;
                    }
                    catch { }
                }
            }

            // Phase 3: broad/generic signatures — many matches, each validated.
            foreach (var (sig, relOff, instrLen, desc) in GomBroadSigs)
            {
                try
                {
                    var matches = Memory.FindSignatures(sig, "UnityPlayer.dll", BroadSigMaxMatches);
                    foreach (var addr in matches)
                    {
                        if (!SilkUtils.IsValidVirtualAddress(addr))
                            continue;

                        int rva = Memory.ReadValue<int>(addr + (ulong)relOff, false);
                        ulong ptr = addr + (ulong)instrLen + (ulong)rva;

                        if (!Memory.TryReadPtr(ptr, out var gomAddr, false))
                            continue;

                        tested++;
                        if (!IsValidGomPtr(gomAddr))
                        {
                            rejected++;
                            continue;
                        }

                        Log.WriteLine($"[GOM] Located via broad sig: {desc} @ 0x{addr:X} → 0x{gomAddr:X} " +
                                      $"(validated, {matches.Length} sites scanned)");
                        _cachedGomAddr = gomAddr;
                        return gomAddr;
                    }
                }
                catch { }
            }

            // Phase 4: hardcoded RVA. Version-specific and stale by definition after an
            // engine update — it only counts if it validates like anything else.
            try
            {
                if (Memory.TryReadPtr(unityBase + UnityOffsets.GomFallback, out var fallback, false))
                {
                    tested++;
                    if (IsValidGomPtr(fallback))
                    {
                        Log.WriteLine($"[GOM] Located via hardcoded offset 0x{UnityOffsets.GomFallback:X} → 0x{fallback:X} (validated)");
                        _cachedGomAddr = fallback;
                        return fallback;
                    }

                    rejected++;
                    LogRejected("hardcoded", $"RVA 0x{UnityOffsets.GomFallback:X}", unityBase + UnityOffsets.GomFallback, fallback);
                }
            }
            catch { }

            Log.WriteLine($"[GOM] FAILED to locate GameObjectManager — {tested} candidate(s) tested, " +
                          $"{rejected} rejected by validation. UnityPlayer signatures likely need updating for this build.");
            throw new InvalidOperationException("Failed to locate GameObjectManager");

            static ulong[] FindSites(string sig)
            {
                try
                {
                    return Memory.FindSignatures(sig, "UnityPlayer.dll", SigMaxMatches);
                }
                catch
                {
                    return [];
                }
            }

            static void LogRejected(string phase, string desc, ulong site, ulong candidate) =>
                Log.WriteRateLimited(AppLogLevel.Debug, $"gom_rej_{phase}", TimeSpan.FromSeconds(5),
                    $"[GOM] Rejected {phase} candidate 0x{candidate:X} from 0x{site:X} ({desc}) — failed GOM validation.");
        }

        /// <summary>
        /// Reads the first 7 bytes of a getter function and checks for:
        ///   48 8B 05 XX XX XX XX  (mov rax, [rip+rel32])
        /// If matched, resolves the RIP-relative global and dereferences it.
        /// </summary>
        private static bool TryResolveGetterGlobal(ulong funcAddr, out ulong result)
        {
            result = 0;
            Span<byte> header = stackalloc byte[7];
            if (!Memory.TryReadBuffer(funcAddr, header, false))
                return false;

            // 48 8B 05 = REX.W mov rax, [rip+rel32]
            if (header[0] != 0x48 || header[1] != 0x8B || header[2] != 0x05)
                return false;

            int innerRel = BitConverter.ToInt32(header[3..]);
            ulong globalAddr = funcAddr + 7 + (ulong)innerRel;

            if (!Memory.TryReadPtr(globalAddr, out result, false))
                return false;

            return SilkUtils.IsValidVirtualAddress(result);
        }

        /// <summary>
        /// Validates that <paramref name="ptr"/> points to a plausible GOM struct.
        /// A valid GOM has readable ActiveNodes (0x28) and LastActiveNode (0x20)
        /// pointers, and the first linked-list node has a valid ThisObject.
        /// </summary>
        internal static bool IsValidGomPtr(ulong ptr)
        {
            if (!SilkUtils.IsValidVirtualAddress(ptr))
                return false;

            // Read the two key fields: LastActiveNode (0x20) and ActiveNodes (0x28)
            if (!Memory.TryReadValue<ulong>(ptr + 0x20, out var lastActive, false))
                return false;
            if (!SilkUtils.IsValidVirtualAddress(lastActive))
                return false;

            if (!Memory.TryReadValue<ulong>(ptr + 0x28, out var activeNodes, false))
                return false;
            if (!SilkUtils.IsValidVirtualAddress(activeNodes))
                return false;

            // Probe the first node — it should be a valid LinkedListObject with a valid ThisObject
            if (!Memory.TryReadValue<LinkedListObject>(activeNodes, out var firstNode, false))
                return false;

            return SilkUtils.IsValidVirtualAddress(firstNode.ThisObject);
        }

        /// <summary>
        /// Resets the cached GOM / AllCameras addresses. Call on game stop.
        /// </summary>
        internal static void ResetCachedAddresses()
        {
            _cachedGomAddr = 0;
            ClearCache();
        }

        // ── Linked-list walk ──────────────────────────────────────────────────

        /// <summary>
        /// Searches the GOM active linked list for a GameObject whose name matches <paramref name="name"/>.
        /// Caches the result for faster subsequent lookups.
        /// Returns 0 if not found.
        /// </summary>
        public ulong GetGameObjectByName(string name, bool ignoreCase = true, bool useCache = true)
        {
            if (string.IsNullOrEmpty(name))
                return 0;

            if (useCache)
            {
                lock (_cacheLock)
                {
                    if (_nameCache.TryGetValue(name, out var cached) && SilkUtils.IsValidVirtualAddress(cached))
                        return cached;
                }
            }

            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            if (!Memory.TryReadValue<LinkedListObject>(ActiveNodes, out var first, false)) return 0;
            if (!Memory.TryReadValue<LinkedListObject>(LastActiveNode, out var last, false)) return 0;

            ulong result = WalkList(first, last, forward: true,
                (node) => MatchName(node.ThisObject, name, comparison) ? node.ThisObject : 0);

            if (result == 0)
                result = WalkList(last, first, forward: false,
                    (node) => MatchName(node.ThisObject, name, comparison) ? node.ThisObject : 0);

            if (SilkUtils.IsValidVirtualAddress(result) && useCache)
            {
                lock (_cacheLock)
                    _nameCache[name] = result;
            }

            return result;
        }

        /// <summary>
        /// Walks the GOM active linked list and searches each GameObject's component array
        /// for a component whose IL2CPP klass pointer matches <paramref name="klassPtr"/>.
        /// Returns the objectClass pointer of the first matching component, or 0.
        /// Much faster than name-based search (saves ~2 DMA reads per component).
        /// </summary>
        public ulong FindBehaviourByKlassPtr(ulong klassPtr)
        {
            if (!SilkUtils.IsValidVirtualAddress(klassPtr))
                return 0;

            if (!Memory.TryReadValue<LinkedListObject>(ActiveNodes, out var first, true)) return 0;
            if (!Memory.TryReadValue<LinkedListObject>(LastActiveNode, out var last, true)) return 0;

            ulong result = WalkList(first, last, forward: true,
                node => GetComponentByKlassPtr(node.ThisObject, klassPtr), useCache: true);

            if (result == 0)
                result = WalkList(last, first, forward: false,
                    node => GetComponentByKlassPtr(node.ThisObject, klassPtr), useCache: true);

            return result;
        }

        /// <summary>
        /// Walks the GOM active linked list and searches each GameObject's component array
        /// for a component whose IL2CPP class name matches <paramref name="className"/>.
        /// Returns the objectClass pointer of the first matching component, or 0.
        /// </summary>
        public ulong FindBehaviourByClassName(string className)
        {
            if (string.IsNullOrEmpty(className))
                return 0;

            if (!Memory.TryReadValue<LinkedListObject>(ActiveNodes, out var first, true)) return 0;
            if (!Memory.TryReadValue<LinkedListObject>(LastActiveNode, out var last, true)) return 0;

            ulong result = WalkList(first, last, forward: true,
                node => GetComponentByClassName(node.ThisObject, className), useCache: true);

            if (result == 0)
                result = WalkList(last, first, forward: false,
                    node => GetComponentByClassName(node.ThisObject, className), useCache: true);

            return result;
        }

        /// <summary>
        /// Searches a single GameObject's component array for a component with a matching klass pointer.
        /// Returns the objectClass pointer, or 0.
        /// </summary>
        private static ulong GetComponentByKlassPtr(ulong gameObject, ulong klassPtr)
        {
            // GO_Components is version-specific and mutable (see UnityOffsets.SelectNativeOffsetsForVersion),
            // so it can't be baked into a [FieldOffset]-marshalled struct — read the
            // ComponentArray directly at the current runtime offset instead.
            if (!Memory.TryReadValue<ComponentArray>(gameObject + UnityOffsets.GO_Components, out var compArr, true))
                return 0;

            if (!SilkUtils.IsValidVirtualAddress(compArr.ArrayBase) || compArr.Size == 0)
                return 0;

            int count = (int)Math.Min(compArr.Size, 0x400);
            Span<ComponentArray.Entry> entries = count <= 64
                ? stackalloc ComponentArray.Entry[count]
                : new ComponentArray.Entry[count];

            if (!Memory.TryReadBuffer(compArr.ArrayBase, entries, true))
                return 0;

            for (int i = 0; i < count; i++)
            {
                var compPtr = entries[i].Component;
                if (!SilkUtils.IsValidVirtualAddress(compPtr))
                    continue;

                if (!Memory.TryReadPtr(compPtr + UnityOffsets.Comp_ObjectClass, out var objectClass, true)
                    || !SilkUtils.IsValidVirtualAddress(objectClass))
                    continue;

                if (!Memory.TryReadPtr(objectClass, out var klass, true))
                    continue;

                if (klass == klassPtr)
                    return objectClass;
            }
            return 0;
        }

        /// <summary>
        /// Searches a single GameObject's component array for a component whose IL2CPP class name matches.
        /// Returns the objectClass pointer, or 0.
        /// Uses read caching by default — class names are stable within a session and
        /// caching avoids thousands of redundant DMA reads when walking the GOM
        /// (many GameObjects share the same component types like Transform, MeshRenderer, etc.).
        /// </summary>
        private static ulong GetComponentByClassName(ulong gameObject, string className)
        {
            // See GetComponentByKlassPtr above — same reason for reading ComponentArray
            // directly instead of through the old [FieldOffset]-marshalled GameObject struct.
            if (!Memory.TryReadValue<ComponentArray>(gameObject + UnityOffsets.GO_Components, out var compArr, true))
                return 0;

            if (!SilkUtils.IsValidVirtualAddress(compArr.ArrayBase) || compArr.Size == 0)
                return 0;

            int count = (int)Math.Min(compArr.Size, 0x400);
            Span<ComponentArray.Entry> entries = count <= 64
                ? stackalloc ComponentArray.Entry[count]
                : new ComponentArray.Entry[count];

            if (!Memory.TryReadBuffer(compArr.ArrayBase, entries, true))
                return 0;

            for (int i = 0; i < count; i++)
            {
                var compPtr = entries[i].Component;
                if (!SilkUtils.IsValidVirtualAddress(compPtr))
                    continue;

                if (!Memory.TryReadPtr(compPtr + UnityOffsets.Comp_ObjectClass, out var objectClass, true)
                    || !SilkUtils.IsValidVirtualAddress(objectClass))
                    continue;

                var name = Il2CppClass.ReadName(objectClass, useCache: true);
                if (name is not null && name.Equals(className, StringComparison.Ordinal))
                    return objectClass;
            }
            return 0;
        }

        /// <summary>
        /// Given a behaviour/component pointer, navigates to its parent GameObject via
        /// <c>Comp_GameObject</c> (0x58) and then searches that single GameObject's component
        /// array for a component whose IL2CPP class name matches <paramref name="className"/>.
        /// Returns the objectClass pointer of the matching component, or 0.
        /// </summary>
        public static ulong GetComponentFromBehaviour(ulong behaviour, string className)
        {
            if (!SilkUtils.IsValidVirtualAddress(behaviour))
                return 0;

            if (!Memory.TryReadPtr(behaviour + UnityOffsets.Comp_GameObject, out var gameObject, false)
                || !SilkUtils.IsValidVirtualAddress(gameObject))
                return 0;

            return GetComponentByClassName(gameObject, className);
        }

        // ── Generic linked-list walker ───────────────────────────────────────

        /// <summary>
        /// Walks the GOM linked list from <paramref name="start"/> toward <paramref name="end"/>.
        /// For each valid node, invokes <paramref name="visitor"/>. If the visitor returns
        /// a non-zero value, the walk stops and that value is returned.
        /// </summary>
        private static ulong WalkList(
            LinkedListObject start,
            LinkedListObject end,
            bool forward,
            Func<LinkedListObject, ulong> visitor,
            bool useCache = false)
        {
            var current = start;
            for (int i = 0; i < MaxWalkNodes; i++)
            {
                if (!SilkUtils.IsValidVirtualAddress(current.ThisObject))
                    break;

                var hit = visitor(current);
                if (SilkUtils.IsValidVirtualAddress(hit))
                    return hit;

                if (current.ThisObject == end.ThisObject)
                    break;

                var nextLink = forward ? current.NextObjectLink : current.PreviousObjectLink;
                if (!Memory.TryReadValue<LinkedListObject>(nextLink, out current, useCache))
                    break;
            }
            return 0;
        }

        private static bool MatchName(ulong gameObject, string name, StringComparison comparison)
        {
            if (!Memory.TryReadValue<ulong>(gameObject + UnityOffsets.GO_Name, out var namePtr, false))
                return false;
            if (!SilkUtils.IsValidVirtualAddress(namePtr))
                return false;
            return Memory.TryReadString(namePtr, out var goName, 64, false)
                && goName is not null
                && goName.Contains(name, comparison);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the IL2CPP class name from a Unity object.
    /// Chain: objectClass → [0x0, 0x10] → name C-string
    /// </summary>
    internal static class Il2CppClass
    {
        /// <summary>
        /// Returns the IL2CPP class name for <paramref name="objectClass"/>, or null on failure.
        /// </summary>
        public static string? ReadName(ulong objectClass, int maxLength = 64, bool useCache = false)
        {
            if (!Memory.TryReadPtrChain(objectClass, UnityOffsets.ObjClass_ToNamePtr, out ulong namePtr, useCache))
                return null;
            if (!SilkUtils.IsValidVirtualAddress(namePtr))
                return null;
            return Memory.TryReadString(namePtr, out var name, maxLength, useCache) ? name : null;
        }
    }
}
