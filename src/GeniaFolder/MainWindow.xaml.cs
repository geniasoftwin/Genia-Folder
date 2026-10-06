using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using GeniaFolder.Models;
using GeniaFolder.Services;
using GeniaFolder.Views;
using Microsoft.Win32;

namespace GeniaFolder;

public partial class MainWindow : Window
{
    private readonly FolderRegistryService _registry = new();
    private readonly FolderAppearanceService _appearance = new();
    private readonly FolderIdentityService _folderIdentity = new();
    private readonly FolderLocationResolverService _folderLocationResolver;
    private readonly ProtectionService _protection = new();
    private readonly VaultEncryptionService _vaultEncryption = new();
    private readonly ObservableCollection<FolderRow> _rows = [];
    private List<ManagedFolder> _folders = [];
    private bool _refreshingFolderLocations;

    public MainWindow()
    {
        _folderLocationResolver = new FolderLocationResolverService(
            _folderIdentity);

        InitializeComponent();
        FolderList.ItemsSource = _rows;

        Loaded += async (_, _) => await ReloadAsync();

        // Explorer, removable drives and network locations may change while
        // GeniaFolder is in the background. Refresh availability whenever the
        // user returns to the application.
        Activated += async (_, _) => await RefreshFolderLocationsAsync();
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (System.Windows.Application.Current is not App app || app.ExitRequested)
            return;

        e.Cancel = true;
        Hide();
        app.NotifyHiddenToTray();
    }

    private async Task ReloadAsync()
    {
        _folders = await _registry.LoadAsync();
        await RefreshFolderLocationsAsync();
    }

    private async Task RefreshFolderLocationsAsync()
    {
        if (_refreshingFolderLocations)
            return;

        _refreshingFolderLocations = true;

        try
        {
            var changed = ReconcileFolderLocations();

            if (changed)
                await _registry.SaveAsync(_folders);

            RebuildRows();
        }
        finally
        {
            _refreshingFolderLocations = false;
        }
    }

    private bool ReconcileFolderLocations()
    {
        var changed = false;

        foreach (var folder in _folders)
        {
            var storageState = _protection.HasPreparedProfile(folder.Id)
                ? _protection.GetStorageInfo(folder.Id)?.State
                    ?? VaultStorageState.PlaintextPresent
                : VaultStorageState.PlaintextPresent;

            if (Directory.Exists(folder.Path))
            {
                if (folder.VolumeSerialNumber == 0 ||
                    string.IsNullOrWhiteSpace(folder.FileId))
                {
                    changed |= CaptureFolderIdentity(folder);
                    continue;
                }

                // A path can reappear after a cross-volume move/copy with a
                // different Windows file ID. Never silently trust the path.
                if (_folderIdentity.Matches(
                    folder.Path,
                    folder.VolumeSerialNumber,
                    folder.FileId))
                {
                    continue;
                }

                // Keep the stored identity unchanged. The UI will require an
                // explicit identity/content verification before rebinding.
                continue;
            }

            // In Vault-only/LockPending the missing plaintext path is expected
            // and must never be "repaired" by adopting another directory.
            if (storageState != VaultStorageState.PlaintextPresent ||
                folder.VolumeSerialNumber == 0 ||
                string.IsNullOrWhiteSpace(folder.FileId))
            {
                continue;
            }

            var resolvedPath = _folderLocationResolver.TryResolve(
                folder,
                _folders);

            if (resolvedPath is null)
                continue;

            UpdateManagedFolderPath(
                folder,
                resolvedPath);

            changed = true;
        }

        return changed;
    }

    private static void UpdateManagedFolderPath(
        ManagedFolder folder,
        string path)
    {
        var fullPath = Path.GetFullPath(path);

        folder.Path = fullPath;
        folder.Name =
            Path.GetFileName(
                fullPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar))
            is { Length: > 0 } name
                ? name
                : fullPath;
    }

    private bool CaptureFolderIdentity(ManagedFolder folder)
    {
        var identity = _folderIdentity.TryGetIdentity(folder.Path);
        if (identity is null)
            return false;

        var changed =
            folder.VolumeSerialNumber != identity.VolumeSerialNumber ||
            !string.Equals(
                folder.FileId,
                identity.FileId,
                StringComparison.OrdinalIgnoreCase);

        folder.VolumeSerialNumber = identity.VolumeSerialNumber;
        folder.FileId = identity.FileId;

        return changed;
    }

    private void RebuildRows()
    {
        _rows.Clear();

        foreach (var folder in _folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var exists = Directory.Exists(folder.Path);
            var protectionPrepared = _protection.HasPreparedProfile(folder.Id);
            var vaultInfo = protectionPrepared
                ? _protection.GetVaultInfo(folder.Id)
                : null;
            var storageInfo = protectionPrepared
                ? _protection.GetStorageInfo(folder.Id)
                : null;

            var vaultVerified =
                vaultInfo?.State == VaultCopyState.VerifiedCopy;

            var vaultAvailable =
                vaultVerified &&
                !string.IsNullOrWhiteSpace(vaultInfo!.Path) &&
                Directory.Exists(vaultInfo.Path);

            var storageState =
                storageInfo?.State ?? VaultStorageState.PlaintextPresent;

            var identityKnown =
                folder.VolumeSerialNumber != 0 &&
                !string.IsNullOrWhiteSpace(folder.FileId);

            var identityMatches =
                !exists ||
                !identityKnown ||
                _folderIdentity.Matches(
                    folder.Path,
                    folder.VolumeSerialNumber,
                    folder.FileId);

            var identityMismatch =
                exists &&
                identityKnown &&
                !identityMatches;

            string status;
            MediaBrush statusBrush;
            string protectionAction;
            bool protectionEnabled;
            double cardOpacity;

            if (storageState == VaultStorageState.VaultOnly)
            {
                status = !vaultAvailable
                    ? "КРИТИЧНО: Vault-only активен, но encrypted vault недоступен"
                    : exists
                        ? "ВНИМАНИЕ: Vault-only активен, но plaintext-путь снова существует"
                        : "Заблокировано · Vault-only · plaintext удалён";

                statusBrush = !vaultAvailable || exists
                    ? Brushes.Firebrick
                    : Brushes.DarkGreen;

                protectionAction = "Vault";
                protectionEnabled = vaultAvailable;
                cardOpacity = 1.0;
            }
            else if (storageState == VaultStorageState.LockPending)
            {
                status = vaultAvailable
                    ? "Блокировка не завершена · LockPending · откройте Vault для продолжения"
                    : "КРИТИЧНО: LockPending, но encrypted vault недоступен";

                statusBrush = vaultAvailable
                    ? Brushes.DarkGoldenrod
                    : Brushes.Firebrick;

                protectionAction = "Vault";
                protectionEnabled = vaultAvailable;
                cardOpacity = 1.0;
            }
            else
            {
                if (identityMismatch)
                {
                    status = vaultVerified && vaultAvailable
                        ? "Путь существует, но Windows identity изменился · требуется проверка по encrypted vault"
                        : "Путь существует, но Windows identity изменился · требуется перепривязка";

                    statusBrush = vaultVerified && vaultAvailable
                        ? Brushes.DarkGoldenrod
                        : Brushes.Firebrick;

                    protectionAction = vaultVerified
                        ? "Vault"
                        : protectionPrepared
                            ? "Защита"
                            : "Защита";

                    protectionEnabled = vaultVerified && vaultAvailable;
                    cardOpacity = 1.0;
                }
                else
                {
                    status = !exists && vaultVerified && vaultAvailable
                        ? "Исходная папка отсутствует · encrypted vault доступен для восстановления"
                        : !exists
                            ? "Папка не найдена или диск недоступен"
                            : vaultVerified && vaultAvailable
                                ? "Зашифрованная копия проверена · оригиналы пока на месте"
                                : vaultVerified
                                    ? "Vault зарегистрирован, но сейчас не найден · оригиналы пока на месте"
                                    : protectionPrepared
                                        ? "Ключи Standard готовы · файлы пока НЕ зашифрованы"
                                        : folder.ProtectionLabel;

                    statusBrush = !exists && vaultVerified && vaultAvailable
                        ? Brushes.DarkGoldenrod
                        : !exists
                            ? Brushes.Firebrick
                            : vaultVerified && vaultAvailable
                                ? Brushes.DarkGreen
                                : vaultVerified
                                    ? Brushes.Firebrick
                                    : protectionPrepared
                                        ? Brushes.DarkGoldenrod
                                        : Brushes.Gray;

                    protectionAction = vaultVerified
                        ? "Vault"
                        : protectionPrepared
                            ? "Шифровать"
                            : "Защита";

                    protectionEnabled = vaultVerified
                        ? vaultAvailable
                        : exists;

                    cardOpacity = vaultVerified && vaultAvailable
                        ? 1.0
                        : exists ? 1.0 : 0.72;
                }
            }

            var canLocateMovedFolder =
                storageState == VaultStorageState.PlaintextPresent &&
                (!exists || identityMismatch) &&
                (identityKnown || (vaultVerified && vaultAvailable));

            _rows.Add(new FolderRow(
                folder.Id,
                folder.Name,
                folder.Path,
                new SolidColorBrush(FolderAppearanceService.GetColor(folder.Color)),
                status,
                exists && !identityMismatch,
                statusBrush,
                cardOpacity,
                protectionAction,
                protectionEnabled,
                canLocateMovedFolder
                    ? Visibility.Visible
                    : Visibility.Collapsed));
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку для GeniaFolder" };
        if (dialog.ShowDialog(this) != true)
            return;

        await AddManagedFolderAsync(dialog.FolderName);
    }

    private async void CreateFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateFolderWindow { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            Directory.CreateDirectory(dialog.FolderPath);
            await AddManagedFolderAsync(dialog.FolderPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось создать папку:\n{ex.Message}", "GeniaFolder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task AddManagedFolderAsync(string path)
    {
        path = Path.GetFullPath(path);

        if (_folders.Any(f => string.Equals(Path.GetFullPath(f.Path), path, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Эта папка уже добавлена.", "GeniaFolder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folder = new ManagedFolder
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path,
            Path = path,
            Color = FolderColor.Blue
        };

        CaptureFolderIdentity(folder);

        try
        {
            await _appearance.ApplyColorAsync(path, folder.Color);
            _folders.Add(folder);
            await _registry.SaveAsync(_folders);
            RebuildRows();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось добавить папку:\n{ex.Message}", "GeniaFolder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetAvailableFolder(sender, out var folder))
            return;

        try
        {
            ShellService.OpenFolder(folder.Path);
        }
        catch (Exception ex)
        {
            RebuildRows();
            MessageBox.Show(this, $"Не удалось открыть папку:\n{ex.Message}", "GeniaFolder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Color_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetAvailableFolder(sender, out var folder))
            return;

        var picker = new ColorPickerWindow(folder.Color) { Owner = this };
        if (picker.ShowDialog() != true)
            return;

        try
        {
            await _appearance.ApplyColorAsync(folder.Path, picker.SelectedColor);
            folder.Color = picker.SelectedColor;
            await _registry.SaveAsync(_folders);
            RebuildRows();
        }
        catch (DirectoryNotFoundException)
        {
            RebuildRows();
            ShowFolderUnavailable(folder);
        }
        catch (Exception ex)
        {
            RebuildRows();
            MessageBox.Show(this, $"Не удалось изменить цвет:\n{ex.Message}", "GeniaFolder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Protection_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetFolder(sender, out var folder))
            return;

        if (_protection.HasPreparedProfile(folder.Id))
        {
            var vaultInfo = _protection.GetVaultInfo(folder.Id);

            if (vaultInfo?.State == VaultCopyState.VerifiedCopy)
            {
                var details = new VaultDetailsWindow(
                    folder,
                    vaultInfo,
                    _protection,
                    _vaultEncryption)
                {
                    Owner = this
                };

                var detailsResult = details.ShowDialog();

                if (detailsResult == true &&
                    Directory.Exists(folder.Path) &&
                    (_protection.GetStorageInfo(folder.Id)?.State
                        ?? VaultStorageState.PlaintextPresent) ==
                       VaultStorageState.PlaintextPresent &&
                    CaptureFolderIdentity(folder))
                {
                    await _registry.SaveAsync(_folders);
                }

                RebuildRows();
                return;
            }

            if (!Directory.Exists(folder.Path))
            {
                RebuildRows();
                ShowFolderUnavailable(folder);
                return;
            }

            var vaultWindow = new VaultEncryptionWindow(
                folder,
                _protection,
                _vaultEncryption)
            {
                Owner = this
            };

            if (vaultWindow.ShowDialog() == true &&
                vaultWindow.Result is { } result)
            {
                RebuildRows();

                MessageBox.Show(this,
                    "Encrypted vault создан и полностью проверен.\n\n" +
                    $"Файлов: {result.FileCount}\n" +
                    $"Папок: {result.DirectoryCount}\n" +
                    $"Исходный объём: {FormatBytes(result.PlaintextBytes)}\n" +
                    $"Vault: {result.VaultPath}\n\n" +
                    "Каждый файл был расшифрован в памяти и проверен по SHA-256. " +
                    "Исходные файлы НЕ удалялись и НЕ изменялись.",
                    "GeniaFolder — vault проверен",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return;
        }

        if (!Directory.Exists(folder.Path))
        {
            RebuildRows();
            ShowFolderUnavailable(folder);
            return;
        }

        var hasMasterRecoveryKey =
            _protection.HasMasterRecoveryKey;

        var setup = new ProtectionSetupWindow(
            folder.Name,
            hasMasterRecoveryKey,
            _protection.GetMasterRecoveryFingerprint())
        {
            Owner = this
        };

        if (setup.ShowDialog() != true)
            return;

        PreparedProtectionProfile prepared;

        try
        {
            prepared = _protection.PrepareStandardProfile(
                folder,
                setup.Password,
                setup.MasterRecoveryKey);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Не удалось создать ключи защиты:\n{ex.Message}",
                "GeniaFolder — защита",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        finally
        {
            setup.ClearSecrets();
        }

        if (prepared.IsNewMasterRecoveryKey)
        {
            var recovery = new RecoveryKeyWindow(
                folder.Name,
                prepared.RecoveryKey,
                prepared.MasterRecoveryFingerprint)
            {
                Owner = this
            };

            if (recovery.ShowDialog() != true)
            {
                MessageBox.Show(this,
                    "Настройка защиты отменена. Общий Master Recovery Key и профиль папки не были сохранены.",
                    "GeniaFolder",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
        }

        try
        {
            await _protection.SavePreparedProfileAsync(prepared);
            RebuildRows();

            MessageBox.Show(this,
                "Ключи Standard Protection сохранены.\n\n" +
                "У папки свой FEK и свой пароль. Общий Master Recovery Key этой установки " +
                "может восстановить доступ к этой и другим привязанным папкам.\n\n" +
                "Теперь кнопка «Шифровать» создаст отдельный encrypted vault, " +
                "полностью проверит его и при этом не затронет исходные файлы.",
                "GeniaFolder — следующий шаг",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Не удалось сохранить профиль защиты:\n{ex.Message}",
                "GeniaFolder — защита",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void FindFolder_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!TryGetFolder(sender, out var folder))
            return;

        var storageState = _protection.HasPreparedProfile(folder.Id)
            ? _protection.GetStorageInfo(folder.Id)?.State
                ?? VaultStorageState.PlaintextPresent
            : VaultStorageState.PlaintextPresent;

        if (storageState != VaultStorageState.PlaintextPresent)
        {
            MessageBox.Show(this,
                "Поиск перемещённой plaintext-папки недоступен в Vault-only/LockPending.",
                "GeniaFolder",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = $"Найдите перемещённую папку «{folder.Name}»",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var selectedPath = Path.GetFullPath(dialog.FolderName);

        if (_folders.Any(f =>
            f.Id != folder.Id &&
            Directory.Exists(f.Path) &&
            string.Equals(
                Path.GetFullPath(f.Path),
                selectedPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this,
                "Эта папка уже зарегистрирована в GeniaFolder другой записью.",
                "GeniaFolder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var sameWindowsIdentity =
            folder.VolumeSerialNumber != 0 &&
            !string.IsNullOrWhiteSpace(folder.FileId) &&
            _folderIdentity.Matches(
                selectedPath,
                folder.VolumeSerialNumber,
                folder.FileId);

        if (!sameWindowsIdentity)
        {
            var vaultInfo = _protection.HasPreparedProfile(folder.Id)
                ? _protection.GetVaultInfo(folder.Id)
                : null;

            var vaultAvailable =
                vaultInfo?.State == VaultCopyState.VerifiedCopy &&
                !string.IsNullOrWhiteSpace(vaultInfo.Path) &&
                Directory.Exists(vaultInfo.Path);

            if (!vaultAvailable)
            {
                MessageBox.Show(this,
                    "Windows file ID выбранной папки не совпадает, а проверенного encrypted vault нет. " +
                    "GeniaFolder не может безопасно доказать, что это та же папка.",
                    "GeniaFolder — требуется проверка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var selectedName =
                Path.GetFileName(
                    selectedPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar));

            var candidate = new ManagedFolder
            {
                Id = folder.Id,
                Name = string.IsNullOrWhiteSpace(selectedName)
                    ? folder.Name
                    : selectedName,
                Path = selectedPath,
                Color = folder.Color,
                Protection = folder.Protection,
                AddedAt = folder.AddedAt
            };

            var verify = new VaultRestoreWindow(
                candidate,
                vaultInfo!.Path,
                selectedPath,
                _protection,
                _vaultEncryption,
                verifyExistingPlaintextOnly: true,
                relocationVerification: true)
            {
                Owner = this
            };

            if (verify.ShowDialog() != true)
                return;
        }

        UpdateManagedFolderPath(
            folder,
            selectedPath);

        CaptureFolderIdentity(folder);

        await _registry.SaveAsync(_folders);
        RebuildRows();

        MessageBox.Show(this,
            sameWindowsIdentity
                ? $"Папка найдена и путь обновлён:\n{folder.Path}"
                : $"Содержимое полностью совпало с encrypted vault. Новый путь и Windows identity сохранены:\n{folder.Path}",
            "GeniaFolder",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetFolder(sender, out var folder))
            return;

        var protectedFolder = _protection.HasPreparedProfile(folder.Id);

        if (protectedFolder)
        {
            var storageState = _protection.GetStorageInfo(folder.Id)?.State
                ?? VaultStorageState.PlaintextPresent;

            if (storageState != VaultStorageState.PlaintextPresent)
            {
                MessageBox.Show(this,
                    "Папку в состоянии Vault-only или LockPending нельзя убирать из GeniaFolder. " +
                    "Сначала завершите разблокировку/восстановление, чтобы не потерять доступ к управлению encrypted vault.",
                    "GeniaFolder — защищённая папка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var passwordDialog = new ProtectedActionPasswordWindow(
                folder.Id,
                folder.Name,
                "После проверки будет удалена только карточка из списка GeniaFolder. " +
                "Сама папка, её файлы, protection-профиль и encrypted vault не удаляются.",
                _protection)
            {
                Owner = this
            };

            if (passwordDialog.ShowDialog() != true)
                return;
        }

        var locationNote = Directory.Exists(folder.Path)
            ? "Сама папка и её файлы НЕ будут удалены."
            : "Папка сейчас недоступна. Будет удалена только запись из GeniaFolder.";

        var protectedNote = protectedFolder
            ? "\n\nProtection-профиль и encrypted vault останутся на диске."
            : string.Empty;

        var result = MessageBox.Show(this,
            $"Убрать «{folder.Name}» из GeniaFolder?\n\n{locationNote}{protectedNote}",
            "GeniaFolder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        _folders.Remove(folder);
        await _registry.SaveAsync(_folders);
        RebuildRows();
    }

    private bool TryGetAvailableFolder(object sender, out ManagedFolder folder)
    {
        if (!TryGetFolder(sender, out folder))
            return false;

        if (Directory.Exists(folder.Path))
        {
            var identityKnown =
                folder.VolumeSerialNumber != 0 &&
                !string.IsNullOrWhiteSpace(folder.FileId);

            if (!identityKnown ||
                _folderIdentity.Matches(
                    folder.Path,
                    folder.VolumeSerialNumber,
                    folder.FileId))
            {
                return true;
            }

            RebuildRows();

            MessageBox.Show(this,
                "По сохранённому пути сейчас находится папка с другим Windows identity. " +
                "Используйте «Найти…» и подтвердите её по encrypted vault перед открытием.",
                "GeniaFolder — путь требует проверки",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return false;
        }

        RebuildRows();
        ShowFolderUnavailable(folder);
        return false;
    }

    private void ShowFolderUnavailable(ManagedFolder folder)
    {
        MessageBox.Show(this,
            $"Папка сейчас недоступна:\n{folder.Path}\n\nЕсли это внешний или сетевой диск, подключите его и вернитесь в GeniaFolder — статус обновится автоматически.",
            "GeniaFolder",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private bool TryGetFolder(object sender, out ManagedFolder folder)
    {
        folder = null!;

        if (sender is not Button { Tag: Guid id })
            return false;

        var found = _folders.FirstOrDefault(f => f.Id == id);
        if (found is null)
            return false;

        folder = found;
        return true;
    }

    private sealed record FolderRow(
        Guid Id,
        string Name,
        string Path,
        MediaBrush ColorBrush,
        string Status,
        bool Exists,
        MediaBrush StatusBrush,
        double CardOpacity,
        string ProtectionActionLabel,
        bool ProtectionEnabled,
        Visibility FindVisibility);
}
