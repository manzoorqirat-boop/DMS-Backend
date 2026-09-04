import { useEffect, useId, useState, type ReactNode } from "react";
import { AlertTriangle, Loader2, PenLine } from "lucide-react";

interface SignatureDialogProps {
  open: boolean;
  title: string;
  description: ReactNode;
  /** What the signature asserts, shown above the password field. §11.50(a)(3). */
  meaning: string;
  confirmLabel: string;
  destructive?: boolean;
  /** When set, a reason is collected and required. */
  reasonLabel?: string;
  reasonPlaceholder?: string;
  /** Shown when the action will queue for a countersignature rather than take effect. */
  awaitsCountersignature?: boolean;
  onConfirm: (password: string, reason: string) => Promise<void>;
  onCancel: () => void;
}

/**
 * Collects an electronic signature: the password, and where required a reason.
 *
 * Separate from ConfirmDialog because a signature is not a confirmation. §11.200 treats the
 * signing credential as something applied deliberately per act — being logged in is not
 * signing, or an unattended workstation could issue controlled copies. So this always asks for
 * the password, states what the signature means, and never remembers it between uses.
 *
 * The password is held in component state and cleared on close. It is never logged, never put
 * in a URL, and never stored.
 */
export function SignatureDialog({
  open,
  title,
  description,
  meaning,
  confirmLabel,
  destructive = false,
  reasonLabel,
  reasonPlaceholder,
  awaitsCountersignature = false,
  onConfirm,
  onCancel,
}: SignatureDialogProps) {
  const [password, setPassword] = useState("");
  const [reason, setReason] = useState("");
  const [isPending, setIsPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const passwordId = useId();
  const reasonId = useId();

  // Cleared on every open so a previous attempt's credential never lingers in a mounted
  // component, and so an error from last time doesn't greet the next person.
  useEffect(() => {
    if (open) {
      setPassword("");
      setReason("");
      setError(null);
      setIsPending(false);
    }
  }, [open]);

  useEffect(() => {
    if (!open) return;
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape" && !isPending) onCancel();
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [open, isPending, onCancel]);

  if (!open) return null;

  async function handleConfirm() {
    if (!password) {
      setError("Your password is required to sign.");
      return;
    }
    if (reasonLabel && !reason.trim()) {
      setError("A reason is required.");
      return;
    }

    setIsPending(true);
    setError(null);
    try {
      await onConfirm(password, reason.trim());
      setPassword("");
    } catch (err) {
      setError(err instanceof Error ? err.message : "That action could not be completed.");
      setIsPending(false);
      // Password deliberately kept on failure: a mistyped reason shouldn't cost someone their
      // credential entry, and the backend records the failed attempt either way.
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-ink-950/50 p-4">
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="signature-dialog-title"
        className="w-full max-w-md rounded-xl border border-border bg-surface-raised p-6 shadow-xl"
      >
        <div className="flex items-start gap-3">
          <span
            className={`mt-0.5 flex h-8 w-8 flex-none items-center justify-center rounded-full ${
              destructive ? "bg-danger-tint" : "bg-brand-tint"
            }`}
          >
            <PenLine
              className={`h-4 w-4 ${destructive ? "text-danger" : "text-brand"}`}
              aria-hidden="true"
            />
          </span>
          <div className="min-w-0 flex-1">
            <h2 id="signature-dialog-title" className="font-display text-base font-semibold text-text-primary">
              {title}
            </h2>
            <div className="mt-1.5 text-sm leading-relaxed text-text-secondary">{description}</div>
          </div>
        </div>

        {awaitsCountersignature && (
          <p className="mt-4 rounded-[9px] border border-stage-review/30 bg-stage-review/10 px-3.5 py-2.5 text-[13px] leading-relaxed text-ink-900">
            This action needs a second person to authorise it. Signing here records your part —
            it will not take effect until someone else countersigns.
          </p>
        )}

        {/* §11.50(a)(3): the meaning of the signature, stated where it is applied. */}
        <p className="mt-4 rounded-[9px] border border-border bg-surface px-3.5 py-2.5 text-[13px] text-text-secondary">
          By signing you are recording: <span className="font-medium text-text-primary">{meaning}</span>
        </p>

        {reasonLabel && (
          <div className="mt-4">
            <label htmlFor={reasonId} className="mb-1.5 block text-[13px] font-semibold text-text-primary">
              {reasonLabel}
            </label>
            <textarea
              id={reasonId}
              rows={2}
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              placeholder={reasonPlaceholder}
              className="w-full rounded-[9px] border-[1.5px] border-border bg-surface-raised px-[13px] py-[9px] text-[14px] text-text-primary placeholder:text-text-tertiary focus:border-brand focus:outline-none focus:ring-[3px] focus:ring-brand-tint"
            />
          </div>
        )}

        <div className="mt-4">
          <label htmlFor={passwordId} className="mb-1.5 block text-[13px] font-semibold text-text-primary">
            Your password
          </label>
          <input
            id={passwordId}
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !isPending) handleConfirm();
            }}
            className="w-full rounded-[9px] border-[1.5px] border-border bg-surface-raised px-[13px] py-[9px] text-[14px] text-text-primary focus:border-brand focus:outline-none focus:ring-[3px] focus:ring-brand-tint"
          />
        </div>

        {error && (
          <p role="alert" className="mt-3 flex items-start gap-1.5 text-[13px] text-[#9c332f]">
            <AlertTriangle className="mt-0.5 h-3.5 w-3.5 flex-none" aria-hidden="true" />
            {error}
          </p>
        )}

        <div className="mt-5 flex justify-end gap-2">
          <button
            type="button"
            onClick={onCancel}
            disabled={isPending}
            className="rounded-lg border border-border px-3.5 py-2 text-sm font-medium text-text-secondary transition-colors hover:bg-surface disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={handleConfirm}
            disabled={isPending}
            className={`flex items-center gap-2 rounded-lg px-3.5 py-2 text-sm font-semibold text-white transition-colors disabled:opacity-60 ${
              destructive ? "bg-danger hover:bg-[#b93a3a]" : "bg-brand hover:bg-brand-hover"
            }`}
          >
            {isPending && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
