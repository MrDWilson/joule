import { useState } from "react";
import { AlertCircle, Bell, FileText, ListChecks, Sparkles, type LucideIcon } from "lucide-react";
import { Popover } from "../ui/Popover";
import { EmptyState } from "../ui/States";
import { PlainText } from "../PlainText";
import { investigationVerdict } from "../InvestigationText";
import { headlineOf, needsYou } from "../../lib/insights";
import { buildHash } from "../../lib/router";
import { dayTime } from "../../lib/time";
import { plural, words } from "../../lib/copy";
import type { Payload } from "../../types";

const SEEN_KEY = "joule.notifications.seenAt";
const DAY = 86400000;

export interface NotificationItem {
  id: string;
  icon: LucideIcon;
  /** May be AI or server text (codes, keys, UTC times): render it through <PlainText>. */
  title: string;
  detail: string;
  href: string;
  /** Counts towards the badge until the bell is opened (findings) or until handled (suggestions, reports). */
  unseen: boolean;
  /** What kind of thing it is, as a small chip: "Needs you", "Found something", "Didn't finish", "Report". */
  kind: string;
  /** warn for a check that didn't finish; accent otherwise. */
  tone: "accent" | "warn";
}

/**
 * Everything that wants your attention, from every part of Joule: what needs you (suggestions, to-dos, file edits and
 * trials due a decision, as one row whose count is the Insights badge's), new findings and checks that didn't finish
 * (since you last looked), and unread reports. Each links straight to it.
 */
export function notificationItems(data: Payload, seenAt: number, now = Date.now()): NotificationItem[] {
  const s = data.state;
  const items: NotificationItem[] = [];
  const waiting = needsYou(s, now);
  if (waiting.length) {
    const count = (kind: string) => waiting.filter((i) => i.kind === kind).length;
    const parts = [
      count("proposal") && plural(count("proposal"), words.suggestion),
      count("todo") && plural(count("todo"), words.todo, words.todos),
      count("file") && plural(count("file"), words.fileEdit),
      count("trial") && plural(count("trial"), "trial to decide", "trials to decide"),
    ].filter(Boolean);
    items.push({
      id: "needs-you",
      icon: ListChecks,
      title: `Needs you (${waiting.length})`,
      detail: parts.join(", "),
      href: buildHash("insights", "suggestions"),
      unseen: true,
      kind: "Needs you",
      tone: "accent",
    });
  }
  for (const inv of [...s.investigations].sort((a, b) => Date.parse(b.at) - Date.parse(a.at))) {
    const at = Date.parse(inv.at);
    if (!(now - at < DAY)) break;
    const status = (inv.status || "Completed").toLowerCase();
    if (status === "failed" || status === "interrupted")
      items.push({
        id: `failed-${inv.id}`,
        icon: AlertCircle,
        title: headlineOf(inv) || `The ${words.aiCheck} ${words.didntFinish.toLowerCase()}`,
        detail: dayTime(inv.at),
        href: buildHash("insights", "", { id: inv.id }),
        unseen: at > seenAt,
        kind: words.didntFinish,
        tone: "warn",
      });
    else if (status === "completed" && !inv.dismissedAt && investigationVerdict(inv) !== "no_change")
      items.push({
        id: `finding-${inv.id}`,
        icon: Sparkles,
        title: headlineOf(inv) || "New finding",
        detail: dayTime(inv.at),
        href: buildHash("insights", "", { id: inv.id }),
        unseen: at > seenAt,
        kind: words.foundSomething,
        tone: "accent",
      });
    if (items.length > 12) break;
  }
  for (const n of s.notifications.filter((n) => !n.readAt).slice(0, 5))
    items.push({
      id: `report-${n.id}`,
      icon: FileText,
      title: n.title,
      detail: dayTime(n.at),
      href: buildHash("energy", "reports", {}, new URLSearchParams({ report: n.reportId })),
      unseen: true,
      kind: "Report",
      tone: "accent",
    });
  return items;
}

function readSeen() {
  const v = Number(localStorage.getItem(SEEN_KEY));
  return Number.isFinite(v) ? v : 0;
}

export function NotificationsBell({ data }: { data: Payload }) {
  const [seenAt, setSeenAt] = useState(readSeen);
  const items = notificationItems(data, seenAt);
  const count = items.filter((i) => i.unseen).length;
  return (
    <Popover
      label="Notifications"
      onOpenChange={(open) => {
        // Findings count as seen once the list has been opened.
        if (!open && seenAt !== readSeen()) setSeenAt(readSeen());
        if (open) localStorage.setItem(SEEN_KEY, String(Date.now()));
      }}
      trigger={(props) => (
        <button
          type="button"
          className="icon-button"
          aria-label={count ? `Notifications, ${count} new` : "Notifications"}
          {...props}
        >
          <Bell size={20} aria-hidden="true" />
          {count > 0 && (
            <span className="dot-count" aria-hidden="true">
              {count > 9 ? "9+" : count}
            </span>
          )}
        </button>
      )}
    >
      <h2>Notifications</h2>
      {items.length ? (
        <ul className="notification-list">
          {items.map((i) => {
            const Icon = i.icon;
            return (
              <li key={i.id} className={`tone-${i.tone}`} data-kind={i.id.split("-")[0]}>
                <a href={i.href}>
                  <Icon size={18} aria-hidden="true" />
                  <span className="notification-head">
                    <strong>
                      <PlainText text={i.title} />
                    </strong>
                    {/* The Needs you row's title already names its kind. */}
                    {i.id !== "needs-you" && <em className="notification-kind">{i.kind}</em>}
                  </span>
                  <span className="notification-detail">
                    <PlainText text={i.detail} />
                  </span>
                </a>
              </li>
            );
          })}
        </ul>
      ) : (
        <EmptyState quiet title="Nothing needs you right now" />
      )}
    </Popover>
  );
}
