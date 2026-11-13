import { createSignal, Show } from "solid-js";
import { t } from "../i18n";
import { ApiError, login } from "../api/client";

export function Login(props: { onDone: () => void }) {
  let input!: HTMLInputElement;
  const [error, setError] = createSignal("");
  const [busy, setBusy] = createSignal(false);
  const submit = async (e: SubmitEvent) => {
    e.preventDefault();
    setBusy(true);
    setError("");
    try {
      await login(input.value.trim());
      props.onDone();
    } catch (err) {
      setError(err instanceof ApiError && err.status === 401 ? t("login.wrong") : String((err as Error).message));
      input.select();
    } finally { setBusy(false); }
  };
  return (
    <div class="panel login">
      <h1>{t("login.title")}</h1>
      <p class="muted small">{t("login.text")}</p>
      <form onSubmit={submit}>
        <input ref={input} type="password" autocomplete="current-password" required autofocus aria-invalid={!!error()} />
        <button class="primary" type="submit" disabled={busy()}>{t("login.button")}</button>
      </form>
      <Show when={error()}><p class="login-error" role="alert">{error()}</p></Show>
    </div>
  );
}
