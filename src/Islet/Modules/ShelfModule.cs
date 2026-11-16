using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Islet.Core;

namespace Islet.Modules;

/// <summary>
/// The Shelf: drop files/folders on the island to park them, drag them back out anywhere later.
/// Only the paths are stored (nothing is copied), persisted across restarts in %APPDATA%\Islet\shelf.json.
/// </summary>
public sealed class ShelfModule
{
    private const int MaxItems = 15;
    private readonly ObservableCollection<ShelfItem> _items;
    private readonly string _file = Paths.Data("shelf.json");

    public ShelfModule(ObservableCollection<ShelfItem> items) => _items = items;

    public void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? new();
            AddCore(paths);
        }
        catch (Exception ex) { Log.Error("shelf load", ex); }
    }

    public void Add(IEnumerable<string> paths)
    {
        AddCore(paths);
        Save();
    }

    public void Remove(ShelfItem item)
    {
        _items.Remove(item);
        Save();
    }

    public void Clear()
    {
        _items.Clear();
        Save();
    }

    private void AddCore(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (_items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (_items.Count >= MaxItems) _items.RemoveAt(0);

            var name = Path.GetFileName(path.TrimEnd('\\'));
            _items.Add(new ShelfItem(path, string.IsNullOrEmpty(name) ? path : name, ImageUtil.ForPath(path)));
        }
    }

    private void Save()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(_items.Select(i => i.Path).ToList())); }
        catch (Exception ex) { Log.Error("shelf save", ex); }
    }
}
