using System.Security.Cryptography;
using System.Text;
using GeniaFolder.Models;
using GeniaFolder.Services;

internal static class Program
{
    private const string Password = "Smoke-Test-Password-123!";

    public static async Task<int> Main()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GeniaFolder.SecuritySmokeTests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            Console.WriteLine("=== GeniaFolder Security Smoke Tests ===");

            var source = Path.Combine(root, "source");
            var nested = Path.Combine(source, "nested");
            Directory.CreateDirectory(nested);

            await File.WriteAllTextAsync(
                Path.Combine(source, "root.txt"),
                "GeniaFolder security smoke test\nUTF-8: проверка\n",
                Encoding.UTF8);

            await File.WriteAllTextAsync(
                Path.Combine(nested, "nested.txt"),
                "Nested file used to verify path restoration.",
                Encoding.UTF8);

            var multiChunk = new byte[2 * 1024 * 1024 + 12345];
            RandomNumberGenerator.Fill(multiChunk);
            await File.WriteAllBytesAsync(
                Path.Combine(source, "multi.bin"),
                multiChunk);
            CryptographicOperations.ZeroMemory(multiChunk);

            var folder = new ManagedFolder
            {
                Id = Guid.NewGuid(),
                Name = "Smoke Vault",
                Path = source
            };

            var profilesRoot = Path.Combine(root, "profiles");
            var protection = new ProtectionService(profilesRoot);
            var vault = new VaultEncryptionService();

            var prepared = protection.PrepareStandardProfile(
                folder,
                Password);

            var recoveryKey = prepared.RecoveryKey;

            await protection.SavePreparedProfileAsync(prepared.Profile);
            Pass("profile creation and atomic persistence");

            var wrongPasswordSession = await protection.UnlockWithPasswordAsync(
                folder.Id,
                "Definitely-Wrong-Password!");

            Assert(
                wrongPasswordSession is null,
                "wrong password must not unlock FEK");
            Pass("wrong password rejected");

            var wrongRecovery = MutateRecoveryKey(recoveryKey);
            var wrongRecoverySession = await protection.UnlockWithRecoveryKeyAsync(
                folder.Id,
                wrongRecovery);

            Assert(
                wrongRecoverySession is null,
                "wrong Recovery Key must not unlock FEK");
            Pass("wrong Master Recovery Key rejected");

            VerifiedVaultResult built;

            using (var passwordSession =
                await RequirePasswordSessionAsync(protection, folder.Id))
            {
                built = await vault.BuildOrAdoptVerifiedVaultAsync(
                    folder,
                    passwordSession);

                await protection.MarkVaultVerifiedAsync(
                    folder.Id,
                    built);

                var verified = await vault.VerifyVaultAsync(
                    built.VaultPath,
                    passwordSession);

                Assert(
                    verified.FileCount == 3,
                    "verified vault must contain all source files");
            }

            Pass("vault encrypted and fully verified");

            var passwordRestore = Path.Combine(root, "restore-password");

            using (var passwordSession =
                await RequirePasswordSessionAsync(protection, folder.Id))
            {
                await vault.RestoreVaultAsync(
                    built.VaultPath,
                    passwordRestore,
                    passwordSession);
            }

            await AssertTreesEqualAsync(source, passwordRestore);
            Pass("password restore matches source byte-for-byte");

            var recoveryRestore = Path.Combine(root, "restore-recovery");

            using (var recoverySession =
                await protection.UnlockWithRecoveryKeyAsync(
                    folder.Id,
                    recoveryKey)
                ?? throw new InvalidOperationException(
                    "Valid Master Recovery Key failed to unlock FEK."))
            {
                await vault.RestoreVaultAsync(
                    built.VaultPath,
                    recoveryRestore,
                    recoverySession);
            }

            await AssertTreesEqualAsync(source, recoveryRestore);
            Pass("Master Recovery restore matches source byte-for-byte");

            var corruptManifestVault = Path.Combine(
                root,
                "corrupt-manifest-vault");

            CopyDirectory(built.VaultPath, corruptManifestVault);
            FlipOneByte(Path.Combine(
                corruptManifestVault,
                "manifest.gfm"));

            using (var passwordSession =
                await RequirePasswordSessionAsync(protection, folder.Id))
            {
                await ExpectFailureAsync(
                    "corrupted encrypted manifest",
                    async () =>
                    {
                        await vault.VerifyVaultAsync(
                            corruptManifestVault,
                            passwordSession);
                    });
            }

            var corruptFileVault = Path.Combine(
                root,
                "corrupt-file-vault");

            CopyDirectory(built.VaultPath, corruptFileVault);

            var encryptedFile = Directory
                .EnumerateFiles(
                    Path.Combine(corruptFileVault, "data"),
                    "*.gfc",
                    SearchOption.TopDirectoryOnly)
                .First();

            FlipOneByte(encryptedFile);

            using (var passwordSession =
                await RequirePasswordSessionAsync(protection, folder.Id))
            {
                await ExpectFailureAsync(
                    "corrupted encrypted file",
                    async () =>
                    {
                        await vault.VerifyVaultAsync(
                            corruptFileVault,
                            passwordSession);
                    });
            }

            var cancelledRestore = Path.Combine(
                root,
                "restore-cancelled");

            using (var passwordSession =
                await RequirePasswordSessionAsync(protection, folder.Id))
            using (var cts = new CancellationTokenSource())
            {
                var progress = new CancelAfterFirstFileProgress(cts);

                await ExpectCancellationAsync(
                    async () =>
                    {
                        await vault.RestoreVaultAsync(
                            built.VaultPath,
                            cancelledRestore,
                            passwordSession,
                            progress,
                            cts.Token);
                    });
            }

            Assert(
                !Directory.Exists(cancelledRestore),
                "cancelled restore must not publish destination");

            Assert(
                !Directory.EnumerateDirectories(
                    root,
                    Path.GetFileName(cancelledRestore) + ".tmp-*",
                    SearchOption.TopDirectoryOnly).Any(),
                "cancelled restore must clean staging output");

            Pass("cancelled restore leaves no published/staging plaintext");

            var orphanStaging = built.VaultPath + ".tmp-deadbeef";
            Directory.CreateDirectory(orphanStaging);
            await File.WriteAllTextAsync(
                Path.Combine(orphanStaging, "orphan.bin"),
                "simulated hard-kill staging");

            using (var passwordSession =
                await RequirePasswordSessionAsync(protection, folder.Id))
            {
                await vault.BuildOrAdoptVerifiedVaultAsync(
                    folder,
                    passwordSession);
            }

            Assert(
                !Directory.Exists(orphanStaging),
                "orphan vault staging must be cleaned on next build attempt");

            Pass("orphan staging cleaned after simulated force-kill");

            var vaultInfo = protection.GetVaultInfo(folder.Id)
                ?? throw new InvalidOperationException(
                    "Verified vault info is missing.");

            var vaultOnly = new VaultOnlyService(
                protection,
                vault);

            var changedAfterVault = Path.Combine(
                source,
                "changed-after-vault.txt");

            await File.WriteAllTextAsync(
                changedAfterVault,
                "This file did not exist when the vault was created.");

            using (var passwordSession =
                await RequirePasswordSessionAsync(protection, folder.Id))
            {
                await ExpectInvalidOperationAsync(
                    "Vault-only with stale plaintext",
                    async () =>
                    {
                        await vaultOnly.ActivateOrResumeAsync(
                            folder,
                            vaultInfo,
                            passwordSession);
                    });
            }

            Assert(
                Directory.Exists(source),
                "stale vault rejection must keep plaintext source");

            Assert(
                protection.GetStorageInfo(folder.Id)?.State ==
                VaultStorageState.PlaintextPresent,
                "stale vault rejection must not change storage state");

            File.Delete(changedAfterVault);
            Pass("stale vault cannot delete changed plaintext");

            using (var passwordSession =
                await RequirePasswordSessionAsync(protection, folder.Id))
            {
                await vaultOnly.ActivateOrResumeAsync(
                    folder,
                    vaultInfo,
                    passwordSession);
            }

            Assert(
                !Directory.Exists(source),
                "Vault-only must remove plaintext source path");

            Assert(
                protection.GetStorageInfo(folder.Id)?.State ==
                VaultStorageState.VaultOnly,
                "profile must enter VaultOnly after plaintext removal");

            Pass("Vault-only removes matching plaintext only after verification");

            var orphanRestoreStaging = source + ".tmp-hardkill";
            Directory.CreateDirectory(orphanRestoreStaging);
            await File.WriteAllTextAsync(
                Path.Combine(orphanRestoreStaging, "partial.txt"),
                "simulated interrupted unlock plaintext");

            using (var recoverySession =
                await protection.UnlockWithRecoveryKeyAsync(
                    folder.Id,
                    recoveryKey)
                ?? throw new InvalidOperationException(
                    "Valid Master Recovery Key failed to unlock FEK."))
            {
                await vault.RestoreVaultAsync(
                    built.VaultPath,
                    source,
                    recoverySession);
            }

            Assert(
                !Directory.Exists(orphanRestoreStaging),
                "unlock must clean stale restore staging");

            await protection.MarkPlaintextPresentAsync(
                folder.Id);

            await AssertTreesEqualAsync(
                passwordRestore,
                source);

            Pass("Vault-only unlock restores byte-identical plaintext");

            var pendingPath = Path.Combine(
                root,
                $".geniafolder-plaintext-{folder.Id:N}.pending-delete-smoke");

            await protection.MarkLockPendingAsync(
                folder.Id,
                pendingPath);

            Directory.Move(source, pendingPath);

            using (var passwordSession =
                await RequirePasswordSessionAsync(protection, folder.Id))
            {
                await vaultOnly.ActivateOrResumeAsync(
                    folder,
                    vaultInfo,
                    passwordSession);
            }

            Assert(
                !Directory.Exists(source) &&
                !Directory.Exists(pendingPath),
                "LockPending resume must remove quarantine and source");

            Assert(
                protection.GetStorageInfo(folder.Id)?.State ==
                VaultStorageState.VaultOnly,
                "LockPending resume must finish in VaultOnly");

            Pass("LockPending resumes safely after simulated hard kill");

            using (var passwordSession =
                await RequirePasswordSessionAsync(protection, folder.Id))
            {
                await vault.RestoreVaultAsync(
                    built.VaultPath,
                    source,
                    passwordSession);
            }

            await protection.MarkPlaintextPresentAsync(
                folder.Id);

            await AssertTreesEqualAsync(
                passwordRestore,
                source);

            Pass("final unlock after resumed LockPending is byte-identical");

            Console.WriteLine();
            Console.WriteLine("[PASS] All security smoke tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("[FAIL] Security smoke tests failed.");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            DeleteTreeBestEffort(root);
        }
    }

    private static async Task<UnlockedProtectionSession>
        RequirePasswordSessionAsync(
            ProtectionService protection,
            Guid folderId)
    {
        return await protection.UnlockWithPasswordAsync(
            folderId,
            Password)
            ?? throw new InvalidOperationException(
                "Valid password failed to unlock FEK.");
    }

    private static string MutateRecoveryKey(string recoveryKey)
    {
        var chars = recoveryKey.ToCharArray();

        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] == '-')
                continue;

            chars[i] = chars[i] == '0' ? '1' : '0';
            return new string(chars);
        }

        throw new InvalidOperationException(
            "Recovery Key could not be mutated.");
    }

    private static async Task AssertTreesEqualAsync(
        string expectedRoot,
        string actualRoot)
    {
        Assert(
            Directory.Exists(actualRoot),
            "restored directory was not published");

        var expectedFiles = Directory
            .EnumerateFiles(
                expectedRoot,
                "*",
                SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(expectedRoot, p),
                p => p,
                StringComparer.OrdinalIgnoreCase);

        var actualFiles = Directory
            .EnumerateFiles(
                actualRoot,
                "*",
                SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(actualRoot, p),
                p => p,
                StringComparer.OrdinalIgnoreCase);

        Assert(
            expectedFiles.Count == actualFiles.Count,
            "restored file count differs from source");

        foreach (var pair in expectedFiles)
        {
            Assert(
                actualFiles.TryGetValue(
                    pair.Key,
                    out var actualPath),
                "restored tree is missing: " + pair.Key);

            var expectedHash = await HashFileAsync(pair.Value);
            var actualHash = await HashFileAsync(actualPath!);

            Assert(
                CryptographicOperations.FixedTimeEquals(
                    expectedHash,
                    actualHash),
                "restored bytes differ: " + pair.Key);

            CryptographicOperations.ZeroMemory(expectedHash);
            CryptographicOperations.ZeroMemory(actualHash);
        }
    }

    private static async Task<byte[]> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream);
    }

    private static async Task ExpectFailureAsync(
        string scenario,
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (CryptographicException)
        {
            Pass(scenario + " rejected");
            return;
        }
        catch (InvalidDataException)
        {
            Pass(scenario + " rejected");
            return;
        }
        catch (EndOfStreamException)
        {
            Pass(scenario + " rejected");
            return;
        }

        throw new InvalidOperationException(
            scenario + " was accepted unexpectedly.");
    }

    private static async Task ExpectInvalidOperationAsync(
        string scenario,
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException)
        {
            Pass(scenario + " rejected");
            return;
        }

        throw new InvalidOperationException(
            scenario + " was accepted unexpectedly.");
    }

    private static async Task ExpectCancellationAsync(
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Cancellation request did not cancel restore.");
    }

    private static void FlipOneByte(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        if (stream.Length == 0)
            throw new InvalidOperationException(
                "Cannot corrupt an empty file.");

        var position = stream.Length / 2;
        stream.Position = position;

        var value = stream.ReadByte();
        if (value < 0)
            throw new EndOfStreamException();

        stream.Position = position;
        stream.WriteByte((byte)(value ^ 0x5A));
        stream.Flush(flushToDisk: true);
    }

    private static void CopyDirectory(
        string source,
        string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(
            source,
            "*",
            SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(
                Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(
            source,
            "*",
            SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(
                Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void DeleteTreeBestEffort(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return;

            foreach (var file in Directory.EnumerateFiles(
                root,
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

            var directories = Directory
                .EnumerateDirectories(
                    root,
                    "*",
                    SearchOption.AllDirectories)
                .OrderByDescending(p => p.Length)
                .ToList();

            foreach (var directory in directories)
            {
                try
                {
                    File.SetAttributes(
                        directory,
                        FileAttributes.Normal);
                }
                catch
                {
                }
            }

            try
            {
                File.SetAttributes(root, FileAttributes.Normal);
            }
            catch
            {
            }

            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // CI temp storage is ephemeral; cleanup failure must not mask
            // the actual security-test result.
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Pass(string message) =>
        Console.WriteLine("[PASS] " + message);

    private sealed class CancelAfterFirstFileProgress
        : IProgress<VaultBuildProgress>
    {
        private readonly CancellationTokenSource _cts;

        public CancelAfterFirstFileProgress(
            CancellationTokenSource cts)
        {
            _cts = cts;
        }

        public void Report(VaultBuildProgress value)
        {
            if (value.Stage == "Восстановление" &&
                value.ProcessedFiles >= 1)
            {
                _cts.Cancel();
            }
        }
    }
}
