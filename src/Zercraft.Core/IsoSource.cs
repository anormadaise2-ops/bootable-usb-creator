namespace Zercraft.Core;

public sealed record IsoSource(
    string Name,
    string Description,
    Uri OfficialPage,
    Uri? DownloadUri,
    Uri? Sha256ManifestUri,
    string? FileName,
    string Attribution);

public static class IsoCatalog
{
    public static IReadOnlyList<IsoSource> Sources { get; } =
    [
        new(
            "Ubuntu 26.04.1 LTS — Bureau (64 bits)",
            "Image officielle Ubuntu pour PC AMD64.",
            new Uri("https://releases.ubuntu.com/26.04/"),
            new Uri("https://releases.ubuntu.com/26.04/ubuntu-26.04.1-desktop-amd64.iso"),
            new Uri("https://releases.ubuntu.com/26.04/SHA256SUMS"),
            "ubuntu-26.04.1-desktop-amd64.iso",
            "Canonical / Ubuntu"),
        new(
            "Debian 13.7 — Netinst (64 bits)",
            "Installateur officiel Debian minimal ; Internet est requis pour récupérer les paquets.",
            new Uri("https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/"),
            new Uri("https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/debian-13.7.0-amd64-netinst.iso"),
            new Uri("https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/SHA256SUMS"),
            "debian-13.7.0-amd64-netinst.iso",
            "Debian CD team"),
        new(
            "Windows 11 — Microsoft",
            "Microsoft fournit les ISO via sa page officielle, avec un lien de téléchargement généré sur son site.",
            new Uri("https://www.microsoft.com/software-download/windows11"),
            null,
            null,
            null,
            "Microsoft")];
}
