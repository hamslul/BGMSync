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

    // Song browser state (browse tab)
    private string     _browserSearch   = string.Empty;
    private List<Song> _browserSongs    = new();
    private int        _browserSelected;
    private bool       _songsLoaded;

    // Phase editor — trigger capture
    private int    _capturedTriggerBgmId;
    private string _pendingLabel = string.Empty;

    // Phase editor — inline replacement search
    private string     _replacementSearch  = string.Empty;
    private List<Song> _replacementSongs   = new();
    private int        _capturedReplacementId;

    // Phase editor — custom file source
    private bool   _useCustomFile     = false;
    private string _pendingCustomPath = string.Empty;

    public PluginUI(Plugin plugin, Configuration config, EncounterTracker tracker)
        : base("BGMSync##main",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _config  = config;
        _tracker = tracker;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 460),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    private void EnsureSongsLoaded()
    {
        if (_songsLoaded) return;
        _browserSongs     = SongList.Instance.Search(string.Empty).ToList();
        _replacementSongs = _browserSongs;
        _songsLoaded      = true;
    }

    public override void Draw()
    {
        EnsureSongsLoaded();

        var gameBgmId   = Plugin.Bgm.GetGameBgmId();
        var currentSong = Plugin.Bgm.GetCurrentSongId();

        DrawStatusBar(gameBgmId);
        ImGui.Separator();

        if (ImGui.BeginTabBar("##tabs_v2", ImGuiTabBarFlags.None))
        {
            if (ImGui.BeginTabItem("Phase Mappings"))
            { DrawPhaseMappingsTab(gameBgmId); ImGui.EndTabItem(); }

            if (ImGui.BeginTabItem("Song Browser"))
            { DrawSongBrowserTab(currentSong); ImGui.EndTabItem(); }

            if (ImGui.BeginTabItem("BGM History"))
            { DrawBgmHistoryTab(); ImGui.EndTabItem(); }

            if (ImGui.BeginTabItem("Settings"))
            { DrawSettingsTab(); ImGui.EndTabItem(); }

            ImGui.EndTabBar();
        }
    }

    // ── Status bar ──────────────────────────────────────────────────

    private void DrawStatusBar(int gameBgmId)
    {
        ImGui.TextUnformatted("Game BGM:");
        ImGui.SameLine();
        if (gameBgmId > 0)
        {
            var name = SongList.Instance.GetSongTitle(gameBgmId);
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f),
                string.IsNullOrEmpty(name) ? $"#{gameBgmId}" : $"#{gameBgmId} \u2014 {name}");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "(none)");
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("   |   Override:");
        ImGui.SameLine();
        var customPath = _tracker.PlayingCustomPath;
        if (_tracker.IsActive && !string.IsNullOrEmpty(customPath))
        {
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f),
                $"\u25B6 {System.IO.Path.GetFileName(customPath)} [Custom]");
        }
        else if (_tracker.IsActive && _tracker.PlayingSongId > 1)
        {
            var name = SongList.Instance.GetSongTitle(_tracker.PlayingSongId);
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f),
                $"\u25B6 {name} (#{_tracker.PlayingSongId})");
        }
        else if (_tracker.IsActive && _tracker.PlayingSongId == 1)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1f), "\u25A0 Silenced (waiting for pull)");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "Idle");
        }
    }

    // ── Phase Mappings tab ──────────────────────────────────────────

    private void DrawPhaseMappingsTab(int gameBgmId)
    {
        var tid        = Plugin.ClientState.TerritoryType;
        var currentSongId = Plugin.Bgm.GetCurrentSongId();
        ImGui.TextUnformatted($"Territory: {tid}");
        ImGui.SameLine();

        var canQuickMap = tid != 0 && currentSongId > 1;
        if (!canQuickMap) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Map Current Song to This Territory"))
        {
            _config.TerritoryPhases.TryGetValue(tid, out var existing);
            existing ??= new List<PhaseMapping>();
            existing.Add(new PhaseMapping
            {
                GameBgmId         = 0,
                ReplacementSongId = currentSongId,
                Label             = SongList.Instance.GetSongTitle(currentSongId),
            });
            _config.TerritoryPhases[tid] = existing;
            _config.Save();
        }
        if (!canQuickMap) ImGui.EndDisabled();

        if (currentSongId > 1)
        {
            ImGui.SameLine();
            var songName = SongList.Instance.GetSongTitle(currentSongId);
            ImGui.TextDisabled(string.IsNullOrEmpty(songName) ? $"#{currentSongId}" : $"#{currentSongId} \u2014 {songName}");
        }

        ImGui.Spacing();

        _config.TerritoryPhases.TryGetValue(tid, out var phases);
        phases ??= new List<PhaseMapping>();

        ImGui.TextUnformatted("Phases for this territory:");

        if (phases.Count == 0)
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "  No phases mapped. Add one below.");
        else
            DrawPhaseTable(tid, phases);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawAddPhaseSection(tid, phases, gameBgmId);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawAllMappingsOverview();
    }

    private void DrawAddPhaseSection(uint tid, List<PhaseMapping> phases, int gameBgmId)
    {
        ImGui.TextUnformatted("Add Phase:");
        ImGui.Spacing();

        // ── Trigger ──────────────────────────────────────────
        ImGui.TextUnformatted("  Trigger BGM:");
        ImGui.SameLine();
        if (_capturedTriggerBgmId > 0)
        {
            var name = SongList.Instance.GetSongTitle(_capturedTriggerBgmId);
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f),
                string.IsNullOrEmpty(name) ? $"#{_capturedTriggerBgmId}" : $"#{_capturedTriggerBgmId} \u2014 {name}");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "(none \u2014 wildcard, matches any combat BGM)");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Capture Current") && gameBgmId > 0)
            _capturedTriggerBgmId = gameBgmId;
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear##trigger"))
            _capturedTriggerBgmId = 0;

        ImGui.Spacing();

        // ── Source toggle ─────────────────────────────────────
        ImGui.TextUnformatted("  Source:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Game Song##src", !_useCustomFile))
        {
            _useCustomFile    = false;
            _pendingCustomPath = string.Empty;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Custom File##src", _useCustomFile))
        {
            _useCustomFile        = true;
            _capturedReplacementId = 0;
        }

        ImGui.Spacing();

        // ── Replacement song (popup picker) OR custom file ────
        ImGui.TextUnformatted("  Plays:");
        ImGui.SameLine();

        if (!_useCustomFile)
        {
            if (_capturedReplacementId > 0)
            {
                var name = SongList.Instance.GetSongTitle(_capturedReplacementId);
                ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f),
                    string.IsNullOrEmpty(name) ? $"#{_capturedReplacementId}" : $"#{_capturedReplacementId} \u2014 {name}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear##replacement"))
                    _capturedReplacementId = 0;
                ImGui.SameLine();
            }

            if (ImGui.SmallButton("Pick Song..."))
            {
                _replacementSearch = string.Empty;
                _replacementSongs  = SongList.Instance.Search(string.Empty).ToList();
                ImGui.OpenPopup("##songpicker");
            }

            ImGui.SetNextWindowSize(new Vector2(480, 320), ImGuiCond.Always);
            if (ImGui.BeginPopup("##songpicker"))
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##pickersearch", "Search by name, location, or ID...",
                        ref _replacementSearch, 256))
                    _replacementSongs = SongList.Instance.Search(_replacementSearch).ToList();

                if (ImGui.BeginChild("##pickerlist", new Vector2(0, -30), true))
                {
                    foreach (var song in _replacementSongs)
                    {
                        var isSelected = _capturedReplacementId == song.Id;
                        var label = $"#{song.Id}  {song.DisplayName}";
                        if (!string.IsNullOrEmpty(song.Locations))
                            label += $"  \u2014  {song.Locations}";
                        if (ImGui.Selectable(label + $"##{song.Id}", isSelected))
                        {
                            _capturedReplacementId = song.Id;
                            ImGui.CloseCurrentPopup();
                        }
                    }
                    ImGui.EndChild();
                }
                if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }
        else
        {
            ImGui.SetNextItemWidth(300);
            ImGui.InputTextWithHint("##custompath", "Paste or browse for audio file...",
                ref _pendingCustomPath, 512);
            ImGui.SameLine();
            if (ImGui.SmallButton("Browse##custombrowse"))
            {
                var thread = new System.Threading.Thread(() =>
                {
                    var dlg = new System.Windows.Forms.OpenFileDialog
                    {
                        Title  = "Select Audio File",
                        Filter = "Audio Files|*.mp3;*.wav;*.ogg;*.flac;*.aac;*.m4a;*.wma|All Files|*.*",
                    };
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        _pendingCustomPath = dlg.FileName;
                });
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.Start();
            }
        }

        ImGui.Spacing();

        // ── Label ────────────────────────────────────────────
        ImGui.TextUnformatted("  Label:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##phaselabel", "e.g. Lindwurm, Adds, Enrage...",
            ref _pendingLabel, 64);

        ImGui.Spacing();

        var canAdd = _useCustomFile
            ? !string.IsNullOrEmpty(_pendingCustomPath)
            : _capturedReplacementId > 0;
        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button("Add Phase"))
        {
            phases.Add(new PhaseMapping
            {
                GameBgmId         = _capturedTriggerBgmId,
                ReplacementSongId = _useCustomFile ? 0 : _capturedReplacementId,
                CustomSongPath    = _useCustomFile ? _pendingCustomPath : null,
                Label             = _pendingLabel,
            });
            _config.TerritoryPhases[tid] = phases;
            _config.Save();

            _capturedTriggerBgmId  = 0;
            _capturedReplacementId = 0;
            _pendingCustomPath     = string.Empty;
            _pendingLabel          = string.Empty;
            _replacementSearch     = string.Empty;
            _replacementSongs      = SongList.Instance.Search(string.Empty).ToList();
        }
        if (!canAdd) ImGui.EndDisabled();
    }

    private void DrawPhaseTable(uint tid, List<PhaseMapping> phases)
    {
        if (!ImGui.BeginTable("##phases", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY,
                new Vector2(0, Math.Min(phases.Count * 28 + 30, 180))))
            return;

        ImGui.TableSetupColumn("Phase",       ImGuiTableColumnFlags.WidthFixed,   100);
        ImGui.TableSetupColumn("Trigger",     ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Plays",       ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("",            ImGuiTableColumnFlags.WidthFixed,    55);
        ImGui.TableSetupColumn("",            ImGuiTableColumnFlags.WidthFixed,    20);
        ImGui.TableHeadersRow();

        int? toRemove = null;
        for (int i = 0; i < phases.Count; i++)
        {
            var phase    = phases[i];
            var isActive = _tracker.IsActive && _tracker.CurrentPhaseIndex == i;

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var label = string.IsNullOrEmpty(phase.Label) ? $"Phase {i + 1}" : phase.Label;
            if (isActive) ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f), label);
            else ImGui.TextUnformatted(label);

            ImGui.TableSetColumnIndex(1);
            if (phase.GameBgmId == 0)
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Any combat BGM");
            else
            {
                var triggerName = SongList.Instance.GetSongTitle(phase.GameBgmId);
                ImGui.TextUnformatted(string.IsNullOrEmpty(triggerName)
                    ? $"#{phase.GameBgmId}"
                    : $"{triggerName} (#{phase.GameBgmId})");
            }

            ImGui.TableSetColumnIndex(2);
            string songText;
            if (!string.IsNullOrEmpty(phase.CustomSongPath))
                songText = $"[Custom] {System.IO.Path.GetFileName(phase.CustomSongPath)}";
            else
            {
                var songName = SongList.Instance.GetSongTitle(phase.ReplacementSongId);
                songText = string.IsNullOrEmpty(songName)
                    ? $"#{phase.ReplacementSongId}"
                    : $"{songName} (#{phase.ReplacementSongId})";
            }
            if (isActive) ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f), songText);
            else ImGui.TextUnformatted(songText);

            ImGui.TableSetColumnIndex(3);
            ImGui.PushID(i * 2);
            if (ImGui.SmallButton("\u25B6 Play"))
                _tracker.PreviewPhase(phase);
            ImGui.PopID();

            ImGui.TableSetColumnIndex(4);
            ImGui.PushID(i * 2 + 1);
            if (ImGui.SmallButton("X")) toRemove = i;
            ImGui.PopID();
        }

        if (toRemove.HasValue)
        {
            phases.RemoveAt(toRemove.Value);
            if (phases.Count == 0) _config.TerritoryPhases.Remove(tid);
            _config.Save();
        }

        ImGui.EndTable();
    }

    private void DrawAllMappingsOverview()
    {
        if (_config.TerritoryPhases.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "No territory mappings configured.");
            return;
        }

        ImGui.TextUnformatted("All configured territories:");
        if (!ImGui.BeginTable("##allterritories", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY,
                new Vector2(0, 120)))
            return;

        ImGui.TableSetupColumn("Territory", ImGuiTableColumnFlags.WidthFixed,  80);
        ImGui.TableSetupColumn("Phases",    ImGuiTableColumnFlags.WidthFixed,  50);
        ImGui.TableSetupColumn("Songs",     ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("",          ImGuiTableColumnFlags.WidthFixed,  60);
        ImGui.TableHeadersRow();

        uint? toRemove = null;
        foreach (var (territory, ps) in _config.TerritoryPhases)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(territory.ToString());

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(ps.Count.ToString());

            ImGui.TableSetColumnIndex(2);
            var summary = string.Join(", ", ps.Select(p =>
            {
                var name = SongList.Instance.GetSongTitle(p.ReplacementSongId);
                var lbl  = string.IsNullOrEmpty(p.Label) ? "" : $"[{p.Label}] ";
                return $"{lbl}{name}";
            }));
            ImGui.TextUnformatted(summary);

            ImGui.TableSetColumnIndex(3);
            ImGui.PushID((int)territory);
            if (ImGui.SmallButton("Remove")) toRemove = territory;
            ImGui.PopID();
        }

        if (toRemove.HasValue)
        {
            _config.TerritoryPhases.Remove(toRemove.Value);
            _config.Save();
        }

        ImGui.EndTable();
    }

    // ── BGM History tab ─────────────────────────────────────────────

    private void DrawBgmHistoryTab()
    {
        ImGui.TextWrapped(
            "Recent game BGM changes detected from memory. " +
            "Run through a fight to discover the BGM IDs for each phase, " +
            "then use them as triggers in Phase Mappings.");
        ImGui.Spacing();

        var history = Plugin.Bgm.BgmHistory;
        if (history.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f),
                "No BGM changes recorded yet.");
            return;
        }

        if (ImGui.Button("Clear History"))
            Plugin.Bgm.ClearHistory();

        ImGui.Spacing();

        if (!ImGui.BeginTable("##history", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY,
                new Vector2(0, -30)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Time",   ImGuiTableColumnFlags.WidthFixed,  80);
        ImGui.TableSetupColumn("BGM ID", ImGuiTableColumnFlags.WidthFixed,  60);
        ImGui.TableSetupColumn("Name",   ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("",       ImGuiTableColumnFlags.WidthFixed,  90);
        ImGui.TableHeadersRow();

        for (int i = history.Count - 1; i >= 0; i--)
        {
            var entry = history[i];
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(entry.Time.ToString("HH:mm:ss"));

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(entry.SongId.ToString());

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(SongList.Instance.GetSongTitle(entry.SongId));

            ImGui.TableSetColumnIndex(3);
            ImGui.PushID(i);
            if (ImGui.SmallButton("Use as Trigger"))
                _capturedTriggerBgmId = entry.SongId;
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    // ── Song Browser tab ────────────────────────────────────────────

    private void DrawSongBrowserTab(int currentSongId)
    {
        ImGui.TextUnformatted($"{SongList.Instance.GetSongs().Count} songs loaded.");
        ImGui.SameLine();
        ImGui.TextDisabled("Double-click to preview.");

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##browsersearch", "Search by name, location, or ID...",
                ref _browserSearch, 256))
            _browserSongs = SongList.Instance.Search(_browserSearch).ToList();

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

            foreach (var song in _browserSongs)
            {
                ImGui.TableNextRow();
                var isSelected = _browserSelected == song.Id;
                var isPlaying  = currentSongId > 0 && song.Id == currentSongId;

                ImGui.TableSetColumnIndex(0);
                if (isPlaying) ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f), "\u266A");
                else ImGui.TextUnformatted("");

                ImGui.TableSetColumnIndex(1);
                if (isPlaying) ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f), song.Id.ToString());
                else ImGui.TextUnformatted(song.Id.ToString());

                ImGui.TableSetColumnIndex(2);
                if (isPlaying) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.2f, 1f, 0.4f, 1f));

                if (ImGui.Selectable(song.DisplayName + $"##{song.Id}", isSelected,
                        ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                {
                    _browserSelected = song.Id;
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        Plugin.Bgm.PlaySong(song.Id);
                }

                if (isPlaying) ImGui.PopStyleColor();

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(song.Locations);
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();

        if (_browserSelected > 0 && SongList.Instance.TryGetSong(_browserSelected, out var sel))
        {
            ImGui.TextUnformatted($"Selected: [{sel.Id}] {sel.DisplayName}");
            if (!string.IsNullOrEmpty(sel.Locations))
                ImGui.TextDisabled($"Location: {sel.Locations}");

            ImGui.Spacing();
            if (ImGui.Button("Preview"))
                Plugin.Bgm.PlaySong(_browserSelected);
            ImGui.SameLine();
            if (ImGui.Button("Stop Preview"))
                Plugin.Bgm.StopSong();
        }
    }

    // ── Settings tab ────────────────────────────────────────────────

    private void DrawSettingsTab()
    {
        var notify = _config.ShowChatNotification;
        if (ImGui.Checkbox("Show chat notifications", ref notify))
        { _config.ShowChatNotification = notify; _config.Save(); }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Custom Song Volume:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        var vol = _config.CustomSongVolume;
        if (ImGui.SliderFloat("##customvol", ref vol, 0f, 1f, $"{(int)(vol * 100)}%%"))
        {
            _config.CustomSongVolume = vol;
            _tracker.SetCustomVolume(vol);
            _config.Save();
        }

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
