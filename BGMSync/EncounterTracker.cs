using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;

namespace BGMSync;

/// <summary>
/// Drives phase-aware BGM replacement using Dalamud's built-in duty lifecycle events.
///
///   TerritoryChanged (mapped)  → silence immediately
///   InCombat rising edge       → play matched phase
///   DutyWiped                  → silence, wait for next pull
///   DutyRecommenced            → silence (still waiting for pull)
///   DutyCompleted              → keep playing
///   TerritoryChanged (leaving) → restore game BGM
/// </summary>
internal sealed class EncounterTracker : IDisposable
{
    private readonly Configuration    _config;
    private readonly IClientState     _clientState;
    private readonly IChatGui         _chat;
    private readonly ICondition       _condition;
    private readonly IFramework       _framework;
    private readonly IDutyState       _dutyState;
    private readonly BgmController    _bgm;
    private readonly CustomAudioPlayer _customAudio = new();

    private bool   _inMappedInstance;
    private bool   _wasInCombat;
    private int    _currentPhaseIndex = -1;
    private bool   _isActive;

    public EncounterTracker(
        Configuration config,
        IClientState  clientState,
        IChatGui      chat,
        ICondition    condition,
        IFramework    framework,
        IDutyState    dutyState,
        BgmController bgm)
    {
        _config      = config;
        _clientState = clientState;
        _chat        = chat;
        _condition   = condition;
        _framework   = framework;
        _dutyState   = dutyState;
        _bgm         = bgm;

        _wasInCombat = _condition[ConditionFlag.InCombat];

        _clientState.TerritoryChanged  += OnTerritoryChanged;
        _dutyState.DutyWiped           += OnDutyWiped;
        _dutyState.DutyRecommenced     += OnDutyRecommenced;
        _dutyState.DutyCompleted       += OnDutyCompleted;
        _bgm.GameBgmChanged            += OnGameBgmChanged;
        _framework.Update              += OnFrameworkUpdate;

        // If plugin loads while already inside a mapped territory, mark it.
        // Silence will follow on the next GameBgmChanged.
        var tid = _clientState.TerritoryType;
        if (tid != 0 && _config.TerritoryPhases.ContainsKey(tid))
        {
            _inMappedInstance = true;
            Plugin.Log.Information($"[BGMSync] Plugin loaded inside mapped territory {tid}.");
        }
    }

    public void Dispose()
    {
        _clientState.TerritoryChanged  -= OnTerritoryChanged;
        _dutyState.DutyWiped           -= OnDutyWiped;
        _dutyState.DutyRecommenced     -= OnDutyRecommenced;
        _dutyState.DutyCompleted       -= OnDutyCompleted;
        _bgm.GameBgmChanged            -= OnGameBgmChanged;
        _framework.Update              -= OnFrameworkUpdate;

        _customAudio.Dispose();
        if (_isActive) _bgm.StopSong();
    }

    /// <summary>Apply volume change immediately to any currently-playing custom audio.</summary>
    public void SetCustomVolume(float volume) => _customAudio.Volume = volume;

    /// <summary>Preview a phase outside of an encounter (e.g. from the UI).</summary>
    public void PreviewPhase(PhaseMapping phase)
    {
        if (!string.IsNullOrEmpty(phase.CustomSongPath))
        {
            _customAudio.Volume = _config.CustomSongVolume;
            _customAudio.Play(phase.CustomSongPath);
        }
        else
            _bgm.PlaySong(phase.ReplacementSongId);
    }

    public bool    IsActive          => _isActive;
    public int     PlayingSongId     => _bgm.PlayingSongId;
    public int     CurrentPhaseIndex => _currentPhaseIndex;
    public string? PlayingCustomPath => _customAudio.CurrentPath;

    public void SetPhasesForCurrentTerritory(List<PhaseMapping> phases)
    {
        var tid = _clientState.TerritoryType;
        if (tid == 0) return;
        if (phases.Count == 0) _config.TerritoryPhases.Remove(tid);
        else _config.TerritoryPhases[tid] = phases;
        _config.Save();
    }

    // ── Territory changed ────────────────────────────────────────────

    private void OnTerritoryChanged(ushort tid)
    {
        var wasActive = _isActive;
        _customAudio.Stop();
        if (_isActive) _bgm.StopSong();
        _isActive          = false;
        _inMappedInstance  = false;
        _currentPhaseIndex = -1;

        if (tid != 0 && _config.TerritoryPhases.ContainsKey(tid))
        {
            // Mark as mapped but DO NOT try to silence yet — TerritoryChanged fires before
            // the game's BGM system is ready for the new zone. GetSceneList() returns zero
            // at this point and any PlaySong/SilenceSong call is silently dropped.
            // Silence happens in OnGameBgmChanged when the zone BGM first plays.
            _inMappedInstance = true;
            Plugin.Log.Information($"[BGMSync] Mapped territory {tid} detected — will silence when BGM loads.");
        }
        else if (wasActive && _config.ShowChatNotification)
            _chat.Print("[BGMSync] Left instance \u2014 game BGM restored.");
    }

    // ── Duty lifecycle events ────────────────────────────────────────

    private void OnDutyWiped(object? sender, ushort territory)
    {
        if (!_inMappedInstance) return;
        _customAudio.Stop();
        _bgm.SilenceSong();
        _currentPhaseIndex = -1;
        _isActive          = true;
        Plugin.Log.Information("[BGMSync] Wipe — silenced.");
        if (_config.ShowChatNotification)
            _chat.Print("[BGMSync] Wipe \u2014 music silenced until next pull.");
    }

    private void OnDutyRecommenced(object? sender, ushort territory)
    {
        if (!_inMappedInstance) return;
        // Between wipe and pull — ensure we're silent.
        _customAudio.Stop();
        _bgm.SilenceSong();
        _currentPhaseIndex = -1;
        _isActive          = true;
    }

    private void OnDutyCompleted(object? sender, ushort territory)
    {
        if (!_inMappedInstance) return;
        // Song keeps playing — nothing to change.
        Plugin.Log.Information("[BGMSync] Duty complete — music continues.");
        if (_config.ShowChatNotification && _isActive)
            _chat.Print("[BGMSync] Duty complete \u2014 music continues.");
    }

    // ── Frame update: combat rising edge only ────────────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_inMappedInstance) return;

        var inCombat = _condition[ConditionFlag.InCombat];

        if (inCombat && !_wasInCombat && _currentPhaseIndex < 0)
        {
            // Pull started — check the current game BGM and evaluate all phases.
            // We can't rely solely on GameBgmChanged here because the BGM may already
            // be the trigger song (it won't fire "changed" if it hasn't changed).
            var tid        = _clientState.TerritoryType;
            var currentBgm = _bgm.GetGameBgmId();
            Plugin.Log.Information($"[BGMSync] Combat started in tid={tid}, game BGM=#{currentBgm}");
            EvaluatePhases(tid, currentBgm, inCombat: true);
        }

        _wasInCombat = inCombat;
    }

    // ── Game BGM changed ─────────────────────────────────────────────

    private void OnGameBgmChanged(int oldBgmId, int newBgmId)
    {
        if (!_inMappedInstance) return;
        var tid      = _clientState.TerritoryType;
        var inCombat = _condition[ConditionFlag.InCombat];
        Plugin.Log.Information($"[BGMSync] Game BGM changed: #{oldBgmId} → #{newBgmId}, inCombat={inCombat}, phase={_currentPhaseIndex}");

        if (!inCombat)
        {
            // Not in combat — silence the BGM (covers initial entry and post-wipe zone BGM).
            // Only do this if we're not already holding silence to avoid a feedback loop.
            if (!_isActive)
            {
                _bgm.SilenceSong();
                _isActive = true;
                Plugin.Log.Information("[BGMSync] Not in combat — silenced.");
                if (_config.ShowChatNotification)
                    _chat.Print("[BGMSync] Mapped encounter \u2014 music silenced until combat.");
            }
            return;
        }

        EvaluatePhases(tid, newBgmId, inCombat: true);
    }

    // ── Phase evaluation (shared by combat start and BGM change) ─────

    /// <summary>
    /// Given the current territory and game BGM, decide which phase (if any) to activate.
    /// Exact match always wins. Wildcard only fires at combat start with no active phase.
    /// Unmapped BGM while a specific phase is running → silence.
    /// </summary>
    private void EvaluatePhases(ushort tid, int gameBgm, bool inCombat)
    {
        if (!_config.TerritoryPhases.TryGetValue(tid, out var phases) || phases.Count == 0)
            return;

        int matchIdx    = -1;
        int wildcardIdx = -1;
        for (int i = 0; i < phases.Count; i++)
        {
            if (phases[i].GameBgmId != 0 && phases[i].GameBgmId == gameBgm) { matchIdx = i; break; }
            if (phases[i].GameBgmId == 0 && wildcardIdx < 0) wildcardIdx = i;
        }

        // Exact match — always activate.
        if (matchIdx >= 0)
        {
            ActivatePhase(phases[matchIdx], matchIdx, gameBgm);
            return;
        }

        // Wildcard — only when entering combat with no phase active.
        if (wildcardIdx >= 0 && inCombat && _currentPhaseIndex < 0)
        {
            ActivatePhase(phases[wildcardIdx], wildcardIdx, gameBgm);
            return;
        }

        // BGM is unmapped while a specific (non-wildcard) phase was playing → silence.
        if (_currentPhaseIndex >= 0 && gameBgm > 1 && !IsCurrentPhaseWildcard(tid))
        {
            _customAudio.Stop();
            _bgm.SilenceSong();
            _currentPhaseIndex = -1;
            if (_config.ShowChatNotification)
                _chat.Print("[BGMSync] Phase ended \u2014 music silenced.");
        }
    }

    private void ActivatePhase(PhaseMapping phase, int index, int triggerBgmId)
    {
        if (_isActive && _currentPhaseIndex == index) return;

        string displayName;
        if (!string.IsNullOrEmpty(phase.CustomSongPath))
        {
            _customAudio.Volume = _config.CustomSongVolume;
            _bgm.SilenceSong();
            _customAudio.Play(phase.CustomSongPath);
            displayName = System.IO.Path.GetFileName(phase.CustomSongPath);
        }
        else
        {
            _customAudio.Stop();
            _bgm.PlaySong(phase.ReplacementSongId);
            displayName = SongList.Instance.GetSongTitle(phase.ReplacementSongId) is { Length: > 0 } n
                ? n : $"#{phase.ReplacementSongId}";
        }

        _isActive          = true;
        _currentPhaseIndex = index;

        var label = string.IsNullOrEmpty(phase.Label) ? $"Phase {index + 1}" : phase.Label;
        Plugin.Log.Information($"[BGMSync] {label}: playing '{displayName}' triggered by BGM #{triggerBgmId}");
        if (_config.ShowChatNotification)
            _chat.Print($"[BGMSync] {label}: {displayName}");
    }

    private bool IsCurrentPhaseWildcard(ushort tid)
    {
        if (_currentPhaseIndex < 0) return false;
        return _config.TerritoryPhases.TryGetValue(tid, out var phases)
            && _currentPhaseIndex < phases.Count
            && phases[_currentPhaseIndex].GameBgmId == 0;
    }
}
