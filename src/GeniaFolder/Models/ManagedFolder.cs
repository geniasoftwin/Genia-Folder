namespace GeniaFolder.Models;

public sealed class ManagedFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public FolderColor Color { get; set; } = FolderColor.Blue;
    public ProtectionMode Protection { get; set; } = ProtectionMode.None;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    // Stable Windows filesystem identity. Used to follow ordinary Explorer
    // renames within the same parent directory without confusing them with
    // deletion/restoration.
    public ulong VolumeSerialNumber { get; set; }
    public string FileId { get; set; } = string.Empty;

    public bool Exists => Directory.Exists(Path);
    public string ProtectionLabel => Protection == ProtectionMode.None ? "Без защиты" : Protection.ToString();
}
