using System.Text.Json;
using GeniaFolder.Models;

namespace GeniaFolder.Services;

public sealed class FolderRegistryService
{
    private readonly string _dataFile;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public FolderRegistryService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GeniaFolder",
            "folders.json"))
    {
    }

    public FolderRegistryService(string dataFile)
    {
        _dataFile = Path.GetFullPath(dataFile);
        Directory.CreateDirectory(
            Path.GetDirectoryName(_dataFile)
                ?? throw new InvalidOperationException("Invalid registry path."));
    }

    public async Task<List<ManagedFolder>> LoadAsync()
    {
        await _writeGate.WaitAsync();
        try
        {
            if (!File.Exists(_dataFile))
                return [];

            await using var stream = File.OpenRead(_dataFile);
            return await JsonSerializer.DeserializeAsync<List<ManagedFolder>>(
                stream, _jsonOptions) ?? [];
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<ManagedFolder> folders)
    {
        await _writeGate.WaitAsync();

        var temp = _dataFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            // Snapshot materialization happens under the write gate;
            // concurrent writes cannot share or overwrite a temp file.
            var snapshot = folders.ToList();
            await using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, snapshot, _jsonOptions);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _dataFile, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // A failed cleanup must not hide the original exception.
            }
            _writeGate.Release();
        }
    }
}
