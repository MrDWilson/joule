import type { ReactNode } from "react";
import "./plan-components.css";
import {
  ArrowLeftRight,
  ArrowUpRight,
  BatteryCharging,
  BatteryFull,
  Car,
  CircleHelp,
  House,
  Pause,
  Snowflake,
  Sun,
  type LucideIcon,
} from "lucide-react";
import { Hint } from "../Hint";
import { cn } from "@/lib/utils";
import type { ActionView } from "./windows";

export const actionIcons: Record<string, LucideIcon> = {
  demand: House,
  charge: BatteryCharging,
  "freeze-charge": Snowflake,
  "hold-charge": Pause,
  "no-charge": BatteryFull,
  export: ArrowUpRight,
  "freeze-export": Sun,
  "hold-export": Pause,
  "charge-export": ArrowLeftRight,
  "hold-for-car": Car,
  unknown: CircleHelp,
};
export const iconFor = (a: ActionView) => actionIcons[a.id] ?? actionIcons[a.key] ?? CircleHelp;

/** What an action means, for its Hint: the glossary's description plus the battery's expected behaviour. */
export function ActionMeaning({ action }: { action: ActionView }) {
  return (
    <>
      <strong>{action.label}.</strong> {action.description}
      {action.battery && <> {action.battery}</>}
      {action.reason && (
        <>
          <br />
          <span className="muted">Predbat: {action.reason}</span>
        </>
      )}
    </>
  );
}

/**
 * A short plan action at 12px with its target ("Charge → 100%"), coloured by the glossary tone, and one Hint that
 * explains it (never a title attribute, so touch and keyboard users can read it too).
 */
export function ActionBadge({
  action,
  text,
  tone,
  hint = true,
  className,
  children,
}: {
  action: ActionView;
  /** The badge text; defaults to the action's label. */
  text: string;
  tone?: string;
  hint?: boolean;
  className?: string;
  children?: ReactNode;
}) {
  const Icon = iconFor(action);
  return (
    <span className={cn("plan-badge-wrap", className)}>
      <span className={cn("plan-badge", `tone-${tone ?? action.tone}`)}>
        <Icon size={13} aria-hidden="true" />
        {text}
      </span>
      {children}
      {hint && (
        <Hint label={`What does “${text}” mean?`}>
          <ActionMeaning action={action} />
        </Hint>
      )}
    </span>
  );
}
