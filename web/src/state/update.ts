import { createSignal } from "solid-js";
import { getJson } from "../api/client";
import type { UpdateInfo } from "../api/types";

const [update, setUpdate] = createSignal<UpdateInfo | null>(null);
export { update };

export async function loadUpdate(): Promise<void> {
  try { setUpdate(await getJson<UpdateInfo>("/api/update")); } catch { /* shown again on the next try */ }
}
