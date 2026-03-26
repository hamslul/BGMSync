using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.IO;
using System.Linq;

namespace BGMSync;

/// <summary>
/// Song database — mirrors OrchestrionPlugin's SongList exactly.
/// Loads from Google Sheets (same spreadsheet Orchestrion uses), caches locally,
/// augments with Lumina game data for file existence and flags.
/// </summary>
public sealed class Song
{
    public int    Id            { get; set; }
    public string Name         { get; set; } = string.Empty;
    public string AlternateName{ get; set; } = string.Empty;
    public string Locations    { get; set; } = string.Empty;
    public string AdditionalInfo{ get; set; } = string.Empty;
    public string FilePath     { get; set; } = string.Empty;
    public bool   FileExists   { get; set; }
    public bool   SpecialMode  { get; set; }
    public bool   DisableRestart{ get; set; }
    public TimeSpan Duration   { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Name) ? $"[{Id}]" : Name;
}

internal sealed class SongList
{
    // Same Google Sheet Orchestrion uses
    private const string SheetUrl =
        "https://docs.google.com/spreadsheets/d/1s-xJjxqp6pwS7oewNy1aOQnr3gaJbewvIBbyYchZ6No/gviz/tq?tqx=out:csv&sheet={0}";

    private readonly Dictionary<int, Song> _songs = new();
    private readonly string _cacheDir;
    private readonly IDataManager _dataManager;

    private static SongList? _instance;
    public static SongList Instance => _instance!;

    public static void Init(string pluginDir, IDataManager dataManager)
    {
        _instance = new SongList(pluginDir, dataManager);
    }

    private SongList(string pluginDir, IDataManager dataManager)
    {
        _cacheDir    = pluginDir;
        _dataManager = dataManager;

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var meta = client.GetStringAsync(string.Format(SheetUrl, "metadata")).Result;
            SaveCache("metadata", meta);
            var en = client.GetStringAsync(string.Format(SheetUrl, "en")).Result;
            SaveCache("en", en);
            LoadMetadata(meta);
            LoadLanguage(en, "en");
        }
        catch
        {
            // Fall back to local cache
            try
            {
                LoadMetadata(ReadCache("metadata"));
                LoadLanguage(ReadCache("en"), "en");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[BGMSync] SongList: failed to load song data from cache.");
            }
        }

        Plugin.Log.Information($"[BGMSync] SongList loaded {_songs.Count} songs.");
    }

    // ── Data loading ─────────────────────────────────────────────────

    private void LoadMetadata(string csv)
    {
        foreach (var line in CsvLines(csv))
        {
            var cols = SplitCsv(line);
            if (cols.Count < 2) continue;
            if (!int.TryParse(cols[0], out var id) || id <= 0) continue;

            var song = new Song { Id = id };

            if (double.TryParse(cols[1], out var secs))
                song.Duration = TimeSpan.FromSeconds(secs);

            // Augment with Lumina game data
            try
            {
                var bgmSheet = _dataManager.GetExcelSheet<BGM>();
                if (bgmSheet != null)
                {
                    var row = bgmSheet.GetRowOrDefault((uint)id);
                    if (row != null)
                    {
                        var path = row.Value.File.ToString();
                        song.FilePath    = path ?? string.Empty;
                        song.FileExists  = !string.IsNullOrEmpty(path) &&
                                           _dataManager.FileExists(path);
                        song.DisableRestart = row.Value.DisableRestart;
                        song.SpecialMode    = row.Value.SpecialMode > 0;
                    }
                }
            }
            catch { /* Lumina lookup failure is non-fatal */ }

            _songs[id] = song;
        }
    }

    private void LoadLanguage(string csv, string lang)
    {
        foreach (var line in CsvLines(csv))
        {
            var cols = SplitCsv(line);
            if (cols.Count < 2) continue;
            if (!int.TryParse(cols[0], out var id)) continue;
            if (!_songs.TryGetValue(id, out var song)) continue;

            var name = cols.Count > 1 ? cols[1] : string.Empty;

            // Filter out placeholder/test entries (same as Orchestrion)
            if (lang == "en" && (string.IsNullOrWhiteSpace(name) ||
                name.Equals("Null BGM", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("test", StringComparison.OrdinalIgnoreCase)))
            {
                _songs.Remove(id);
                continue;
            }

            song.Name           = name;
            song.AlternateName  = cols.Count > 2 ? cols[2] : string.Empty;
            song.Locations      = cols.Count > 4 ? cols[4] : string.Empty;
            song.AdditionalInfo = cols.Count > 5 ? cols[5] : string.Empty;
        }
    }

    // ── Public API ───────────────────────────────────────────────────

    public IReadOnlyDictionary<int, Song> GetSongs() => _songs;

    public bool TryGetSong(int id, out Song song)
    {
        var result = _songs.TryGetValue(id, out var s);
        song = s ?? new Song();
        return result;
    }

    public string GetSongTitle(int id) =>
        _songs.TryGetValue(id, out var s) ? s.DisplayName : string.Empty;

    public IEnumerable<Song> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _songs.Values.OrderBy(s => s.Id);

        query = query.Trim().ToLowerInvariant();
        return _songs.Values
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        s.AlternateName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        s.Locations.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        s.Id.ToString().Contains(query))
            .OrderBy(s => s.Id);
    }

    // ── Cache helpers ────────────────────────────────────────────────

    private void SaveCache(string sheet, string content)
    {
        try { File.WriteAllText(CachePath(sheet), content); } catch { }
    }

    private string ReadCache(string sheet)
    {
        var path = CachePath(sheet);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private string CachePath(string sheet) =>
        Path.Combine(_cacheDir, $"xiv_bgm_{sheet}.csv");

    // ── CSV parsing (same approach as Orchestrion) ───────────────────

    private static IEnumerable<string> CsvLines(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1); // skip header

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var current = string.Empty;
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                { current += '"'; i++; }
                else
                { inQuotes = !inQuotes; }
            }
            else if (c == ',' && !inQuotes)
            { result.Add(current.Trim()); current = string.Empty; }
            else
            { current += c; }
        }
        result.Add(current.Trim());
        return result;
    }
}
