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
    private readonly ProtectionService _protection = new();
    private readonly VaultEncryptionService _vaultEncryption = new();
    private readonly ObservableCollection<FolderRow> _rows = [];
    private List<ManagedFolder> _folders = [];

    public MainWindow()
    {
        InitializeComponent();
        FolderList.ItemsSource = _rows;

        Loaded += async (_, _) => await ReloadAsync();

        // Explorer, removable drives and network locations may change while
        // GeniaFolder is in the background. Refresh availability whenever the
        // user returns to the application.
        Activated += (_, _) => RebuildRows();
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
        RebuildRows();
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

            _rows.Add(new FolderRow(
                folder.Id,
                folder.Name,
                folder.Path,
                new SolidColorBrush(FolderAppearanceService.GetColor(folder.Color)),
                status,
                exists,
                statusBrush,
                cardOpacity,
                protectionAction,
                protectionEnabled));
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

                details.ShowDialog();
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

        var setup = new ProtectionSetupWindow(folder.Name) { Owner = this };
        if (setup.ShowDialog() != true)
            return;

        PreparedProtectionProfile prepared;

        try
        {
            prepared = _protection.PrepareStandardProfile(
                folder,
                setup.Password);
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

        var recovery = new RecoveryKeyWindow(
            folder.Name,
            prepared.RecoveryKey,
            prepared.Fingerprint)
        {
            Owner = this
        };

        if (recovery.ShowDialog() != true)
        {
            MessageBox.Show(this,
                "Настройка защиты отменена. Профиль ключей не был сохранён.",
                "GeniaFolder",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            await _protection.SavePreparedProfileAsync(prepared.Profile);
            RebuildRows();

            MessageBox.Show(this,
                "Ключи Standard Protection сохранены.\n\n" +
                "Пароль и Master Recovery Key проверены, FEK хранится только в зашифрованном виде.\n\n" +
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

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetFolder(sender, out var folder))
            return;

        if (_protection.HasPreparedProfile(folder.Id))
        {
            MessageBox.Show(this,
                "Защищённую папку пока нельзя просто убрать из GeniaFolder: профиль ключей и encrypted vault могут стать недоступны из интерфейса.\n\nСначала разблокируйте/восстановите данные. Отдельное безопасное удаление vault и профиля добавим позже.",
                "GeniaFolder — защищённая папка",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var locationNote = Directory.Exists(folder.Path)
            ? "Сама папка и её файлы НЕ будут удалены."
            : "Папка сейчас недоступна. Будет удалена только запись из GeniaFolder.";

        var result = MessageBox.Show(this,
            $"Убрать «{folder.Name}» из GeniaFolder?\n\n{locationNote}",
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
            return true;

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
        bool ProtectionEnabled);
}
