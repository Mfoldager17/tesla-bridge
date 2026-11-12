import { For } from "solid-js";

/** Tab buttons (the panes are the caller's – show the one that matches `value`). */
export function Tabs<T extends string>(props: { tabs: [T, string][]; value: T; onChange: (tab: T) => void }) {
  return (
    <div class="tabs" role="tablist">
      <For each={props.tabs}>{([id, label]) => (
        <button role="tab" type="button" aria-selected={props.value === id} onClick={() => props.onChange(id)}>{label}</button>
      )}</For>
    </div>
  );
}
