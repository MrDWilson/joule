import { useState } from "react";
import {
  AlertCircle,
  Bell,
  FileText,
  ListChecks,
  Smartphone,
  Sparkles,
  WifiOff,
  X,
  type LucideIcon,
} from "lucide-react";
import { Popover } from "../ui/Popover";
import { Button } from "../ui/Button";
import { PlainText } from "../PlainText";
import { api } from "../../lib/api";
import { plainError } from "../../lib/errors";
import { textParts } from "../../lib/sanitize";
import { dayTime } from "../../lib/time";
import { bellItems, isUnread, noPending as none, type PendingActions } from "../../lib/inbox";
import type { Payload } from "../../types";

const icons: Record<string, LucideIcon> = {
  needs_you: ListChecks,
  problem: Sparkles,
  unfinished: AlertCircle,
  offline: WifiOff,
  report: FileText,
};

/** The title as words, for a button's accessible name ("Dismiss: Lower the reserve"). */
const spoken = (text: string) =>
  textParts(text)
    .map((p) => p.value)
    .join("");

/**
 * Everything that wants your attention, from every part of Joule: each suggestion, to-do, file edit and trial due a decision,
 * new findings, checks that keep not finishing, sensors or Predbat offline, and new reports. Opening an item marks it read;
 * each can be dismissed, and the panel can mark everything read or clear it. All of it is kept on the server, so it sticks
 * across browsers and restarts. Anything handled elsewhere in Joule drops out of the count by itself.
 */
export function NotificationsBell({ data, onChange }: { data: Payload; onChange: () => Promise<void> | void }) {
  const [pending, setPending] = useState<PendingActions>(none);
  const [error, setError] = useState("");
  const { items, unread } = bellItems(data.state.inbox, pending);

  async function act(path: string, optimistic: (p: PendingActions) => PendingActions) {
    setError("");
    setPending(optimistic);
    try {
      await api(path, {});
      await onChange();
    } catch (e) {
      setError(plainError(e));
    }
    // The fresh state now carries the change (or it failed and the list goes back as it was).
    setPending(none);
  }
  const read = (id: string) => act(`/inbox/${encodeURIComponent(id)}/read`, (p) => ({ ...p, read: [...p.read, id] }));
  const dismiss = (id: string) =>
    act(`/inbox/${encodeURIComponent(id)}/dismiss`, (p) => ({ ...p, dismissed: [...p.dismissed, id] }));

  return (
    <Popover
      label="Notifications"
      className="notifications-popover"
      trigger={(props) => (
        <button
          type="button"
          className="icon-button"
          aria-label={unread ? `Notifications, ${unread} unread` : "Notifications"}
          {...props}
        >
          <Bell size={20} aria-hidden="true" />
          {unread > 0 && (
            <span className="dot-count" aria-hidden="true">
              {unread > 9 ? "9+" : unread}
            </span>
          )}
        </button>
      )}
    >
      <div className="notifications-head">
        <h2>Notifications</h2>
        {items.length > 0 && (
          <span className="notifications-actions">
            <Button
              variant="ghost"
              size="sm"
              disabled={!unread}
              onClick={() => act("/inbox/read-all", (p) => ({ ...p, allRead: true }))}
            >
              Mark all as read
            </Button>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => act("/inbox/dismiss-all", (p) => ({ ...p, allDismissed: true }))}
            >
              Clear all
            </Button>
          </span>
        )}
      </div>
      {error && (
        <p className="sheet-warning" role="alert">
          {error}
        </p>
      )}
      {items.length ? (
        <ul className="notification-list">
          {items.map((i) => {
            const Icon = icons[i.event] ?? Sparkles;
            const unseen = isUnread(i);
            return (
              <li
                key={i.id}
                className={`tone-${i.tone}${unseen ? " unread" : ""}${i.open ? "" : " handled"}`}
                data-kind={i.event}
              >
                <a href={i.link} onClick={() => void (i.readAt ? null : read(i.id))}>
                  <Icon size={18} aria-hidden="true" />
                  <span className="notification-head">
                    <strong>
                      {unseen && <span className="sr-only">Unread: </span>}
                      <PlainText text={i.title} />
                    </strong>
                    <em className="notification-kind">{i.open ? i.label : "Done"}</em>
                  </span>
                  <span className="notification-detail">
                    {i.detail && (
                      <>
                        <PlainText text={i.detail} />
                        {" · "}
                      </>
                    )}
                    {dayTime(i.at)}
                  </span>
                </a>
                <button
                  type="button"
                  className="notification-dismiss"
                  aria-label={`Dismiss: ${spoken(i.title)}`}
                  onClick={() => void dismiss(i.id)}
                >
                  <X size={16} aria-hidden="true" />
                </button>
              </li>
            );
          })}
        </ul>
      ) : (
        <p className="notifications-empty">Nothing new. Suggestions, findings and alerts show up here.</p>
      )}
      <a className="notifications-phone" href="#/setup/notifications">
        <Smartphone size={15} aria-hidden="true" /> Get these on your phone
      </a>
    </Popover>
  );
}
