/**
 * A rendered Mermaid diagram as a PNG: drawn onto a canvas at twice the size (sharp on high-DPI
 * screens and in documents), with the background and font it has on screen so it looks the same.
 */
export async function diagramPng(svg: SVGSVGElement, background: string, scale = 2): Promise<Blob> {
  const box = svg.viewBox?.baseVal;
  const rect = svg.getBoundingClientRect();
  const width = box && box.width > 0 ? box.width : rect.width;
  const height = box && box.height > 0 ? box.height : rect.height;
  if (!(width > 0 && height > 0)) throw new Error("The diagram has no size yet.");

  const clone = svg.cloneNode(true) as SVGSVGElement;
  clone.setAttribute("xmlns", "http://www.w3.org/2000/svg");
  clone.setAttribute("width", String(width));
  clone.setAttribute("height", String(height));
  clone.removeAttribute("style"); // Mermaid's max-width would shrink it
  clone.style.fontFamily = getComputedStyle(svg).fontFamily; // labels use "inherit", which a standalone image doesn't have

  const image = new Image();
  image.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(new XMLSerializer().serializeToString(clone))}`;
  await image.decode();

  const canvas = document.createElement("canvas");
  canvas.width = Math.ceil(width * scale);
  canvas.height = Math.ceil(height * scale);
  const context = canvas.getContext("2d");
  if (!context) throw new Error("This browser can't draw images.");
  context.fillStyle = background;
  context.fillRect(0, 0, canvas.width, canvas.height);
  context.drawImage(image, 0, 0, canvas.width, canvas.height);

  return new Promise((resolve, reject) => canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error("Couldn't create the image."))), "image/png"));
}

/** The element's visible background colour, or white when it's transparent. */
export function backgroundOf(element: Element): string {
  for (let node: Element | null = element; node; node = node.parentElement) {
    const color = getComputedStyle(node).backgroundColor;
    if (color && color !== "transparent" && !/rgba\(\s*0,\s*0,\s*0,\s*0\s*\)/.test(color)) return color;
  }
  return "#ffffff";
}
