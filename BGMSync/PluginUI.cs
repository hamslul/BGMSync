using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BGMSync;

internal sealed class PluginUI : Window, IDisposable
{
    private readonly Configuration    _config;
    private readonly EncounterTracker _tracker;

    private string _songIdInputStr = string.Empty;
    private int    _songIdInput;

    private string     _searchQuery   = string.Empty;
    private List<Song> _filteredSongs = new();
    private int        _selectedSongId;
    private bool       _songsLoaded;

    public PluginUI(Plugin plugin, Configuration config, EncounterTracker tracker)
        : base("BGMSync##main",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _config  = config;
        _tracker = tracker;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(540, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    private void EnsureSongsLoaded()
    {
        if (_songsLoaded) return;
        _filteredSongs = SongList.Instance.Search(string.Empty).ToList();
        _songsLoaded   = true;
    }

    public override void Draw()
    {
        EnsureSongsLoaded();

        var currentSongId = Plugin.Bgm.GetCurrentSongId();

        // Status bar
        ImGui.TextUnformatted("Status:");
        ImGui.SameLine();
        if (_tracker.IsActive)
        {
            var name = SongList.Instance.GetSongTitle(_tracker.PlayingSongId);
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f),
                $"\u25B6 Playing: {name} (#{_tracker.PlayingSongId})");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Idle");
        }

        if (currentSongId > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"  \u266A Game BGM: #{currentSongId}");
        }

        ImGui.Separator();

        if (ImGui.BeginTabBar("##tabs"))
        {
            if (ImGui.BeginTabItem("Territory Mappings"))
            { DrawMappingsTab(); ImGui.EndTabItem(); }

            if (ImGui.BeginTabItem("Song Browser"))
            { DrawSongBrowserTab(currentSongId); ImGui.EndTabItem(); }

            if (ImGui.BeginTabItem("Settings"))
            { DrawSettingsTab(); ImGui.EndTabItem(); }

            ImGui.EndTabBar();
        }
    }

    private void DrawMappingsTab()
    {
        var tid = Plugin.ClientState.TerritoryType;
        ImGui.TextUnformatted($"Current Territory ID : {tid}");
        _config.TerritoryToSongId.TryGetValue(tid, out var mapped);
        var mappedName = mapped > 0
            ? $"{SongList.Instance.GetSongTitle(mapped)} (#{mapped})"
            : "(none)";
        ImGui.TextUnformatted($"Mapped Song          : {mappedName}");

        ImGui.Spacing();
        ImGui.TextUnformatted("Set song ID for current territory:");
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputText("##songid", ref _songIdInputStr, 8, ImGuiInputTextFlags.CharsDecimal))
            int.TryParse(_songIdInputStr, out _songIdInput);
        ImGui.SameLine();
        if (ImGui.Button("Apply"))
            _tracker.SetMappingForCurrentTerritory(_songIdInput);
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            _songIdInputStr = string.Empty;
            _songIdInput    = 0;
            _tracker.SetMappingForCurrentTerritory(0);
        }

        ImGui.TextDisabled("Tip: find song IDs in the Song Browser tab.");
        ImGui.Separator();
        ImGui.TextUnformatted("All mappings:");
        ImGui.Spacing();

        if (ImGui.BeginTable("##mappings", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY,
                new Vector2(0, 180)))
        {
            ImGui.TableSetupColumn("Territory ID", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Song ID",      ImGuiTableColumnFlags.WidthFixed,  70);
            ImGui.TableSetupColumn("Song Name",    ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Action",       ImGuiTableColumnFlags.WidthFixed,  60);
            ImGui.TableHeadersRow();

            uint? toRemove = null;
            foreach (var (territory, songId) in _config.TerritoryToSongId)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(territory.ToString());
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(songId.ToString());
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(SongList.Instance.GetSongTitle(songId));
                ImGui.TableSetColumnIndex(3);
                ImGui.PushID((int)territory);
                if (ImGui.SmallButton("Remove")) toRemove = territory;
                ImGui.PopID();
            }
            if (toRemove.HasValue)
            {
                _config.TerritoryToSongId.Remove(toRemove.Value);
                _config.Save();
            }
            ImGui.EndTable();
        }
    }

    private void DrawSongBrowserTab(int currentSongId)
    {
        ImGui.TextUnformatted($"{SongList.Instance.GetSongs().Count} songs loaded.");
        ImGui.SameLine();
        ImGui.TextDisabled("Double-click to map to current territory.");
        if (currentSongId > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f), $"  \u266A = currently playing (#{currentSongId})");
        }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##search", "Search by name, location, or ID...",
                ref _searchQuery, 256))
            _filteredSongs = SongList.Instance.Search(_searchQuery).ToList();

        ImGui.Spacing();

        if (ImGui.BeginTable("##songs", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable,
                new Vector2(0, -90)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("\u266A",   ImGuiTableColumnFlags.WidthFixed,   20);
            ImGui.TableSetupColumn("ID",       ImGuiTableColumnFlags.WidthFixed,   50);
            ImGui.TableSetupColumn("Name",     ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (var song in _filteredSongs)
            {
                ImGui.TableNextRow();
                var isSelected = _selectedSongId == song.Id;
                var isPlaying  = currentSongId > 0 && song.Id == currentSongId;

                ImGui.TableSetColumnIndex(0);
                if (isPlaying)
                    ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f), "\u266A");
                else
                    ImGui.TextUnformatted("");

                ImGui.TableSetColumnIndex(1);
                if (isPlaying)
                    ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f), song.Id.ToString());
                else
                    ImGui.TextUnformatted(song.Id.ToString());

                ImGui.TableSetColumnIndex(2);
                if (isPlaying)
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.2f, 1f, 0.4f, 1f));

                if (ImGui.Selectable(song.DisplayName + $"##{song.Id}",
                        isSelected,
                        ImGuiSelectableFlags.SpanAllColumns |
                        ImGuiSelectableFlags.AllowDoubleClick))
                {
                    _selectedSongId = song.Id;
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        _songIdInputStr = song.Id.ToString();
                        _songIdInput    = song.Id;
                        _tracker.SetMappingForCurrentTerritory(song.Id);
                        Plugin.Chat.Print(
                            $"[BGMSync] Mapped territory {Plugin.ClientState.TerritoryType} " +
                            $"to: {song.DisplayName} (#{song.Id})");
                    }
                }

                if (isPlaying)
                    ImGui.PopStyleColor();

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(song.Locations);
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();

        if (_selectedSongId > 0 && SongList.Instance.TryGetSong(_selectedSongId, out var sel))
        {
            ImGui.TextUnformatted($"Selected: [{sel.Id}] {sel.DisplayName}");
            if (!string.IsNullOrEmpty(sel.Locations))
                ImGui.TextDisabled($"Location: {sel.Locations}");
            if (!string.IsNullOrEmpty(sel.AdditionalInfo))
                ImGui.TextDisabled($"Info: {sel.AdditionalInfo}");

            ImGui.Spacing();
            if (ImGui.Button("Preview"))
                Plugin.Bgm.PlaySong(_selectedSongId);
            ImGui.SameLine();
            if (ImGui.Button("Stop Preview"))
                Plugin.Bgm.StopSong();
            ImGui.SameLine();
            if (ImGui.Button("Set for current territory"))
            {
                _songIdInputStr = _selectedSongId.ToString();
                _songIdInput    = _selectedSongId;
                _tracker.SetMappingForCurrentTerritory(_selectedSongId);
            }
        }
    }

    private void DrawSettingsTab()
    {
        var notify = _config.ShowChatNotification;
        if (ImGui.Checkbox("Show chat notifications", ref notify))
        { _config.ShowChatNotification = notify; _config.Save(); }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Stop BGM (restore game music)"))
            Plugin.Bgm.StopSong();
        ImGui.SameLine();
        if (ImGui.Button("Silence BGM"))
            Plugin.Bgm.SilenceSong();
    }
}
