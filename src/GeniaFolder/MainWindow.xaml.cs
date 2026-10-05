using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GeniaFolder.Models;
using GeniaFolder.Services;
using GeniaFolder.Views;
using Microsoft.Win32;

namespace GeniaFolder;

public partial class MainWindow : Window
{
    private readonly FolderRegistryService _registry = new();
    private readonly FolderAppearanceService _appearance = new();
    private readonly ObservableCollection<FolderRow> _rows = [];
    private List<ManagedFolder> _folders = [];

    public MainWindow()
    {
        InitializeComponent();
        FolderList.ItemsSource = _rows;
        Loaded += async (_, _) => await ReloadAsync();
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
            _rows.Add(new FolderRow(
                folder.Id,
                folder.Name,
                folder.Path,
                new SolidColorBrush(FolderAppearanceService.GetColor(folder.Color)),
                folder.Exists ? folder.ProtectionLabel : "Папка не найдена"));
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
        if (!TryGetFolder(sender, out var folder)) return;
        try { ShellService.OpenFolder(folder.Path); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "GeniaFolder", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Color_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetFolder(sender, out var folder)) return;

        var picker = new ColorPickerWindow(folder.Color) { Owner = this };
        if (picker.ShowDialog() != true) return;

        try
        {
            await _appearance.ApplyColorAsync(folder.Path, picker.SelectedColor);
            folder.Color = picker.SelectedColor;
            await _registry.SaveAsync(_folders);
            RebuildRows();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось изменить цвет:\n{ex.Message}", "GeniaFolder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Protection_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetFolder(sender, out _)) return;
        MessageBox.Show(this,
            "Защиту подключим следующим этапом: пароль + шифрование + бумажный Master Recovery Key.\n\nGeniaFolder не будет выдавать обычную папку с UI-паролем за защищённую.",
            "GeniaFolder — защита",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetFolder(sender, out var folder)) return;

        var result = MessageBox.Show(this,
            $"Убрать «{folder.Name}» из GeniaFolder?\n\nСама папка и её файлы НЕ будут удалены.",
            "GeniaFolder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        _folders.Remove(folder);
        await _registry.SaveAsync(_folders);
        RebuildRows();
    }

    private bool TryGetFolder(object sender, out ManagedFolder folder)
    {
        folder = null!;
        if (sender is not Button { Tag: Guid id }) return false;
        var found = _folders.FirstOrDefault(f => f.Id == id);
        if (found is null) return false;
        folder = found;
        return true;
    }

    private sealed record FolderRow(Guid Id, string Name, string Path, Brush ColorBrush, string Status);
}
