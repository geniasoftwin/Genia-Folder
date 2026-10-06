using System.Text;

namespace GeniaFolder.Services;

public sealed class FolderMarkerService
{
    public const string MarkerFileName = ".geniafolder.id";
    private const string Prefix = "GeniaFolder.Folder.v1|";

    public Guid? TryReadFolderId(string folderPath)
    {
        try
        {
            var markerPath = Path.Combine(
                Path.GetFullPath(folderPath),
                MarkerFileName);

            if (!File.Exists(markerPath))
                return null;

            var text = File.ReadAllText(markerPath, Encoding.UTF8).Trim();
            if (!text.StartsWith(Prefix, StringComparison.Ordinal))
                return null;

            var value = text[Prefix.Length..];
            return Guid.TryParseExact(value, "N", out var id)
                ? id
                : null;
        }
        catch
        {
            return null;
        }
    }

    public bool Matches(string folderPath, Guid folderId) =>
        TryReadFolderId(folderPath) == folderId;

    public void EnsureMarker(string folderPath, Guid folderId)
    {
        folderPath = Path.GetFullPath(folderPath);

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException(folderPath);

        var markerPath = Path.Combine(
            folderPath,
            MarkerFileName);

        var existing = TryReadFolderId(folderPath);
        if (existing is Guid existingId)
        {
            if (existingId != folderId)
            {
                throw new InvalidOperationException(
                    $"Папка уже содержит другой GeniaFolder ID: {existingId:N}.");
            }

            EnsureHiddenSystem(markerPath);
            return;
        }

        if (File.Exists(markerPath))
        {
            throw new InvalidDataException(
                "Файл .geniafolder.id существует, но имеет некорректный формат.");
        }

        var temp = markerPath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            File.WriteAllText(
                temp,
                Prefix + folderId.ToString("N"),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            File.Move(temp, markerPath, overwrite: false);
            EnsureHiddenSystem(markerPath);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.SetAttributes(temp, FileAttributes.Normal);
                    File.Delete(temp);
                }
                catch
                {
                }
            }
        }
    }

    private static void EnsureHiddenSystem(string path)
    {
        var attributes = File.GetAttributes(path);
        File.SetAttributes(
            path,
            attributes |
            FileAttributes.Hidden |
            FileAttributes.System);
    }
}
