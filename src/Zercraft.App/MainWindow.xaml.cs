using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Zercraft.Core;

namespace Zercraft.App;

public partial class MainWindow : Window
{
    private readonly DiskDiscoveryService _diskDiscovery = new();
    private readonly IsoDownloadService _isoDownloader = new();
    private readonly List<string> _backupFiles = [];
    private string? _imagePath;
    private long _imageSize;
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();
        IsoSourceList.ItemsSource = IsoCatalog.Sources;
        IsoSourceList.SelectedIndex = 0;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) =>
        await RefreshDisksAsync();

    private void ChooseIsoButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir une image ISO",
            Filter = "Images ISO (*.iso)|*.iso|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var file = new FileInfo(dialog.FileName);
        if (file.Length == 0)
        {
            MessageBox.Show(this, "Le fichier ISO est vide.", "Image invalide",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _imagePath = file.FullName;
        _imageSize = file.Length;
        IsoPathText.Text = _imagePath;
        IsoSizeText.Text = FormatSize(_imageSize);
        UpdateWriteButton();
        StatusText.Text = "ISO sélectionnée. Vérifiez le disque USB avant de continuer.";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshDisksAsync();

    private async Task RefreshDisksAsync()
    {
        if (_isBusy)
        {
            return;
        }

        RefreshButton.IsEnabled = false;
        DiskList.ItemsSource = null;
        DiskDetailsText.Text = "Recherche des périphériques USB…";
        try
        {
            var disks = await _diskDiscovery.GetUsbDisksAsync();
            DiskList.ItemsSource = disks;
            DiskDetailsText.Text = disks.Count == 0
                ? "Aucune clé USB détectée. Branchez une clé, puis actualisez."
                : "Choisissez un disque USB en ligne et inscriptible. Les disques système ne sont jamais admissibles.";
            StatusText.Text = disks.Count == 0 ? "Aucun périphérique USB détecté." : "Choisissez une clé USB.";
        }
        catch (Exception exception)
        {
            DiskDetailsText.Text = "La détection des disques a échoué.";
            StatusText.Text = exception.Message;
        }
        finally
        {
            RefreshButton.IsEnabled = !_isBusy;
            UpdateWriteButton();
        }
    }

    private void DiskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DiskList.SelectedItem is UsbDisk disk)
        {
            var state = DiskSafety.IsEligible(disk)
                ? $"Prêt · {disk.OperationalStatus} · {disk.PartitionStyle}"
                : "Refusé : disque hors ligne, protégé en écriture, système ou non admissible.";
            DiskDetailsText.Text = $"{disk.DisplayName}{Environment.NewLine}{state}";
        }

        UpdateWriteButton();
    }

    private async void WriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_imagePath is null || DiskList.SelectedItem is not UsbDisk selectedDisk)
        {
            return;
        }

        FileInfo image;
        try
        {
            image = new FileInfo(_imagePath);
            if (!image.Exists || image.Length != _imageSize)
            {
                StatusText.Text = "Le fichier ISO a changé ou n’existe plus. Sélectionnez-le à nouveau.";
                return;
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Impossible de vérifier l’image : {exception.Message}";
            return;
        }

        if (!DiskSafety.IsEligible(selectedDisk) || selectedDisk.SizeBytes < _imageSize)
        {
            StatusText.Text = "Le disque est inadmissible ou trop petit pour cette image.";
            return;
        }

        var confirmation = new ConfirmWriteWindow(selectedDisk, _imagePath, _imageSize) { Owner = this };
        if (confirmation.ShowDialog() != true || !confirmation.Confirmed)
        {
            return;
        }

        try
        {
            var disks = await _diskDiscovery.GetUsbDisksAsync();
            var current = disks.SingleOrDefault(disk => disk.Number == selectedDisk.Number);
            if (current is null
                || !DiskSafety.IsSameDevice(selectedDisk, current)
                || !DiskSafety.IsEligible(current)
                || current.SizeBytes < _imageSize)
            {
                StatusText.Text = "Le disque a changé depuis sa sélection. Aucun octet n’a été écrit. Actualisez puis recommencez.";
                await RefreshDisksAsync();
                return;
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Impossible de revalider la clé. Aucun octet n’a été écrit : {exception.Message}";
            return;
        }

        await WriteImageAsync(selectedDisk);
    }

    private async Task WriteImageAsync(UsbDisk disk)
    {
        BeginOperation("Écriture en cours. Ne débranchez pas la clé ; une interruption peut la rendre inutilisable.");
        WriteProgress.Value = 0;
        try
        {
            await using var source = new FileStream(
                _imagePath!, FileMode.Open, FileAccess.Read, FileShare.Read,
                RawImageWriter.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length != _imageSize)
            {
                throw new IOException("Le fichier ISO a changé avant le début de l’écriture.");
            }

            var devicePath = $@"\\.\PhysicalDrive{disk.Number}";
            await using var destination = new FileStream(
                devicePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                RawImageWriter.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var liveDisks = await _diskDiscovery.GetUsbDisksAsync(_operationCancellation!.Token);
            var liveTarget = liveDisks.SingleOrDefault(candidate => candidate.Number == disk.Number);
            if (liveTarget is null
                || !DiskSafety.IsSameDevice(disk, liveTarget)
                || !DiskSafety.IsEligible(liveTarget))
            {
                throw new IOException("Le disque USB a changé après son ouverture. Aucun octet n’a été écrit.");
            }

            var progress = new Progress<long>(written =>
            {
                WriteProgress.Value = written * 100d / _imageSize;
                StatusText.Text = $"Écriture… {FormatSize(written)} sur {FormatSize(_imageSize)}.";
            });
            var written = await RawImageWriter.WriteAsync(
                source, destination, _imageSize, progress, _operationCancellation!.Token);
            destination.Flush(flushToDisk: true);
            WriteProgress.Value = 100;
            StatusText.Text = $"Image écrite ({FormatSize(written)}). Le contenu n’a pas été relu.";
            MessageBox.Show(this,
                "L’image a été écrite. La clé n’a pas été relue pour vérification ; éjectez-la proprement avant utilisation.",
                "Écriture terminée", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Écriture annulée. La clé peut être partielle et ne pas démarrer ; recréez-la avant utilisation.";
        }
        catch (UnauthorizedAccessException exception)
        {
            StatusText.Text = $"Accès refusé au disque physique. Relancez Zercraft en tant qu’administrateur. {exception.Message}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Échec d’écriture ; la clé peut être partielle. {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private void ChooseBackupFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir les fichiers à sauvegarder",
            Filter = "Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _backupFiles.Clear();
        _backupFiles.AddRange(dialog.FileNames);
        var totalBytes = _backupFiles.Sum(path => new FileInfo(path).Length);
        BackupFilesText.Text = $"{_backupFiles.Count} fichier(s) sélectionné(s) · {FormatSize(totalBytes)}";
    }

    private async void CreateBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_backupFiles.Count == 0)
        {
            MessageBox.Show(this, "Choisissez au moins un fichier.", "Sauvegarde",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var password = BackupPassword.Password;
        if (password.Length < BackupArchive.MinimumPasswordLength)
        {
            MessageBox.Show(this, $"Le code d’accès doit contenir au moins {BackupArchive.MinimumPasswordLength} caractères.",
                "Code trop court", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!string.Equals(password, ConfirmBackupPassword.Password, StringComparison.Ordinal))
        {
            MessageBox.Show(this, "Les codes d’accès ne correspondent pas.", "Confirmation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Enregistrer la sauvegarde chiffrée",
            Filter = "Sauvegarde Zercraft (*.zercraftbackup)|*.zercraftbackup",
            DefaultExt = ".zercraftbackup",
            AddExtension = true,
            FileName = $"Zercraft-{DateTime.Now:yyyyMMdd-HHmm}.zercraftbackup",
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (File.Exists(dialog.FileName))
        {
            MessageBox.Show(this, "Ce fichier existe déjà. Choisissez un autre nom pour préserver la sauvegarde existante.",
                "Fichier existant", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BeginOperation("Création et chiffrement de la sauvegarde…");
        try
        {
            var progress = new Progress<long>(count =>
                StatusText.Text = $"Sauvegarde et chiffrement… fichier {count} sur {_backupFiles.Count}.");
            await BackupArchive.CreateAsync(
                _backupFiles.ToArray(), dialog.FileName, password, progress, _operationCancellation!.Token);
            StatusText.Text = $"Sauvegarde chiffrée créée : {dialog.FileName}";
            BackupPassword.Clear();
            ConfirmBackupPassword.Clear();
            MessageBox.Show(this, "Sauvegarde chiffrée enregistrée. Les fichiers d’origine n’ont pas été modifiés.",
                "Sauvegarde terminée", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Sauvegarde annulée ; aucun fichier de destination incomplet n’a été conservé.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Échec de la sauvegarde : {exception.Message}";
        }
        finally
        {
            BackupPassword.Clear();
            ConfirmBackupPassword.Clear();
            EndOperation();
        }
    }

    private async void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        var fileDialog = new OpenFileDialog
        {
            Title = "Choisir une sauvegarde Zercraft",
            Filter = "Sauvegardes Zercraft (*.zercraftbackup)|*.zercraftbackup",
            CheckFileExists = true,
            Multiselect = false
        };
        if (fileDialog.ShowDialog(this) != true)
        {
            return;
        }

        var passwordDialog = new PasswordDialog { Owner = this };
        if (passwordDialog.ShowDialog() != true)
        {
            return;
        }

        var folderDialog = new OpenFolderDialog { Title = "Choisir le dossier où restaurer les fichiers" };
        if (folderDialog.ShowDialog(this) != true)
        {
            return;
        }

        BeginOperation("Vérification et restauration de la sauvegarde…");
        try
        {
            var restoredPath = await BackupArchive.RestoreAsync(
                fileDialog.FileName, folderDialog.FolderName, passwordDialog.Password,
                _operationCancellation!.Token);
            StatusText.Text = $"Restauration terminée dans un nouveau dossier : {restoredPath}";
            MessageBox.Show(this,
                $"Les fichiers ont été restaurés sans écraser de fichiers existants :{Environment.NewLine}{restoredPath}",
                "Restauration terminée", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Restauration annulée. Le dossier temporaire a été supprimé.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Échec de la restauration : {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private void IsoSourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsoSourceList.SelectedItem is not IsoSource source)
        {
            return;
        }

        IsoSourceDescription.Text = source.Description;
        IsoAttributionText.Text = $"Source : {source.Attribution} · {source.OfficialPage.Host}";
        DownloadIsoButton.Content = source.DownloadUri is null
            ? "Ouvrir le site officiel"
            : "Télécharger et vérifier";
    }

    private async void DownloadIsoButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsoSourceList.SelectedItem is not IsoSource source)
        {
            return;
        }

        if (source.DownloadUri is null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(source.OfficialPage.AbsoluteUri) { UseShellExecute = true });
                DownloadDetailsText.Text =
                    "Microsoft fournit le lien ISO sur son site ; téléchargez-le là-bas, puis choisissez le fichier dans l’onglet « Clé bootable ». Zercraft n’héberge pas l’image.";
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                DownloadDetailsText.Text = $"Impossible d’ouvrir le navigateur : {exception.Message}";
            }

            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = $"Télécharger depuis {source.Attribution}",
            Filter = "Images ISO (*.iso)|*.iso",
            DefaultExt = ".iso",
            AddExtension = true,
            FileName = source.FileName!,
            OverwritePrompt = false
        };
        if (saveDialog.ShowDialog(this) != true)
        {
            return;
        }

        if (File.Exists(saveDialog.FileName))
        {
            MessageBox.Show(this, "Le fichier existe déjà. Choisissez un autre nom pour éviter l’écrasement.",
                "Fichier existant", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BeginOperation($"Téléchargement depuis {source.Attribution}…");
        DownloadProgress.IsIndeterminate = true;
        DownloadProgress.Value = 0;
        try
        {
            var progress = new Progress<long>(bytes =>
                DownloadDetailsText.Text = $"Téléchargé : {FormatSize(bytes)}. Vérification SHA-256…");
            var path = await _isoDownloader.DownloadVerifiedAsync(
                source, saveDialog.FileName, progress, _operationCancellation!.Token);
            DownloadDetailsText.Text =
                $"Téléchargement et SHA-256 vérifiés depuis {source.Attribution} : {path}";
            StatusText.Text = "ISO téléchargée et vérifiée avec le manifeste SHA-256 officiel.";
            MessageBox.Show(this,
                "Le SHA-256 du fichier correspond au manifeste officiel téléchargé par HTTPS. La signature GPG du manifeste n’est pas vérifiée par cette version.",
                "Vérification terminée", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            DownloadDetailsText.Text = "Téléchargement annulé ; le fichier partiel a été supprimé.";
            StatusText.Text = DownloadDetailsText.Text;
        }
        catch (Exception exception)
        {
            DownloadDetailsText.Text = $"Téléchargement ou vérification échoué : {exception.Message}";
            StatusText.Text = DownloadDetailsText.Text;
        }
        finally
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
            EndOperation();
        }
    }

    private void BeginOperation(string status)
    {
        _isBusy = true;
        _operationCancellation = new CancellationTokenSource();
        MainTabs.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        StatusText.Text = status;
    }

    private void EndOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        _isBusy = false;
        MainTabs.IsEnabled = true;
        CancelButton.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = false;
        UpdateWriteButton();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = "Annulation demandée ; attendez la fin de l’opération.";
        _operationCancellation?.Cancel();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isBusy)
        {
            return;
        }

        e.Cancel = true;
        MessageBox.Show(this,
            "Une opération est en cours. Annulez-la avec le bouton prévu ou attendez sa fin avant de fermer Zercraft.",
            "Opération en cours", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void UpdateWriteButton()
    {
        WriteButton.IsEnabled = !_isBusy
            && _imagePath is not null
            && DiskList.SelectedItem is UsbDisk disk
            && DiskSafety.IsEligible(disk)
            && disk.SizeBytes >= _imageSize;
    }

    private static string FormatSize(long sizeBytes) =>
        sizeBytes >= 1_000_000_000
            ? $"{sizeBytes / 1_000_000_000d:0.##} Go"
            : $"{sizeBytes / 1_000_000d:0.##} Mo";
}
