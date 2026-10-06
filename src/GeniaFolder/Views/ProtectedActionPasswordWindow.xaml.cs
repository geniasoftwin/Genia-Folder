using System.Windows;
using GeniaFolder.Services;

namespace GeniaFolder.Views;

public partial class ProtectedActionPasswordWindow : Window
{
    private readonly Guid _folderId;
    private readonly ProtectionService _protection;

    public ProtectedActionPasswordWindow(
        Guid folderId,
        string folderName,
        string actionDescription,
        ProtectionService protection)
    {
        InitializeComponent();

        _folderId = folderId;
        _protection = protection;

        InfoText.Text =
            $"Для защищённой папки «{folderName}» требуется пароль. " +
            actionDescription;

        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void PasswordBox_PasswordChanged(
        object sender,
        RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        ConfirmButton.IsEnabled = PasswordBox.Password.Length > 0;
    }

    private async void Confirm_Click(
        object sender,
        RoutedEventArgs e)
    {
        var password = PasswordBox.Password;

        if (string.IsNullOrEmpty(password))
            return;

        ConfirmButton.IsEnabled = false;
        PasswordBox.IsEnabled = false;
        ErrorText.Text = "Проверка пароля…";

        try
        {
            var valid = await _protection.VerifyPasswordAsync(
                _folderId,
                password);

            PasswordBox.Clear();
            password = string.Empty;

            if (!valid)
            {
                ErrorText.Text = "Неверный пароль.";
                PasswordBox.IsEnabled = true;
                PasswordBox.Focus();
                return;
            }

            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text =
                "Не удалось проверить пароль: " + ex.Message;
            PasswordBox.IsEnabled = true;
            PasswordBox.Focus();
        }
        finally
        {
            password = string.Empty;

            if (DialogResult != true)
                ConfirmButton.IsEnabled =
                    PasswordBox.Password.Length > 0;
        }
    }
}
