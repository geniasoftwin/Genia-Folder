namespace GeniaFolder.Models;

public sealed class ManagedFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public FolderColor Color { get; set; } = FolderColor.Blue;
    public ProtectionMode Protection { get; set; } = ProtectionMode.None;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    public bool Exists => Directory.Exists(Path);
    public string ProtectionLabel => Protection == ProtectionMode.None ? "Без защиты" : Protection.ToString();
}
