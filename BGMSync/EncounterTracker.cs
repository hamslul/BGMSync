using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using System;

namespace BGMSync;

/// <summary>
/// Watches InCombat and BoundByDuty condition flags to drive BGM directly
/// via BgmController — no external plugins required.
///
///   Entered mapped instance  -> SilenceSong()
///   InCombat rising edge     -> PlaySong(songId)
///   InCombat falling edge    -> SilenceSong()
///   Left instance            -> StopSong()
/// </summary>
internal sealed class EncounterTracker : IDisposable
{
    private readonly Configuration _config;
    private readonly IClientState  _clientState;
    private readonly IChatGui      _chat;
    private readonly ICondition    _condition;
    private readonly IFramework    _framework;
    private readonly BgmController _bgm;

    private bool   _isActive;
    private bool   _wasInCombat;
    private bool   _wasInInstance;
    private int    _playingSongId;
    private ushort _lastTerritoryId;

    public EncounterTracker(
        Configuration config,
        IClientState  clientState,
        IChatGui      chat,
        ICondition    condition,
        IFramework    framework,
        BgmController bgm)
    {
        _config          = config;
        _clientState     = clientState;
        _chat            = chat;
        _condition       = condition;
        _framework       = framework;
        _bgm             = bgm;
        _wasInCombat     = _condition[ConditionFlag.InCombat];
        _wasInInstance   = _condition[ConditionFlag.BoundByDuty];
        _lastTerritoryId = _clientState.TerritoryType;

        _framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        if (_isActive)
        {
            _bgm.StopSong();
            _isActive = false;
        }
    }

    public bool IsActive      => _isActive;
    public int  PlayingSongId => _playingSongId;

    public void SetMappingForCurrentTerritory(int songId)
    {
        var tid = _clientState.TerritoryType;
        if (tid == 0) return;
        if (songId <= 0)
            _config.TerritoryToSongId.Remove(tid);
        else
            _config.TerritoryToSongId[tid] = songId;
        _config.Save();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var inCombat   = _condition[ConditionFlag.InCombat];
        var inInstance = _condition[ConditionFlag.BoundByDuty];
        var currentTid = _clientState.TerritoryType;

        // Territory changed — check for mapped instance entry
        if (currentTid != 0 && currentTid != _lastTerritoryId)
        {
            _lastTerritoryId = currentTid;
            _wasInCombat     = false;
            _isActive        = false;
            _playingSongId   = 0;

            if (inInstance && _config.TerritoryToSongId.ContainsKey(currentTid))
            {
                _bgm.SilenceSong();
                if (_config.ShowChatNotification)
                    _chat.Print("[BGMSync] Mapped instance entered — music ready for pull.");
            }
        }

        // Left instance — restore full game BGM
        if (!inInstance && _wasInInstance)
        {
            _bgm.StopSong();
            _isActive      = false;
            _playingSongId = 0;
            if (_config.ShowChatNotification)
                _chat.Print("[BGMSync] Left instance — game BGM restored.");
        }
        // InCombat rising edge — play encounter music
        else if (inCombat && !_wasInCombat)
        {
            var tid = _clientState.TerritoryType;
            if (_config.TerritoryToSongId.TryGetValue(tid, out var songId) && songId > 0)
            {
                _bgm.PlaySong(songId);
                _isActive      = true;
                _playingSongId = songId;
                var name = SongList.Instance.GetSongTitle(songId);
                Plugin.Log.Information($"[BGMSync] Playing song {songId} ({name}) for territory {tid}");
                if (_config.ShowChatNotification)
                    _chat.Print($"[BGMSync] Encounter music started: {name} (#{songId})");
            }
        }
        // InCombat falling edge — silence
        else if (!inCombat && _wasInCombat)
        {
            if (_isActive)
            {
                _bgm.SilenceSong();
                _isActive      = false;
                _playingSongId = 0;
                if (_config.ShowChatNotification)
                    _chat.Print("[BGMSync] Encounter music stopped.");
            }
        }

        _wasInCombat   = inCombat;
        _wasInInstance = inInstance;
    }
}
