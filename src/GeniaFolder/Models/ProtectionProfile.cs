namespace GeniaFolder.Models;

public sealed class ProtectionProfile
{
    public int FormatVersion { get; set; } = 1;
    public Guid ProfileId { get; set; }
    public Guid FolderId { get; set; }
    public string FolderPathSnapshot { get; set; } = string.Empty;
    public ProtectionMode Mode { get; set; } = ProtectionMode.Standard;
    public DateTimeOffset CreatedAt { get; set; }

    public PasswordKdfSettings PasswordKdf { get; set; } = new();
    public WrappedKeyMaterial PasswordWrappedFek { get; set; } = new();

    public RecoveryKdfSettings RecoveryKdf { get; set; } = new();
    public WrappedKeyMaterial RecoveryWrappedFek { get; set; } = new();
}

public sealed class PasswordKdfSettings
{
    public string Algorithm { get; set; } = "PBKDF2-HMAC-SHA256";
    public int Iterations { get; set; }
    public string Salt { get; set; } = string.Empty;
}

public sealed class RecoveryKdfSettings
{
    public string Algorithm { get; set; } = "HMAC-SHA256-v1";
    public string Salt { get; set; } = string.Empty;
}

public sealed class WrappedKeyMaterial
{
    public string Algorithm { get; set; } = "AES-256-GCM";
    public string Nonce { get; set; } = string.Empty;
    public string Ciphertext { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
}
