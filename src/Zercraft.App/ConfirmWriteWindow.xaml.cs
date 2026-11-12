using System.Windows;
using System.Windows.Controls;
using Zercraft.Core;

namespace Zercraft.App;

public partial class ConfirmWriteWindow : Window
{
    private readonly string _expectedPhrase;

    public ConfirmWriteWindow(UsbDisk disk, string imagePath, long imageSize)
    {
        InitializeComponent();
        _expectedPhrase = DiskSafety.ConfirmationPhrase(disk);
        var identity = string.IsNullOrWhiteSpace(disk.SerialNumber)
            ? "Numéro de série non fourni par Windows."
            : $"N° série : {disk.SerialNumber}.";
        TargetText.Text =
            $"Cible : disque {disk.Number} — {disk.FriendlyName} ({FormatSize(disk.SizeBytes)}). {identity}";
        ImageText.Text = $"ISO : {imagePath} ({FormatSize(imageSize)}).";
        PhraseText.Text = _expectedPhrase;
    }

    public bool Confirmed { get; private set; }

    private void ConfirmationInput_TextChanged(object sender, TextChangedEventArgs e) =>
        ConfirmButton.IsEnabled = string.Equals(
            ConfirmationInput.Text,
            _expectedPhrase,
            StringComparison.Ordinal);

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmButton.IsEnabled)
        {
            return;
        }

        Confirmed = true;
        DialogResult = true;
    }

    private static string FormatSize(long sizeBytes) =>
        sizeBytes >= 1_000_000_000
            ? $"{sizeBytes / 1_000_000_000d:0.##} Go"
            : $"{sizeBytes / 1_000_000d:0.##} Mo";
}
