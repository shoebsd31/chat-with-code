/**
 * Save a file in the browser. Returns false if the browser refused (for example when the page the
 * extension runs in doesn't allow downloads), so the caller can offer another way.
 */
export function downloadBlob(fileName: string, blob: Blob): boolean {
  try {
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.rel = "noopener";
    link.style.display = "none";
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 30_000);
    return true;
  } catch {
    return false;
  }
}

export const downloadText = (fileName: string, text: string, type = "text/markdown;charset=utf-8") =>
  downloadBlob(fileName, new Blob([text], { type }));
