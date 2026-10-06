import type { ButtonHTMLAttributes, AnchorHTMLAttributes } from "react";
import { Slot } from "@radix-ui/react-slot";
import { cva } from "class-variance-authority";
import { cn } from "@/lib/utils";

/**
 * Buttons. One rule for variants:
 *   primary   the single main action of a view or card ("Apply change", "Ask")
 *   secondary everything else that does something ("Download", "Refresh")
 *   ghost     tertiary and dismiss actions ("Not now", "Cancel")
 *   link      navigation that reads as text ("View full plan →")
 *   danger    anything that undoes, disconnects or deletes (pair it with a confirm)
 * Sizes: sm 32px, md 36px, lg 44px. On touch screens every size grows to a 44px hit area.
 * Disabled buttons use the raised surface and muted text (not an opacity dim), so their label stays readable.
 */
export type ButtonVariant = "primary" | "secondary" | "ghost" | "link" | "danger";
/** Older names still used by some components: default = primary, outline = secondary. */
type LegacyVariant = "default" | "outline";
export type ButtonSize = "sm" | "md" | "lg";

const canonical: Record<ButtonVariant | LegacyVariant, ButtonVariant> = {
  primary: "primary",
  default: "primary",
  secondary: "secondary",
  outline: "secondary",
  ghost: "ghost",
  link: "link",
  danger: "danger",
};

export const buttonVariants = cva("button", {
  variants: {
    variant: {
      primary: "button-primary",
      secondary: "button-secondary",
      ghost: "button-ghost",
      link: "button-link",
      danger: "button-danger",
    },
    size: { sm: "button-sm", md: "button-md", lg: "button-lg" },
  },
  defaultVariants: { variant: "primary", size: "md" },
});

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant | LegacyVariant | null;
  size?: ButtonSize;
  asChild?: boolean;
}

export function Button({ className = "", variant = "primary", size = "md", asChild = false, ...props }: ButtonProps) {
  const C = asChild ? Slot : "button";
  return <C className={cn(buttonVariants({ variant: canonical[variant ?? "primary"], size }), className)} {...props} />;
}

/** A link styled as a button (for navigation to a route): real <a href>, so it can be opened in a new tab. */
export function ButtonLink({
  className = "",
  variant = "secondary",
  size = "md",
  ...props
}: AnchorHTMLAttributes<HTMLAnchorElement> & { variant?: ButtonVariant; size?: ButtonSize }) {
  return <a className={cn(buttonVariants({ variant, size }), className)} {...props} />;
}
