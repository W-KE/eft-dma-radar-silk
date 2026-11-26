// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Web.Data
{
    /// <summary>
    /// The slow-changing part of the radar state: map config, loot, corpses, containers,
    /// exfils, switches, doors, transits and airdrops. Loot alone refreshes only every few
    /// seconds yet is the bulk of the payload, so this block is sent to a client only when
    /// its content changed since the version that client already holds
    /// (see <see cref="WebRadarUpdate.WorldVersion"/>).
    /// </summary>
    public sealed class WebRadarWorld
    {
        public WebRadarMapInfo? Map { get; set; }
        public WebRadarLootItem[]? Loot { get; set; }
        public WebRadarCorpse[]? Corpses { get; set; }
        public WebRadarContainer[]? Containers { get; set; }
        public WebRadarExfil[]? Exfils { get; set; }
        public WebRadarSwitch[]? Switches { get; set; }
        public WebRadarDoor[]? Doors { get; set; }
        public WebRadarTransit[]? Transits { get; set; }
        public WebRadarAirdrop[]? Airdrops { get; set; }
    }
}
