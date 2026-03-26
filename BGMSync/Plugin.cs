using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace BGMSync;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState     { get; private set; } = null!;
    [PluginService] internal static ICondition              Condition       { get; private set; } = null!;
    [PluginService] internal static IDutyState              DutyState       { get; private set; } = null!;
    [PluginService] internal static IChatGui                Chat            { get; private set; } = null!;
    [PluginService] internal static ICommandManager         Commands        { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework       { get; private set; } = null!;
    [PluginService] internal static ISigScanner             SigScanner      { get; private set; } = null!;
    [PluginService] internal static IDataManager            DataManager     { get; private set; } = null!;
    [PluginService] internal static IDtrBar                 DtrBar          { get; private set; } = null!;

    internal static BgmController Bgm { get; private set; } = null!;
    private Dalamud.Game.Gui.Dtr.IDtrBarEntry? _dtrEntry;
    private int _lastDtrSongId = -1;

    public Configuration     Config  { get; }
    private EncounterTracker _tracker;

    private readonly WindowSystem _windowSystem = new("BGMSync");
    private readonly PluginUI     _ui;

    private const string CommandName = "/bgmsync";

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        SongList.Init(PluginInterface.GetPluginConfigDirectory(), DataManager);
        Bgm = new BgmController(SigScanner, Framework);
        _tracker = new EncounterTracker(Config, ClientState, Chat, Condition, Framework, Bgm);

        _ui = new PluginUI(this, Config, _tracker);
        _windowSystem.AddWindow(_ui);

        PluginInterface.UiBuilder.Draw         += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   += OpenConfigUi;

        Commands.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = "Open BGMSync configuration window."
        });

        _dtrEntry = DtrBar.Get("BGMSync");
        _dtrEntry.Shown = true;
        _dtrEntry.OnClick = _ => OpenConfigUi();
        Framework.Update += UpdateDtr;

        Log.Information($"[BGMSync] Loaded. SongList has {SongList.Instance.GetSongs().Count} songs. Use /bgmsync to configure.");
    }

    public string Name => "BGMSync";

    public void Dispose()
    {
        Framework.Update -= UpdateDtr;
        _dtrEntry?.Remove();
        Commands.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw         -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= OpenConfigUi;
        _windowSystem.RemoveAllWindows();
        _tracker.Dispose();
        Bgm.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().ToLower() == "debug")
            Bgm.DumpScenes();
        else
            OpenConfigUi();
    }

    private void UpdateDtr(IFramework framework)
    {
        var songId = Bgm.GetCurrentSongId();
        if (songId == _lastDtrSongId) return;
        _lastDtrSongId = songId;
        if (_dtrEntry == null) return;

        if (songId > 0 && SongList.Instance.TryGetSong(songId, out var song))
            _dtrEntry.Text = new Dalamud.Game.Text.SeStringHandling.SeString(
                new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload($"♪ - #{songId} {song.DisplayName}"));
        else
            _dtrEntry.Text = new Dalamud.Game.Text.SeStringHandling.SeString(
                new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload("BGMSync"));
    }

    private void OpenConfigUi() => _ui.Toggle();
}
