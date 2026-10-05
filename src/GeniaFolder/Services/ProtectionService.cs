using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GeniaFolder.Models;

namespace GeniaFolder.Services;

public sealed class ProtectionService
{
    public const int MinimumPasswordLength = 12;

    private const int PasswordIterations = 600_000;
    private const int FekSize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] RecoveryContext =
        Encoding.UTF8.GetBytes("GeniaFolder Recovery KEK v1");

    private readonly string _profilesRoot;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public ProtectionService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GeniaFolder",
            "protection"))
    {
    }

    public ProtectionService(string profilesRoot)
    {
        if (string.IsNullOrWhiteSpace(profilesRoot))
            throw new ArgumentException("Profiles root is required.", nameof(profilesRoot));

        _profilesRoot = Path.GetFullPath(profilesRoot);
        Directory.CreateDirectory(_profilesRoot);
    }

    public bool HasPreparedProfile(Guid folderId) =>
        File.Exists(GetProfilePath(folderId));

    public VaultCopyInfo? GetVaultInfo(Guid folderId)
    {
        var profile = LoadProfile(folderId);
        if (profile is null)
            return null;

        return new VaultCopyInfo
        {
            State = profile.Vault.State,
            Path = profile.Vault.Path,
            VerifiedAt = profile.Vault.VerifiedAt,
            FileCount = profile.Vault.FileCount,
            DirectoryCount = profile.Vault.DirectoryCount,
            PlaintextBytes = profile.Vault.PlaintextBytes,
            ManifestCiphertextSha256 = profile.Vault.ManifestCiphertextSha256
        };
    }

    public async Task<UnlockedProtectionSession?> UnlockWithPasswordAsync(
        Guid folderId,
        string password)
    {
        var profile = await LoadProfileAsync(folderId);
        if (profile is null)
            return null;

        try
        {
            var fek = UnwrapWithPassword(profile, password);

            if (fek.Length != FekSize)
            {
                CryptographicOperations.ZeroMemory(fek);
                throw new CryptographicException("Некорректный FEK.");
            }

            return new UnlockedProtectionSession(
                profile.ProfileId,
                profile.FolderId,
                fek);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public async Task<UnlockedProtectionSession?> UnlockWithRecoveryKeyAsync(
        Guid folderId,
        string recoveryKey)
    {
        var profile = await LoadProfileAsync(folderId);
        if (profile is null)
            return null;

        try
        {
            var fek = UnwrapWithRecovery(profile, recoveryKey);

            if (fek.Length != FekSize)
            {
                CryptographicOperations.ZeroMemory(fek);
                throw new CryptographicException("Некорректный FEK.");
            }

            return new UnlockedProtectionSession(
                profile.ProfileId,
                profile.FolderId,
                fek);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task MarkVaultVerifiedAsync(
        Guid folderId,
        VerifiedVaultResult result)
    {
        var profile = await LoadProfileAsync(folderId)
            ?? throw new InvalidOperationException("Профиль защиты не найден.");

        profile.Vault = new VaultCopyInfo
        {
            State = VaultCopyState.VerifiedCopy,
            Path = result.VaultPath,
            VerifiedAt = DateTimeOffset.UtcNow,
            FileCount = result.FileCount,
            DirectoryCount = result.DirectoryCount,
            PlaintextBytes = result.PlaintextBytes,
            ManifestCiphertextSha256 = result.ManifestCiphertextSha256
        };

        await SaveProfileAtomicAsync(profile, overwrite: true);
    }

    public PreparedProtectionProfile PrepareStandardProfile(
        ManagedFolder folder,
        string password)
    {
        ArgumentNullException.ThrowIfNull(folder);

        if (password.Length < MinimumPasswordLength)
            throw new ArgumentException(
                $"Пароль должен содержать не менее {MinimumPasswordLength} символов.",
                nameof(password));

        if (HasPreparedProfile(folder.Id))
            throw new InvalidOperationException("Для этой папки профиль защиты уже создан.");

        var profileId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        var fek = RandomNumberGenerator.GetBytes(FekSize);
        var passwordSalt = RandomNumberGenerator.GetBytes(SaltSize);
        var recoverySalt = RandomNumberGenerator.GetBytes(SaltSize);
        var recoverySecret = RandomNumberGenerator.GetBytes(32);
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        byte[]? passwordKek = null;
        byte[]? recoveryKek = null;

        try
        {
            passwordKek = Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                passwordSalt,
                PasswordIterations,
                HashAlgorithmName.SHA256,
                FekSize);

            recoveryKek = DeriveRecoveryKek(recoverySecret, recoverySalt);

            var aad = BuildAssociatedData(
                profileId,
                folder.Id,
                ProtectionMode.Standard);

            var profile = new ProtectionProfile
            {
                FormatVersion = 1,
                ProfileId = profileId,
                FolderId = folder.Id,
                FolderPathSnapshot = folder.Path,
                Mode = ProtectionMode.Standard,
                CreatedAt = createdAt,
                PasswordKdf = new PasswordKdfSettings
                {
                    Iterations = PasswordIterations,
                    Salt = Convert.ToBase64String(passwordSalt)
                },
                PasswordWrappedFek = WrapKey(fek, passwordKek, aad),
                RecoveryKdf = new RecoveryKdfSettings
                {
                    Salt = Convert.ToBase64String(recoverySalt)
                },
                RecoveryWrappedFek = WrapKey(fek, recoveryKek, aad)
            };

            var recoveryKey = RecoveryKeyCodec.Encode(recoverySecret);
            var fingerprint = GetProfileFingerprint(profile);

            // Fail closed if either recovery route cannot reproduce the FEK
            // before anything is persisted.
            VerifyPreparedProfile(profile, password, recoveryKey, fek);

            return new PreparedProtectionProfile(
                profile,
                recoveryKey,
                fingerprint);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fek);
            CryptographicOperations.ZeroMemory(passwordSalt);
            CryptographicOperations.ZeroMemory(recoverySalt);
            CryptographicOperations.ZeroMemory(recoverySecret);
            CryptographicOperations.ZeroMemory(passwordBytes);

            if (passwordKek is not null)
                CryptographicOperations.ZeroMemory(passwordKek);

            if (recoveryKek is not null)
                CryptographicOperations.ZeroMemory(recoveryKek);
        }
    }

    public async Task SavePreparedProfileAsync(ProtectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (File.Exists(GetProfilePath(profile.FolderId)))
            throw new InvalidOperationException("Профиль защиты уже существует.");

        await SaveProfileAtomicAsync(profile, overwrite: false);
    }

    public async Task<bool> VerifyPasswordAsync(Guid folderId, string password)
    {
        var profile = await LoadProfileAsync(folderId);
        if (profile is null)
            return false;

        byte[]? fek = null;

        try
        {
            fek = UnwrapWithPassword(profile, password);
            return fek.Length == FekSize;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            if (fek is not null)
                CryptographicOperations.ZeroMemory(fek);
        }
    }

    public async Task<bool> VerifyRecoveryKeyAsync(Guid folderId, string recoveryKey)
    {
        var profile = await LoadProfileAsync(folderId);
        if (profile is null)
            return false;

        byte[]? fek = null;

        try
        {
            fek = UnwrapWithRecovery(profile, recoveryKey);
            return fek.Length == FekSize;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (fek is not null)
                CryptographicOperations.ZeroMemory(fek);
        }
    }

    public string GetProfileFingerprint(Guid folderId)
    {
        var path = GetProfilePath(folderId);
        if (!File.Exists(path))
            return string.Empty;

        using var stream = File.OpenRead(path);
        var profile = JsonSerializer.Deserialize<ProtectionProfile>(stream, _jsonOptions);
        return profile is null ? string.Empty : GetProfileFingerprint(profile);
    }

    private ProtectionProfile? LoadProfile(Guid folderId)
    {
        var path = GetProfilePath(folderId);
        if (!File.Exists(path))
            return null;

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ProtectionProfile>(
            stream,
            _jsonOptions);
    }

    private async Task<ProtectionProfile?> LoadProfileAsync(Guid folderId)
    {
        var path = GetProfilePath(folderId);
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProtectionProfile>(
            stream,
            _jsonOptions);
    }

    private async Task SaveProfileAtomicAsync(
        ProtectionProfile profile,
        bool overwrite)
    {
        var path = GetProfilePath(profile.FolderId);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            await using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    profile,
                    _jsonOptions);

                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static void VerifyPreparedProfile(
        ProtectionProfile profile,
        string password,
        string recoveryKey,
        byte[] expectedFek)
    {
        var passwordFek = UnwrapWithPassword(profile, password);
        var recoveryFek = UnwrapWithRecovery(profile, recoveryKey);

        try
        {
            if (!CryptographicOperations.FixedTimeEquals(passwordFek, expectedFek) ||
                !CryptographicOperations.FixedTimeEquals(recoveryFek, expectedFek))
            {
                throw new CryptographicException(
                    "Проверка созданного профиля защиты не пройдена.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordFek);
            CryptographicOperations.ZeroMemory(recoveryFek);
        }
    }

    private static byte[] UnwrapWithPassword(
        ProtectionProfile profile,
        string password)
    {
        if (profile.FormatVersion != 1 ||
            profile.PasswordKdf.Algorithm != "PBKDF2-HMAC-SHA256")
        {
            throw new CryptographicException("Неподдерживаемый формат защиты.");
        }

        var salt = Convert.FromBase64String(profile.PasswordKdf.Salt);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        byte[]? kek = null;

        try
        {
            kek = Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                profile.PasswordKdf.Iterations,
                HashAlgorithmName.SHA256,
                FekSize);

            return UnwrapKey(
                profile.PasswordWrappedFek,
                kek,
                BuildAssociatedData(
                    profile.ProfileId,
                    profile.FolderId,
                    profile.Mode));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(salt);

            if (kek is not null)
                CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static byte[] UnwrapWithRecovery(
        ProtectionProfile profile,
        string recoveryKey)
    {
        if (!RecoveryKeyCodec.TryDecode(recoveryKey, out var recoverySecret))
            throw new FormatException("Некорректный Master Recovery Key.");

        var salt = Convert.FromBase64String(profile.RecoveryKdf.Salt);
        byte[]? kek = null;

        try
        {
            kek = DeriveRecoveryKek(recoverySecret, salt);

            return UnwrapKey(
                profile.RecoveryWrappedFek,
                kek,
                BuildAssociatedData(
                    profile.ProfileId,
                    profile.FolderId,
                    profile.Mode));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoverySecret);
            CryptographicOperations.ZeroMemory(salt);

            if (kek is not null)
                CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static WrappedKeyMaterial WrapKey(
        byte[] plaintextKey,
        byte[] kek,
        byte[] aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextKey.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(kek, TagSize);
        aes.Encrypt(nonce, plaintextKey, ciphertext, tag, aad);

        return new WrappedKeyMaterial
        {
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(ciphertext),
            Tag = Convert.ToBase64String(tag)
        };
    }

    private static byte[] UnwrapKey(
        WrappedKeyMaterial wrapped,
        byte[] kek,
        byte[] aad)
    {
        if (wrapped.Algorithm != "AES-256-GCM")
            throw new CryptographicException("Неподдерживаемый алгоритм ключа.");

        var nonce = Convert.FromBase64String(wrapped.Nonce);
        var ciphertext = Convert.FromBase64String(wrapped.Ciphertext);
        var tag = Convert.FromBase64String(wrapped.Tag);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(kek, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    private static byte[] DeriveRecoveryKek(
        byte[] recoverySecret,
        byte[] salt)
    {
        var message = new byte[RecoveryContext.Length + salt.Length];

        try
        {
            Buffer.BlockCopy(
                RecoveryContext,
                0,
                message,
                0,
                RecoveryContext.Length);

            Buffer.BlockCopy(
                salt,
                0,
                message,
                RecoveryContext.Length,
                salt.Length);

            using var hmac = new HMACSHA256(recoverySecret);
            return hmac.ComputeHash(message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
        }
    }

    private static byte[] BuildAssociatedData(
        Guid profileId,
        Guid folderId,
        ProtectionMode mode) =>
        Encoding.UTF8.GetBytes(
            $"GeniaFolder|profile-v1|{profileId:N}|{folderId:N}|{mode}");

    private static string GetProfileFingerprint(ProtectionProfile profile)
    {
        var data = Encoding.UTF8.GetBytes(
            $"{profile.ProfileId:N}|{profile.FolderId:N}|{profile.FormatVersion}");

        try
        {
            var hash = SHA256.HashData(data);
            return string.Join(
                "-",
                Convert.ToHexString(hash.AsSpan(0, 6))
                    .Chunk(4)
                    .Select(chars => new string(chars)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private string GetProfilePath(Guid folderId) =>
        Path.Combine(_profilesRoot, $"{folderId:N}.json");

    private static class RecoveryKeyCodec
    {
        private const string Prefix = "GF1";
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        public static string Encode(byte[] secret)
        {
            if (secret.Length != 32)
                throw new ArgumentException("Recovery secret must be 256-bit.");

            var checksumHash = SHA256.HashData(secret);
            var payload = new byte[36];

            try
            {
                Buffer.BlockCopy(secret, 0, payload, 0, 32);
                Buffer.BlockCopy(checksumHash, 0, payload, 32, 4);

                var encoded = EncodeBase32(payload);
                var groups = Enumerable.Range(0, (encoded.Length + 4) / 5)
                    .Select(i => encoded.Substring(
                        i * 5,
                        Math.Min(5, encoded.Length - i * 5)));

                return Prefix + "-" + string.Join("-", groups);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(checksumHash);
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        public static bool TryDecode(string value, out byte[] secret)
        {
            secret = [];

            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = new string(
                value.Trim()
                    .ToUpperInvariant()
                    .Where(c => c != '-' && !char.IsWhiteSpace(c))
                    .ToArray());

            if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
                return false;

            byte[] payload;

            try
            {
                payload = DecodeBase32(normalized[Prefix.Length..]);
            }
            catch (FormatException)
            {
                return false;
            }

            if (payload.Length != 36)
            {
                CryptographicOperations.ZeroMemory(payload);
                return false;
            }

            var candidate = payload[..32];
            var expectedChecksum = payload[32..36];
            var actualHash = SHA256.HashData(candidate);

            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                    expectedChecksum,
                    actualHash.AsSpan(0, 4)))
                {
                    CryptographicOperations.ZeroMemory(candidate);
                    return false;
                }

                secret = candidate;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedChecksum);
                CryptographicOperations.ZeroMemory(actualHash);
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        private static string EncodeBase32(byte[] data)
        {
            var output = new StringBuilder((data.Length * 8 + 4) / 5);
            var buffer = 0;
            var bitsInBuffer = 0;

            foreach (var b in data)
            {
                buffer = (buffer << 8) | b;
                bitsInBuffer += 8;

                while (bitsInBuffer >= 5)
                {
                    bitsInBuffer -= 5;
                    output.Append(Alphabet[(buffer >> bitsInBuffer) & 31]);
                    buffer &= (1 << bitsInBuffer) - 1;
                }
            }

            if (bitsInBuffer > 0)
                output.Append(Alphabet[(buffer << (5 - bitsInBuffer)) & 31]);

            return output.ToString();
        }

        private static byte[] DecodeBase32(string text)
        {
            var bytes = new List<byte>(text.Length * 5 / 8);
            var buffer = 0;
            var bitsInBuffer = 0;

            foreach (var raw in text)
            {
                var c = raw switch
                {
                    'O' => '0',
                    'I' or 'L' => '1',
                    _ => raw
                };

                var value = Alphabet.IndexOf(c);
                if (value < 0)
                    throw new FormatException("Invalid Base32 character.");

                buffer = (buffer << 5) | value;
                bitsInBuffer += 5;

                if (bitsInBuffer >= 8)
                {
                    bitsInBuffer -= 8;
                    bytes.Add((byte)((buffer >> bitsInBuffer) & 0xFF));
                    buffer &= (1 << bitsInBuffer) - 1;
                }
            }

            return bytes.ToArray();
        }
    }
}

public sealed record PreparedProtectionProfile(
    ProtectionProfile Profile,
    string RecoveryKey,
    string Fingerprint);


public sealed class UnlockedProtectionSession : IDisposable
{
    private byte[] _keyMaterial;
    private bool _disposed;

    public Guid ProfileId { get; }
    public Guid FolderId { get; }

    internal byte[] KeyMaterial
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _keyMaterial;
        }
    }

    internal UnlockedProtectionSession(
        Guid profileId,
        Guid folderId,
        byte[] keyMaterial)
    {
        ProfileId = profileId;
        FolderId = folderId;
        _keyMaterial = keyMaterial;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        CryptographicOperations.ZeroMemory(_keyMaterial);
        _keyMaterial = [];
        _disposed = true;
    }
}
