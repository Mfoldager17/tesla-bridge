import { createSignal } from "solid-js";

const TOKEN_KEY = "tb_token";

function load(key: string): string | null {
  try { return localStorage.getItem(key); } catch { return null; }
}
function save(key: string, value: string): void {
  try { localStorage.setItem(key, value); } catch { /* private mode – the token lives for this tab only */ }
}

/** install.sh prints a link with ?token=… – remember it and take it out of the address bar. */
function tokenFromUrl(): string | null {
  const url = new URL(location.href);
  const q = url.searchParams.get("token");
  if (!q) return null;
  url.searchParams.delete("token");
  history.replaceState(null, "", url.pathname + url.search + url.hash);
  return q;
}

const initial = tokenFromUrl() ?? load(TOKEN_KEY) ?? "";
if (initial) save(TOKEN_KEY, initial);

const [token, setTokenSignal] = createSignal(initial);
/** True once the bridge has said the token is missing or wrong. */
const [needsLogin, setNeedsLogin] = createSignal(false);
export { token, needsLogin };

export function setToken(value: string): void {
  save(TOKEN_KEY, value);
  setTokenSignal(value);
  setNeedsLogin(false);
}

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

/** fetch against the bridge with the token; 401 shows the login. */
export async function api(path: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers);
  if (token()) headers.set("Authorization", `Bearer ${token()}`);
  const res = await fetch(path, { ...init, headers });
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
  const data = (await res.json().catch(() => ({}))) as T & { error?: string };
  if (!res.ok) throw new ApiError(res.status, data.error ?? String(res.status));
  return data;
}

/** The URL of the live event stream (EventSource can't send headers, so the token goes in the query). */
export function eventsUrl(): string {
  return "/api/events" + (token() ? `?token=${encodeURIComponent(token())}` : "");
}
