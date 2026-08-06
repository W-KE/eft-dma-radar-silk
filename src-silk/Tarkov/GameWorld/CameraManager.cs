// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.IO;
using eft_dma_radar.Silk.Tarkov.Unity;
using eft_dma_radar.Silk.Tarkov.Unity.Collections;
using VmmSharpEx;
using VmmSharpEx.Options;

using static eft_dma_radar.Silk.Tarkov.Unity.UnityOffsets;

namespace eft_dma_radar.Silk.Tarkov.GameWorld
{
    /// <summary>
    /// Resolves FPS + Optic cameras and provides scatter-batched ViewMatrix/FOV reads.
    /// Exposes a static <see cref="WorldToScreen"/> method used by the advanced aimview
    /// and (future) ESP overlay.
    /// <para>
    /// <b>Resolution order:</b>
    /// <list type="number">
    ///   <item>IL2CPP EFT.CameraControl.CameraManager.Instance (primary).</item>
    ///   <item>Unity AllCameras static + GameObject name search (fallback).</item>
    /// </list>
    /// </para>
    /// </summary>
    internal sealed class CameraManager
    {
        #region Static State

        private static ulong _eftCameraManagerInstance;
        private static ulong _eftCameraManagerClassPtr;
        private static ulong _allCamerasAddr;
        private static bool _staticInitDone;

        /// <summary>Component → GameObject offset that actually produced readable camera names.</summary>
        private static uint? _camGoOffset;

        // -- Camera offset cache -------------------------------------------------

        private static readonly string CameraCacheFilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "eft-dma-radar-silk", "camera_offsets.json");

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private sealed class CameraOffsetCache
        {
            public uint UnityPlayerTimestamp { get; set; }
            public uint UnityPlayerSizeOfImage { get; set; }
            public ulong AllCamerasRva { get; set; }
            public uint ViewMatrix { get; set; }
            public uint FOV { get; set; }
            public uint AspectRatio { get; set; }
        }

        #endregion

        #region Static W2S State

        private const int VIEWPORT_TOLERANCE = 800;

        /// <summary>True if the CameraManager is active and reading data.</summary>
        public static bool IsActive { get; private set; }

        /// <summary>Game Viewport width (pixels).</summary>
        public static int ViewportWidth { get; private set; }

        /// <summary>Game Viewport height (pixels).</summary>
        public static int ViewportHeight { get; private set; }

        /// <summary>Center of the game viewport.</summary>
        public static Vector2 ViewportCenter => new(ViewportWidth / 2f, ViewportHeight / 2f);

        /// <summary>True if the local player's optic camera is active (scope).</summary>
        public static bool IsScoped { get; private set; }

        /// <summary>True if the local player is Aiming Down Sights.</summary>
        public static bool IsADS { get; private set; }

        /// <summary>Current camera vertical FOV (degrees). 0 if not yet read.</summary>
        public static float CurrentFov => _fov;

        /// <summary>Current camera aspect ratio (W/H). 0 if not yet read.</summary>
        public static float CurrentAspect => _aspect;

        private static float _fov;
        private static float _aspect;
        private static readonly ViewMatrix _viewMatrix = new();

        private static float _jitterX;
        private static float _jitterY;

        // Cached scoped projection values — recomputed in UpdateCamera when FOV/Aspect changes,
        // avoids MathF.Cos/Sin on every WorldToScreen call while scoped.
        private static float _scopedScaleX;
        private static float _scopedScaleY;


        /// <summary>
        /// Update the Viewport dimensions for W2S calculations.
        /// Call once at CameraManager init or when config changes.
        /// </summary>
        public static void UpdateViewportRes(int width, int height)
        {
            ViewportWidth = width;
            ViewportHeight = height;
            Log.WriteLine($"[CameraManager] Viewport set to {width}x{height}");
        }

        #endregion

        #region Instance Fields

        /// <summary>FPS Camera pointer (unscoped).</summary>
        public ulong FPSCamera { get; }

        /// <summary>Optic Camera pointer (ads/scoped). May be resolved lazily after construction.</summary>
        public ulong OpticCamera { get; private set; }

        /// <summary>Counter for rate-limiting lazy OpticCamera resolution retries.</summary>
        private int _opticRetryTick;

        /// <summary>How often to retry lazy OpticCamera resolution while ADS (every Nth UpdateCamera call).</summary>
        private const int OpticRetryInterval = 30;

        /// <summary>Counter for rate-limiting the scoped check (4 sequential DMA reads).</summary>
        private int _scopeCheckTick;

        /// <summary>How often to run the full scoped check (every Nth UpdateCamera call).</summary>
        private const int ScopeCheckInterval = 4;

        #endregion

        #region Constructor / Init

        static CameraManager()
        {
            Memory.GameStopped += (_, _) =>
            {
                _eftCameraManagerInstance = default;
                _eftCameraManagerClassPtr = default;
                _allCamerasAddr = default;
                _camGoOffset = default;
                _staticInitDone = false;
                IsActive = false;
                IsScoped = false;
                IsADS = false;
            };
        }

        private CameraManager(ulong fpsCamera, ulong opticCamera)
        {
            FPSCamera = fpsCamera;
            OpticCamera = opticCamera;
            IsActive = true;

            Log.WriteLine($"[CameraManager] FPSCamera:   0x{FPSCamera:X}");
            if (opticCamera != 0)
                Log.WriteLine($"[CameraManager] OpticCamera: 0x{OpticCamera:X}");
            else
                Log.WriteLine("[CameraManager] OpticCamera: not yet resolved (will resolve on ADS)");
        }

        /// <summary>
        /// Non-throwing factory. Returns <c>null</c> when camera pointers cannot
        /// be resolved (e.g. raid still loading). Safe to call repeatedly.
        /// </summary>
        public static CameraManager? TryCreate()
        {
            if (!TryResolveCameras(out var fpsCam, out var opticCam))
                return null;

            return new CameraManager(fpsCam, opticCam);
        }

        /// <summary>
        /// Snapshot of the UnityPlayer-derived camera offsets for the startup health
        /// report. <c>Resolved</c> is false until <see cref="Initialize"/> has run —
        /// camera setup is deferred to the aimview phase, so at startup these values are
        /// still the compiled-in defaults.
        /// </summary>
        internal static (bool Resolved, ulong AllCamerasAddr, uint ViewMatrix, uint Fov, uint Aspect) GetOffsetHealth() =>
            (_staticInitDone, _allCamerasAddr, Camera.ViewMatrix, Camera.FOV, Camera.AspectRatio);

        /// <summary>
        /// Re-runs the AllCameras validation against the currently resolved address.
        /// Used by the health report; safe to call at any time.
        /// </summary>
        internal static bool ValidateResolvedAllCameras() =>
            _allCamerasAddr.IsValidVirtualAddress() && ValidateAllCamerasAddr(_allCamerasAddr);

        /// <summary>
        /// Pre-warms static camera data on game startup (once per game session).
        /// Tries to restore AllCameras address and Camera struct offsets from a
        /// cached file, falling back to signature scans if the cache is stale.
        /// </summary>
        public static void Initialize()
        {
            if (_staticInitDone)
                return;
            try
            {
                if (TryLoadCameraCache())
                {
                    _staticInitDone = true;
                    return;
                }

                _allCamerasAddr = ResolveAllCamerasAddr();
                ResolveCameraOffsets();
                _staticInitDone = true;

                SaveCameraCache();
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[CameraManager] Static init failed: {ex.Message}");
            }
        }

        #endregion

        #region WorldToScreen

        /// <summary>
        /// Translates a 3D world position to a 2D screen position using the live ViewMatrix.
        /// </summary>
        /// <param name="worldPos">World-space position.</param>
        /// <param name="scrPos">Screen-space position (pixels).</param>
        /// <param name="onScreenCheck">If true, returns false when off-screen.</param>
        /// <param name="useTolerance">If true, expands the on-screen check by <see cref="VIEWPORT_TOLERANCE"/>.</param>
        /// <returns>True if the projection succeeded.</returns>
        public static bool WorldToScreen(ref Vector3 worldPos, out Vector2 scrPos, bool onScreenCheck = false, bool useTolerance = false)
        {
            // Reject positions at or near world origin
            if (worldPos.LengthSquared() < 1f)
            {
                scrPos = default;
                return false;
            }

            float w = Vector3.Dot(_viewMatrix.Translation, worldPos) + _viewMatrix.M44;

            if (w < 0.098f)
            {
                scrPos = default;
                return false;
            }

            float x = Vector3.Dot(_viewMatrix.Right, worldPos) + _viewMatrix.M14;
            float y = Vector3.Dot(_viewMatrix.Up, worldPos) + _viewMatrix.M24;

            // TAA / DLSS jitter compensation
            x += _jitterX * w;
            y += _jitterY * w;

            if (IsScoped)
            {
                x *= _scopedScaleX;
                y *= _scopedScaleY;
            }

            var center = ViewportCenter;
            scrPos = new Vector2(
                center.X * (1f + x / w),
                center.Y * (1f - y / w));

            if (onScreenCheck)
            {
                int left = useTolerance ? -VIEWPORT_TOLERANCE : 0;
                int right = useTolerance ? ViewportWidth + VIEWPORT_TOLERANCE : ViewportWidth;
                int top = useTolerance ? -VIEWPORT_TOLERANCE : 0;
                int bottom = useTolerance ? ViewportHeight + VIEWPORT_TOLERANCE : ViewportHeight;

                if (scrPos.X < left || scrPos.X > right ||
                    scrPos.Y < top || scrPos.Y > bottom)
                {
                    scrPos = default;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns the FOV magnitude (distance from screen center) for a screen point.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetFovMagnitude(Vector2 point)
        {
            return Vector2.Distance(ViewportCenter, point);
        }

        /// <summary>
        /// Builds a synthetic ViewMatrix from a world-space position and EFT rotation angles,
        /// using the same transposed convention as the live game view matrix.
        /// </summary>
        public static ViewMatrix BuildViewMatrix(Vector3 position, float yawDeg, float pitchDeg)
        {
            float yaw = yawDeg * (MathF.PI / 180f);
            float pitch = -pitchDeg * (MathF.PI / 180f);

            float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
            float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);

            var forward = new Vector3(sy * cp, sp, cy * cp);
            var right = new Vector3(cy, 0f, -sy);
            var up = new Vector3(-sy * sp, cp, -cy * sp);

            return new ViewMatrix
            {
                Translation = forward,
                Right = right,
                Up = up,
                M44 = -Vector3.Dot(forward, position),
                M14 = -Vector3.Dot(right, position),
                M24 = -Vector3.Dot(up, position),
            };
        }

        #endregion

        #region Scatter Read (Camera Worker)

        /// <summary>
        /// Updates camera data via VmmScatter — called from the camera worker.
        /// Reads ViewMatrix from the active camera and FOV/Aspect from FPS camera.
        /// </summary>
        public void UpdateCamera(LocalPlayer? localPlayer)
        {
            IsADS = localPlayer?.IsADS ?? false;

            // Lazy optic camera resolution — retry while ADS is active until it succeeds.
            // Throttled so we don't spam DMA reads every tick when OpticCamera is genuinely
            // unavailable. Optic camera presence is not required for scope detection or
            // scoped projection, but using it gives slightly more accurate scope view matrix.
            if (IsADS && !OpticCamera.IsValidVirtualAddress())
            {
                if (++_opticRetryTick >= OpticRetryInterval)
                {
                    _opticRetryTick = 0;
                    if (TryResolveOpticCameraFromInstance(out var optic) && optic.IsValidVirtualAddress())
                    {
                        OpticCamera = optic;
                        Log.WriteLine($"[CameraManager] OpticCamera lazily resolved: 0x{optic:X}");
                    }
                    else if (_allCamerasAddr.IsValidVirtualAddress())
                    {
                        TryResolveOpticViaAllCameras(out optic);
                        if (optic.IsValidVirtualAddress())
                        {
                            OpticCamera = optic;
                            Log.WriteLine($"[CameraManager] OpticCamera lazily resolved via AllCameras: 0x{optic:X}");
                        }
                    }
                }
            }

            // Rate-limit the scoped check — it does 4 sequential DMA reads.
            // Only re-evaluate every Nth tick; when not ADS, skip entirely.
            if (IsADS && ++_scopeCheckTick >= ScopeCheckInterval)
            {
                _scopeCheckTick = 0;
                IsScoped = CheckIfScoped(localPlayer!);
            }
            else if (!IsADS)
            {
                IsScoped = false;
                _scopeCheckTick = 0;
            }

            bool usingOptic = IsADS && IsScoped && OpticCamera.IsValidVirtualAddress();
            ulong camera = usingOptic ? OpticCamera : FPSCamera;

            Log.WriteRateLimited(AppLogLevel.Debug, "cam_dbg_select", TimeSpan.FromSeconds(1),
                $"[CameraManager] UpdateCamera: IsADS={IsADS} IsScoped={IsScoped} usingCamera={(usingOptic ? "Optic" : "FPS")} " +
                $"FPS=0x{FPSCamera:X} Optic=0x{OpticCamera:X} OpticValid={OpticCamera.IsValidVirtualAddress()}");

            if (!camera.IsValidVirtualAddress())
                return;

            ulong vmAddr = camera + Camera.ViewMatrix;

            // Single scatter: ViewMatrix + FOV + Aspect
            using var scatter = Memory.GetScatter(VmmFlags.NOCACHE);
            scatter.PrepareReadValue<Matrix4x4>(vmAddr);

            if (FPSCamera.IsValidVirtualAddress())
            {
                scatter.PrepareReadValue<float>(FPSCamera + Camera.FOV);
                scatter.PrepareReadValue<float>(FPSCamera + Camera.AspectRatio);
            }

            scatter.Execute();

            // Process ViewMatrix
            if (scatter.ReadValue<Matrix4x4>(vmAddr, out var vm))
            {
                _viewMatrix.Update(ref vm);
                _jitterX = _viewMatrix.JitterX;
                _jitterY = _viewMatrix.JitterY;

                Log.WriteRateLimited(AppLogLevel.Debug, "cam_dbg_vm_sanity", TimeSpan.FromSeconds(1),
                    $"[CameraManager] ViewMatrix @ 0x{vmAddr:X}: |Right|={_viewMatrix.Right.Length():0.###} " +
                    $"|Up|={_viewMatrix.Up.Length():0.###} |Translation|={_viewMatrix.Translation.Length():0.###} " +
                    $"Right·Up={Vector3.Dot(_viewMatrix.Right, _viewMatrix.Up):0.###} M44={_viewMatrix.M44:0.###}");

                // Confirmed by a live capture: turning the camera while standing still swung
                // |Right| from 6.7 to 102.7 and Right·Up as far as cos≈-0.999 (Right and Up
                // nearly ANTI-PARALLEL, not perpendicular) — proof that Camera.ViewMatrix
                // (0x128) is not reading a real view-projection matrix on this build. The
                // sig-scan never flagged it because it silently matched a wrong call site
                // that happens to resolve to the same displacement (0x128) as before the
                // update — no UPDATED, no FAILED, just quietly wrong.
                if (!IsPlausibleBasis(_viewMatrix.Right, _viewMatrix.Up, _viewMatrix.Translation, _viewMatrix.M44))
                    TryAutoDetectViewMatrixOffset(FPSCamera, OpticCamera);
            }

            // Process FOV + Aspect
            if (FPSCamera.IsValidVirtualAddress())
            {
                bool fovChanged = false;
                if (scatter.ReadValue<float>(FPSCamera + Camera.FOV, out var fov) && fov > 1f && fov < 180f)
                {
                    fovChanged = fov != _fov;
                    _fov = fov;
                }

                if (scatter.ReadValue<float>(FPSCamera + Camera.AspectRatio, out var aspect) && aspect > 0.1f && aspect < 5f)
                {
                    fovChanged |= aspect != _aspect;
                    _aspect = aspect;
                }

                // Recompute cached scoped projection scale when FOV/Aspect changes.
                //
                // NOTE: do not "improve" this by folding the optic's ScopeZoomValue in.
                // The FPS camera's own FOV already tracks ADS/scope state (hipfire 65 →
                // ~46 on a 4x → ~35 at max magnification), so the magnification is
                // already accounted for here. Two previous attempts (61a11ae, 7c952ba)
                // multiplied the zoom in on top of that and were both wrong; neither was
                // ever exercised, because CameraManager failed to resolve any camera for
                // the whole period they were authored in.
                if (fovChanged && _fov > 0f && _aspect > 0f)
                {
                    float angleRadHalf = (MathF.PI / 180f) * _fov * 0.5f;
                    float angleCtg = MathF.Cos(angleRadHalf) / MathF.Sin(angleRadHalf);
                    _scopedScaleX = 1f / (angleCtg * _aspect * 0.5f);
                    _scopedScaleY = 1f / (angleCtg * 0.5f);
                }
            }

            Log.WriteRateLimited(AppLogLevel.Debug, "cam_dbg_scale", TimeSpan.FromSeconds(1),
                $"[CameraManager] Scale: IsScoped={IsScoped} usingOptic={usingOptic} fov={_fov:0.#} aspect={_aspect:0.###} " +
                $"scopedScaleX={_scopedScaleX:0.###} scopedScaleY={_scopedScaleY:0.###}");
        }

        #region ViewMatrix Auto-Detect

        /// <summary>True once <see cref="TryAutoDetectViewMatrixOffset"/> has confirmed a good offset.</summary>
        private static bool _viewMatrixConfirmed;

        /// <summary>
        /// A candidate that passed validation once, held until it either proves itself live
        /// (basis direction actually rotates across ticks) or times out.
        /// </summary>
        private static uint? _viewMatrixPendingCandidate;
        private static Vector3 _viewMatrixPendingRight;
        private static int _viewMatrixPendingTicks;
        private static int _viewMatrixPendingReadFailures;

        /// <summary>
        /// Offsets that passed the static plausibility check but were proven static (never
        /// rotated) or stopped validating. Excluded from further search so we don't loop on
        /// the same false positive forever.
        /// </summary>
        private static readonly HashSet<uint> _viewMatrixRejectedOffsets = new();

        // Widened from an initial ±0x400: two consecutive real candidates (0x128, then 0x98)
        // both turned out wrong and neither offset ever surfaced a third candidate within
        // ±0x400 of the current value, meaning the true offset sits further out. Batched via
        // scatter (see TryAutoDetectViewMatrixOffset) so the wider range doesn't cost a
        // sequential DMA round-trip per offset.
        private const uint VmProbeRange = 0x2000;
        private const uint VmProbeStep = 0x8;

        // A real Right/Up pair must be close to perpendicular (cos ≈ 0). The captured failure
        // measured cos ≈ -0.999 (nearly ANTI-parallel) — 0.15 leaves generous room for
        // projection skew while still rejecting anything that isn't genuinely orthogonal.
        private const float VmMaxCosAngle = 0.15f;
        private const float VmMinMag = 0.01f;
        private const float VmMaxMag = 10_000f;
        private const float VmMaxTranslationMag = 1_000_000f;

        // How much the Right vector must rotate (dot product of consecutive reads) before a
        // pending candidate is trusted. 0x98 read EXACTLY (1,0,0)/(0,1,0)/Translation=0 on
        // every single frame across a 20+ second log while the camera was actively rotated —
        // a genuine view matrix cannot stay bit-identical through real camera movement.
        private const float VmMaxUnchangedCos = 0.999f;

        // Give a pending candidate a few seconds of real gameplay to prove it rotates before
        // giving up on it and moving to the next candidate in the probe range.
        private const int VmPendingTimeoutTicks = 180;

        // A candidate that fails to re-validate on a single tick is NOT necessarily wrong —
        // this environment's scatter reads occasionally fail transiently (see the
        // "Rotation scatter read failed" / "Position scatter read failed" warnings that show
        // up elsewhere in the same logs). 0xC8 passed once and was permanently blacklisted the
        // very next tick under the old one-strike rule, which is exactly the kind of DMA
        // hiccup this tolerance is meant to survive.
        private const int VmMaxConsecutiveReadFailures = 5;

        /// <summary>Resets auto-detect state so a game restart re-probes. Call on game stop.</summary>
        internal static void ResetViewMatrixDetection()
        {
            _viewMatrixConfirmed = false;
            _viewMatrixPendingCandidate = null;
            _viewMatrixPendingTicks = 0;
            _viewMatrixPendingReadFailures = 0;
            _viewMatrixRejectedOffsets.Clear();
        }

        /// <summary>
        /// True if <paramref name="right"/>/<paramref name="up"/>/<paramref name="translation"/>/
        /// <paramref name="m44"/> look like they came from a genuine view-projection matrix:
        /// non-degenerate magnitude and close to perpendicular. Exactly <c>M44 == 0</c> is
        /// rejected outright — every captured failure showed precisely 0, which a real
        /// camera-space w-component essentially never is. <paramref name="translation"/> must
        /// also be non-degenerate: it encodes the camera's world position dotted into the view
        /// basis, so it is exactly zero only for a camera sitting at the world origin — never
        /// true in a raid. A candidate at 0x98 read Translation=(0,0,0) on every frame and
        /// passed every other check here, which is exactly what broke ESP distance/scale
        /// (WorldToScreen's w = Dot(Translation, worldPos) + M44 collapsed to a constant).
        /// </summary>
        private static bool IsPlausibleBasis(Vector3 right, Vector3 up, Vector3 translation, float m44)
        {
            if (m44 == 0f)
                return false;
            if (!float.IsFinite(right.X) || !float.IsFinite(right.Y) || !float.IsFinite(right.Z))
                return false;
            if (!float.IsFinite(up.X) || !float.IsFinite(up.Y) || !float.IsFinite(up.Z))
                return false;
            if (!float.IsFinite(translation.X) || !float.IsFinite(translation.Y) || !float.IsFinite(translation.Z))
                return false;

            float rightLen = right.Length();
            float upLen = up.Length();
            if (rightLen < VmMinMag || rightLen > VmMaxMag)
                return false;
            if (upLen < VmMinMag || upLen > VmMaxMag)
                return false;

            float translationLen = translation.Length();
            if (translationLen < VmMinMag || translationLen > VmMaxTranslationMag)
                return false;

            float cos = Vector3.Dot(right, up) / (rightLen * upLen);
            return MathF.Abs(cos) < VmMaxCosAngle;
        }

        private static bool TryExtractBasis(ulong addr, out Vector3 right, out Vector3 up, out Vector3 translation, out float m44)
        {
            right = default;
            up = default;
            translation = default;
            m44 = 0f;

            if (!Memory.TryReadValue<Matrix4x4>(addr, out var m, false))
                return false;

            m44 = m.M44;
            right = new Vector3(m.M11, m.M21, m.M31);
            up = new Vector3(m.M12, m.M22, m.M32);
            // Same convention as ViewMatrix.Update: Translation = (M14, M24, M34).
            translation = new Vector3(m.M14, m.M24, m.M34);
            return true;
        }

        /// <summary>
        /// Validates a candidate ViewMatrix offset against the FPS camera. The FPS camera is
        /// always live, so it alone is authoritative.
        /// <para>
        /// Deliberately does NOT require the optic camera's copy at the same offset to also
        /// pass: 0xC8 passed once and then failed 5 straight rechecks ~20ms apart while the
        /// player stood still not scoped — far too fast and regular to be a random DMA
        /// hiccup. Unity has no reason to keep a non-rendering camera's view matrix current,
        /// so the optic camera's field at this offset is very likely stale/garbage whenever
        /// it isn't actually the one rendering (i.e. whenever not ADS+scoped) — cross-checking
        /// against it was killing genuinely correct FPS-camera candidates.
        /// </para>
        /// </summary>
        private static bool TryValidateViewMatrixOffset(uint offset, ulong fpsCamera, ulong opticCamera, out Vector3 fpsRight)
        {
            fpsRight = default;

            if (!TryExtractBasis(fpsCamera + offset, out var right, out var up, out var translation, out var m44))
                return false;
            if (!IsPlausibleBasis(right, up, translation, m44))
                return false;

            fpsRight = right;
            return true;
        }

        /// <summary>
        /// Searches ±<see cref="VmProbeRange"/> of the current <see cref="Camera.ViewMatrix"/>
        /// for an offset that behaves like a real, LIVE view-projection matrix. A candidate
        /// must pass the static plausibility checks AND show its Right vector actually rotate
        /// across ticks before being trusted — a static/default field (e.g. an identity matrix
        /// baked into the Camera object) can pass every static check forever without ever
        /// moving, which is exactly how 0x98 slipped through the old "confirm twice" logic
        /// (which re-checked the SAME frozen value against itself and trivially matched).
        /// </summary>
        private static void TryAutoDetectViewMatrixOffset(ulong fpsCamera, ulong opticCamera)
        {
            if (_viewMatrixConfirmed || !fpsCamera.IsValidVirtualAddress())
                return;

            Log.WriteRateLimited(AppLogLevel.Warning, "cam_vm_search", TimeSpan.FromSeconds(2),
                $"[CameraManager] Camera.ViewMatrix (0x{Camera.ViewMatrix:X}) failed validation " +
                "— searching for a replacement.");

            if (_viewMatrixPendingCandidate is uint pending)
            {
                _viewMatrixPendingTicks++;

                if (TryValidateViewMatrixOffset(pending, fpsCamera, opticCamera, out var right))
                {
                    _viewMatrixPendingReadFailures = 0;

                    float cos = Vector3.Dot(right, _viewMatrixPendingRight);
                    if (cos < VmMaxUnchangedCos)
                    {
                        Log.WriteLine($"[CameraManager] Auto-detected Camera.ViewMatrix: " +
                                      $"0x{Camera.ViewMatrix:X}→0x{pending:X} (basis rotated between reads, cos={cos:0.###}).");
                        Camera.ViewMatrix = pending;
                        _viewMatrixConfirmed = true;
                        _viewMatrixPendingCandidate = null;
                        SaveCameraCache();
                        return;
                    }

                    if (_viewMatrixPendingTicks < VmPendingTimeoutTicks)
                        return; // still plausible, just hasn't rotated yet this tick — keep waiting

                    Log.WriteLine($"[CameraManager] Camera.ViewMatrix candidate 0x{pending:X} never " +
                                  $"rotated across {_viewMatrixPendingTicks} reads — rejecting as static, resuming search.");
                    _viewMatrixRejectedOffsets.Add(pending);
                    _viewMatrixPendingCandidate = null;
                    _viewMatrixPendingTicks = 0;
                    _viewMatrixPendingReadFailures = 0;
                }
                else
                {
                    _viewMatrixPendingReadFailures++;
                    if (_viewMatrixPendingReadFailures < VmMaxConsecutiveReadFailures)
                    {
                        Log.WriteLine($"[CameraManager] Camera.ViewMatrix candidate 0x{pending:X} failed to " +
                                      $"read/validate this tick ({_viewMatrixPendingReadFailures}/{VmMaxConsecutiveReadFailures}) " +
                                      "— keeping it pending in case this was a transient DMA read failure.");
                        return; // give it more chances before giving up — don't blacklist on one bad read
                    }

                    Log.WriteLine($"[CameraManager] Camera.ViewMatrix candidate 0x{pending:X} failed to " +
                                  $"validate {_viewMatrixPendingReadFailures} times in a row — giving up on it, resuming search.");
                    _viewMatrixRejectedOffsets.Add(pending);
                    _viewMatrixPendingCandidate = null;
                    _viewMatrixPendingTicks = 0;
                    _viewMatrixPendingReadFailures = 0;
                }
            }

            uint origin = Camera.ViewMatrix;
            uint lo = origin > VmProbeRange ? origin - VmProbeRange : 0;
            uint hi = origin + VmProbeRange;

            // ±0x2000 in 8-byte steps is ~1024 offsets — a sequential TryReadValue per offset
            // would mean ~1024 individual DMA round-trips on every single failed UpdateCamera
            // call. Batched into one scatter round-trip the same way
            // TransformHierarchy.FindPointerCandidates is, this stays cheap enough to run
            // every tick instead of needing a separate throttle. FPS camera only — see
            // TryValidateViewMatrixOffset for why the optic camera isn't cross-checked here.
            using var scatter = Memory.GetScatter(VmmSharpEx.Options.VmmFlags.NOCACHE);
            for (uint off = lo; off <= hi; off += VmProbeStep)
            {
                if (_viewMatrixRejectedOffsets.Contains(off))
                    continue;
                scatter.PrepareReadValue<Matrix4x4>(fpsCamera + off);
            }
            scatter.Execute();

            for (uint off = lo; off <= hi; off += VmProbeStep)
            {
                if (_viewMatrixRejectedOffsets.Contains(off))
                    continue;
                if (!scatter.ReadValue<Matrix4x4>(fpsCamera + off, out var m))
                    continue;

                var right = new Vector3(m.M11, m.M21, m.M31);
                var up = new Vector3(m.M12, m.M22, m.M32);
                var translation = new Vector3(m.M14, m.M24, m.M34);
                if (!IsPlausibleBasis(right, up, translation, m.M44))
                    continue;

                Log.WriteLine($"[CameraManager] Camera.ViewMatrix candidate 0x{off:X} passed static " +
                              "checks — waiting for the camera to rotate to confirm it's live, not static.");
                _viewMatrixPendingCandidate = off;
                _viewMatrixPendingRight = right;
                _viewMatrixPendingTicks = 0;
                _viewMatrixPendingReadFailures = 0;
                return;
            }

            Log.WriteRateLimited(AppLogLevel.Warning, "cam_vm_fail", TimeSpan.FromSeconds(10),
                $"[CameraManager] Camera.ViewMatrix auto-detect found no valid candidate in " +
                $"±0x{VmProbeRange:X} of 0x{origin:X}. ESP projection will be wrong.");
        }

        #endregion

        /// <summary>
        /// Reads <c>CameraManager.Instance → OpticCameraManager → CurrentOpticSight</c>:
        /// the game's own record of which optic sight is being looked through right now.
        /// Null/zero means the player is not behind a scope, whatever else is bolted to the
        /// weapon.
        /// <para>
        /// Returns <c>null</c> when the chain could not be read at all, so the caller can
        /// distinguish "not scoped" from "don't know" and fall back rather than guess.
        /// </para>
        /// </summary>
        private static bool? TryReadCurrentOpticSight(out ulong sight)
        {
            sight = 0;

            if (!_eftCameraManagerInstance.IsValidVirtualAddress())
                return null;

            if (!Memory.TryReadPtr(_eftCameraManagerInstance + Offsets.EFTCameraManager.OpticCameraManager,
                                   out var opticCameraManager, false))
                return null;

            // Read as a raw value, not TryReadPtr: a genuinely null CurrentOpticSight is the
            // answer "not scoped", and must not be confused with a failed read.
            if (!Memory.TryReadValue<ulong>(opticCameraManager + Offsets.OpticCameraManager.CurrentOpticSight,
                                            out var raw, false))
                return null;

            sight = raw;
            return raw.IsValidVirtualAddress();
        }

        /// <summary>
        /// Checks whether the local player is currently looking through a magnified optic.
        /// </summary>
        private bool CheckIfScoped(LocalPlayer localPlayer)
        {
            try
            {
                // Ask the game which sight is active.
                //
                // The old test — SightComponent.ScopeZoomValue > 1 over every mounted optic —
                // is not a scoped test at all. Captures show that field reading 26.5 and 3.2
                // on different sights and staying pinned while magnification changed, and any
                // value it returns clears the "> 1" bar, so IsScoped was effectively just
                // IsADS. Aiming through a second mount therefore kept projecting through the
                // optic camera with scoped scaling applied, which is the "only correct on the
                // main scope" symptom. CurrentOpticSight answers the actual question and
                // changes as the player switches sights.
                var viaSight = TryReadCurrentOpticSight(out var sightPtr);
                if (viaSight.HasValue)
                {
                    Log.WriteRateLimited(AppLogLevel.Debug, "scope_dbg_cursight", TimeSpan.FromSeconds(1),
                        $"[CameraManager] CheckIfScoped: CurrentOpticSight=0x{sightPtr:X} → scoped={viaSight.Value}");
                    return viaSight.Value;
                }

                Log.WriteRateLimited(AppLogLevel.Debug, "scope_dbg_cursight_fail", TimeSpan.FromSeconds(5),
                    $"[CameraManager] CheckIfScoped: CurrentOpticSight unreadable " +
                    $"(instance=0x{_eftCameraManagerInstance:X}) — falling back to sight-list probe.");

                // Fallback only — see above for why this test is not trustworthy.
                if (localPlayer.PWA == 0)
                {
                    Log.WriteRateLimited(AppLogLevel.Debug, "scope_dbg_pwa", TimeSpan.FromSeconds(2),
                        "[CameraManager] CheckIfScoped: PWA is 0.");
                    return false;
                }

                if (!Memory.TryReadPtr(localPlayer.PWA + Offsets.ProceduralWeaponAnimation._optics, out var opticsPtr, false))
                {
                    Log.WriteRateLimited(AppLogLevel.Debug, "scope_dbg_optptr", TimeSpan.FromSeconds(2),
                        $"[CameraManager] CheckIfScoped: failed to read _optics ptr @ 0x{localPlayer.PWA:X}.");
                    return false;
                }

                using var optics = MemList<ulong>.Get(opticsPtr);
                if (optics.Count <= 0)
                {
                    Log.WriteRateLimited(AppLogLevel.Debug, "scope_dbg_optlist", TimeSpan.FromSeconds(2),
                        "[CameraManager] CheckIfScoped: optics list is empty.");
                    return false;
                }

                // A weapon can carry more than one sight (e.g. red dot + flip-to-side
                // magnifier, or an offset iron sight next to a scope). optics[0] is just
                // whichever mount is first in the list, not necessarily the one the player
                // is currently looking through — so check all of them and treat "scoped"
                // as true if ANY mounted optic reports real magnification. We don't need to
                // identify which one is actually active: the scale itself is derived from
                // the FPS camera's own live FOV (see UpdateCamera), which already tracks
                // whichever sight is in use.
                int checkedCount = Math.Min(optics.Count, 8);
                for (int i = 0; i < checkedCount; i++)
                {
                    var pSightComponent = Memory.ReadPtr(optics[i] + Offsets.SightNBone.Mod, false);
                    if (!pSightComponent.IsValidVirtualAddress())
                        continue;

                    var scopeZoomValue = Memory.ReadValue<float>(pSightComponent + Offsets.SightComponent.ScopeZoomValue, false);
                    Log.WriteRateLimited(AppLogLevel.Debug, "scope_dbg_zoom", TimeSpan.FromSeconds(1),
                        $"[CameraManager] CheckIfScoped: opticsCount={optics.Count} i={i} pSight=0x{pSightComponent:X} scopeZoomValue={scopeZoomValue:0.###}");

                    if (scopeZoomValue > 1f)
                        return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Debug, "scope_dbg_ex", TimeSpan.FromSeconds(5),
                    $"[CameraManager] CheckIfScoped exception: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Camera Resolution

        /// <summary>
        /// Multi-path resolver:
        ///  1) EFT.CameraControl.CameraManager.Instance
        ///  2) Unity AllCameras + GameObject name search
        /// </summary>
        private static bool TryResolveCameras(out ulong fpsCamera, out ulong opticCamera)
        {
            _eftCameraManagerInstance = FindCameraManagerInstance();
            if (_eftCameraManagerInstance.IsValidVirtualAddress())
            {
                Log.WriteLine($"[CameraManager] CameraManager.Instance @ 0x{_eftCameraManagerInstance:X}");
                if (TryResolveViaCameraManagerInstance(out fpsCamera, out opticCamera))
                {
                    Log.WriteLine($"[CameraManager] Using CameraManager.Instance — FPS: 0x{fpsCamera:X}, Optic: {(opticCamera != 0 ? $"0x{opticCamera:X}" : "deferred")}");
                    return true;
                }
                Log.WriteLine("[CameraManager] Instance found but camera fields unreadable — falling back.");
            }

            if (TryResolveViaAllCamerasByName(out fpsCamera, out opticCamera))
            {
                Log.WriteLine("[CameraManager] Using Unity AllCameras fallback.");
                return true;
            }

            fpsCamera = 0;
            opticCamera = 0;
            Log.WriteLine("[CameraManager] Could not resolve cameras via any path.");
            return false;
        }

        /// <summary>
        /// Primary path: use EFT.CameraControl.CameraManager.Instance.
        /// </summary>
        private static bool TryResolveViaCameraManagerInstance(out ulong fpsCamera, out ulong opticCamera)
        {
            fpsCamera = 0;
            opticCamera = 0;

            if (!_eftCameraManagerInstance.IsValidVirtualAddress())
                return false;

            // FPS camera (required)
            if (!Memory.TryReadPtr(_eftCameraManagerInstance + Offsets.EFTCameraManager.Camera, out var fpsCameraRef, false))
                return false;

            if (!TryReadObjectClassName(fpsCameraRef, out var name, 32)
                || !string.Equals(name, "Camera", StringComparison.Ordinal))
                return false;

            if (!Memory.TryReadPtr(fpsCameraRef + ObjectClass.MonoBehaviourOffset, out fpsCamera, false))
                return false;

            if (!ValidateCameraMatrix(fpsCamera))
            {
                fpsCamera = 0;
                return false;
            }

            // Optic camera (optional — resolved lazily when ADS is detected)
            TryResolveOpticCameraFromInstance(out opticCamera);

            return true;
        }

        /// <summary>
        /// Best-effort optic camera resolution from the CameraManager.Instance.
        /// Failures are silently ignored — optic camera is optional.
        /// </summary>
        private static bool TryResolveOpticCameraFromInstance(out ulong opticCamera)
        {
            opticCamera = 0;

            if (!_eftCameraManagerInstance.IsValidVirtualAddress())
                return false;

            if (!Memory.TryReadPtr(_eftCameraManagerInstance + Offsets.EFTCameraManager.OpticCameraManager, out var opticCameraManager, false))
                return false;

            if (!Memory.TryReadPtr(opticCameraManager + Offsets.OpticCameraManager.Camera, out var opticCameraRef, false))
                return false;

            if (!TryReadObjectClassName(opticCameraRef, out var name, 32)
                || !string.Equals(name, "Camera", StringComparison.Ordinal))
                return false;

            if (!Memory.TryReadPtr(opticCameraRef + ObjectClass.MonoBehaviourOffset, out opticCamera, false))
                return false;

            return true;
        }

        /// <summary>
        /// Lazy optic-only resolution via AllCameras list.
        /// </summary>
        private static bool TryResolveOpticViaAllCameras(out ulong opticCamera)
        {
            opticCamera = 0;
            try
            {
                if (!_allCamerasAddr.IsValidVirtualAddress())
                    return false;

                if (!Memory.TryReadPtr(_allCamerasAddr, out var allCamerasPtr, false))
                    return false;

                if (!Memory.TryReadPtr(allCamerasPtr + 0x0, out var itemsPtr, false) ||
                    !Memory.TryReadValue<int>(allCamerasPtr + 0x8, out var count, false))
                    return false;

                if (!itemsPtr.IsValidVirtualAddress() || count <= 0 || count > 1024)
                    return false;

                FindCamerasByName(itemsPtr, count, out _, out opticCamera);
                return opticCamera.IsValidVirtualAddress();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Backup path: Unity AllCameras static + GameObject name search.
        /// </summary>
        private static bool TryResolveViaAllCamerasByName(out ulong fpsCamera, out ulong opticCamera)
        {
            fpsCamera = 0;
            opticCamera = 0;

            try
            {
                if (!_allCamerasAddr.IsValidVirtualAddress())
                {
                    Log.WriteRateLimited(AppLogLevel.Debug, "allcam_dbg_addr", TimeSpan.FromSeconds(5),
                        "[CameraManager] AllCameras fallback: _allCamerasAddr invalid.");
                    return false;
                }

                if (!Memory.TryReadPtr(_allCamerasAddr, out var allCamerasPtr, false))
                {
                    Log.WriteRateLimited(AppLogLevel.Debug, "allcam_dbg_ptr", TimeSpan.FromSeconds(5),
                        $"[CameraManager] AllCameras fallback: failed to read list ptr @ 0x{_allCamerasAddr:X}.");
                    return false;
                }

                if (!Memory.TryReadPtr(allCamerasPtr + 0x0, out var itemsPtr, false) ||
                    !Memory.TryReadValue<int>(allCamerasPtr + 0x8, out var count, false))
                {
                    Log.WriteRateLimited(AppLogLevel.Debug, "allcam_dbg_items", TimeSpan.FromSeconds(5),
                        $"[CameraManager] AllCameras fallback: failed to read items/count @ 0x{allCamerasPtr:X}.");
                    return false;
                }

                if (!itemsPtr.IsValidVirtualAddress() || count <= 0 || count > 1024)
                {
                    Log.WriteRateLimited(AppLogLevel.Debug, "allcam_dbg_count", TimeSpan.FromSeconds(5),
                        $"[CameraManager] AllCameras fallback: itemsPtr=0x{itemsPtr:X} count={count} (out of range).");
                    return false;
                }

                FindCamerasByName(itemsPtr, count, out fpsCamera, out opticCamera);

                Log.WriteRateLimited(AppLogLevel.Debug, "allcam_dbg_found", TimeSpan.FromSeconds(5),
                    $"[CameraManager] AllCameras fallback: scanned {count} cameras, fps=0x{fpsCamera:X} optic=0x{opticCamera:X}");

                if (!fpsCamera.IsValidVirtualAddress() || !ValidateCameraMatrix(fpsCamera))
                    fpsCamera = 0;

                if (!opticCamera.IsValidVirtualAddress())
                    opticCamera = 0;

                return fpsCamera != 0; // Optic camera is optional
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[CameraManager] AllCameras fallback error: {ex.Message}");
                fpsCamera = 0;
                opticCamera = 0;
                return false;
            }
        }

        /// <summary>
        /// Candidate native Component → GameObject offsets, most likely first.
        /// <see cref="Comp_GameObject"/> (0x58) is the authoritative value for EFT's
        /// Unity 2022 build; the others cover layouts seen in adjacent Unity versions
        /// (0x38 = Unity 6 / Arena) so a future engine bump degrades to a probe instead
        /// of a hard failure. The winner is cached in <see cref="_camGoOffset"/>.
        /// </summary>
        private static readonly uint[] CameraGameObjectOffsets = [Comp_GameObject, 0x38, 0x30, GO_ObjectClass];

        /// <summary>
        /// Scans AllCameras list for "FPS Camera" / "Optic Camera" style names.
        /// </summary>
        private static void FindCamerasByName(ulong itemsPtr, int count, out ulong fpsCamera, out ulong opticCamera)
        {
            fpsCamera = 0;
            opticCamera = 0;

            int max = Math.Min(count, 100);
            List<string>? seenNames = Log.EnableDebugLogging ? new List<string>(max) : null;

            for (int i = 0; i < max; i++)
            {
                ulong entryAddr = itemsPtr + (uint)(i * 0x8);
                if (!Memory.TryReadPtr(entryAddr, out var cameraPtr, false))
                    continue;

                if (!TryReadCameraGameObjectName(cameraPtr, out var name) || name is not string goName)
                    continue;

                seenNames?.Add(goName);

                bool isFps =
                    goName.Contains("FPS", StringComparison.OrdinalIgnoreCase) &&
                    goName.Contains("Camera", StringComparison.OrdinalIgnoreCase);

                bool isOptic =
                    (goName.Contains("Optic", StringComparison.OrdinalIgnoreCase) ||
                     goName.Contains("BaseOptic", StringComparison.OrdinalIgnoreCase)) &&
                    goName.Contains("Camera", StringComparison.OrdinalIgnoreCase);

                if (isFps && fpsCamera == 0)
                    fpsCamera = cameraPtr;

                if (isOptic && opticCamera == 0)
                    opticCamera = cameraPtr;

                if (fpsCamera != 0 && opticCamera != 0)
                    break;
            }

            if (seenNames is not null && fpsCamera == 0)
            {
                Log.WriteRateLimited(AppLogLevel.Debug, "allcam_dbg_names", TimeSpan.FromSeconds(5),
                    $"[CameraManager] AllCameras fallback: no FPS match among {seenNames.Count} named cameras " +
                    $"(goOffset={(_camGoOffset.HasValue ? $"0x{_camGoOffset.Value:X}" : "unresolved")}): [{string.Join(", ", seenNames)}]");
            }
        }

        /// <summary>
        /// Reads the GameObject name of a native Unity Camera from the AllCameras list:
        /// <c>Camera + Comp_GameObject → GameObject + GO_Name → C-string</c>.
        /// <para>
        /// The Component → GameObject offset is engine-version specific, so the first
        /// successful read probes <see cref="CameraGameObjectOffsets"/> and caches the
        /// winner. Names are validated as printable ASCII — a bad offset yields a
        /// pointer-shaped garbage read that would otherwise pass as a "name".
        /// </para>
        /// </summary>
        private static bool TryReadCameraGameObjectName(ulong cameraPtr, out string? name)
        {
            if (_camGoOffset is uint known)
                return TryReadCameraGameObjectName(cameraPtr, known, out name);

            foreach (var candidate in CameraGameObjectOffsets)
            {
                if (!TryReadCameraGameObjectName(cameraPtr, candidate, out name))
                    continue;

                _camGoOffset = candidate;
                Log.WriteLine($"[CameraManager] Camera→GameObject offset resolved: 0x{candidate:X} (first name: '{name}')");
                return true;
            }

            name = null;
            return false;
        }

        private static bool TryReadCameraGameObjectName(ulong cameraPtr, uint goOffset, out string? name)
        {
            name = null;

            if (!Memory.TryReadPtr(cameraPtr + goOffset, out var gameObject, false))
                return false;

            if (!Memory.TryReadPtr(gameObject + GO_Name, out var namePtr, false))
                return false;

            // GameObject names are native C-strings (UTF-8), not Unity managed strings
            if (!Memory.TryReadString(namePtr, out name, 64, false) || !IsPlausibleObjectName(name))
            {
                name = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// True if <paramref name="s"/> looks like a real Unity object name (non-empty,
        /// printable ASCII). Rejects the mojibake that a wrong pointer chain produces.
        /// </summary>
        private static bool IsPlausibleObjectName(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return false;

            foreach (var c in s)
            {
                if (c < ' ' || c > '~')
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Quick sanity check for a camera's view matrix.
        /// </summary>
        private static bool ValidateCameraMatrix(ulong cameraPtr)
        {
            if (!Memory.TryReadValue<Matrix4x4>(cameraPtr + Camera.ViewMatrix, out var vm, false))
                return false;

            if (float.IsNaN(vm.M11) || float.IsInfinity(vm.M11) ||
                float.IsNaN(vm.M22) || float.IsInfinity(vm.M22) ||
                float.IsNaN(vm.M33) || float.IsInfinity(vm.M33) ||
                float.IsNaN(vm.M44) || float.IsInfinity(vm.M44))
                return false;

            if (vm.M11 == 0f && vm.M22 == 0f && vm.M33 == 0f && vm.M44 == 0f)
                return false;

            if (Math.Abs(vm.M41) > 5000f || Math.Abs(vm.M42) > 5000f || Math.Abs(vm.M43) > 5000f)
                return false;

            return true;
        }

        /// <summary>
        /// Reads the ObjectClass name from a given object pointer (ObjectClass → +0x0 → +0x10 → C-string).
        /// This is an IL2CPP class name (null-terminated UTF-8), NOT a Unity managed string.
        /// </summary>
        private static bool TryReadObjectClassName(ulong objectClassPtr, out string? name, int maxLen)
        {
            name = null;
            if (!objectClassPtr.IsValidVirtualAddress())
                return false;

            if (!Memory.TryReadPtrChain(objectClassPtr, ObjClass_ToNamePtr, out var namePtr, false))
                return false;

            return Memory.TryReadString(namePtr, out name, maxLen, false) && !string.IsNullOrEmpty(name);
        }

        #endregion

        #region CameraManager.Instance Resolution

        /// <summary>
        /// Pattern scan to find EFT.CameraControl.CameraManager.Instance via GameAssembly.dll.
        /// </summary>
        private static ulong FindCameraManagerInstance()
        {
            // Preferred: TypeInfoTable → klass → static fields. The type index is resolved
            // by name on every dump, so this works whenever the class is in the table.
            //
            // The RVA pattern scan below cannot be relied on: GetInstance_RVA only holds a
            // real value when the dumper resolves the get_Instance METHOD, and a partial
            // dump silently leaves the (stale) hardcoded 0x1221240 in place. FindInstance
            // then fails, resolution drops to the AllCameras name scan, and that yields a
            // DIFFERENT optic camera object — captures show aspect 1 via this path vs 1.778
            // via AllCameras. That non-determinism made behaviour vary run to run.
            var viaTable = TryResolveInstanceViaTypeInfoTable();
            if (viaTable != 0)
                return viaTable;

            try
            {
                var gameAssemblyBase = Memory.GameAssemblyBase;
                if (!gameAssemblyBase.IsValidVirtualAddress())
                    return 0;

                ulong methodAddr = gameAssemblyBase + Offsets.EFTCameraManager.GetInstance_RVA;

                Span<byte> methodBytes = stackalloc byte[128];
                if (!Memory.TryReadBuffer(methodAddr, methodBytes, false))
                    return 0;

                // Pattern 1: lea rcx, [rip+offset] → class metadata
                for (int i = 0; i < methodBytes.Length - 7; i++)
                {
                    if (methodBytes[i] == 0x48 && methodBytes[i + 1] == 0x8D && methodBytes[i + 2] == 0x0D)
                    {
                        int disp32 = BitConverter.ToInt32(methodBytes.Slice(i + 3, 4));
                        ulong classMetadataAddr = methodAddr + (ulong)i + 7 + (ulong)disp32;

                        if (!Memory.TryReadPtr(classMetadataAddr, out var classPtr, false))
                            continue;

                        var knownOffset = Offsets.Il2CppClass.StaticFields;
                        ReadOnlySpan<uint> fallbackOffsets = [knownOffset - 0x10, knownOffset - 0x08, knownOffset + 0x08, knownOffset + 0x10, knownOffset + 0x18];

                        if (TryReadStaticInstance(classPtr, knownOffset, out var instance))
                        {
                            _eftCameraManagerClassPtr = classPtr;
                            return instance;
                        }

                        foreach (var offset in fallbackOffsets)
                        {
                            if (offset == knownOffset) continue;
                            if (TryReadStaticInstance(classPtr, offset, out instance))
                            {
                                _eftCameraManagerClassPtr = classPtr;
                                return instance;
                            }
                        }
                    }
                }

                // Pattern 2: mov rax, [rip+offset] → direct static field
                for (int i = 32; i < methodBytes.Length - 7; i++)
                {
                    if (methodBytes[i] == 0x48 && methodBytes[i + 1] == 0x8B && methodBytes[i + 2] == 0x05)
                    {
                        int disp32 = BitConverter.ToInt32(methodBytes.Slice(i + 3, 4));
                        ulong staticFieldAddr = methodAddr + (ulong)i + 7 + (ulong)disp32;

                        if (!Memory.TryReadPtr(staticFieldAddr, out var instancePtr, false))
                            continue;

                        if (Memory.TryReadPtr(instancePtr + Offsets.EFTCameraManager.Camera, out var testCamera, false)
                            && testCamera.IsValidVirtualAddress())
                            return instancePtr;
                    }
                }

                Log.WriteRateLimited(AppLogLevel.Debug, "cm_dbg_rva", TimeSpan.FromSeconds(10),
                    $"[CameraManager] FindInstance: no pattern matched @ 0x{methodAddr:X} " +
                    $"(rva=0x{Offsets.EFTCameraManager.GetInstance_RVA:X}). Expected when the dump did not " +
                    "resolve get_Instance; the TypeInfoTable path above is the real route.");

                return 0;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[CameraManager] FindInstance error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Resolves <c>CameraManager.Instance</c> through the IL2CPP TypeInfoTable, the same
        /// route <see cref="IL2CPP.BtrControllerResolver"/> uses. Needs no function RVA and no
        /// byte-pattern scan, so it survives a partial dump.
        /// <para>
        /// The static field's own offset is not in the schema, so the few plausible slots are
        /// probed and each candidate is validated by requiring <c>instance + Camera</c> to
        /// point at an object whose IL2CPP class is <c>Camera</c> — the same check the caller
        /// applies.
        /// </para>
        /// </summary>
        private static ulong TryResolveInstanceViaTypeInfoTable()
        {
            try
            {
                var gaBase = Memory.GameAssemblyBase;
                var typeIndex = Offsets.Special.CameraManager_TypeIndex;

                if (!gaBase.IsValidVirtualAddress() || typeIndex == 0 || Offsets.Special.TypeInfoTableRva == 0)
                    return 0;

                if (!Memory.TryReadPtr(gaBase + Offsets.Special.TypeInfoTableRva, out var tablePtr, false)
                    || !tablePtr.IsValidVirtualAddress())
                    return 0;

                if (!Memory.TryReadPtr(tablePtr + (ulong)typeIndex * 8, out var klassPtr, false)
                    || !klassPtr.IsValidVirtualAddress())
                    return 0;

                if (!Memory.TryReadPtr(klassPtr + Offsets.Il2CppClass.StaticFields, out var staticFields, false)
                    || !staticFields.IsValidVirtualAddress())
                    return 0;

                ReadOnlySpan<uint> slots = [0x0, 0x8, 0x10, 0x18];
                foreach (var slot in slots)
                {
                    if (!Memory.TryReadPtr(staticFields + slot, out var candidate, false)
                        || !candidate.IsValidVirtualAddress())
                        continue;

                    if (!Memory.TryReadPtr(candidate + Offsets.EFTCameraManager.Camera, out var cameraRef, false)
                        || !TryReadObjectClassName(cameraRef, out var name, 32)
                        || !string.Equals(name, "Camera", StringComparison.Ordinal))
                        continue;

                    Log.WriteLine($"[CameraManager] Instance via TypeInfoTable: 0x{candidate:X} " +
                                  $"(typeIndex={typeIndex}, klass=0x{klassPtr:X}, staticFields+0x{slot:X})");
                    _eftCameraManagerClassPtr = klassPtr;
                    return candidate;
                }

                return 0;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[CameraManager] TypeInfoTable Instance resolution error: {ex.Message}");
                return 0;
            }
        }

        private static bool TryReadStaticInstance(ulong classPtr, uint staticFieldsOffset, out ulong instance)
        {
            instance = 0;

            if (!Memory.TryReadPtr(classPtr + staticFieldsOffset, out var staticFieldsPtr, false))
                return false;

            if (!Memory.TryReadPtr(staticFieldsPtr, out var instancePtr, false))
                return false;

            if (!Memory.TryReadPtr(instancePtr + Offsets.EFTCameraManager.Camera, out var testCamera, false))
                return false;

            instance = instancePtr;
            return true;
        }

        #endregion

        #region AllCameras Resolution

        /// <summary>
        /// Candidate signatures for locating AllCameras global in UnityPlayer.dll.
        /// </summary>
        private static readonly (string Sig, int RelOffset, int InstrLen, string Desc)[] AllCamerasSigs =
        [
            ("48 8B 05 ? ? ? ? 49 C7 C6 ? ? ? ? 8B 48 ? 85 C9 0F 84 ? ? ? ? 48 89 9C 24", 3, 7, "AllCameras: mov rax,[rip]; mov r14,imm; test ecx; jz; mov [rsp],rbx"),
            ("4C 8B 05 ? ? ? ? 33 D2 49 8B 48", 3, 7, "AllCameras: mov r8,[rip]; xor edx; mov rcx,[r8]"),
            ("48 8B 05 ? ? ? ? 49 C7 C6 ? ? ? ? 8B 48 ? 85 C9 0F 84 ? ? ? ? 48 89 B4 24", 3, 7, "AllCameras: mov rax,[rip]; mov r14,imm; test ecx; jz; mov [rsp],rsi"),
            ("48 8B 1D ? ? ? ? 48 8B 73 ? 48 8B 43 ? 48 FF C6", 3, 7, "AllCameras: mov rbx,[rip]; mov rsi,[rbx]; mov rax,[rbx]; inc rsi"),
        ];

        private static ulong ResolveAllCamerasAddr()
        {
            var unityBase = Memory.UnityBase;
            if (!unityBase.IsValidVirtualAddress())
                return 0;

            foreach (var (sig, relOff, instrLen, desc) in AllCamerasSigs)
            {
                var sigAddr = Memory.FindSignature(sig, "UnityPlayer.dll");
                if (sigAddr == 0)
                    continue;

                if (!Memory.TryReadValue<int>(sigAddr + (ulong)relOff, out var disp32, false))
                    continue;

                ulong resolved = sigAddr + (ulong)instrLen + (ulong)(long)disp32;
                if (!resolved.IsValidVirtualAddress())
                    continue;

                if (!Memory.TryReadPtr(resolved, out var listPtr, false))
                    continue;

                if (!Memory.TryReadPtr(listPtr, out var items, false))
                    continue;

                if (!Memory.TryReadValue<int>(listPtr + 0x8, out var count, false))
                    continue;

                if (items.IsValidVirtualAddress() && count >= 0 && count < 1024)
                    return resolved;
            }

            // Fallback: hardcoded RVA. It is version-specific, so after an engine update
            // it points at whatever now lives there. Validate it exactly like a sig hit —
            // `unityBase + AllCameras` is always a well-formed address, so the old
            // IsValidVirtualAddress check accepted a stale offset unconditionally and
            // handed back a garbage list.
            var fallbackAddr = unityBase + AllCameras;
            if (ValidateAllCamerasAddr(fallbackAddr))
            {
                Log.WriteLine($"[CameraManager] AllCameras sig scan missed — hardcoded fallback 0x{AllCameras:X} validated.");
                return fallbackAddr;
            }

            Log.Write(AppLogLevel.Warning,
                $"[CameraManager] AllCameras resolution FAILED — no signature matched and the hardcoded " +
                $"RVA 0x{AllCameras:X} does not validate against this UnityPlayer build.");
            return 0;
        }

        private static bool ValidateAllCamerasAddr(ulong addr)
        {
            if (!addr.IsValidVirtualAddress())
                return false;

            if (!Memory.TryReadPtr(addr, out var listPtr, false))
                return false;

            if (!Memory.TryReadPtr(listPtr, out var items, false))
                return false;

            if (!Memory.TryReadValue<int>(listPtr + 0x8, out var count, false))
                return false;

            return items.IsValidVirtualAddress() && count >= 0 && count < 1024;
        }

        #endregion

        #region Camera Struct Offset Sig Scan

        private readonly record struct CameraOffsetSig(
            string Sig,
            int OffsetPos,
            int DispSize,
            bool IsCallSite,
            int TargetBodyDispOffset,
            int TargetBodyDispSize,
            string Desc);

        private static readonly CameraOffsetSig[] ViewMatrixSigs =
        [
            new("E8 ? ? ? ? 48 3B 58 ? 0F 83 ? ? ? ? ? ? ? 48 8D 0C 5D ? ? ? ? 48 03 CB ? ? ? ? E8 ? ? ? ? 4C 8B C7 49 FF C0 ? ? ? ? ? 75",
                0, 4, IsCallSite: true, TargetBodyDispOffset: 3, TargetBodyDispSize: 4,
                "ViewMatrix call-site: call GetWorldToCameraMatrix"),
        ];

        private static readonly CameraOffsetSig[] FovSigs =
        [
            new("83 B9 ? ? ? ? 02 75 ? F3 0F 10 81 ? ? ? ? C3 F3 0F 10 81 ? ? ? ? C3", 22, 4, IsCallSite: false, 0, 0,
                "GetFieldOfView: cmp [rcx+?],2; movss xmm0,[rcx+FOV]; ret"),
        ];

        private static readonly CameraOffsetSig[] AspectRatioSigs =
        [
            new("E8 ? ? ? ? F3 44 0F 59 05 ? ? ? ? F3 0F 59 C6",
                0, 4, IsCallSite: true, TargetBodyDispOffset: 4, TargetBodyDispSize: 4,
                "AspectRatio call-site: call get_aspect"),
        ];

        private static void ResolveCameraOffsets()
        {
            var unityBase = Memory.UnityBase;
            if (!unityBase.IsValidVirtualAddress())
                return;

            ApplyCameraOffset(ViewMatrixSigs, "ViewMatrix", unityBase, ref Camera.ViewMatrix);
            ApplyCameraOffset(FovSigs, "FOV", unityBase, ref Camera.FOV);
            ApplyCameraOffset(AspectRatioSigs, "AspectRatio", unityBase, ref Camera.AspectRatio);
        }

        private static void ApplyCameraOffset(CameraOffsetSig[] sigs, string fieldName, ulong unityBase, ref uint target)
        {
            var resolved = TryResolveCameraOffset(sigs, unityBase);
            if (resolved.HasValue && resolved.Value != target)
            {
                Log.WriteLine($"[CameraManager] Camera.{fieldName} UPDATED: 0x{target:X} → 0x{resolved.Value:X}");
                target = resolved.Value;
            }
            else if (!resolved.HasValue)
            {
                // The hardcoded value came from a previous UnityPlayer build. Keeping it is
                // better than zero, but it is a guess — say so at warning level so it shows
                // up without debug logging after an engine update.
                Log.Write(AppLogLevel.Warning,
                    $"[CameraManager] Camera.{fieldName} sig scan FAILED — falling back to 0x{target:X} " +
                    "from the previous build. Verify against this UnityPlayer version.");
            }
        }

        private static uint? TryResolveCameraOffset(CameraOffsetSig[] sigs, ulong unityBase)
        {
            foreach (var entry in sigs)
            {
                var sigAddr = Memory.FindSignature(entry.Sig, "UnityPlayer.dll");
                if (sigAddr == 0)
                    continue;

                uint offset;

                if (entry.IsCallSite)
                {
                    if (!Memory.TryReadValue<int>(sigAddr + (ulong)entry.OffsetPos + 1, out var callRel32, false))
                        continue;
                    ulong callTarget = sigAddr + 5 + (ulong)(long)callRel32;

                    if (!callTarget.IsValidVirtualAddress())
                        continue;

                    offset = entry.TargetBodyDispSize switch
                    {
                        1 => Memory.TryReadValue<byte>(callTarget + (ulong)entry.TargetBodyDispOffset, out var b, false) ? b : 0u,
                        4 => Memory.TryReadValue<uint>(callTarget + (ulong)entry.TargetBodyDispOffset, out var u, false) ? u : 0u,
                        _ => 0,
                    };
                }
                else
                {
                    offset = entry.DispSize switch
                    {
                        1 => Memory.TryReadValue<byte>(sigAddr + (ulong)entry.OffsetPos, out var b, false) ? b : 0u,
                        4 => Memory.TryReadValue<uint>(sigAddr + (ulong)entry.OffsetPos, out var u, false) ? u : 0u,
                        _ => 0,
                    };
                }

                if (offset > 0 && offset < 0x1000)
                    return offset;
            }

            return null;
        }

        #endregion

        #region Camera Offset Cache

        private static bool TryLoadCameraCache()
        {
            try
            {
                if (!File.Exists(CameraCacheFilePath))
                    return false;

                var unityBase = Memory.UnityBase;
                if (!unityBase.IsValidVirtualAddress())
                    return false;

                var (timestamp, sizeOfImage) = Memory.ReadPeFingerprint(unityBase);
                if (timestamp == 0 || sizeOfImage == 0)
                    return false;

                var json = File.ReadAllText(CameraCacheFilePath);
                var cache = JsonSerializer.Deserialize<CameraOffsetCache>(json, _jsonOpts);
                if (cache is null)
                    return false;

                if (cache.UnityPlayerTimestamp != timestamp || cache.UnityPlayerSizeOfImage != sizeOfImage)
                {
                    Log.WriteLine("[CameraManager] Camera cache PE mismatch — will sig-scan.");
                    return false;
                }

                if (cache.AllCamerasRva == 0 || cache.ViewMatrix == 0 || cache.FOV == 0 || cache.AspectRatio == 0)
                    return false;

                ulong resolvedAddr = unityBase + cache.AllCamerasRva;
                if (!ValidateAllCamerasAddr(resolvedAddr))
                {
                    Log.WriteLine("[CameraManager] Camera cache AllCameras validation failed — will sig-scan.");
                    return false;
                }

                _allCamerasAddr = resolvedAddr;
                Camera.ViewMatrix = cache.ViewMatrix;
                Camera.FOV = cache.FOV;
                Camera.AspectRatio = cache.AspectRatio;

                Log.WriteLine($"[CameraManager] Offsets restored from cache (VM=0x{cache.ViewMatrix:X}, FOV=0x{cache.FOV:X}, AR=0x{cache.AspectRatio:X})");
                return true;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[CameraManager] Cache load failed: {ex.Message}");
                return false;
            }
        }

        private static void SaveCameraCache()
        {
            try
            {
                var unityBase = Memory.UnityBase;
                if (!unityBase.IsValidVirtualAddress() || !_allCamerasAddr.IsValidVirtualAddress())
                    return;

                var (timestamp, sizeOfImage) = Memory.ReadPeFingerprint(unityBase);

                var cache = new CameraOffsetCache
                {
                    UnityPlayerTimestamp = timestamp,
                    UnityPlayerSizeOfImage = sizeOfImage,
                    AllCamerasRva = _allCamerasAddr - unityBase,
                    ViewMatrix = Camera.ViewMatrix,
                    FOV = Camera.FOV,
                    AspectRatio = Camera.AspectRatio,
                };

                var json = JsonSerializer.Serialize(cache, _jsonOpts);
                Directory.CreateDirectory(Path.GetDirectoryName(CameraCacheFilePath)!);
                File.WriteAllText(CameraCacheFilePath, json);
                Log.WriteLine($"[CameraManager] Cache saved → {CameraCacheFilePath}");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[CameraManager] Cache save failed: {ex.Message}");
            }
        }

        #endregion
    }
}
