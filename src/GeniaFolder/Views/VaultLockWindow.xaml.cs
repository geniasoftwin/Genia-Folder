using System.ComponentModel;
using System.Windows;
using GeniaFolder.Models;
using GeniaFolder.Services;

namespace GeniaFolder.Views;

public partial class VaultLockWindow : Window
{
    private readonly ManagedFolder _folder;
    private readonly VaultCopyInfo _vaultInfo;
    private readonly ProtectionService _protection;
    private readonly VaultOnlyService _vaultOnly;

    private CancellationTokenSource? _cancellation;
    private bool _running;
    private volatile bool _commitStarted;
    private bool _cancelRequested;
    private bool _closeAfterCancel;

    public VaultLockWindow(
        ManagedFolder folder,
        VaultCopyInfo vaultInfo,
        ProtectionService protection,
        VaultEncryptionService vault)
    {
        InitializeComponent();

        _folder = folder;
        _vaultInfo = vaultInfo;
        _protection = protection;
        _vaultOnly = new VaultOnlyService(protection, vault);

        var storage = protection.GetStorageInfo(folder.Id);

        WarningText.Text = storage?.State == VaultStorageState.LockPending
            ? "Предыдущая блокировка была прервана. GeniaFolder повторно проверит vault и безопасно продолжит транзакцию LockPending. Не удаляйте vault и pending-quarantine вручную."
            : $"Для «{folder.Name}» GeniaFolder сначала заново проверит encrypted vault, затем byte-for-byte сверит текущую исходную папку с manifest. Только при полном совпадении plaintext будет удалён, а профиль перейдёт в Vault-only.";

        PasswordBox.PasswordChanged += (_, _) => UpdateActionState();
        Loaded += (_, _) => PasswordBox.Focus();
        Closing += VaultLockWindow_Closing;
    }

    private void Confirmation_Changed(object sender, RoutedEventArgs e) =>
        UpdateActionState();

    private void UpdateActionState()
    {
        if (_running)
            return;

        LockButton.IsEnabled =
            PasswordBox.Password.Length > 0 &&
            RecoveryConfirmedBox.IsChecked == true &&
            RemovalConfirmedBox.IsChecked == true;
    }

    private async void Lock_Click(object sender, RoutedEventArgs e)
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

        if (RecoveryConfirmedBox.IsChecked != true ||
            RemovalConfirmedBox.IsChecked != true)
        {
            ErrorText.Text = "Подтвердите оба пункта безопасности.";
            return;
        }

        SetRunning(true);
        _commitStarted = false;
        _cancelRequested = false;
        _closeAfterCancel = false;
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
                StatusText.Text = "Vault-only не активирован.";
                SetRunning(false);
                PasswordBox.Focus();
                return;
            }

            var progress = new Progress<VaultBuildProgress>(
                UpdateProgress);

            await Task.Run(
                () => _vaultOnly.ActivateOrResumeAsync(
                    _folder,
                    _vaultInfo,
                    session,
                    progress,
                    _cancellation.Token,
                    onNonCancellablePhase: () =>
                    {
                        _commitStarted = true;
                        Dispatcher.BeginInvoke(() =>
                        {
                            CancelButton.IsEnabled = false;
                            StatusText.Text =
                                "Папка изолирована. Завершаем обязательную проверку и транзакцию…";
                        });
                    }));

            SetRunning(false);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            var closeAfterCancel = _closeAfterCancel;

            ErrorText.Text = string.Empty;
            StatusText.Text =
                "Блокировка отменена до destructive commit. Исходная папка не удалялась.";
            ProgressBar.Value = 0;
            SetRunning(false);

            if (closeAfterCancel)
                DialogResult = false;
        }
        catch (Exception ex)
        {
            ErrorText.Text =
                "Vault-only не активирован: " + ex.Message;

            var storage = _protection.GetStorageInfo(_folder.Id);
            StatusText.Text = storage?.State == VaultStorageState.LockPending
                ? "Профиль находится в LockPending. Следующая попытка безопасно продолжит транзакцию."
                : "Plaintext не удаляется, если проверка vault/исходной папки не пройдена.";

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
            RequestCancel(closeAfterCancel: false);
            return;
        }

        DialogResult = false;
    }

    private void VaultLockWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (!_running)
            return;

        e.Cancel = true;

        if (_commitStarted)
        {
            StatusText.Text =
                "Идёт финализация Vault-only. Окно закроется после завершения транзакции.";
            return;
        }

        RequestCancel(closeAfterCancel: true);
    }

    private void RequestCancel(bool closeAfterCancel)
    {
        if (closeAfterCancel)
            _closeAfterCancel = true;

        if (_commitStarted || _cancelRequested)
            return;

        _cancelRequested = true;
        CancelButton.IsEnabled = false;
        StatusText.Text = "Останавливаем до destructive commit…";
        _cancellation?.Cancel();
    }

    private void SetRunning(bool running)
    {
        _running = running;
        PasswordBox.IsEnabled = !running;
        RecoveryConfirmedBox.IsEnabled = !running;
        RemovalConfirmedBox.IsEnabled = !running;
        LockButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        CancelButton.Content = running ? "Остановить" : "Отмена";

        if (!running)
        {
            _commitStarted = false;
            _cancelRequested = false;
            UpdateActionState();
        }
    }

    private void UpdateProgress(VaultBuildProgress progress)
    {
        if (_cancelRequested)
            return;

        if (progress.Stage is "Изоляция plaintext" or
            "Финальная сверка изолированных данных" or
            "Безвозвратная финализация Vault-only")
        {
            _commitStarted = true;
            CancelButton.IsEnabled = false;
        }

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
