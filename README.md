# Zercraft

Zercraft is a Windows desktop utility for preparing compatible bootable USB media and keeping encrypted file backups on removable storage.

## Features

- Lists USB disks using Windows Storage cmdlets; refuses boot/system, offline, read-only and non-USB disks.
- Revalidates the selected disk identity immediately before writing. Raw-image writing requires the user to confirm the displayed disk by typing `EFFACER <disk number>`.
- Downloads selected Ubuntu and Debian images from their official HTTPS hosts and verifies the downloaded file against the SHA-256 manifest published by that host. Microsoft provides Windows ISO downloads through its official page because the direct URL is generated there. The app does not verify the manifest's GPG signature.
- Stores selected files in a password-encrypted `.zercraftbackup` file using PBKDF2-SHA256, AES-256-CBC and HMAC-SHA256. The password is not stored. Restore authenticates the backup before decrypting and always writes into a new folder; it does not overwrite existing files or remove originals.
- Website source is in `site/`; GitHub Actions workflows publish GitHub Pages and build a Windows installer for `v*` tags.

## Build and test on Windows

Install the .NET 8 SDK. From the repository root:

```powershell
dotnet run --project tests\Zercraft.Tests\Zercraft.Tests.csproj --configuration Release
dotnet build src\Zercraft.App\Zercraft.App.csproj --configuration Release
dotnet publish src\Zercraft.App\Zercraft.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts\publish
```

The GitHub release workflow creates `ZercraftSetup.exe` with Inno Setup and publishes it alongside a portable build and SHA-256 file when a `v*` tag is pushed. Installers are unsigned; Zercraft branding is not a digital signature.

## Important limitations and safety

- Raw ISO writing destroys all data on the selected USB disk. The app never writes automatically or in the background. A cancelled or failed write can leave the key unusable.
- Raw writing is for hybrid images intended for direct USB writing. It does not convert an ISO or guarantee that ordinary Windows installation ISOs will boot this way. For Windows, use Microsoft's official media-creation instructions.
- Physical-disk writes may require starting Zercraft with administrator rights. Do not select a disk unless its number, model and capacity are the intended target.
- The backup code cannot be recovered. Keep a safe copy; losing it makes the backup unreadable. HMAC protects the archive from modification, but the app does not provide key recovery.
- No physical USB device or ISO has been used by automated tests. The tests cover disk eligibility and identity guards, raw copying in memory, and encrypted backup/restore behavior.
- Search engines may discover the public GitHub Pages site, but indexing is controlled by each search provider and is not guaranteed.

## Official ISO sources

- [Microsoft Windows 11](https://www.microsoft.com/software-download/windows11)
- [Ubuntu releases](https://releases.ubuntu.com/)
- [Debian installation images](https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/)
