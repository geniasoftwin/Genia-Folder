using System.Windows;
using System.Windows.Media;
using GeniaFolder.Models;
using GeniaFolder.Services;
using Microsoft.Win32;

namespace GeniaFolder.Views;

public partial class VaultDetailsWindow : Window
{
    private readonly ManagedFolder _folder;
    private readonly VaultCopyInfo _vaultInfo;
    private readonly ProtectionService _protection;
    private readonly VaultEncryptionService _vault;

    public VaultDetailsWindow(
        ManagedFolder folder,
        VaultCopyInfo vaultInfo,
        ProtectionService protection,
        VaultEncryptionService vault)
    {
        InitializeComponent();

        _folder = folder;
        _vaultInfo = vaultInfo;
        _protection = protection;
        _vault = vault;

        SummaryText.Text =
            $"Файлов: {vaultInfo.FileCount}\n" +
            $"Папок: {vaultInfo.DirectoryCount}\n" +
            $"Исходный объём: {FormatBytes(vaultInfo.PlaintextBytes)}\n" +
            $"Проверен: {vaultInfo.VerifiedAt?.ToLocalTime():g}";

        VaultPathText.Text = $"Vault: {vaultInfo.Path}";

        RefreshStorageState();
    }

    private void RefreshStorageState()
    {
        var storage = _protection.GetStorageInfo(_folder.Id)
            ?? new VaultStorageInfo();

        switch (storage.State)
        {
            case VaultStorageState.VaultOnly:
                StateBorder.Background = new SolidColorBrush(
                    Color.FromRgb(0xEE, 0xF8, 0xF0));
                StateBorder.BorderBrush = new SolidColorBrush(
                    Color.FromRgb(0xA7, 0xD7, 0xAF));
                StateText.Foreground = new SolidColorBrush(
                    Color.FromRgb(0x24, 0x5D, 0x2E));

                StateText.Text = Directory.Exists(_folder.Path)
                    ? "ВНИМАНИЕ: профиль находится в Vault-only, но исходный plaintext-путь снова существует. Автоматическая разблокировка остановлена, чтобы не перезаписать данные."
                    : "Vault-only активен. Обычная plaintext-папка удалена из файловой системы. Доступ возвращается только через проверенное восстановление из encrypted vault.";

                StorageActionButton.Content = Directory.Exists(_folder.Path)
                    ? "Завершить разблокировку"
                    : "Разблокировать папку";
                break;

            case VaultStorageState.LockPending:
                StateBorder.Background = new SolidColorBrush(
                    Color.FromRgb(0xFF, 0xF8, 0xE1));
                StateBorder.BorderBrush = new SolidColorBrush(
                    Color.FromRgb(0xF4, 0xD7, 0x7D));
                StateText.Foreground = new SolidColorBrush(
                    Color.FromRgb(0x6B, 0x57, 0x15));

                StateText.Text =
                    "Предыдущая блокировка была прервана. Профиль находится в LockPending. GeniaFolder может повторно проверить vault и безопасно продолжить транзакцию.";
                StorageActionButton.Content = "Продолжить блокировку";
                break;

            default:
                StateBorder.Background = new SolidColorBrush(
                    Color.FromRgb(0xEE, 0xF8, 0xF0));
                StateBorder.BorderBrush = new SolidColorBrush(
                    Color.FromRgb(0xA7, 0xD7, 0xAF));
                StateText.Foreground = new SolidColorBrush(
                    Color.FromRgb(0x24, 0x5D, 0x2E));

                StateText.Text =
                    "Vault прошёл полную криптографическую проверку, но plaintext пока существует. Можно восстановить отдельную копию или перевести папку в настоящий Vault-only.";
                StorageActionButton.Content = "Заблокировать папку";
                break;
        }
    }

    private async void StorageAction_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!Directory.Exists(_vaultInfo.Path))
        {
            MessageBox.Show(this,
                "Зарегистрированный encrypted vault сейчас не найден.",
                "GeniaFolder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var storage = _protection.GetStorageInfo(_folder.Id)
            ?? new VaultStorageInfo();

        if (storage.State == VaultStorageState.VaultOnly)
        {
            var existingPlaintext = Directory.Exists(_folder.Path);

            var restore = new VaultRestoreWindow(
                _folder,
                _vaultInfo.Path,
                _folder.Path,
                _protection,
                _vault,
                verifyExistingPlaintextOnly: existingPlaintext)
            {
                Owner = this
            };

            if (restore.ShowDialog() != true)
                return;

            var restored = restore.Result;

            if (!existingPlaintext && restored is null)
                return;

            try
            {
                await _protection.MarkPlaintextPresentAsync(
                    _folder.Id);

                var appearance = new FolderAppearanceService();
                await appearance.ApplyColorAsync(
                    _folder.Path,
                    _folder.Color);

                var message = existingPlaintext
                    ? "Существующая plaintext-папка полностью совпала с encrypted vault. Прерванная разблокировка безопасно завершена."
                    : "Папка разблокирована и полностью восстановлена из encrypted vault.\n\n" +
                      $"Файлов: {restored!.FileCount}\n" +
                      $"Объём: {FormatBytes(restored.PlaintextBytes)}\n" +
                      $"Путь: {restored.RestoredPath}\n\n" +
                      "Encrypted vault сохранён как проверенная резервная копия.";

                MessageBox.Show(this,
                    message,
                    "GeniaFolder — папка разблокирована",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Plaintext доступен, но не удалось завершить состояние профиля/цвет папки:\n" +
                    ex.Message +
                    "\n\nНе удаляйте эту папку. Повторно откройте Vault: GeniaFolder сможет byte-for-byte сверить её и завершить разблокировку.",
                    "GeniaFolder — требуется проверка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        var lockWindow = new VaultLockWindow(
            _folder,
            _vaultInfo,
            _protection,
            _vault)
        {
            Owner = this
        };

        if (lockWindow.ShowDialog() == true)
        {
            MessageBox.Show(this,
                "Vault-only активирован. Обычная plaintext-папка удалена из файловой системы. " +
                "Encrypted vault и Master Recovery остаются единственными путями к данным.",
                "GeniaFolder — папка заблокирована",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult = true;
        }
        else
        {
            RefreshStorageState();
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_vaultInfo.Path))
        {
            MessageBox.Show(this,
                "Зарегистрированный vault сейчас не найден.",
                "GeniaFolder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку, внутри которой создать восстановленную копию",
            Multiselect = false
        };

        var sourceParent = Directory.GetParent(_folder.Path)?.FullName;
        if (!string.IsNullOrWhiteSpace(sourceParent) &&
            Directory.Exists(sourceParent))
        {
            dialog.InitialDirectory = sourceParent;
        }

        if (dialog.ShowDialog(this) != true)
            return;

        var selectedParent = Path.GetFullPath(dialog.FolderName);

        if (IsSameOrChildPath(selectedParent, _folder.Path))
        {
            MessageBox.Show(this,
                "Папку восстановления нельзя создавать внутри исходной папки. " +
                "Выберите соседнюю или другую папку.",
                "GeniaFolder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (IsSameOrChildPath(selectedParent, _vaultInfo.Path))
        {
            MessageBox.Show(this,
                "Папку восстановления нельзя создавать внутри encrypted vault.",
                "GeniaFolder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var destination = BuildUniqueDestination(
            selectedParent,
            _folder.Name);

        var restore = new VaultRestoreWindow(
            _folder,
            _vaultInfo.Path,
            destination,
            _protection,
            _vault)
        {
            Owner = this
        };

        if (restore.ShowDialog() != true ||
            restore.Result is not { } result)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            "Восстановленная копия полностью проверена.\n\n" +
            $"Файлов: {result.FileCount}\n" +
            $"Папок: {result.DirectoryCount}\n" +
            $"Объём: {FormatBytes(result.PlaintextBytes)}\n" +
            $"Путь: {result.RestoredPath}\n\n" +
            "Открыть восстановленную папку?",
            "GeniaFolder — восстановление завершено",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (answer == MessageBoxResult.Yes)
            ShellService.OpenFolder(result.RestoredPath);
    }

    private static bool IsSameOrChildPath(
        string candidate,
        string root)
    {
        var fullCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(
                   fullCandidate,
                   fullRoot,
                   StringComparison.OrdinalIgnoreCase) ||
               fullCandidate.StartsWith(
                   fullRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildUniqueDestination(
        string parent,
        string folderName)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        var baseName = $"{folderName} — восстановлено {timestamp}";
        var candidate = Path.Combine(parent, baseName);

        if (!Directory.Exists(candidate) && !File.Exists(candidate))
            return candidate;

        for (var i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(parent, $"{baseName} ({i})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(
            parent,
            $"{baseName} {Guid.NewGuid():N}");
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
}
