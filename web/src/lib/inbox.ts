import type { InboxItem } from "../types";

/**
 * The bell's list and badge from the server's inbox (src/Joule.Api/Notifications/NotificationInbox.cs). The server keeps
 * read and dismissed, and marks each item open while the thing it is about still needs you; this only decides what to show.
 */

/** Handled items stay in the list (muted) this long; the server keeps them two weeks. */
export const HANDLED_SHOWN_MS = 2 * 86400000;
export const MAX_SHOWN = 30;

/** What the person has done in the panel before the server's answer arrives, so the list responds at once. */
export interface PendingActions {
  read: string[];
  dismissed: string[];
  allRead?: boolean;
  allDismissed?: boolean;
}
export const noPending: PendingActions = { read: [], dismissed: [] };

/** Counts in the badge: still needs you, not read, not dismissed. */
export const isUnread = (i: InboxItem) => i.open && !i.readAt && !i.dismissedAt;

/**
 * Not dismissed, newest first, at most 30; handled items only for two days. The badge counts only open, unread items.
 * Pending actions apply on top.
 */
export function bellItems(inbox: InboxItem[] | undefined, pending: PendingActions = noPending, now = Date.now()) {
  const stamp = new Date(now).toISOString();
  const items = (inbox ?? [])
    .map((i) => ({
      ...i,
      readAt: i.readAt ?? (pending.allRead || pending.allDismissed || pending.read.includes(i.id) ? stamp : null),
      dismissedAt: i.dismissedAt ?? (pending.allDismissed || pending.dismissed.includes(i.id) ? stamp : null),
    }))
    .filter((i) => !i.dismissedAt && (i.open || now - Date.parse(i.resolvedAt ?? i.at) < HANDLED_SHOWN_MS))
    .sort((a, b) => Date.parse(b.at) - Date.parse(a.at))
    .slice(0, MAX_SHOWN);
  return { items, unread: items.filter(isUnread).length };
}
