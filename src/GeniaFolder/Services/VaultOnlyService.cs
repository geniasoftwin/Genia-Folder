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

        if (session.FolderId != folder.Id ||
            vaultInfo.State != VaultCopyState.VerifiedCopy ||
            string.IsNullOrWhiteSpace(vaultInfo.Path) ||
            !Directory.Exists(vaultInfo.Path))
        {
            throw new InvalidOperationException(
                "Ключ или проверенный encrypted vault недоступен.");
        }

        await _vault.VerifyVaultAsync(
            vaultInfo.Path,
            session,
            progress,
            cancellationToken).ConfigureAwait(false);

        var storage = _protection.GetStorageInfo(folder.Id)
            ?? new VaultStorageInfo();

        if (storage.State == VaultStorageState.VaultOnly)
            return;

        if (storage.State == VaultStorageState.DeletionCommitted)
        {
            // This state is durable: a complete verification of the isolated
            // plaintext succeeded BEFORE any deletion began. Resuming a
            // partially deleted quarantine must never hash partial contents.
            await CompleteCommittedDeletionAsync(
                folder, storage, progress).ConfigureAwait(false);
            return;
        }

        if (storage.State == VaultStorageState.LockPending)
        {
            await ResumePendingLockAsync(
                folder, vaultInfo, storage, session,
                progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!Directory.Exists(folder.Path))
            throw new DirectoryNotFoundException(
                "Исходная plaintext-папка не найдена.");

        // Early check improves user feedback, but the safety-critical check
        // happens only AFTER the folder has been atomically quarantined.
        await _vault.VerifySourceMatchesVaultAsync(
            folder, vaultInfo.Path, session,
            progress, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var parent = Directory.GetParent(
            Path.GetFullPath(folder.Path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))?.FullName
            ?? throw new InvalidOperationException(
                "Корень диска нельзя перевести в Vault-only.");

        var pendingPath = Path.Combine(
            parent,
            $".geniafolder-plaintext-{folder.Id:N}.pending-delete-{Guid.NewGuid():N}");

        // LockPending is persisted BEFORE moving any user data.
        await _protection.MarkLockPendingAsync(
            folder.Id, pendingPath).ConfigureAwait(false);

        // Cancellation is intentionally ignored from here until quarantine
        // is verified/rolled back or deletion is committed.
        await QuarantineVerifyAndCommitAsync(
            folder, vaultInfo, pendingPath, session,
            progress).ConfigureAwait(false);
    }

    private async Task ResumePendingLockAsync(
        ManagedFolder folder,
        VaultCopyInfo vaultInfo,
        VaultStorageInfo storage,
        UnlockedProtectionSession session,
        IProgress<VaultBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pendingPath = storage.PendingPlaintextPath;
        ValidatePendingPath(folder, pendingPath);

        var sourceExists = Directory.Exists(folder.Path);
        var pendingExists = Directory.Exists(pendingPath);

        if (sourceExists && pendingExists)
            throw new InvalidOperationException(
                "Исходная папка и quarantine существуют одновременно. Удаление заблокировано.");

        if (sourceExists)
        {
            await _vault.VerifySourceMatchesVaultAsync(
                folder, vaultInfo.Path, session,
                progress, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            await QuarantineVerifyAndCommitAsync(
                folder, vaultInfo, pendingPath,
                session, progress).ConfigureAwait(false);
            return;
        }

        if (!pendingExists)
        {
            // In LockPending the destructive commit was NEVER recorded.
            // We cannot assume any deletion is safe or complete.
            throw new InvalidOperationException(
                "LockPending: отсутствуют и оригинал, и карантин. Требуется ручная проверка.");
        }

        await VerifyQuarantineAndCommitAsync(
            folder, vaultInfo, pendingPath, session,
            progress).ConfigureAwait(false);
    }

    private async Task QuarantineVerifyAndCommitAsync(
        ManagedFolder folder,
        VaultCopyInfo vaultInfo,
        string pendingPath,
        UnlockedProtectionSession session,
        IProgress<VaultBuildProgress>? progress)
    {
        ValidatePendingPath(folder, pendingPath);

        if (Directory.Exists(pendingPath))
            throw new IOException("Quarantine уже существует.");

        progress?.Report(new VaultBuildProgress(
            "Изоляция plaintext",
            0, vaultInfo.FileCount, 0, vaultInfo.PlaintextBytes));

        Directory.Move(folder.Path, pendingPath);
        TryHidePendingDirectory(pendingPath);

        await VerifyQuarantineAndCommitAsync(
            folder, vaultInfo, pendingPath, session,
            progress).ConfigureAwait(false);
    }

    private async Task VerifyQuarantineAndCommitAsync(
        ManagedFolder folder,
        VaultCopyInfo vaultInfo,
        string pendingPath,
        UnlockedProtectionSession session,
        IProgress<VaultBuildProgress>? progress)
    {
        ValidatePendingPath(folder, pendingPath);

        // The original path is no longer writable through its previous name.
        // Verify what will ACTUALLY be deleted, not the old mutable path.
        var quarantinedFolder = new ManagedFolder
        {
            Id = folder.Id,
            Name = folder.Name,
            Path = pendingPath
        };

        try
        {
            progress?.Report(new VaultBuildProgress(
                "Финальная сверка изолированных данных",
                0, vaultInfo.FileCount, 0, vaultInfo.PlaintextBytes));

            await _vault.VerifySourceMatchesVaultAsync(
                quarantinedFolder,
                vaultInfo.Path,
                session,
                progress,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Mismatch or I/O failure: do not delete anything. If possible,
            // restore the original path and roll the profile back as well.
            if (Directory.Exists(pendingPath) &&
                !Directory.Exists(folder.Path))
            {
                Directory.Move(pendingPath, folder.Path);
                await _protection.MarkPlaintextPresentAsync(
                    folder.Id).ConfigureAwait(false);
            }

            throw;
        }

        // CRITICAL boundary: after this durable state update, interrupted
        // deletion can be safely resumed without needing the removed bytes.
        await _protection.MarkDeletionCommittedAsync(
            folder.Id).ConfigureAwait(false);

        await CompleteCommittedDeletionAsync(
            folder,
            new VaultStorageInfo
            {
                State = VaultStorageState.DeletionCommitted,
                PendingPlaintextPath = pendingPath
            },
            progress).ConfigureAwait(false);
    }

    private async Task CompleteCommittedDeletionAsync(
        ManagedFolder folder,
        VaultStorageInfo storage,
        IProgress<VaultBuildProgress>? progress)
    {
        ValidatePendingPath(folder, storage.PendingPlaintextPath);

        if (Directory.Exists(folder.Path))
            throw new InvalidOperationException(
                "В состоянии DeletionCommitted снова появился оригинальный путь. Удаление остановлено.");

        progress?.Report(new VaultBuildProgress(
            "Безвозвратная финализация Vault-only",
            0, 0, 0, 0));

        // The quarantine was fully checked before DeletionCommitted.
        // If process terminated during deletion, this may be partial.
        VaultEncryptionService.DeletePlaintextTreeOrThrow(
            storage.PendingPlaintextPath);

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
