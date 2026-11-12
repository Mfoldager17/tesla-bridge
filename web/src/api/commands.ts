import { api } from "./client";
import { vin } from "../state/selection";
import { toast } from "../components/Toast";
import { t } from "../i18n";

/**
 * Sends a command to the shown car, like MQTT's tesla/<VIN>/cmd/<name> (same names and payloads).
 * Answers straight away; the result arrives as a "command" event and the state as field events.
 */
export async function send(name: string, payload: string | number = "PRESS"): Promise<void> {
  const v = vin();
  if (!v) return;
  try {
    const r = await api(`/api/vehicles/${v}/cmd/${name}`, { method: "POST", body: String(payload) });
    if (!r.ok) {
      const err = (await r.json().catch(() => ({}))) as { error?: string };
      toast(t("err.cmd", name, err.error ?? r.status));
    }
  } catch (e) {
    if ((e as Error).message !== "unauthorized") toast(t("err.cmd", name, (e as Error).message));
  }
}

/** Asks first (unlocking, opening the frunk …). */
export function sendConfirmed(question: string, name: string, payload: string | number = "PRESS"): void {
  if (confirm(question)) void send(name, payload);
}
