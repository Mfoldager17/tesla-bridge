import { createSignal } from "solid-js";
import { api } from "../api/client";

/** GET /api/setup */
export interface SetupStatus {
  partner_domain: string;
  public_key_url: string;
  redirect_uri: string;
  pairing_url: string;
  credentials_set: boolean;
  key: { present: boolean; fingerprint: string | null; error: string | null; file: string };
  registration: { registered: boolean; matches: boolean; error: string | null; checked_at: string } | null;
  logged_in: boolean;
  vehicles: { vin: string; name: string; synced?: boolean; key_paired?: boolean; hostname?: string; error?: string }[];
}

const [setup, setSetup] = createSignal<SetupStatus | null>(null);
export { setup };

/** check = ask Tesla now (otherwise the remembered status, checked again by itself when it's old) */
export async function loadSetup(check = false): Promise<void> {
  try {
    const r = await api("/api/setup" + (check ? "?check=true" : ""));
    if (r.ok) setSetup((await r.json()) as SetupStatus);
  } catch { /* shown as unknown */ }
}

export type StepState = "done" | "bad" | "";

/** The six steps' states – used for the page and the count in the menu. */
export function stepStates(st: SetupStatus): StepState[] {
  const v = st.vehicles[0] ?? {};
  const reg = st.registration;
  return [
    st.key.present ? "done" : "bad",
    st.credentials_set ? "done" : "bad",
    !reg ? "" : reg.matches ? "done" : "bad",
    st.logged_in ? "done" : "",
    v.key_paired === true ? "done" : v.key_paired === false ? "bad" : "",
    v.synced === true ? "done" : v.error ? "bad" : "",
  ];
}
