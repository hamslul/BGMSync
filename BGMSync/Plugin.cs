using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace BGMSync;

/// <summary>
/// BGMSync — standalone encounter BGM sync plugin.
/// No dependency on OrchestrionPlugin.
/// Song list loaded from same Google Sheet source Orchestrion uses.
/// BGM controlled via direct memory manipulation identical to OrchestrionPlugin.
/// </summary>
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

    internal static BgmController Bgm { get; private set; } = null!;

    public Configuration     Config  { get; }
    private EncounterTracker _tracker;

    private readonly WindowSystem _windowSystem = new("BGMSync");
    private readonly PluginUI     _ui;

    private const string CommandName = "/bgmsync";

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Init song database (loads Google Sheets + Lumina, same as Orchestrion)
        SongList.Init(PluginInterface.GetPluginConfigDirectory(), DataManager);

        // Init direct BGM controller (same memory technique as Orchestrion)
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

        Log.Information($"[BGMSync] Loaded. SongList has {SongList.Instance.GetSongs().Count} songs. Use /bgmsync to configure.");
    }

    public string Name => "BGMSync";

    public void Dispose()
    {
        Commands.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw         -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= OpenConfigUi;
        _windowSystem.RemoveAllWindows();
        _tracker.Dispose();
        Bgm.Dispose();
    }

    private void OnCommand(string command, string args) => OpenConfigUi();
    private void OpenConfigUi() => _ui.Toggle();
}
