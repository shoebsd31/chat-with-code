import { useEffect, useRef, useState } from "react";
import { copyText } from "./clipboard";
import { Icon } from "./Icon";

/** A small "Copy" button that says whether copying really worked. */
export function CopyButton({ value, label }: { value: string; label: string }) {
  const [state, setState] = useState<"idle" | "copied" | "failed">("idle");
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);

  const copy = async () => {
    const ok = await copyText(value);
    setState(ok ? "copied" : "failed");
    clearTimeout(timer.current);
    timer.current = setTimeout(() => setState("idle"), ok ? 1500 : 4000);
  };

  return (
    <>
      <button
        type="button"
        className="btn ghost small"
        onClick={copy}
        aria-label={label}
        title={state === "failed" ? "Your browser blocked copying. Select the text and press Ctrl+C." : undefined}
      >
        <Icon name={state === "copied" ? "check" : state === "failed" ? "warn" : "copy"} />{" "}
        {state === "copied" ? "Copied" : state === "failed" ? "Couldn't copy" : "Copy"}
      </button>
      <span className="sr-only" role="status">
        {state === "copied" ? "Copied to the clipboard" : state === "failed" ? "Couldn't copy. Select the text and press Ctrl+C." : ""}
      </span>
    </>
  );
}
