using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace BGMSync;

/// <summary>
/// Persisted plugin settings. Saved by Dalamud automatically.
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ---------------------------------------------------------------
    // Encounter → song mappings
    // ---------------------------------------------------------------

    /// <summary>
    /// Maps a territory/content-finder ID to an Orchestrion song ID.
    /// Key   = TerritoryType row ID (ushort cast to int for JSON compat)
    /// Value = Orchestrion song ID
    /// </summary>
    public Dictionary<uint, int> TerritoryToSongId { get; set; } = new();

    // ---------------------------------------------------------------
    // Behaviour toggles
    // ---------------------------------------------------------------

    /// <summary>When true, print a chat message when BGMSync takes over music.</summary>
    public bool ShowChatNotification { get; set; } = true;

    /// <summary>When true, stop music immediately on wipe (don't wait for fade).</summary>
    public bool StopOnWipe { get; set; } = true;

    /// <summary>When true, stop music when duty is completed.</summary>
    public bool StopOnComplete { get; set; } = true;

    // ---------------------------------------------------------------
    // Save helper (called by plugin after any mutation)
    // ---------------------------------------------------------------
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
