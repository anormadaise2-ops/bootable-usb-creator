const copyButton = document.querySelector("[data-copy-command]");
const copyStatus = document.querySelector(".copy-status");
const commandOutput = document.querySelector("[data-install-command]");

const command = [
  "$ErrorActionPreference = 'Stop'",
  "$release = 'https://github.com/anormadaise2-ops/bootable-usb-creator/releases/latest/download'",
  "$dir = Join-Path $env:TEMP ('Zercraft-' + [guid]::NewGuid().ToString('N'))",
  "New-Item -ItemType Directory -Path $dir | Out-Null",
  "$sums = Join-Path $dir 'SHA256SUMS.txt'",
  "$installer = Join-Path $dir 'ZercraftSetup.exe'",
  'curl.exe --fail --location --silent --show-error --output $sums "$release/SHA256SUMS.txt"',
  "if ($LASTEXITCODE -ne 0) { throw 'Échec du téléchargement du manifeste SHA-256.' }",
  'curl.exe --fail --location --silent --show-error --output $installer "$release/ZercraftSetup.exe"',
  "if ($LASTEXITCODE -ne 0) { throw 'Échec du téléchargement de l’installateur.' }",
  "$line = Get-Content -LiteralPath $sums | Where-Object { $_ -match '^([0-9a-fA-F]{64})\\s+ZercraftSetup\\.exe$' } | Select-Object -First 1",
  "if (-not $line) { throw 'Le manifeste ne contient pas le SHA-256 de ZercraftSetup.exe.' }",
  "$expected = ($line -split '\\s+')[0].ToLowerInvariant()",
  "$actual = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()",
  "if ($actual -ne $expected) { Remove-Item -LiteralPath $installer -Force; throw 'SHA-256 incorrect ; l’installateur ne sera pas lancé.' }",
  "Start-Process -FilePath $installer"
].join("\n");

if (commandOutput) {
  commandOutput.textContent = command;
}

copyButton?.addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(command);
    copyStatus.textContent = "Commande copiée. Collez-la dans PowerShell pour télécharger et vérifier l’installateur.";
  } catch {
    copyStatus.textContent = "Le presse-papiers est indisponible. Affichez puis copiez la commande ci-dessous.";
  }
});
