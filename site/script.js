const copyButton = document.querySelector("[data-copy-command]");
const copyStatus = document.querySelector(".copy-status");

copyButton?.addEventListener("click", async () => {
  const command = `Start-Process "${window.location.origin}${window.location.pathname}"`;
  try {
    await navigator.clipboard.writeText(command);
    copyStatus.textContent = "Commande copiée. Collez-la dans PowerShell.";
  } catch {
    copyStatus.textContent = command;
  }
});
