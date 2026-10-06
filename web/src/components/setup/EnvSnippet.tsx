import { useState } from "react";
import { Check, Copy } from "lucide-react";
import { Button } from "../ui";

/** Environment lines to paste into Joule's .env or compose file, with a Copy button. */
export function EnvSnippet({ lines, label = "Settings to add" }: { lines: string[]; label?: string }) {
  const [copied, setCopied] = useState(false);
  const text = lines.join("\n");
  return (
    <div className="env-snippet">
      <pre tabIndex={0} aria-label={label}>
        {text}
      </pre>
      <Button
        variant="secondary"
        size="sm"
        className="env-copy"
        aria-label={copied ? "Copied" : `Copy: ${label}`}
        onClick={async () => {
          try {
            await navigator.clipboard.writeText(text);
            setCopied(true);
            setTimeout(() => setCopied(false), 2000);
          } catch {
            setCopied(false);
          }
        }}
      >
        {copied ? <Check size={14} aria-hidden="true" /> : <Copy size={14} aria-hidden="true" />}
        {copied ? "Copied" : "Copy"}
      </Button>
    </div>
  );
}
