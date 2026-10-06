import { useState } from "react";
import { BookOpen, Undo2 } from "lucide-react";
import { Button, Chip, Modal } from "./ui";
import type { Mutate } from "../completion-types";
import type { MemoryFact } from "../types";
import { dayLabel } from "../lib/time";
import { RichText } from "./InvestigationText";

const shortLabel = (text: string) => (text.length > 80 ? `${text.slice(0, 80).trimEnd()}…` : text);

/** Where a fact came from, as a short label: "You added", "From your reply · 5 Oct", "Joule learned". */
function sourceText(fact: MemoryFact) {
  if (fact.source === "user") return "You added";
  if (fact.source === "user-reply") return `From your reply · ${dayLabel(fact.createdAt)}`;
  return "Joule learned";
}

/** The facts every check reads: add one, or remove one (with an undo for a moment after). */
export function MemoryList({ memory, mutate, busy }: { memory: MemoryFact[]; mutate: Mutate; busy: boolean }) {
  const [text, setText] = useState("");
  const [removed, setRemoved] = useState<MemoryFact | null>(null);
  const trimmed = text.trim();
  return (
    <div className="memory">
      {memory.length ? (
        <ul className="memory-list" aria-label="Remembered facts">
          {memory.map((fact) => (
            <li key={fact.id}>
              <div className="memory-fact">
                <p>
                  <RichText text={fact.text} />
                </p>
                <Chip tone={fact.source === "model" ? "violet" : "neutral"}>{sourceText(fact)}</Chip>
              </div>
              <Button
                variant="ghost"
                size="sm"
                disabled={busy}
                aria-label={`Remove: ${shortLabel(fact.text)}`}
                onClick={async () => {
                  if (await mutate(`/memory/${encodeURIComponent(fact.id)}/delete`, {}, "Removed.")) setRemoved(fact);
                }}
              >
                Remove
              </Button>
            </li>
          ))}
        </ul>
      ) : (
        <p className="muted">Nothing yet. Add anything a check should always know.</p>
      )}
      {removed && (
        <p className="memory-undo" role="status">
          Removed “{shortLabel(removed.text)}”.
          <Button
            variant="link"
            size="sm"
            disabled={busy}
            onClick={async () => {
              if (await mutate("/memory", { text: removed.text }, "Put back.")) setRemoved(null);
            }}
          >
            <Undo2 size={14} aria-hidden="true" />
            Undo
          </Button>
        </p>
      )}
      <form
        className="memory-form"
        onSubmit={async (e) => {
          e.preventDefault();
          if (!trimmed) return;
          if (await mutate("/memory", { text: trimmed }, "Remembered.")) setText("");
        }}
      >
        <label className="field">
          Add a fact
          <input
            maxLength={300}
            value={text}
            placeholder="There is a 10 kW heat pump"
            onChange={(e) => setText(e.target.value)}
          />
        </label>
        <Button variant="secondary" disabled={busy || !trimmed}>
          Remember
        </Button>
      </form>
    </div>
  );
}

/** "What Joule knows about your home": a button that opens the facts in a drawer (a bottom sheet on phones). */
export function MemoryPanel({ memory, mutate, busy }: { memory: MemoryFact[]; mutate: Mutate; busy: boolean }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button type="button" className="memory-button" onClick={() => setOpen(true)}>
        <BookOpen size={16} aria-hidden="true" />
        <span>
          What Joule knows about your home
          <small>
            {memory.length
              ? `${memory.length} ${memory.length === 1 ? "fact" : "facts"} every check reads`
              : "Nothing yet"}
          </small>
        </span>
      </button>
      <Modal
        open={open}
        onOpenChange={setOpen}
        title="What Joule knows about your home"
        description="Every check reads these. One sentence each, for example ‘There is a 10 kW heat pump’."
      >
        <MemoryList memory={memory} mutate={mutate} busy={busy} />
      </Modal>
    </>
  );
}
