using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BGMSync;

/// <summary>
/// BGMScene memory layout — matches OrchestrionPlugin's struct exactly.
/// The game maintains 12 of these in a contiguous array. Lower scene index = higher priority.
/// Size is 0x60 bytes per scene (verified against OrchestrionPlugin's Sequential layout).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x60)]
internal struct BGMScene
{
    [FieldOffset(0x00)] public int    SceneIndex;
    [FieldOffset(0x04)] public byte   Flags;
    [FieldOffset(0x0C)] public ushort BgmReference;   // Non-zero when scene is active
    [FieldOffset(0x0E)] public ushort BgmId;          // Actual playing BGM ID
    [FieldOffset(0x10)] public ushort PreviousBgmId;
    [FieldOffset(0x12)] public byte   TimerEnable;
    [FieldOffset(0x14)] public float  Timer;
}

/// <summary>Timestamped record of a game BGM change, used for phase discovery in the UI.</summary>
internal readonly record struct BgmChange(int SongId, DateTime Time);

internal sealed unsafe class BgmController : IDisposable
{
    private const string BaseSig         = "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 51 83 78 08 0B";
    private const string RestartSig      = "E8 ?? ?? ?? ?? 88 9E ?? ?? ?? ?? 84 DB";
    private const int    SceneCount      = 12;
    private const int    SceneListOffset = 0xC0;
    private const int    OurScene        = 0;
    private const int    MaxHistory      = 64;

    private nint _baseAddress;
    private bool _isValid;
    private int  _playingSongId;
    private bool _isActive;
    private int  _lastGameBgmId;

    private readonly IFramework _framework;
    private readonly List<BgmChange> _bgmHistory = new();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint AddDisableRestartIdDelegate(BGMScene* scene, ushort songId);
    private AddDisableRestartIdDelegate? _addDisableRestartId;

    public bool IsAvailable   => _isValid;
    public int  PlayingSongId => _playingSongId;
    public bool IsActive      => _isActive;

    /// <summary>Fires when the game's underlying BGM changes (not our override). Args: (oldId, newId).</summary>
    public event Action<int, int>? GameBgmChanged;

    /// <summary>Recent game BGM changes for the UI's phase discovery view.</summary>
    public IReadOnlyList<BgmChange> BgmHistory => _bgmHistory;

    /// <summary>
    /// Reads the game's current BGM directly from memory, using the same
    /// scene-scanning approach as OrchestrionPlugin. Iterates scenes in
    /// priority order and returns the first active, valid BGM ID.
    /// When our override is active, skips our scene to read the game's BGM underneath.
    /// </summary>
    public int GetGameBgmId()
    {
        if (!_isValid) return 0;
        var list = GetSceneList();
        if (list == nint.Zero) return 0;
        var bgms = (BGMScene*)list;

        for (int i = 0; i < SceneCount; i++)
        {
            // Skip our override scene when active
            if (_isActive && i == OurScene) continue;

            // Match OrchestrionPlugin: BgmReference must be non-zero (scene is active),
            // and BgmId must be valid (not 0, not 9999 which is a silence placeholder).
            if (bgms[i].BgmReference == 0) continue;
            if (bgms[i].BgmId != 0 && bgms[i].BgmId != 9999)
                return bgms[i].BgmId;
        }
        return 0;
    }

    /// <summary>Returns our override if active, otherwise the game's actual BGM.</summary>
    public int GetCurrentSongId()
    {
        if (_isActive) return _playingSongId;
        return GetGameBgmId();
    }

    public BgmController(ISigScanner sigScanner, IFramework framework)
    {
        _framework = framework;
        try
        {
            _baseAddress = sigScanner.GetStaticAddressFromSig(BaseSig);
            if (_baseAddress == nint.Zero) { Plugin.Log.Error("[BGMSync] Base=zero"); return; }

            var manager   = Marshal.ReadIntPtr(_baseAddress);
            var sceneList = manager != nint.Zero ? Marshal.ReadIntPtr(manager + SceneListOffset) : nint.Zero;
            Plugin.Log.Information($"[BGMSync] Base=0x{_baseAddress:X} Manager=0x{manager:X} SceneList=0x{sceneList:X}");
            if (sceneList == nint.Zero) { Plugin.Log.Error("[BGMSync] SceneList=zero"); return; }

            _isValid = true;
            _lastGameBgmId = GetGameBgmId();
            _framework.Update += OnUpdate;

            try
            {
                var rp = sigScanner.ScanText(RestartSig);
                if (rp != nint.Zero)
                    _addDisableRestartId = Marshal.GetDelegateForFunctionPointer<AddDisableRestartIdDelegate>(rp);
            }
            catch { }
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "[BGMSync] Init failed."); }
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        if (_isValid && _isActive) ClearScene();
    }

    private void OnUpdate(IFramework framework)
    {
        if (!_isValid) return;

        // Monitor game BGM changes every frame.
        var gameBgm = GetGameBgmId();
        if (gameBgm != _lastGameBgmId)
        {
            var old = _lastGameBgmId;
            _lastGameBgmId = gameBgm;

            if (gameBgm > 0)
            {
                _bgmHistory.Add(new BgmChange(gameBgm, DateTime.Now));
                if (_bgmHistory.Count > MaxHistory)
                    _bgmHistory.RemoveAt(0);
            }

            GameBgmChanged?.Invoke(old, gameBgm);
        }

        // Keep our override pinned in scene 0 (game can overwrite it).
        if (_isActive)
        {
            var list = GetSceneList();
            if (list == nint.Zero) return;
            var bgms = (BGMScene*)list;
            if (bgms[OurScene].BgmId != (ushort)_playingSongId)
            {
                bgms[OurScene].BgmReference  = (ushort)_playingSongId;
                bgms[OurScene].BgmId         = (ushort)_playingSongId;
                bgms[OurScene].PreviousBgmId = (ushort)_playingSongId;
            }
        }
    }

    public void PlaySong(int songId)
    {
        if (!_isValid) return;
        var list = GetSceneList();
        if (list == nint.Zero) return;
        var bgms = (BGMScene*)list;

        bgms[OurScene].BgmReference  = (ushort)songId;
        bgms[OurScene].BgmId         = (ushort)songId;
        bgms[OurScene].PreviousBgmId = (ushort)songId;

        _playingSongId = songId;
        _isActive      = true;
        Plugin.Log.Information($"[BGMSync] PlaySong({songId})");

        var disableRestart = false;
        if (songId > 0 && SongList.Instance.TryGetSong(songId, out var song))
            disableRestart = song.DisableRestart;
        if (disableRestart && _addDisableRestartId != null)
            _addDisableRestartId(&bgms[OurScene], (ushort)songId);
    }

    public void StopSong()
    {
        if (!_isValid) return;
        ClearScene();
        Plugin.Log.Information("[BGMSync] StopSong.");
    }

    public void SilenceSong() => PlaySong(1);

    public void ClearHistory() => _bgmHistory.Clear();

    public void DumpScenes()
    {
        var manager = Marshal.ReadIntPtr(_baseAddress);
        Plugin.Log.Information($"[BGMSync] Manager=0x{manager:X}");
        var list = GetSceneList();
        Plugin.Log.Information($"[BGMSync] SceneList=0x{list:X}  (scene stride=0x{0x60:X})");
        if (list == nint.Zero) return;
        var bgms = (BGMScene*)list;
        for (int i = 0; i < SceneCount; i++)
        {
            Plugin.Log.Information(
                $"[BGMSync] Scene[{i:D2}] Idx={bgms[i].SceneIndex} Flags=0x{bgms[i].Flags:X2} " +
                $"Ref={bgms[i].BgmReference} BgmId={bgms[i].BgmId} Prev={bgms[i].PreviousBgmId}");
        }
    }

    private nint GetSceneList()
    {
        var manager = Marshal.ReadIntPtr(_baseAddress);
        return manager == nint.Zero ? nint.Zero : Marshal.ReadIntPtr(manager + SceneListOffset);
    }

    private void ClearScene()
    {
        try
        {
            var list = GetSceneList();
            if (list == nint.Zero) return;
            var bgms = (BGMScene*)list;
            bgms[OurScene].BgmReference  = 0;
            bgms[OurScene].BgmId         = 0;
            bgms[OurScene].PreviousBgmId = 0;
            _playingSongId = 0;
            _isActive      = false;
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[BGMSync] ClearScene failed."); }
    }
}
