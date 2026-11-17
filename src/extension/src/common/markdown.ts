import DOMPurify from "dompurify";
import hljs from "highlight.js/lib/common";
import { Marked, type Tokens } from "marked";
import type { Citation } from "./api";
import { copyText } from "./clipboard";
import { backgroundOf, diagramPng } from "./diagramImage";
import { downloadBlob } from "./download";
import { iconPaths, type IconName } from "./Icon";

/**
 * Renders an answer: Markdown with highlighted code, [n] citation markers turned into
 * chips (links once citations are known), and ```mermaid blocks left as placeholders for
 * the Mermaid renderer. The result is sanitized before it reaches the page.
 */
export function renderAnswer(markdown: string, citations: ReadonlyMap<number, Citation>): string {
  const marked = new Marked({ gfm: true, breaks: false });
  marked.use({
    extensions: [
      {
        name: "citation",
        level: "inline",
        start: (src: string) => src.match(/\[\d{1,3}\]/)?.index,
        tokenizer(src: string) {
          const match = /^\[(\d{1,3})\]/.exec(src);
          return match ? { type: "citation", raw: match[0], number: Number(match[1]) } : undefined;
        },
        renderer(token) {
          const n = (token as unknown as { number: number }).number;
          const citation = citations.get(n);
          if (citation?.url) {
            const title = `${citation.path} lines ${citation.startLine}-${citation.endLine}`;
            return `<a class="cite" href="${escapeAttr(citation.url)}" target="_blank" rel="noopener noreferrer" title="${escapeAttr(title)}" aria-label="Source ${n}: ${escapeAttr(title)}">${n}</a>`;
          }
          return `<span class="cite" aria-label="Source ${n}">${n}</span>`;
        },
      },
    ],
    renderer: {
      code({ text, lang }: Tokens.Code) {
        const language = (lang ?? "").trim().split(/\s+/)[0].toLowerCase();
        if (language === "mermaid") {
          // The diagram source stays as element text: the sanitizer drops attributes containing "-->",
          // which almost every Mermaid diagram has.
          return `<div class="mermaid-block"><pre><code>${escapeHtml(text)}</code></pre></div>`;
        }
        const highlighted = language && hljs.getLanguage(language) ? hljs.highlight(text, { language }).value : escapeHtml(text);
        return `<pre class="code"><code class="hljs${language ? ` language-${escapeAttr(language)}` : ""}">${highlighted}</code></pre>`;
      },
      link({ href, title, tokens }: Tokens.Link) {
        const label = this.parser.parseInline(tokens);
        return `<a href="${escapeAttr(href)}" target="_blank" rel="noopener noreferrer"${title ? ` title="${escapeAttr(title)}"` : ""}>${label}</a>`;
      },
    },
  });

  const html = marked.parse(markdown, { async: false });
  return DOMPurify.sanitize(html, { ADD_ATTR: ["target"], FORBID_TAGS: ["style", "form", "input"] });
}

export function escapeHtml(value: string): string {
  return value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function escapeAttr(value: string): string {
  return escapeHtml(value).replace(/"/g, "&quot;");
}

/** Draw every Mermaid placeholder inside an element. Mermaid is loaded only when a diagram appears. */
export async function renderMermaid(container: HTMLElement, dark: boolean): Promise<void> {
  const blocks = Array.from(container.querySelectorAll<HTMLElement>(".mermaid-block:not([data-rendered])"));
  if (blocks.length === 0) return;
  const { default: mermaid } = await import("mermaid");
  // Plain SVG text labels (no embedded HTML), so the sanitizer keeps every label.
  mermaid.initialize({ startOnLoad: false, securityLevel: "strict", theme: dark ? "dark" : "default", fontFamily: "inherit", htmlLabels: false, flowchart: { htmlLabels: false } });
  for (const block of blocks) {
    block.dataset.rendered = "true";
    const code = block.querySelector("code")?.textContent ?? "";
    try {
      const { svg } = await mermaid.render(`mermaid-${crypto.randomUUID()}`, code);
      const figure = document.createElement("div");
      figure.className = "mermaid-figure";
      figure.setAttribute("role", "img");
      figure.setAttribute("aria-label", "Diagram");
      figure.innerHTML = DOMPurify.sanitize(svg, { USE_PROFILES: { svg: true, svgFilters: true }, ADD_TAGS: ["foreignObject"] });
      block.replaceChildren(diagramToolbar(code, figure), figure);
    } catch {
      block.insertAdjacentHTML("afterbegin", `<p class="mermaid-error">Couldn't draw this diagram, so here is its source.</p>`);
      block.prepend(diagramToolbar(code));
    }
  }
}

/**
 * The buttons above a diagram: "Copy diagram" copies the Mermaid source (it pastes into an Azure DevOps
 * wiki page, Markdown files or mermaid.live), and "Download PNG" saves it as an image. A blocked download
 * can't be detected, so afterwards there's also a way to open the image in a new tab.
 */
function diagramToolbar(code: string, figure?: HTMLElement): HTMLElement {
  const bar = document.createElement("div");
  bar.className = "mermaid-toolbar";

  const copy = toolbarButton("copy", "Copy diagram", "Copies the diagram's Mermaid source. Paste it into an Azure DevOps wiki page, Markdown or mermaid.live.");
  copy.button.addEventListener("click", async () => {
    const ok = await copyText(code);
    copy.flash(ok ? "check" : "warn", ok ? "Copied" : "Couldn't copy", ok ? 1500 : 4000);
  });
  bar.append(copy.button);

  const svg = figure?.querySelector("svg");
  if (figure && svg) {
    const png = toolbarButton("download", "Download PNG", "Saves the diagram as a PNG image.");
    const status = document.createElement("span");
    status.className = "mermaid-status";
    status.setAttribute("role", "status");
    let lastUrl: string | undefined;

    png.button.addEventListener("click", async () => {
      try {
        const blob = await diagramPng(svg, backgroundOf(figure));
        if (lastUrl) URL.revokeObjectURL(lastUrl);
        lastUrl = URL.createObjectURL(blob);
        downloadBlob("diagram.png", blob);
        png.flash("check", "Downloaded", 1500);

        const open = document.createElement("a");
        open.href = lastUrl;
        open.target = "_blank";
        open.rel = "noopener";
        open.textContent = "Open image";
        status.replaceChildren(document.createTextNode("Not saved? "), open);
        setTimeout(() => status.replaceChildren(), 12_000);
      } catch {
        png.flash("warn", "Couldn't create the image", 4000);
      }
    });
    bar.prepend(status);
    bar.append(png.button);
  }

  return bar;
}

function toolbarButton(icon: IconName, text: string, title: string) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "btn ghost small";
  button.title = title;
  const show = (i: IconName, t: string) => button.replaceChildren(iconSvg(i), document.createTextNode(` ${t}`));
  show(icon, text);
  let timer: ReturnType<typeof setTimeout> | undefined;
  return {
    button,
    flash(i: IconName, t: string, ms: number) {
      show(i, t);
      clearTimeout(timer);
      timer = setTimeout(() => show(icon, text), ms);
    },
  };
}

function iconSvg(name: IconName): SVGSVGElement {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("class", "icon");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("aria-hidden", "true");
  const path = document.createElementNS(ns, "path");
  path.setAttribute("d", iconPaths[name]);
  svg.append(path);
  return svg;
}
