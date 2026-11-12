import { createSignal, For, Show } from "solid-js";
import { t, locale, type Key } from "../../i18n";
import { api, sendJson } from "../../api/client";
import { loadModelInfo, modelInfo } from "../../state/carModel";
import { nf } from "../../lib/format";
import { toast } from "../../components/Toast";

/** Settings → Car view: the bundled 3D Model Y, your own .glb, or the drawing. */
export function CarModelPanel() {
  let file!: HTMLInputElement;
  const [uploading, setUploading] = createSignal(false);
  const setView = async (view: string) => {
    await sendJson("/api/settings", "PUT", { car_view: view, push: false });
    await loadModelInfo();
  };
  const upload = async (f: File) => {
    setUploading(true);
    try {
      const r = await api("/api/car-model", { method: "POST", headers: { "Content-Type": "model/gltf-binary" }, body: f });
      const j = (await r.json().catch(() => ({}))) as { error?: string };
      if (!r.ok) { toast(j.error ?? String(r.status)); return; }
      toast(t("model.uploaded"));
      await setView("custom");
    } finally { setUploading(false); file.value = ""; }
  };
  const remove = async () => {
    if (!confirm(t("confirm.removeModel"))) return;
    await api("/api/car-model", { method: "DELETE" });
    if (modelInfo()?.view === "custom") await setView("builtin"); else await loadModelInfo();
  };
  return (
    <div class="panel">
      <h2>{t("model.title")}</h2>
      <p class="sub">{t("model.sub")}</p>
      <Show when={modelInfo()}>{(m) => (
        <>
          <div class="views" role="radiogroup">
            <For each={[["builtin", m().builtin], ["custom", m().present], ["drawing", true]] as [string, boolean][]}>{([v, ok]) => (
              <button type="button" class="view-opt" role="radio" aria-checked={m().view === v} disabled={!ok && v !== "custom"}
                onClick={() => (v === "custom" && !m().present ? file.click() : void setView(v))}>
                <b>{t(`view.${v}` as Key)}</b>
                <span>{v === "custom" && !ok ? t("view.noCustom") : t(`view.${v}Sub` as Key)}</span>
              </button>
            )}</For>
          </div>
          <Show when={m().present}>
            <p class="small">{t("model.has", m().source === "uploaded" ? t("model.srcUploaded") : m().file ?? "",
              nf((m().size ?? 0) / 1048576, 1), m().updated_at ? new Date(m().updated_at!).toLocaleDateString(locale()) : "")}</p>
          </Show>
          <div class="toolbar">
            <label class="primary" style={{ cursor: "pointer" }}>
              <input ref={file} type="file" accept=".glb,model/gltf-binary" hidden onChange={(e) => e.currentTarget.files?.[0] && upload(e.currentTarget.files[0])} />
              <span class="btnlike">{uploading() ? t("model.uploading") : m().present ? t("model.replace") : t("model.upload")}</span>
            </label>
            <Show when={m().present && m().source === "uploaded"}><button type="button" class="danger" onClick={remove}>{t("model.remove")}</button></Show>
          </div>
          <details class="small muted"><summary>{t("model.howTitle")}</summary><p>{t("model.how1")}</p><p>{t("model.how2")}</p></details>
        </>
      )}</Show>
    </div>
  );
}
