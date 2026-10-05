namespace GeniaFolder.Models;

public sealed class VaultManifest
{
    public int FormatVersion { get; set; } = 1;
    public Guid ProfileId { get; set; }
    public Guid FolderId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<VaultDirectoryEntry> Directories { get; set; } = [];
    public List<VaultFileEntry> Files { get; set; } = [];
}

public sealed class VaultDirectoryEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public FileAttributes Attributes { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
}

public sealed class VaultFileEntry
{
    public Guid FileId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long Length { get; set; }
    public FileAttributes Attributes { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public string PlaintextSha256 { get; set; } = string.Empty;
}

public sealed record VaultBuildProgress(
    string Stage,
    int ProcessedFiles,
    int TotalFiles,
    long ProcessedBytes,
    long TotalBytes);

public sealed record VerifiedVaultResult(
    string VaultPath,
    int FileCount,
    int DirectoryCount,
    long PlaintextBytes,
    string ManifestCiphertextSha256);
