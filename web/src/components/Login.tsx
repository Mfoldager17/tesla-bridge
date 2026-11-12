import { t } from "../i18n";
import { setToken } from "../api/client";

export function Login(props: { onDone: () => void }) {
  let input!: HTMLInputElement;
  return (
    <div class="panel login">
      <h1>{t("login.title")}</h1>
      <p class="muted small">{t("login.text")}</p>
      <form onSubmit={(e) => { e.preventDefault(); setToken(input.value.trim()); props.onDone(); }}>
        <input ref={input} type="password" autocomplete="current-password" required autofocus />
        <button class="primary" type="submit">{t("login.button")}</button>
      </form>
    </div>
  );
}
