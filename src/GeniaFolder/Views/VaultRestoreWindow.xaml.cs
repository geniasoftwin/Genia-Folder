using System.ComponentModel;
using System.Windows;
using GeniaFolder.Models;
using GeniaFolder.Services;

namespace GeniaFolder.Views;

public partial class VaultRestoreWindow : Window
{
    private readonly ManagedFolder _folder;
    private readonly string _vaultPath;
    private readonly string _destinationPath;
    private readonly ProtectionService _protection;
    private readonly VaultEncryptionService _vault;

    private CancellationTokenSource? _cancellation;
    private bool _running;
    private bool _cancelRequested;
    private bool _closeAfterCancel;

    public VaultRestoreResult? Result { get; private set; }

    public VaultRestoreWindow(
        ManagedFolder folder,
        string vaultPath,
        string destinationPath,
        ProtectionService protection,
        VaultEncryptionService vault)
    {
        InitializeComponent();

        _folder = folder;
        _vaultPath = vaultPath;
        _destinationPath = destinationPath;
        _protection = protection;
        _vault = vault;

        InfoText.Text =
            $"GeniaFolder расшифрует «{folder.Name}» в отдельную новую папку. " +
            "FEK можно разблокировать паролем папки или бумажным Master Recovery Key. " +
            "Каждый AES-GCM блок будет аутентифицирован, а SHA-256 каждого " +
            "восстановленного файла должен совпасть с hash из manifest.";

        DestinationText.Text = destinationPath;

        Loaded += (_, _) => FocusCredentialInput();
        Closing += VaultRestoreWindow_Closing;
    }

    private void UnlockMode_Checked(object sender, RoutedEventArgs e)
    {
        if (PasswordPanel is null || RecoveryPanel is null)
            return;

        var useRecovery = RecoveryRadio.IsChecked == true;

        PasswordPanel.Visibility = useRecovery
            ? Visibility.Collapsed
            : Visibility.Visible;

        RecoveryPanel.Visibility = useRecovery
            ? Visibility.Visible
            : Visibility.Collapsed;

        ErrorText.Text = string.Empty;

        if (IsLoaded)
            FocusCredentialInput();
    }

    private void FocusCredentialInput()
    {
        if (RecoveryRadio.IsChecked == true)
        {
            RecoveryKeyBox.Focus();
            RecoveryKeyBox.SelectAll();
        }
        else
        {
            PasswordBox.Focus();
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
            return;

        ErrorText.Text = string.Empty;

        var useRecovery = RecoveryRadio.IsChecked == true;
        var password = PasswordBox.Password;
        var recoveryKey = RecoveryKeyBox.Text.Trim();

        if (useRecovery)
        {
            if (string.IsNullOrWhiteSpace(recoveryKey))
            {
                ErrorText.Text = "Введите Master Recovery Key.";
                RecoveryKeyBox.Focus();
                return;
            }
        }
        else if (string.IsNullOrEmpty(password))
        {
            ErrorText.Text = "Введите пароль папки.";
            PasswordBox.Focus();
            return;
        }

        SetRunning(true);
        _cancelRequested = false;
        _closeAfterCancel = false;
        _cancellation = new CancellationTokenSource();

        PasswordBox.Clear();
        RecoveryKeyBox.Clear();

        UnlockedProtectionSession? session = null;

        try
        {
            StatusText.Text = useRecovery
                ? "Проверка Master Recovery Key…"
                : "Проверка пароля…";

            session = useRecovery
                ? await _protection.UnlockWithRecoveryKeyAsync(
                    _folder.Id,
                    recoveryKey)
                : await _protection.UnlockWithPasswordAsync(
                    _folder.Id,
                    password);

            password = string.Empty;
            recoveryKey = string.Empty;

            _cancellation.Token.ThrowIfCancellationRequested();

            if (session is null)
            {
                ErrorText.Text = useRecovery
                    ? "Master Recovery Key не подходит к этому профилю."
                    : "Неверный пароль.";

                StatusText.Text = "Восстановление не начиналось.";
                SetRunning(false);
                FocusCredentialInput();
                return;
            }

            var progress = new Progress<VaultBuildProgress>(UpdateProgress);

            Result = await Task.Run(
                () => _vault.RestoreVaultAsync(
                    _vaultPath,
                    _destinationPath,
                    session,
                    progress,
                    _cancellation.Token));

            _cancellation.Token.ThrowIfCancellationRequested();

            SetRunning(false);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            var closeAfterCancel = _closeAfterCancel;

            Result = null;
            ErrorText.Text = string.Empty;
            StatusText.Text =
                "Восстановление отменено. Временная папка очищена.";
            ProgressBar.Value = 0;
            SetRunning(false);

            if (closeAfterCancel)
                DialogResult = false;
        }
        catch (Exception ex)
        {
            var closeAfterFailure = _closeAfterCancel;

            Result = null;
            ErrorText.Text =
                "Не удалось восстановить vault: " + ex.Message;
            StatusText.Text =
                "Исходная папка и encrypted vault не изменялись.";
            SetRunning(false);

            if (closeAfterFailure)
                DialogResult = false;
        }
        finally
        {
            session?.Dispose();
            _cancellation?.Dispose();
            _cancellation = null;
            password = string.Empty;
            recoveryKey = string.Empty;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            RequestCancel(closeAfterCancel: false);
            return;
        }

        DialogResult = false;
    }

    private void VaultRestoreWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (!_running)
            return;

        e.Cancel = true;
        RequestCancel(closeAfterCancel: true);
    }

    private void RequestCancel(bool closeAfterCancel)
    {
        if (closeAfterCancel)
            _closeAfterCancel = true;

        if (_cancelRequested)
            return;

        _cancelRequested = true;
        CancelButton.IsEnabled = false;
        StatusText.Text =
            "Безопасно останавливаем восстановление и очищаем временную папку…";
        _cancellation?.Cancel();
    }

    private void SetRunning(bool running)
    {
        _running = running;
        PasswordRadio.IsEnabled = !running;
        RecoveryRadio.IsEnabled = !running;
        PasswordBox.IsEnabled = !running;
        RecoveryKeyBox.IsEnabled = !running;
        StartButton.IsEnabled = !running;
        CancelButton.IsEnabled = true;
        CancelButton.Content = running ? "Остановить" : "Отмена";

        if (!running)
            _cancelRequested = false;
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
