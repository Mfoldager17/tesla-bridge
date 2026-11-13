import { createSignal, Show } from "solid-js";
import { t } from "../../i18n";
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
  const change = async (patch: { enabled?: boolean; allow_security_commands?: boolean }) => {
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
            <span>{t("mcp.security")}</span>
            <select value={m().allow_security_commands ? "yes" : "no"}
              onChange={(e) => {
                const yes = e.currentTarget.value === "yes";
                if (yes && !confirm(t("mcp.securityConfirm"))) { e.currentTarget.value = "no"; return; }
                void change({ allow_security_commands: yes });
              }}>
              <option value="no">{t("mcp.securityNo")}</option>
              <option value="yes">{t("mcp.securityYes")}</option>
            </select>
          </label>
        </div>
        <p class="small muted">{t("mcp.securitySub")}</p>

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
