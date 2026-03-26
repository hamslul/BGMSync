using Dalamud.Plugin.Services;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BGMSync;

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
    private const string BaseSig         = "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 51 83 78 08 0B";
    private const string RestartSig      = "E8 ?? ?? ?? ?? 88 9E ?? ?? ?? ?? 84 DB";
    private const int    SceneCount      = 12;
    private const int    SceneListOffset = 0xC0;
    private const int    OurScene        = 0;

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

    public int GetCurrentSongId()
    {
        if (_isActive) return _playingSongId;
        return GetSongIdForCurrentTerritory();
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

    public void StopSong()
    {
        if (!_isValid) return;
        ClearScene();
        Plugin.Log.Information("[BGMSync] StopSong.");
    }

    public void SilenceSong() => PlaySong(1);

    public void DumpScenes()
    {
        var manager = Marshal.ReadIntPtr(_baseAddress);
        Plugin.Log.Information($"[BGMSync] Manager=0x{manager:X}");
        var list = GetSceneList();
        Plugin.Log.Information($"[BGMSync] SceneList=0x{list:X}");
        if (list == nint.Zero) return;
        for (int i = 0; i < 12; i++)
        {
            var sb = new StringBuilder($"[BGMSync] Scene[{i:D2}] ");
            for (int off = 0; off < 0x30; off += 2)
            {
                var val = (ushort)(Marshal.ReadInt16(list + i * 0x30 + off) & 0xFFFF);
                if (val != 0) sb.Append($"[0x{off:X2}]={val} ");
            }
            Plugin.Log.Information(sb.ToString());
        }
    }

    // Territory IDs whose place names don't match song location strings
    // Key = territory ID, Value = search term to use instead
    private static readonly System.Collections.Generic.Dictionary<ushort, string> _territoryOverrides = new()
    {
        { 177, "Inn" },  // Mizzenmast Inn (Limsa) -> search "Inn"
        { 179, "Inn" },  // Gridania inn
        { 178, "Inn" },  // Ul'dah inn
        { 429, "Inn" },  // Ishgard inn
        { 629, "Inn" },  // Kugane inn
        { 843, "Inn" },  // Crystarium inn
        { 990, "Inn" },  // Old Sharlayan inn
        { 1186, "Inn" }, // Tuliyollal inn
    };

    private int GetSongIdForCurrentTerritory()
    {
        try
        {
            var territoryId = Plugin.ClientState.TerritoryType;
            if (territoryId == 0) return 0;

            var songs = SongList.Instance.GetSongs().Values.ToList();

            // Check territory override first
            if (_territoryOverrides.TryGetValue(territoryId, out var overrideTerm))
            {
                foreach (var song in songs)
                {
                    if (!string.IsNullOrEmpty(song.Locations) &&
                        song.Locations.Contains(overrideTerm, StringComparison.OrdinalIgnoreCase))
                        return song.Id;
                }
            }

            var territory = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRow(territoryId);
            if (territory == null) return 0;

            var placeName = territory.Value.PlaceName.Value.Name.ToString();
            if (string.IsNullOrEmpty(placeName)) return 0;

            var parts = placeName.Split(' ');

            for (int len = parts.Length; len >= 1; len--)
            {
                var term = string.Join(' ', parts.Take(len));
                foreach (var song in songs)
                {
                    if (!string.IsNullOrEmpty(song.Locations) &&
                        song.Locations.Contains(term, StringComparison.OrdinalIgnoreCase))
                        return song.Id;
                }
            }
        }
        catch { }
        return 0;
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
