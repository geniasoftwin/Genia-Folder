using System.Windows;
using Microsoft.Win32;

namespace GeniaFolder.Views;

public partial class CreateFolderWindow : Window
{
    public string FolderPath { get; private set; } = string.Empty;

    public CreateFolderWindow()
    {
        InitializeComponent();

        var initialLocation = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        LocationBox.Text = Directory.Exists(initialLocation) ? initialLocation : Environment.CurrentDirectory;

        LocationBox.TextChanged += (_, _) => UpdatePreview();
        NameBox.TextChanged += (_, _) => UpdatePreview();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            Keyboard.Focus(NameBox);
        };

        UpdatePreview();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку, внутри которой создать новую",
            Multiselect = false
        };

        if (Directory.Exists(LocationBox.Text))
            dialog.InitialDirectory = LocationBox.Text;

        if (dialog.ShowDialog(this) == true)
            LocationBox.Text = dialog.FolderName;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var parent = LocationBox.Text.Trim();
        var name = NameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            MessageBox.Show(this, "Укажите существующую папку расположения.", "GeniaFolder", MessageBoxButton.OK, MessageBoxImage.Warning);
            LocationBox.Focus();
            return;
        }

        if (!IsValidFolderName(name, out var error))
        {
            MessageBox.Show(this, error, "GeniaFolder", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            NameBox.SelectAll();
            return;
        }

        var path = Path.GetFullPath(Path.Combine(parent, name));
        if (Directory.Exists(path) || File.Exists(path))
        {
            MessageBox.Show(this, "Объект с таким именем уже существует.", "GeniaFolder", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            NameBox.SelectAll();
            return;
        }

        FolderPath = path;
        DialogResult = true;
    }

    private void UpdatePreview()
    {
        var parent = LocationBox.Text.Trim();
        var name = NameBox.Text.Trim();

        PreviewText.Text = string.IsNullOrWhiteSpace(name)
            ? "Будет создано: введите имя новой папки"
            : $"Будет создано: {Path.Combine(parent, name)}";
    }

    private static bool IsValidFolderName(string name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Введите имя папки.";
            return false;
        }

        if (name is "." or "..")
        {
            error = "Недопустимое имя папки.";
            return false;
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            error = "Имя папки не должно заканчиваться пробелом или точкой.";
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Имя папки содержит недопустимые символы.";
            return false;
        }

        string[] reserved =
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ];

        var baseName = Path.GetFileNameWithoutExtension(name);
        if (reserved.Contains(baseName, StringComparer.OrdinalIgnoreCase))
        {
            error = "Это имя зарезервировано Windows.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
