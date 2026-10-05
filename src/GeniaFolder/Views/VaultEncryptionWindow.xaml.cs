using System.ComponentModel;
using System.Windows;
using GeniaFolder.Models;
using GeniaFolder.Services;

namespace GeniaFolder.Views;

public partial class VaultEncryptionWindow : Window
{
    private readonly ManagedFolder _folder;
    private readonly ProtectionService _protection;
    private readonly VaultEncryptionService _vault;

    private CancellationTokenSource? _cancellation;
    private bool _running;
    private bool _cancelRequested;
    private bool _commitStarted;

    public VerifiedVaultResult? Result { get; private set; }

    public VaultEncryptionWindow(
        ManagedFolder folder,
        ProtectionService protection,
        VaultEncryptionService vault)
    {
        InitializeComponent();

        _folder = folder;
        _protection = protection;
        _vault = vault;

        InfoText.Text =
            $"Для «{folder.Name}» будет создан отдельный encrypted vault. " +
            "FEK разблокируется вашим паролем только в памяти процесса. " +
            "Каждый файл шифруется AES-256-GCM чанками и затем полностью проверяется.";

        Loaded += (_, _) => PasswordBox.Focus();
        Closing += VaultEncryptionWindow_Closing;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
            return;

        ErrorText.Text = string.Empty;

        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            ErrorText.Text = "Введите пароль папки.";
            PasswordBox.Focus();
            return;
        }

        SetRunning(true);
        _cancelRequested = false;
        _commitStarted = false;
        _cancellation = new CancellationTokenSource();

        var password = PasswordBox.Password;
        PasswordBox.Clear();

        UnlockedProtectionSession? session = null;

        try
        {
            StatusText.Text = "Проверка пароля…";

            session = await _protection.UnlockWithPasswordAsync(
                _folder.Id,
                password);

            password = string.Empty;
            _cancellation.Token.ThrowIfCancellationRequested();

            if (session is null)
            {
                ErrorText.Text = "Неверный пароль.";
                StatusText.Text = "Vault не создавался.";
                SetRunning(false);
                PasswordBox.Focus();
                return;
            }

            var progress = new Progress<VaultBuildProgress>(
                UpdateProgress);

            Result = await Task.Run(
                () => _vault.BuildOrAdoptVerifiedVaultAsync(
                    _folder,
                    session,
                    progress,
                    _cancellation.Token));

            _cancellation.Token.ThrowIfCancellationRequested();

            // From this point onward we only commit already-verified metadata.
            // Do not accept a late cancel that could leave the UI claiming
            // cancellation after the profile has actually been committed.
            _commitStarted = true;
            CancelButton.IsEnabled = false;
            StatusText.Text = "Vault полностью проверен. Сохраняем состояние профиля…";

            await _protection.MarkVaultVerifiedAsync(
                _folder.Id,
                Result);

            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            Result = null;
            ErrorText.Text = string.Empty;
            StatusText.Text =
                "Операция отменена. Временный vault очищен; исходные файлы не изменялись.";
            ProgressBar.Value = 0;
            SetRunning(false);
        }
        catch (Exception ex)
        {
            ErrorText.Text =
                "Не удалось создать проверенный vault: " + ex.Message;

            StatusText.Text =
                "Исходная папка не изменялась. Можно исправить проблему и повторить.";
            SetRunning(false);
        }
        finally
        {
            session?.Dispose();
            _cancellation?.Dispose();
            _cancellation = null;
            password = string.Empty;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            if (_commitStarted || _cancelRequested)
                return;

            _cancelRequested = true;
            CancelButton.IsEnabled = false;
            StatusText.Text =
                "Безопасно останавливаем и очищаем временный vault…";
            _cancellation?.Cancel();
            return;
        }

        DialogResult = false;
    }

    private void VaultEncryptionWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (!_running)
            return;

        e.Cancel = true;

        if (_commitStarted || _cancelRequested)
            return;

        _cancelRequested = true;
        CancelButton.IsEnabled = false;
        StatusText.Text =
            "Безопасно останавливаем и очищаем временный vault…";
        _cancellation?.Cancel();
    }

    private void SetRunning(bool running)
    {
        _running = running;
        PasswordBox.IsEnabled = !running;
        StartButton.IsEnabled = !running;
        CancelButton.IsEnabled = true;
        CancelButton.Content = running ? "Остановить" : "Отмена";

        if (!running)
        {
            _cancelRequested = false;
            _commitStarted = false;
        }
    }

    private void UpdateProgress(VaultBuildProgress progress)
    {
        if (_cancelRequested)
            return;

        StatusText.Text =
            $"{progress.Stage}: {progress.ProcessedFiles}/{progress.TotalFiles} файлов" +
            (progress.TotalBytes > 0
                ? $" · {FormatBytes(progress.ProcessedBytes)} / {FormatBytes(progress.TotalBytes)}"
                : string.Empty);

        if (progress.TotalBytes > 0)
        {
            ProgressBar.Value = Math.Clamp(
                progress.ProcessedBytes * 100.0 / progress.TotalBytes,
                0,
                100);
        }
        else if (progress.TotalFiles > 0)
        {
            ProgressBar.Value = Math.Clamp(
                progress.ProcessedFiles * 100.0 / progress.TotalFiles,
                0,
                100);
        }
        else
        {
            ProgressBar.Value = 0;
        }
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
