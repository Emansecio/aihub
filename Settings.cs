using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AIHub;

public sealed record CustomShortcut(string Id, string Name, string Path);

public sealed class Settings
{
    public string SelectedId { get; set; } = "codex";
    public bool RightSide { get; set; }
    public double VerticalPosition { get; set; } = 0.5;
    public string Monitor { get; set; } = "";
    public List<CustomShortcut> CustomShortcuts { get; set; } = new();
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIHub", "settings.json");

    public static Settings Load(string? path = null)
    {
        try
        {
            var value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path ?? FilePath)) ?? new Settings();
            value.VerticalPosition = double.IsFinite(value.VerticalPosition) ? Math.Clamp(value.VerticalPosition, 0, 1) : 0.5;
            value.CustomShortcuts ??= new();
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new Settings(); }
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
