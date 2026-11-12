import { t, lang, type Key } from "../i18n";
import type { View } from "../components/Nav";

/** Phase 2.1: the frame is new, the pages come in 2.2–2.4. Until then they point to the current dashboard. */
export function Placeholder(props: { view: View; phase: string }) {
  return (
    <section class="view">
      <h1>{t(`nav.${props.view}` as Key)}</h1>
      <div class="panel">
        <p class="muted" style={{ margin: 0 }}>
          {lang() === "da"
            ? `Denne side flyttes til det nye dashboard i fase ${props.phase}. Indtil da: `
            : `This page moves to the new dashboard in phase ${props.phase}. Until then: `}
          <a href={`/#${props.view}`}>{lang() === "da" ? "det nuværende dashboard" : "the current dashboard"}</a>
        </p>
      </div>
    </section>
  );
}
