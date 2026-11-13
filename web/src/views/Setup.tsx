import { createSignal, For, onCleanup, Show, type JSX } from "solid-js";
import { t } from "../i18n";
import { api } from "../api/client";
import { vin } from "../state/selection";
import { loadSetup, openBroker, setup, stepStates, type SetupStatus } from "../state/setup";
import { update } from "../state/update";
import { state } from "../state/live";
import { esc } from "../lib/format";

/** A button that shows "working…" while its action runs. */
function ActionButton(props: { label: string; primary?: boolean; disabled?: boolean; run: () => Promise<void> }) {
  const [busy, setBusy] = createSignal(false);
  return (
    <button type="button" class={props.primary ? "primary" : ""} disabled={props.disabled || busy()}
      onClick={async () => { setBusy(true); try { await props.run(); } finally { setBusy(false); } }}>
      {busy() ? t("working") : props.label}
    </button>
  );
}

function steps(st: SetupStatus): { title: string; detail: string; action?: JSX.Element }[] {
  const v = st.vehicles[0] ?? {};
  const reg = st.registration;
  const c = (s: string | null | undefined) => `<code>${esc(s)}</code>`;
  const link = (u: string) => `<a href="${esc(u)}" target="_blank" rel="noopener">${esc(u)}</a>`;
  const post = (path: string, err: "err.register" | "err.push") => async () => {
    const r = await api(path, { method: "POST" });
    if (!r.ok) { const j = (await r.json().catch(() => ({}))) as { error?: string }; alert(t(err, j.error ?? r.status)); }
    await loadSetup(true);
  };
  return [
    { title: t("s.key"), detail: st.key.present ? t("s.keyOk", c(st.key.fingerprint), link(st.public_key_url)) : t("s.keyMissing", c(st.key.file)) + (st.key.error ? " " + esc(st.key.error) : "") },
    { title: t("s.app"), detail: st.credentials_set ? t("s.appOk", c(st.partner_domain), c(st.redirect_uri)) : esc(t("s.appMissing")) },
    { title: t("s.reg"), detail: !reg ? t("s.regUnknown") : reg.error ? esc(reg.error) : reg.matches ? t("s.regOk") : reg.registered ? t("s.regOther") : t("s.regNo"),
      action: <ActionButton label={t("s.register")} disabled={!(st.key.present && st.credentials_set)} run={post("/api/setup/register", "err.register")} /> },
    { title: t("s.login"), detail: st.logged_in ? t("s.loginOk") : t("s.loginNo"),
      action: <a href="/api/auth/start" target="_blank" rel="noopener">
        <button type="button" class={st.logged_in ? "" : "primary"} disabled={!st.credentials_set}>{st.logged_in ? t("s.loginAgain") : t("s.loginBtn")}</button></a> },
    { title: t("s.pair"), detail: (v.key_paired === true ? t("s.pairOk") : t("s.pairNo")) + `<br>${link(st.pairing_url)}`
        + (v.key_paired === true ? "" : `<br><img class="qr" alt="QR" src="/api/setup/pair-qr.svg">`) },
    { title: t("s.tele"), detail: v.error ? esc(v.error) : v.synced === true ? t("s.teleOk", c(v.hostname ?? "")) : v.synced === false ? t("s.teleWait") : t("s.teleNone"),
      action: <ActionButton label={t("s.send")} disabled={!st.logged_in} run={post(`/api/vehicles/${vin()}/telemetry-config`, "err.push")} /> },
  ];
}

export function Setup() {
  void loadSetup(false);
  const onFocus = () => void loadSetup(false);
  window.addEventListener("focus", onFocus);
  onCleanup(() => window.removeEventListener("focus", onFocus));
  const version = () => update()?.installed_display ?? state.info?.version_display ?? state.info?.version ?? "?";
  return (
    <section class="view">
      <h1>{t("nav.setup")}</h1>
      <p class="lede">{t("setup.lede")}</p>
      <Show when={setup() && openBroker(setup()!)}>
        <div class="panel warnbox" role="alert">
          <h2>{t("mqttOpen.title")}</h2>
          <p innerHTML={t("mqttOpen.text")} />
        </div>
      </Show>
      <Show when={setup()}>{(st) => {
        const states = () => stepStates(st());
        const list = () => steps(st());
        return (
          <div class="panel">
            <div class="toolbar">
              <span class="muted">{t("setup.summary", states().filter((s) => s === "done").length, states().length)}</span>
              <span class="spacer" />
              <ActionButton label={t("setup.check")} run={() => loadSetup(true)} />
            </div>
            <ol class="steps">
              <For each={list()}>{(s, i) => (
                <li class={states()[i()]}>
                  <div class="n">{states()[i()] === "done" ? "✓" : states()[i()] === "bad" ? "!" : i() + 1}</div>
                  <div><div class="t">{s.title}</div><div class="d" innerHTML={s.detail} /></div>
                  <div class="a">{s.action}</div>
                </li>
              )}</For>
            </ol>
          </div>
        );
      }}</Show>
      <p class="small muted" style={{ "margin-top": "22px" }}>
        Tesla Bridge v{version()}
        <Show when={update()?.update_available}>
          {" · "}<a href={update()!.release_url ?? "https://github.com/Mfoldager17/tesla-bridge/releases"} target="_blank" rel="noopener">{t("ver.update", update()!.latest_version)}</a>
          {" – "}<code>docker compose pull && docker compose up -d</code>
        </Show>
      </p>
    </section>
  );
}
