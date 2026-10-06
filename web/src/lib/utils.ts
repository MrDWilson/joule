import { clsx } from "clsx";
import type { ClassValue } from "clsx";

/** Joins class names, skipping falsy ones. (Plain clsx: the app uses its own CSS, not Tailwind utility classes.) */
export function cn(...inputs: ClassValue[]) {
  return clsx(inputs);
}
