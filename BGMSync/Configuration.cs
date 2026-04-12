using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace BGMSync;

/// <summary>
/// A single phase within a fight. Maps a game BGM ID (the trigger) to a
/// replacement song ID (what BGMSync plays instead).
/// </summary>
[Serializable]
public class PhaseMapping
{
    /// <summary>
    /// The game's BGM ID that triggers this phase.
    /// Set to 0 for a wildcard that matches any combat BGM.
    /// </summary>
    public int GameBgmId { get; set; }

    /// <summary>Orchestrion song ID to play when this phase triggers.</summary>
    public int ReplacementSongId { get; set; }

    /// <summary>User-friendly label, e.g. "Phase 1", "Adds", "Enrage".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Path to a custom audio file on the user's machine.
    /// When set, this overrides ReplacementSongId and plays via NAudio.
    /// </summary>
    public string? CustomSongPath { get; set; }
}

/// <summary>
/// Persisted plugin settings. Saved by Dalamud automatically.
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    // ---------------------------------------------------------------
    // Phase mappings (v2)
    // ---------------------------------------------------------------

    /// <summary>
    /// Per-territory phase mappings. Each territory can have multiple phases,
    /// each triggered by a specific game BGM ID.
    /// Key = TerritoryType row ID.
    /// </summary>
    public Dictionary<uint, List<PhaseMapping>> TerritoryPhases { get; set; } = new();

    // ---------------------------------------------------------------
    // Legacy (v1) — kept only for migration
    // ---------------------------------------------------------------

    /// <summary>Old v1 format: territory → single song ID. Migrated on load.</summary>
    public Dictionary<uint, int>? TerritoryToSongId { get; set; }

    // ---------------------------------------------------------------
    // Behaviour toggles
    // ---------------------------------------------------------------

    /// <summary>When true, print a chat message when BGMSync takes over music.</summary>
    public bool ShowChatNotification { get; set; } = true;

    /// <summary>When true, stop music immediately on wipe.</summary>
    public bool StopOnWipe { get; set; } = true;

    /// <summary>When true, stop music when duty is completed.</summary>
    public bool StopOnComplete { get; set; } = true;

    /// <summary>Volume for custom audio files (0.0 – 1.0).</summary>
    public float CustomSongVolume { get; set; } = 1f;

    // ---------------------------------------------------------------
    // Migration & save
    // ---------------------------------------------------------------

    /// <summary>Migrate v1 single-song mappings to v2 phase mappings.</summary>
    public void Migrate()
    {
        if (TerritoryToSongId is not { Count: > 0 }) return;

        foreach (var (tid, songId) in TerritoryToSongId)
        {
            if (songId > 0 && !TerritoryPhases.ContainsKey(tid))
            {
                TerritoryPhases[tid] = new List<PhaseMapping>
                {
                    new() { GameBgmId = 0, ReplacementSongId = songId, Label = "All Phases" }
                };
            }
        }

        TerritoryToSongId = null;
        Version = 2;
        Save();
        Plugin.Log.Information("[BGMSync] Migrated v1 config to v2 phase mappings.");
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
