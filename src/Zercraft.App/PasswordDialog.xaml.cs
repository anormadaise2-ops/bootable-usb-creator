using System.Windows;
using Zercraft.Core;

namespace Zercraft.App;

public partial class PasswordDialog : Window
{
    public PasswordDialog()
    {
        InitializeComponent();
    }

    public string Password => PasswordInput.Password;

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordInput.Password.Length < BackupArchive.MinimumPasswordLength)
        {
            ErrorText.Text = $"Le code doit contenir au moins {BackupArchive.MinimumPasswordLength} caractères.";
            return;
        }

        DialogResult = true;
    }
}
