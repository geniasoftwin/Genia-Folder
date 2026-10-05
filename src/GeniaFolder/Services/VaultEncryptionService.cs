using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GeniaFolder.Models;

namespace GeniaFolder.Services;

public sealed class VaultEncryptionService
{
    private const int ChunkSize = 1024 * 1024;
    private const int NonceSize = 12;
    private const int NoncePrefixSize = 8;
    private const int TagSize = 16;

    private const string ManifestFileName = "manifest.gfm";
    private const string DataDirectoryName = "data";

    private static readonly byte[] FileMagic = Encoding.ASCII.GetBytes("GFCHNK01");
    private static readonly byte[] ManifestMagic = Encoding.ASCII.GetBytes("GFMETA01");

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false
    };

    public async Task<VerifiedVaultResult> BuildOrAdoptVerifiedVaultAsync(
        ManagedFolder folder,
        UnlockedProtectionSession session,
        IProgress<VaultBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(session);

        if (folder.Id != session.FolderId)
            throw new InvalidOperationException("Ключ не принадлежит выбранной папке.");

        var sourceRoot = Path.GetFullPath(folder.Path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException(sourceRoot);

        if ((File.GetAttributes(sourceRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Защита папок-ссылок/reparse point пока не поддерживается.");

        var parent = Directory.GetParent(sourceRoot)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("Корень диска нельзя преобразовать в vault.");

        var finalVaultPath = Path.Combine(
            parent,
            $".geniafolder-vault-{folder.Id:N}");

        if (Directory.Exists(finalVaultPath))
        {
            progress?.Report(new VaultBuildProgress(
                "Проверка существующей зашифрованной копии",
                0,
                0,
                0,
                0));

            return await VerifyVaultAsync(
                finalVaultPath,
                session,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        var stagingPath = finalVaultPath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(stagingPath);
            var dataPath = Path.Combine(stagingPath, DataDirectoryName);
            Directory.CreateDirectory(dataPath);

            var source = EnumerateSource(sourceRoot, cancellationToken);

            progress?.Report(new VaultBuildProgress(
                "Подготовка",
                0,
                source.Files.Count,
                0,
                source.TotalBytes));

            var manifest = new VaultManifest
            {
                ProfileId = session.ProfileId,
                FolderId = session.FolderId,
                CreatedAt = DateTimeOffset.UtcNow,
                Directories = source.Directories
                    .Select(d => new VaultDirectoryEntry
                    {
                        RelativePath = d.RelativePath,
                        Attributes = d.Attributes,
                        LastWriteTimeUtc = d.LastWriteTimeUtc
                    })
                    .ToList()
            };

            long encryptedBytes = 0;
            var processedFiles = 0;

            foreach (var sourceFile in source.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileId = Guid.NewGuid();
                var encryptedPath = Path.Combine(
                    dataPath,
                    fileId.ToString("N") + ".gfc");

                var hash = await EncryptFileAsync(
                    sourceFile,
                    encryptedPath,
                    fileId,
                    session,
                    cancellationToken).ConfigureAwait(false);

                manifest.Files.Add(new VaultFileEntry
                {
                    FileId = fileId,
                    RelativePath = sourceFile.RelativePath,
                    Length = sourceFile.Length,
                    Attributes = sourceFile.Attributes,
                    LastWriteTimeUtc = sourceFile.LastWriteTimeUtc,
                    PlaintextSha256 = hash
                });

                processedFiles++;
                encryptedBytes += sourceFile.Length;

                progress?.Report(new VaultBuildProgress(
                    "Шифрование",
                    processedFiles,
                    source.Files.Count,
                    encryptedBytes,
                    source.TotalBytes));
            }

            var manifestPath = Path.Combine(stagingPath, ManifestFileName);
            await WriteEncryptedManifestAsync(
                manifestPath,
                manifest,
                session,
                cancellationToken).ConfigureAwait(false);

            var verified = await VerifyVaultAsync(
                stagingPath,
                session,
                progress,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            Directory.Move(stagingPath, finalVaultPath);

            try
            {
                File.SetAttributes(
                    finalVaultPath,
                    File.GetAttributes(finalVaultPath) |
                    FileAttributes.Hidden |
                    FileAttributes.System);
            }
            catch
            {
                // Hiding is cosmetic. The verified encryption is what matters.
            }

            return verified with { VaultPath = finalVaultPath };
        }
        catch
        {
            TryDeleteDirectory(stagingPath);
            throw;
        }
    }

    public async Task<VerifiedVaultResult> VerifyVaultAsync(
        string vaultPath,
        UnlockedProtectionSession session,
        IProgress<VaultBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(vaultPath, ManifestFileName);
        var dataPath = Path.Combine(vaultPath, DataDirectoryName);

        if (!File.Exists(manifestPath) || !Directory.Exists(dataPath))
            throw new CryptographicException("Структура vault неполна.");

        var manifest = await ReadEncryptedManifestAsync(
            manifestPath,
            session,
            cancellationToken).ConfigureAwait(false);

        if (manifest.FormatVersion != 1 ||
            manifest.ProfileId != session.ProfileId ||
            manifest.FolderId != session.FolderId)
        {
            throw new CryptographicException("Vault не соответствует профилю защиты.");
        }

        var totalBytes = manifest.Files.Sum(f => f.Length);
        long verifiedBytes = 0;
        var verifiedFiles = 0;

        progress?.Report(new VaultBuildProgress(
            "Проверка",
            0,
            manifest.Files.Count,
            0,
            totalBytes));

        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var encryptedPath = Path.Combine(
                dataPath,
                entry.FileId.ToString("N") + ".gfc");

            await VerifyEncryptedFileAsync(
                encryptedPath,
                entry,
                session,
                cancellationToken).ConfigureAwait(false);

            verifiedFiles++;
            verifiedBytes += entry.Length;

            progress?.Report(new VaultBuildProgress(
                "Проверка",
                verifiedFiles,
                manifest.Files.Count,
                verifiedBytes,
                totalBytes));
        }

        var ciphertextHash = await ComputeFileSha256Async(
            manifestPath,
            cancellationToken).ConfigureAwait(false);

        return new VerifiedVaultResult(
            vaultPath,
            manifest.Files.Count,
            manifest.Directories.Count,
            totalBytes,
            ciphertextHash);
    }

    private async Task<string> EncryptFileAsync(
        SourceFile sourceFile,
        string outputPath,
        Guid fileId,
        UnlockedProtectionSession session,
        CancellationToken cancellationToken)
    {
        var before = new FileInfo(sourceFile.FullPath);
        var expectedLength = before.Length;
        var expectedWriteTime = before.LastWriteTimeUtc;

        if (expectedLength != sourceFile.Length ||
            expectedWriteTime != sourceFile.LastWriteTimeUtc)
        {
            throw new IOException(
                $"Файл изменился до начала шифрования: {sourceFile.RelativePath}");
        }

        await using var input = new FileStream(
            sourceFile.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            ChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        using var aes = new AesGcm(session.KeyMaterial, TagSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
        var plaintext = new byte[ChunkSize];
        var ciphertext = new byte[ChunkSize];
        var tag = new byte[TagSize];
        var nonce = new byte[NonceSize];

        try
        {
            writer.Write(FileMagic);
            writer.Write(1);
            writer.Write(ChunkSize);
            writer.Write(expectedLength);
            writer.Write(fileId.ToByteArray());
            writer.Write(noncePrefix);

            uint chunkIndex = 0;
            long totalRead = 0;

            while (true)
            {
                var read = await ReadChunkAsync(
                    input,
                    plaintext,
                    cancellationToken).ConfigureAwait(false);

                if (read == 0)
                    break;

                if (chunkIndex == uint.MaxValue)
                    throw new CryptographicException("Файл слишком велик для формата vault v1.");

                hash.AppendData(plaintext, 0, read);

                Buffer.BlockCopy(
                    noncePrefix,
                    0,
                    nonce,
                    0,
                    NoncePrefixSize);

                BinaryPrimitives.WriteUInt32BigEndian(
                    nonce.AsSpan(NoncePrefixSize, 4),
                    chunkIndex);

                var aad = BuildChunkAssociatedData(
                    session.ProfileId,
                    session.FolderId,
                    fileId,
                    chunkIndex,
                    expectedLength,
                    read);

                try
                {
                    aes.Encrypt(
                        nonce,
                        plaintext.AsSpan(0, read),
                        ciphertext.AsSpan(0, read),
                        tag,
                        aad);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(aad);
                }

                writer.Write(read);
                await output.WriteAsync(
                    ciphertext.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);

                writer.Write(tag);

                CryptographicOperations.ZeroMemory(
                    ciphertext.AsSpan(0, read));

                totalRead += read;
                chunkIndex++;
            }

            writer.Flush();
            output.Flush(flushToDisk: true);

            if (totalRead != expectedLength)
            {
                throw new IOException(
                    $"Размер файла изменился во время шифрования: {sourceFile.RelativePath}");
            }

            before.Refresh();

            if (before.Length != expectedLength ||
                before.LastWriteTimeUtc != expectedWriteTime)
            {
                throw new IOException(
                    $"Файл изменился во время шифрования: {sourceFile.RelativePath}");
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(noncePrefix);
        }
    }

    private async Task VerifyEncryptedFileAsync(
        string encryptedPath,
        VaultFileEntry expected,
        UnlockedProtectionSession session,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(encryptedPath))
            throw new CryptographicException(
                $"В vault отсутствует блок файла {expected.FileId:N}.");

        await using var input = new FileStream(
            encryptedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

        var magic = reader.ReadBytes(FileMagic.Length);
        if (!magic.SequenceEqual(FileMagic))
            throw new CryptographicException("Неверная сигнатура зашифрованного файла.");

        var version = reader.ReadInt32();
        var chunkSize = reader.ReadInt32();
        var originalLength = reader.ReadInt64();
        var fileId = new Guid(reader.ReadBytes(16));
        var noncePrefix = reader.ReadBytes(NoncePrefixSize);

        if (version != 1 ||
            chunkSize != ChunkSize ||
            originalLength != expected.Length ||
            fileId != expected.FileId ||
            noncePrefix.Length != NoncePrefixSize)
        {
            throw new CryptographicException("Заголовок зашифрованного файла повреждён.");
        }

        using var aes = new AesGcm(session.KeyMaterial, TagSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var ciphertext = new byte[ChunkSize];
        var plaintext = new byte[ChunkSize];
        var tag = new byte[TagSize];
        var nonce = new byte[NonceSize];

        try
        {
            uint chunkIndex = 0;
            long totalPlaintext = 0;

            while (totalPlaintext < originalLength)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (input.Position >= input.Length)
                    throw new CryptographicException("Зашифрованный файл обрезан.");

                var length = reader.ReadInt32();

                if (length <= 0 ||
                    length > ChunkSize ||
                    totalPlaintext + length > originalLength)
                {
                    throw new CryptographicException("Некорректный размер блока vault.");
                }

                await input.ReadExactlyAsync(
                    ciphertext.AsMemory(0, length),
                    cancellationToken).ConfigureAwait(false);

                await input.ReadExactlyAsync(
                    tag,
                    cancellationToken).ConfigureAwait(false);

                Buffer.BlockCopy(
                    noncePrefix,
                    0,
                    nonce,
                    0,
                    NoncePrefixSize);

                BinaryPrimitives.WriteUInt32BigEndian(
                    nonce.AsSpan(NoncePrefixSize, 4),
                    chunkIndex);

                var aad = BuildChunkAssociatedData(
                    session.ProfileId,
                    session.FolderId,
                    fileId,
                    chunkIndex,
                    originalLength,
                    length);

                try
                {
                    aes.Decrypt(
                        nonce,
                        ciphertext.AsSpan(0, length),
                        tag,
                        plaintext.AsSpan(0, length),
                        aad);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(aad);
                }

                hash.AppendData(plaintext, 0, length);

                CryptographicOperations.ZeroMemory(
                    plaintext.AsSpan(0, length));

                CryptographicOperations.ZeroMemory(
                    ciphertext.AsSpan(0, length));

                totalPlaintext += length;
                chunkIndex++;
            }

            if (input.Position != input.Length)
                throw new CryptographicException("В зашифрованном файле обнаружены лишние данные.");

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());

            if (!string.Equals(
                actualHash,
                expected.PlaintextSha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException(
                    $"Контрольная сумма файла не совпала: {expected.RelativePath}");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(noncePrefix);
            CryptographicOperations.ZeroMemory(magic);
        }
    }

    private async Task WriteEncryptedManifestAsync(
        string path,
        VaultManifest manifest,
        UnlockedProtectionSession session,
        CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            _jsonOptions);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        var aad = BuildManifestAssociatedData(
            session.ProfileId,
            session.FolderId);

        try
        {
            using var aes = new AesGcm(session.KeyMaterial, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

            await using var output = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);

            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            writer.Write(ManifestMagic);
            writer.Write(1);
            writer.Write(nonce);
            writer.Write(ciphertext.Length);

            await output.WriteAsync(
                ciphertext,
                cancellationToken).ConfigureAwait(false);

            writer.Write(tag);
            writer.Flush();
            output.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private async Task<VaultManifest> ReadEncryptedManifestAsync(
        string path,
        UnlockedProtectionSession session,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

        var magic = reader.ReadBytes(ManifestMagic.Length);
        if (!magic.SequenceEqual(ManifestMagic))
            throw new CryptographicException("Неверная сигнатура manifest vault.");

        var version = reader.ReadInt32();
        var nonce = reader.ReadBytes(NonceSize);
        var cipherLength = reader.ReadInt32();

        if (version != 1 ||
            nonce.Length != NonceSize ||
            cipherLength < 0 ||
            cipherLength > 256 * 1024 * 1024)
        {
            throw new CryptographicException("Заголовок manifest vault повреждён.");
        }

        var ciphertext = new byte[cipherLength];
        var tag = new byte[TagSize];
        var plaintext = new byte[cipherLength];
        var aad = BuildManifestAssociatedData(
            session.ProfileId,
            session.FolderId);

        try
        {
            await input.ReadExactlyAsync(
                ciphertext,
                cancellationToken).ConfigureAwait(false);

            await input.ReadExactlyAsync(
                tag,
                cancellationToken).ConfigureAwait(false);

            if (input.Position != input.Length)
                throw new CryptographicException("Manifest vault содержит лишние данные.");

            using var aes = new AesGcm(session.KeyMaterial, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);

            return JsonSerializer.Deserialize<VaultManifest>(
                plaintext,
                _jsonOptions)
                ?? throw new CryptographicException("Manifest vault пуст.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(magic);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private static SourceSnapshot EnumerateSource(
        string root,
        CancellationToken cancellationToken)
    {
        var directories = new List<SourceDirectory>();
        var files = new List<SourceFile>();
        long totalBytes = 0;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = stack.Pop();

            foreach (var directoryPath in Directory.EnumerateDirectories(current))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var info = new DirectoryInfo(directoryPath);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"Ссылки/reparse point пока не поддерживаются: {directoryPath}");
                }

                var relative = Path.GetRelativePath(root, directoryPath);
                directories.Add(new SourceDirectory(
                    relative,
                    info.Attributes,
                    info.LastWriteTimeUtc));

                stack.Push(directoryPath);
            }

            foreach (var filePath in Directory.EnumerateFiles(current))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase) &&
                    IsGeneratedRootMetadata(Path.GetFileName(filePath)))
                {
                    continue;
                }

                var info = new FileInfo(filePath);

                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"Файлы-ссылки/reparse point пока не поддерживаются: {filePath}");
                }

                var relative = Path.GetRelativePath(root, filePath);

                files.Add(new SourceFile(
                    filePath,
                    relative,
                    info.Length,
                    info.Attributes,
                    info.LastWriteTimeUtc));

                checked
                {
                    totalBytes += info.Length;
                }
            }
        }

        files.Sort((a, b) =>
            StringComparer.OrdinalIgnoreCase.Compare(
                a.RelativePath,
                b.RelativePath));

        directories.Sort((a, b) =>
            StringComparer.OrdinalIgnoreCase.Compare(
                a.RelativePath,
                b.RelativePath));

        return new SourceSnapshot(directories, files, totalBytes);
    }

    private static bool IsGeneratedRootMetadata(string fileName) =>
        string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, ".geniafolder.ico", StringComparison.OrdinalIgnoreCase) ||
        (fileName.StartsWith(".geniafolder-", StringComparison.OrdinalIgnoreCase) &&
         fileName.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));

    private static async Task<int> ReadChunkAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(total, buffer.Length - total),
                cancellationToken).ConfigureAwait(false);

            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    private static byte[] BuildChunkAssociatedData(
        Guid profileId,
        Guid folderId,
        Guid fileId,
        uint chunkIndex,
        long originalLength,
        int chunkLength)
    {
        var aad = new byte[16 + 16 + 16 + 4 + 8 + 4];

        profileId.TryWriteBytes(aad.AsSpan(0, 16));
        folderId.TryWriteBytes(aad.AsSpan(16, 16));
        fileId.TryWriteBytes(aad.AsSpan(32, 16));

        BinaryPrimitives.WriteUInt32BigEndian(
            aad.AsSpan(48, 4),
            chunkIndex);

        BinaryPrimitives.WriteInt64BigEndian(
            aad.AsSpan(52, 8),
            originalLength);

        BinaryPrimitives.WriteInt32BigEndian(
            aad.AsSpan(60, 4),
            chunkLength);

        return aad;
    }

    private static byte[] BuildManifestAssociatedData(
        Guid profileId,
        Guid folderId)
    {
        var aad = new byte[32];
        profileId.TryWriteBytes(aad.AsSpan(0, 16));
        folderId.TryWriteBytes(aad.AsSpan(16, 16));
        return aad;
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer,
                    cancellationToken).ConfigureAwait(false);

                if (read == 0)
                    break;

                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            foreach (var file in Directory.EnumerateFiles(
                path,
                "*",
                SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A failed staging cleanup never justifies touching source data.
        }
    }

    private sealed record SourceSnapshot(
        List<SourceDirectory> Directories,
        List<SourceFile> Files,
        long TotalBytes);

    private sealed record SourceDirectory(
        string RelativePath,
        FileAttributes Attributes,
        DateTime LastWriteTimeUtc);

    private sealed record SourceFile(
        string FullPath,
        string RelativePath,
        long Length,
        FileAttributes Attributes,
        DateTime LastWriteTimeUtc);
}
