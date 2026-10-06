import { useLayoutEffect, useRef, type ReactNode } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import * as SwitchPrimitive from "@radix-ui/react-switch";
import { X } from "lucide-react";
import { Button } from "./Button";

/**
 * A dialog. On desktop a centred card; under 700px a bottom sheet with a sticky header (and a 44px close button) and,
 * when `footer` is given (or the content ends with .dialog-actions), a sticky footer for the actions.
 * Closing returns keyboard focus to whatever opened it, so a long page doesn't lose your place.
 */
export function Modal({
  open,
  onOpenChange,
  title,
  description,
  children,
  footer,
}: {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  title: string;
  description: string;
  children: ReactNode;
  footer?: ReactNode;
}) {
  const opener = useRef<HTMLElement | null>(null);
  // Remember the opener before the dialog moves focus inside itself.
  useLayoutEffect(() => {
    if (open && document.activeElement instanceof HTMLElement && document.activeElement !== document.body)
      opener.current = document.activeElement;
  }, [open]);
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="overlay" />
        <Dialog.Content
          className="modal"
          onCloseAutoFocus={(e) => {
            const target = opener.current;
            if (target && target.isConnected) {
              e.preventDefault();
              target.focus();
            }
            opener.current = null;
          }}
        >
          <div className="modal-heading">
            <div>
              <Dialog.Title>{title}</Dialog.Title>
              <Dialog.Description>{description}</Dialog.Description>
            </div>
            <Dialog.Close asChild>
              <Button variant="ghost" className="modal-close" aria-label="Close dialog">
                <X size={20} />
              </Button>
            </Dialog.Close>
          </div>
          {children}
          {footer && <div className="modal-footer">{footer}</div>}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

export function Switch({
  checked,
  onCheckedChange,
  label,
  disabled = false,
  id,
}: {
  checked: boolean;
  onCheckedChange: (v: boolean) => void;
  label: string;
  disabled?: boolean;
  id?: string;
}) {
  return (
    <SwitchPrimitive.Root
      id={id}
      className="switch"
      checked={checked}
      onCheckedChange={onCheckedChange}
      aria-label={label}
      disabled={disabled}
    >
      <SwitchPrimitive.Thumb className="switch-thumb" />
    </SwitchPrimitive.Root>
  );
}
