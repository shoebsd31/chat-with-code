/**
 * Copy text to the clipboard. Returns whether it worked.
 *
 * The extension runs in an Azure DevOps iframe that isn't granted the "clipboard-write" permission,
 * so navigator.clipboard.writeText is usually refused there. The older execCommand("copy") on a
 * selected, off-screen textarea still works inside the iframe during a click, so fall back to that.
 */
export async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // Refused (no permission in the iframe): try the fallback below.
  }
  return copyWithSelection(text);
}

function copyWithSelection(text: string): boolean {
  const active = document.activeElement as HTMLElement | null;
  const area = document.createElement("textarea");
  area.value = text;
  area.setAttribute("readonly", "");
  area.style.position = "fixed";
  area.style.top = "-1000px";
  area.style.opacity = "0";
  document.body.appendChild(area);
  area.select();
  let ok = false;
  try {
    ok = document.execCommand("copy");
  } catch {
    ok = false;
  }
  area.remove();
  active?.focus?.();
  return ok;
}
