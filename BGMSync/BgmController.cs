using Dalamud.Plugin.Services;
using System;
using System.Runtime.InteropServices;

namespace BGMSync;

// Confirmed offsets from spy analysis:
// Orchestrion writes song ID to 0x0C (BgmReference), 0x0E (BgmId), 0x10 (PreviousBgmId)
[StructLayout(LayoutKind.Explicit, Size = 0x30)]
internal struct BGMScene
{
    [FieldOffset(0x04)] public ushort GameBgmId;
    [FieldOffset(0x0C)] public ushort BgmReference;
    [FieldOffset(0x0E)] public ushort BgmId;
    [FieldOffset(0x10)] public ushort PreviousBgmId;
}

internal sealed unsafe class BgmController : IDisposable
{
    private const string BaseSig       = "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 51 83 78 08 0B";
    private const string RestartSig    = "E8 ?? ?? ?? ?? 88 9E ?? ?? ?? ?? 84 DB";
    private const int    SceneCount    = 12;
    private const int    SceneListOffset = 0xC0;
    private const int    OurScene      = 0;

    private nint _baseAddress;
    private bool _isValid;
    private int  _playingSongId;
    private bool _isActive;
    private readonly IFramework _framework;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint AddDisableRestartIdDelegate(BGMScene* scene, ushort songId);
    private AddDisableRestartIdDelegate? _addDisableRestartId;

    public bool IsAvailable   => _isValid;
    public int  PlayingSongId => _playingSongId;

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

    // Re-apply every frame in case game overwrites
    private void OnUpdate(IFramework framework)
    {
        if (!_isValid || !_isActive) return;
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

    /// <summary>
    /// Silence BGM — play song 1 (empty track).
    /// Used during encounter pauses while still in instance.
    /// </summary>
    public void SilenceSong() => PlaySong(1);

    /// <summary>
    /// Fully release BGM control and let the game resume its own music.
    /// Called when leaving an instance — zeros all three fields so the
    /// game's own scene system takes back control naturally.
    /// </summary>
    public void StopSong()
    {
        if (!_isValid) return;
        ClearScene();
        Plugin.Log.Information("[BGMSync] StopSong — released BGM control, game resumes naturally.");
    }

    private void ClearScene()
    {
        try
        {
            var list = GetSceneList();
            if (list == nint.Zero) return;
            var bgms = (BGMScene*)list;

            // Zero all three fields — this releases our hold so the game's
            // own scene system picks up whatever it should be playing.
            bgms[OurScene].BgmReference  = 0;
            bgms[OurScene].BgmId         = 0;
            bgms[OurScene].PreviousBgmId = 0;

            _playingSongId = 0;
            _isActive      = false;
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[BGMSync] ClearScene failed."); }
    }

    private nint GetSceneList()
    {
        var manager = Marshal.ReadIntPtr(_baseAddress);
        return manager == nint.Zero ? nint.Zero : Marshal.ReadIntPtr(manager + SceneListOffset);
    }
}
