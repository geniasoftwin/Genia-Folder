using System.Windows;
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
