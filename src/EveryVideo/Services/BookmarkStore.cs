using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveryVideo.Services;

public sealed class Bookmark
{
    public string Media { get; set; } = "";
    public string Title { get; set; } = "";
    public long PositionMs { get; set; }
    public string Note { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;

    [JsonIgnore] public string Time => TimeFormat.Format(PositionMs);
    [JsonIgnore] public string CreatedText => Created.ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore] public string NoteOrTime => string.IsNullOrWhiteSpace(Note) ? "(메모 없음)" : Note;
}

/// <summary>동영상별 즐겨찾기(특정 시점). %AppData%\EveryVideo\bookmarks.json</summary>
public static class BookmarkStore
{
    private static readonly string FilePath = Path.Combine(Settings.DataDir, "bookmarks.json");
    private static List<Bookmark>? _all;

    public static event Action? Changed;

    public static List<Bookmark> All
    {
        get
        {
            if (_all != null) return _all;
            try
            {
                _all = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch (Exception e)
            {
                Log.Write("bookmarks load", e);
                _all = new();
            }
            return _all;
        }
    }

    public static List<Bookmark> For(string media) =>
        All.Where(b => string.Equals(b.Media, media, StringComparison.OrdinalIgnoreCase))
           .OrderBy(b => b.PositionMs).ToList();

    public static void Add(Bookmark b)
    {
        All.Add(b);
        Save();
    }

    public static void Remove(Bookmark b)
    {
        All.Remove(b);
        Save();
    }

    public static void Update() => Save();

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(All, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Log.Write("bookmarks save", e);
        }
        Changed?.Invoke();
    }
}
