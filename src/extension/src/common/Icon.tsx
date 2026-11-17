export const iconPaths = {
  chat: "M4 5h16v10H9l-5 4V5z",
  plus: "M12 5v14M5 12h14",
  send: "M4 12l16-8-6 16-2-7-8-1z",
  stop: "M7 7h10v10H7z",
  trash: "M5 7h14M10 7V5h4v2M7 7l1 12h8l1-12",
  copy: "M8 8h11v11H8zM5 16V5h11",
  check: "M5 12l5 5 9-10",
  repo: "M6 4h11v16H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM4 16h13",
  close: "M6 6l12 12M18 6L6 18",
  menu: "M4 7h16M4 12h16M4 17h16",
  file: "M7 3h7l4 4v14H7zM14 3v4h4",
  warn: "M12 4l9 16H3zM12 10v4M12 17v.5",
  refresh: "M20 11a8 8 0 1 0-2.3 5.7M20 5v6h-6",
  chevron: "M8 10l4 4 4-4",
  search: "M11 18a7 7 0 1 1 0-14 7 7 0 0 1 0 14zM16 16l4 4",
  code: "M9 7l-5 5 5 5M15 7l5 5-5 5",
  doc: "M7 3h7l4 4v14H7zM14 3v4h4M10 12h5M10 16h5",
  settings: "M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6zM12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M5.6 18.4l2.1-2.1M16.3 7.7l2.1-2.1",
  download: "M12 4v11M7 10l5 5 5-5M5 20h14",
} as const;

export type IconName = keyof typeof iconPaths;

export function Icon({ name, label }: { name: IconName; label?: string }) {
  return (
    <svg className="icon" viewBox="0 0 24 24" aria-hidden={label ? undefined : true} role={label ? "img" : undefined} aria-label={label}>
      <path d={iconPaths[name]} />
    </svg>
  );
}
