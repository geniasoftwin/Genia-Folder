using System.Text.Json;
using GeniaFolder.Models;

namespace GeniaFolder.Services;

public sealed class FolderRegistryService
{
    private readonly string _dataFile;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public FolderRegistryService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = System.IO.Path.Combine(appData, "GeniaFolder");
        Directory.CreateDirectory(root);
        _dataFile = System.IO.Path.Combine(root, "folders.json");
    }

    public async Task<List<ManagedFolder>> LoadAsync()
    {
        if (!File.Exists(_dataFile))
            return [];

        await using var stream = File.OpenRead(_dataFile);
        return await JsonSerializer.DeserializeAsync<List<ManagedFolder>>(stream, _jsonOptions) ?? [];
    }

    public async Task SaveAsync(IEnumerable<ManagedFolder> folders)
    {
        var temp = _dataFile + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, folders, _jsonOptions);
            await stream.FlushAsync();
        }

        File.Move(temp, _dataFile, true);
    }
}
