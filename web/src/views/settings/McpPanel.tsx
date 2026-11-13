import { createSignal, For, Show } from "solid-js";
import { t, type Key } from "../../i18n";
import { sendJson } from "../../api/client";
import { setSettings, settings, type McpSettings } from "../../state/settings";
import { toast } from "../../components/Toast";

/**
 * Settings → AI assistants (MCP): on/off, whether commands that open the car are allowed, and the MCP token – shown
 * once when it's made, then only its hash is kept.
 */
export function McpPanel() {
  const mcp = () => settings()?.mcp;
  const [token, setToken] = createSignal<string | null>(null);
  const url = () => `${location.origin}/mcp`;
  const shown = () => token() ?? "<token>";

  const save = (m: McpSettings) => setSettings({ ...settings()!, mcp: m });
  const change = async (patch: { enabled?: boolean; policy?: McpSettings["policy"]; allowed_commands?: string[] }) => {
    try {
      save(await sendJson<McpSettings>("/api/mcp-settings", "PUT", patch));
      toast(t("mcp.saved"));
    } catch (e) { toast((e as Error).message); }
  };
  const newToken = async () => {
    if (mcp()?.token_set && !confirm(t("mcp.replaceConfirm"))) return;
    try {
      const r = await sendJson<{ token: string }>("/api/mcp-token", "POST");
      setToken(r.token);
      save({ ...mcp()!, token_set: true });
    } catch (e) { toast((e as Error).message); }
  };
  const revoke = async () => {
    if (!confirm(t("mcp.revokeConfirm"))) return;
    try {
      await sendJson("/api/mcp-token", "DELETE");
      setToken(null);
      save({ ...mcp()!, token_set: false });
      toast(t("mcp.revoked"));
    } catch (e) { toast((e as Error).message); }
  };
  const setPolicy = (policy: McpSettings["policy"], el: HTMLSelectElement) => {
    if (policy === "all" && !confirm(t("mcp.securityConfirm"))) { el.value = mcp()!.policy; return; }
    // Custom starts from what the current choice allows, so it's a small step from there.
    const allowed = policy === "custom" && mcp()!.policy !== "custom"
      ? mcp()!.commands.filter((c) => c.allowed).map((c) => c.id) : undefined;
    void change({ policy, allowed_commands: allowed });
  };
  const toggle = (id: string, on: boolean, security: boolean, el: HTMLInputElement) => {
    if (on && security && !confirm(t("mcp.securityOneConfirm", t(`mcpcmd.${id}` as Key)))) { el.checked = false; return; }
    const set = new Set(mcp()!.allowed_commands);
    if (on) set.add(id); else set.delete(id);
    void change({ allowed_commands: [...set] });
  };
  const groups = () => {
    const by = new Map<string, McpSettings["commands"]>();
    for (const c of mcp()!.commands) by.set(c.group, [...(by.get(c.group) ?? []), c]);
    const order = ["climate", "charging", "access", "car"];
    return [...by.entries()].sort(([a], [b]) => order.indexOf(a) - order.indexOf(b));
  };
  const copy = async (text: string) => {
    try { await navigator.clipboard.writeText(text); toast(t("mcp.copied")); } catch { /* the text can be selected by hand */ }
  };

  return (
    <Show when={mcp()}>{(m) => (
      <div class="panel mcp">
        <h2>{t("mcp.title")}</h2>
        <p class="sub">{t("mcp.sub")}</p>
        <div class="mcp-row">
          <label>
            <span>{t("mcp.status")}</span>
            <select value={m().enabled ? "on" : "off"} onChange={(e) => change({ enabled: e.currentTarget.value === "on" })}>
              <option value="off">{t("mcp.off")}</option>
              <option value="on">{t("mcp.on")}</option>
            </select>
          </label>
          <label>
            <span>{t("mcp.commands")}</span>
            <select value={m().policy} onChange={(e) => setPolicy(e.currentTarget.value as McpSettings["policy"], e.currentTarget)}>
              <option value="read_only">{t("mcp.policy.read_only")}</option>
              <option value="safe">{t("mcp.policy.safe")}</option>
              <option value="all">{t("mcp.policy.all")}</option>
              <option value="custom">{t("mcp.policy.custom")}</option>
            </select>
          </label>
        </div>
        <p class="small muted">{t(`mcp.policySub.${m().policy}` as Key)}</p>
        <Show when={m().policy === "custom"}>
          <div class="mcp-cmds">
            <For each={groups()}>{([group, cmds]) => (
              <fieldset>
                <legend>{t(`mcpgroup.${group}` as Key)}</legend>
                <For each={cmds}>{(c) => (
                  <label class="mcp-cmd" classList={{ sec: c.security }}>
                    <input type="checkbox" checked={c.allowed}
                      onChange={(e) => toggle(c.id, e.currentTarget.checked, c.security, e.currentTarget)} />
                    <span>{t(`mcpcmd.${c.id}` as Key)}</span>
                    <Show when={c.security}><span class="mcp-sec" title={t("mcp.securityHint")}>{t("mcp.securityTag")}</span></Show>
                  </label>
                )}</For>
              </fieldset>
            )}</For>
          </div>
        </Show>

        <Show when={m().enabled}>
          <h3>{t("mcp.token")}</h3>
          <Show when={token()} fallback={
            <p class="small">{m().token_set ? t("mcp.tokenSet") : t("mcp.tokenNone")}</p>}>
            <p class="small">{t("mcp.tokenOnce")}</p>
            <div class="mcp-token">
              <code>{token()}</code>
              <button type="button" onClick={() => void copy(token()!)}>{t("mcp.copy")}</button>
            </div>
          </Show>
          <div class="mcp-actions">
            <button type="button" class={m().token_set ? "" : "primary"} onClick={() => void newToken()}>
              {m().token_set ? t("mcp.newToken") : t("mcp.makeToken")}
            </button>
            <Show when={m().token_set}>
              <button type="button" class="danger" onClick={() => void revoke()}>{t("mcp.revoke")}</button>
            </Show>
          </div>

          <h3>{t("mcp.connect")}</h3>
          <p class="small">{t("mcp.connectSub")}</p>
          <p class="small muted">Claude Code</p>
          <pre>{`claude mcp add --transport http tesla-bridge ${url()} \\\n  --header "Authorization: Bearer ${shown()}"`}</pre>
          <p class="small muted">{t("mcp.desktop")}</p>
          <pre>{JSON.stringify({ mcpServers: { "tesla-bridge": { command: "npx", args: ["-y", "mcp-remote", url(), "--header", `Authorization: Bearer ${shown()}`] } } }, null, 2)}</pre>
        </Show>
      </div>
    )}</Show>
  );
}
