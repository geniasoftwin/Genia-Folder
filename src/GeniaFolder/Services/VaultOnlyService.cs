using GeniaFolder.Models;

namespace GeniaFolder.Services;

public sealed class VaultOnlyService
{
    private readonly ProtectionService _protection;
    private readonly VaultEncryptionService _vault;

    public VaultOnlyService(
        ProtectionService protection,
        VaultEncryptionService vault)
    {
        _protection = protection;
        _vault = vault;
    }

    public async Task ActivateOrResumeAsync(
        ManagedFolder folder,
        VaultCopyInfo vaultInfo,
        UnlockedProtectionSession session,
        IProgress<VaultBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(vaultInfo);
        ArgumentNullException.ThrowIfNull(session);

        if (vaultInfo.State != VaultCopyState.VerifiedCopy ||
            string.IsNullOrWhiteSpace(vaultInfo.Path) ||
            !Directory.Exists(vaultInfo.Path))
        {
            throw new InvalidOperationException(
                "Проверенный encrypted vault недоступен.");
        }

        progress?.Report(new VaultBuildProgress(
            "Повторная проверка vault",
            0,
            vaultInfo.FileCount,
            0,
            vaultInfo.PlaintextBytes));

        await _vault.VerifyVaultAsync(
            vaultInfo.Path,
            session,
            progress,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var storage = _protection.GetStorageInfo(folder.Id)
            ?? new VaultStorageInfo();

        if (storage.State == VaultStorageState.VaultOnly)
            return;

        if (storage.State == VaultStorageState.LockPending)
        {
            await ResumePendingLockAsync(
                folder,
                vaultInfo,
                storage,
                session,
                progress,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        if (!Directory.Exists(folder.Path))
        {
            throw new DirectoryNotFoundException(
                "Исходная plaintext-папка не найдена.");
        }

        await _vault.VerifySourceMatchesVaultAsync(
            folder,
            vaultInfo.Path,
            session,
            progress,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var parent = Directory.GetParent(
            Path.GetFullPath(folder.Path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar))?.FullName;

        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException(
                "Корень диска нельзя перевести в Vault-only.");
        }

        var pendingPath = Path.Combine(
            parent,
            $".geniafolder-plaintext-{folder.Id:N}.pending-delete-{Guid.NewGuid():N}");

        await _protection.MarkLockPendingAsync(
            folder.Id,
            pendingPath).ConfigureAwait(false);

        // Point of no return for this attempt. From here cancellation is not
        // observed: either we finish, or crash recovery resumes LockPending.
        progress?.Report(new VaultBuildProgress(
            "Финализация Vault-only",
            vaultInfo.FileCount,
            vaultInfo.FileCount,
            vaultInfo.PlaintextBytes,
            vaultInfo.PlaintextBytes));

        Directory.Move(folder.Path, pendingPath);

        TryHidePendingDirectory(pendingPath);

        VaultEncryptionService.DeletePlaintextTreeOrThrow(
            pendingPath);

        await _protection.MarkVaultOnlyAsync(
            folder.Id).ConfigureAwait(false);
    }

    private async Task ResumePendingLockAsync(
        ManagedFolder folder,
        VaultCopyInfo vaultInfo,
        VaultStorageInfo storage,
        UnlockedProtectionSession session,
        IProgress<VaultBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceExists = Directory.Exists(folder.Path);
        var pendingPath = storage.PendingPlaintextPath;

        if (!string.IsNullOrWhiteSpace(pendingPath))
            ValidatePendingPath(folder, pendingPath);

        var pendingExists =
            !string.IsNullOrWhiteSpace(pendingPath) &&
            Directory.Exists(pendingPath);

        if (sourceExists && pendingExists)
        {
            throw new InvalidOperationException(
                "Обнаружены одновременно исходная папка и pending-quarantine. " +
                "Автоматическое удаление остановлено.");
        }

        if (sourceExists && !pendingExists)
        {
            await _vault.VerifySourceMatchesVaultAsync(
                folder,
                vaultInfo.Path,
                session,
                progress,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(pendingPath))
            {
                var parent = Directory.GetParent(
                    Path.GetFullPath(folder.Path)
                        .TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar))?.FullName
                    ?? throw new InvalidOperationException(
                        "Не удалось определить родительскую папку.");

                pendingPath = Path.Combine(
                    parent,
                    $".geniafolder-plaintext-{folder.Id:N}.pending-delete-{Guid.NewGuid():N}");

                await _protection.MarkLockPendingAsync(
                    folder.Id,
                    pendingPath).ConfigureAwait(false);
            }

            progress?.Report(new VaultBuildProgress(
                "Продолжение Vault-only",
                vaultInfo.FileCount,
                vaultInfo.FileCount,
                vaultInfo.PlaintextBytes,
                vaultInfo.PlaintextBytes));

            Directory.Move(folder.Path, pendingPath);
            pendingExists = true;
            TryHidePendingDirectory(pendingPath);
        }

        if (pendingExists)
        {
            // The presence of our recorded quarantine means the atomic move
            // already happened after source/vault equality was verified.
            VaultEncryptionService.DeletePlaintextTreeOrThrow(
                pendingPath);
        }

        // Both source and pending being absent is a valid crash point:
        // deletion completed, but profile commit did not.
        await _protection.MarkVaultOnlyAsync(
            folder.Id).ConfigureAwait(false);
    }

    private static void ValidatePendingPath(
        ManagedFolder folder,
        string pendingPath)
    {
        var sourceRoot = Path.GetFullPath(folder.Path)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

        var parent = Directory.GetParent(sourceRoot)?.FullName
            ?? throw new InvalidOperationException(
                "Не удалось определить родительскую папку.");

        var fullParent = Path.GetFullPath(parent)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

        var fullPending = Path.GetFullPath(pendingPath)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

        var pendingParent = Directory.GetParent(fullPending)?.FullName;

        if (string.IsNullOrWhiteSpace(pendingParent) ||
            !string.Equals(
                Path.GetFullPath(pendingParent)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                fullParent,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "LockPending содержит небезопасный путь quarantine.");
        }

        var expectedPrefix =
            $".geniafolder-plaintext-{folder.Id:N}.pending-delete-";

        var pendingName = Path.GetFileName(fullPending);

        if (!pendingName.StartsWith(
            expectedPrefix,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "LockPending quarantine не принадлежит этой папке.");
        }
    }

    private static void TryHidePendingDirectory(string path)
    {
        try
        {
            File.SetAttributes(
                path,
                File.GetAttributes(path) |
                FileAttributes.Hidden |
                FileAttributes.System);
        }
        catch
        {
            // Cosmetic only. Transaction state lives in the profile.
        }
    }
}
