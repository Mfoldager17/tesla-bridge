import { createSignal, Show } from "solid-js";

const [message, setMessage] = createSignal<string | null>(null);
let timer: ReturnType<typeof setTimeout> | undefined;

export function toast(text: string): void {
  setMessage(text);
  clearTimeout(timer);
  timer = setTimeout(() => setMessage(null), 4500);
}

export function Toast() {
  return <Show when={message()}>{(m) => <div class="toast" role="status">{m()}</div>}</Show>;
}
