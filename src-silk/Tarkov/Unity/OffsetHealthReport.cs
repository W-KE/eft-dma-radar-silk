// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.GameWorld;
using eft_dma_radar.Silk.Tarkov.Unity.IL2CPP;

namespace eft_dma_radar.Silk.Tarkov.Unity
{
    /// <summary>
    /// One-shot PASS/FAIL summary of everything the radar resolves out of the two game
    /// modules, emitted once per startup.
    /// <para>
    /// Exists because a game update breaks these silently. The 2022.3.43.16329911 bump
    /// left the radar completely inert in raid, and the only clue in a 13-minute log was
    /// a GOM read failing several minutes after startup — the actual cause (a signature
    /// resolving to the wrong global) produced a cheerful "Located via direct sig" line.
    /// This block turns that into a single visible FAIL next to the group that broke.
    /// </para>
    /// </summary>
    internal static class OffsetHealthReport
    {
        private const string LogTag = "[OffsetHealth]";

        private static bool _emitted;

        /// <summary>Resets the once-per-session latch. Call on game stop.</summary>
        internal static void Reset() => _emitted = false;

        /// <summary>
        /// Builds and logs the report. Purely diagnostic — never throws, and never
        /// changes any resolved value.
        /// </summary>
        internal static void Emit()
        {
            if (_emitted)
                return;
            _emitted = true;

            try
            {
                var rows = new List<(string Group, string Detail, bool? Pass)>();

                // ── Modules ──────────────────────────────────────────────────────
                var unityBase = Memory.UnityBase;
                var gaBase = Memory.GameAssemblyBase;

                rows.Add(("UnityPlayer", $"base=0x{unityBase:X} ver={Describe(Memory.UnityPlayerVersion)}",
                          unityBase.IsValidVirtualAddress()));
                rows.Add(("GameAssembly", $"base=0x{gaBase:X}", gaBase.IsValidVirtualAddress()));

                // ── GOM ──────────────────────────────────────────────────────────
                // The single most load-bearing address: LocalGameWorld finds the raid
                // through it, so a wrong value means the radar does nothing at all.
                //
                // Re-running IsValidGomPtr here (rather than just checking the address is
                // non-null) produced a false FAIL: GetAddr already required it to pass before
                // ever caching it, but the game's object graph is still mid-bootstrap at the
                // point this report runs (right after the IL2CPP dump, well before the main
                // menu scene has populated anything), so the SAME address's linked list can
                // transiently read as empty/incoherent here even though it is correct and
                // works fine once a raid actually starts. GetAddr's own validation is
                // authoritative — trust it rather than re-checking under worse timing.
                var gom = Memory.GOM;
                rows.Add(("GOM", $"0x{gom:X}", gom.IsValidVirtualAddress()));

                // ── IL2CPP ───────────────────────────────────────────────────────
                rows.Add(("TypeInfoTable", $"rva=0x{Offsets.Special.TypeInfoTableRva:X}",
                          Offsets.Special.TypeInfoTableRva != 0));

                var unresolved = UnresolvedTypeIndices();
                rows.Add(("TypeIndices",
                          unresolved.Count == 0 ? "all resolved" : $"unresolved: {string.Join(", ", unresolved)}",
                          unresolved.Count == 0));

                // ── TransformAccess ──────────────────────────────────────────────
                // Not resolvable at startup — no live TransformInternal exists until a
                // raid is entered, so this only ever confirms itself in
                // LocalGameWorld.ValidateTransformReadable. Reported here purely as a
                // status flag so a look at the box after entering a raid shows whether it
                // ever had to fall back to auto-detection.
                rows.Add(("TransformAccess",
                          $"H=0x{UnityOffsets.TransformAccess.HierarchyOffset:X} I=0x{UnityOffsets.TransformAccess.IndexOffset:X} " +
                          (UnityOffsets.TransformAccess.Confirmed ? "(confirmed)" : "(not yet confirmed — resolves in raid)"),
                          UnityOffsets.TransformAccess.Confirmed ? true : null));

                rows.Add(("TransformHierarchy",
                          $"V=0x{UnityOffsets.TransformHierarchy.VerticesOffset:X} I=0x{UnityOffsets.TransformHierarchy.IndicesOffset:X} " +
                          (UnityOffsets.TransformHierarchy.Confirmed ? "(confirmed)" : "(not yet confirmed — resolves in raid)"),
                          UnityOffsets.TransformHierarchy.Confirmed ? true : null));

                // ── Camera (deferred to the aimview phase) ───────────────────────
                var cam = CameraManager.GetOffsetHealth();
                if (!cam.Resolved)
                {
                    rows.Add(("Camera", "deferred — resolves on first raid", null));
                }
                else
                {
                    rows.Add(("AllCameras", $"0x{cam.AllCamerasAddr:X}", CameraManager.ValidateResolvedAllCameras()));
                    rows.Add(("Camera offsets",
                              $"VM=0x{cam.ViewMatrix:X} FOV=0x{cam.Fov:X} AR=0x{cam.Aspect:X}",
                              cam.ViewMatrix != 0 && cam.Fov != 0 && cam.Aspect != 0));
                }

                Log.WriteBlock(BuildBox(rows));
            }
            catch (Exception ex)
            {
                Log.WriteLine($"{LogTag} Report failed: {ex.Message}");
            }
        }

        private static string Describe(string? s) => string.IsNullOrWhiteSpace(s) ? "(unknown)" : s;

        /// <summary>
        /// Type indices still sitting at 0 after the dump — i.e. the class name was not
        /// found in the current build's type table and anything keyed off it will fail.
        /// </summary>
        private static List<string> UnresolvedTypeIndices()
        {
            var result = new List<string>();
            foreach (var fi in typeof(Offsets.Special).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (fi.IsLiteral || !fi.Name.EndsWith("_TypeIndex", StringComparison.Ordinal))
                    continue;

                if (fi.GetValue(null) is uint v && v == 0)
                    result.Add(fi.Name[..^"_TypeIndex".Length]);
            }
            return result;
        }

        private static List<string> BuildBox(List<(string Group, string Detail, bool? Pass)> rows)
        {
            const string title = "OFFSET HEALTH";
            int labelW = rows.Count == 0 ? 0 : rows.Max(r => r.Group.Length);

            var body = rows
                .Select(r => $"  {Status(r.Pass)}  {r.Group.PadRight(labelW)}  {r.Detail}")
                .ToList();

            int w = Math.Max(title.Length + 4, body.Count == 0 ? 0 : body.Max(b => b.Length) + 3);

            var lines = new List<string> { $"{LogTag} ╔{new string('═', w)}╗" };

            int pad = w - title.Length, left = pad / 2;
            lines.Add($"{LogTag} ║{new string(' ', left)}{title}{new string(' ', pad - left)}║");
            lines.Add($"{LogTag} ╠{new string('═', w)}╣");

            foreach (var b in body)
                lines.Add($"{LogTag} ║{b.PadRight(w)}║");

            lines.Add($"{LogTag} ╚{new string('═', w)}╝");

            int failed = rows.Count(r => r.Pass == false);
            if (failed > 0)
                lines.Add($"{LogTag} {failed} check(s) FAILED — offsets/signatures likely stale for this game build.");

            return lines;
        }

        private static string Status(bool? pass) => pass switch
        {
            true => "PASS",
            false => "FAIL",
            _ => "....",
        };
    }
}
