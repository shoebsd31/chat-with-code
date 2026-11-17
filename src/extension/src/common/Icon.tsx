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
} as const;

export type IconName = keyof typeof iconPaths;

export function Icon({ name, label }: { name: IconName; label?: string }) {
  return (
    <svg className="icon" viewBox="0 0 24 24" aria-hidden={label ? undefined : true} role={label ? "img" : undefined} aria-label={label}>
      <path d={iconPaths[name]} />
    </svg>
  );
}
