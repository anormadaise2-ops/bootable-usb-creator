using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Zercraft.Core;

public sealed class IsoDownloadService(HttpClient? httpClient = null)
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromHours(4)
    };

    private readonly HttpClient _httpClient = httpClient ?? SharedClient;

    public async Task<string> DownloadVerifiedAsync(
        IsoSource source,
        string destinationPath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (source.DownloadUri is null || source.Sha256ManifestUri is null || source.FileName is null)
        {
            throw new InvalidOperationException("Cette source impose le téléchargement depuis son site officiel.");
        }

        EnsureOfficialHttps(source.OfficialPage);
        EnsureOfficialHttps(source.DownloadUri);
        EnsureOfficialHttps(source.Sha256ManifestUri);

        var expectedHash = await GetExpectedHashAsync(
            source.Sha256ManifestUri,
            source.FileName,
            cancellationToken);
        var partialPath = destinationPath + ".partial";
        try
        {
            using var response = await _httpClient.GetAsync(
                source.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not Uri finalDownloadUri)
            {
                throw new InvalidDataException("La réponse ISO ne contient pas d’adresse source.");
            }

            EnsureOfficialHttps(finalDownloadUri);

            var totalBytes = response.Content.Headers.ContentLength;
            await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var partial = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long downloaded = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await remote.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await partial.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                downloaded += read;
                progress?.Report(downloaded);
            }

            await partial.FlushAsync(cancellationToken);
            if (totalBytes.HasValue && downloaded != totalBytes.Value)
            {
                throw new IOException("Le téléchargement est incomplet (taille reçue différente de la taille annoncée).");
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                Convert.FromHexString(expectedHash)))
            {
                throw new CryptographicException(
                    $"Le SHA-256 téléchargé ne correspond pas au manifeste officiel. Fichier supprimé : {partialPath}");
            }
        }
        catch
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            throw;
        }

        try
        {
            File.Move(partialPath, destinationPath);
        }
        catch
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            throw;
        }

        return destinationPath;
    }

    private async Task<string> GetExpectedHashAsync(
        Uri manifestUri,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(manifestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not Uri finalManifestUri)
        {
            throw new InvalidDataException("La réponse du manifeste ne contient pas d’adresse source.");
        }

        EnsureOfficialHttps(finalManifestUri);
        var manifest = await response.Content.ReadAsStringAsync(cancellationToken);
        var pattern = new Regex(
            @"^(?<hash>[0-9a-fA-F]{64})[ \t]+\*?(?<file>[^\r\n]+?)[ \t]*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        foreach (Match match in pattern.Matches(manifest))
        {
            if (string.Equals(
                Path.GetFileName(match.Groups["file"].Value.Trim()),
                fileName,
                StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["hash"].Value.ToUpperInvariant();
            }
        }

        throw new InvalidDataException(
            $"Aucun SHA-256 pour {fileName} dans le manifeste officiel {manifestUri}.");
    }

    private static void EnsureOfficialHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("releases.ubuntu.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("cdimage.debian.org", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("www.microsoft.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Source de téléchargement non autorisée : {uri.Host}");
        }
    }
}
