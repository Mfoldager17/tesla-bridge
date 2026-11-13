import { createSignal } from "solid-js";

// The dashboard never keeps the token: it trades it once for an HttpOnly session cookie (POST /api/session), which
// the browser then sends by itself – also with the live event stream, the QR code and the Tesla sign-in link.

/** True once the bridge has said the token is missing or wrong. */
const [needsLogin, setNeedsLogin] = createSignal(false);
export { needsLogin };

/** Signs in with the token. Throws ApiError(401) when it's wrong. */
export async function login(token: string): Promise<void> {
  const res = await fetch("/api/session", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ token }),
  });
  if (!res.ok) throw new ApiError(res.status, res.status === 401 ? "wrong token" : String(res.status));
  setNeedsLogin(false);
}

export async function logout(): Promise<void> {
  await fetch("/api/session", { method: "DELETE" });
  setNeedsLogin(true);
}

/**
 * Before 1.2 the token came in the address (?token=… from install.sh) and was kept in localStorage. Either is traded
 * for a session cookie once, and then removed.
 */
export async function migrateOldToken(): Promise<void> {
  const url = new URL(location.href);
  let old = url.searchParams.get("token");
  if (old) {
    url.searchParams.delete("token");
    history.replaceState(null, "", url.pathname + url.search + url.hash);
  }
  try {
    old ??= localStorage.getItem("tb_token");
    localStorage.removeItem("tb_token");
  } catch { /* no storage */ }
  if (old) await login(old).catch(() => undefined);
}

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

/** fetch against the bridge (the session cookie goes along); 401 shows the login. */
export async function api(path: string, init: RequestInit = {}): Promise<Response> {
  const res = await fetch(path, init);
  if (res.status === 401) {
    setNeedsLogin(true);
    throw new ApiError(401, "unauthorized");
  }
  return res;
}

export async function getJson<T>(path: string): Promise<T> {
  const res = await api(path);
  if (!res.ok) throw new ApiError(res.status, `${path}: ${res.status}`);
  return (await res.json()) as T;
}

export async function sendJson<T>(path: string, method: "POST" | "PUT" | "DELETE", body?: unknown): Promise<T> {
  const res = await api(path, {
    method,
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  // Errors are RFC 9457 problem details with the message in both "detail" and "error".
  const data = (await res.json().catch(() => ({}))) as T & { error?: string; detail?: string };
  if (!res.ok) throw new ApiError(res.status, data.error ?? data.detail ?? String(res.status));
  return data;
}

/** The live event stream. */
export const EVENTS_URL = "/api/events";
