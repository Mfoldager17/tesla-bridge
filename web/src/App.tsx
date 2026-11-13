import { createEffect, createResource, Match, onCleanup, onMount, Switch, type ParentProps } from "solid-js";
import { HashRouter, Route, Navigate } from "@solidjs/router";
import { EVENTS_URL, getJson, migrateOldToken, needsLogin } from "./api/client";
import type { CarState, Info, LiveEvent } from "./api/types";
import { addEvents, connect, connected, setCars, setInfo, state } from "./state/live";
import { vin, setVin } from "./state/selection";
import { loadUpdate } from "./state/update";
import { loadModelInfo } from "./state/carModel";
import { loadSettings } from "./state/settings";
import { Nav, carStatus } from "./components/Nav";
import { Login } from "./components/Login";
import { Toast } from "./components/Toast";
import { Overview } from "./views/Overview";
import { Controls } from "./views/Controls";
import { Data } from "./views/Data";
import { Usage } from "./views/Usage";
import { Settings } from "./views/Settings";
import { Setup } from "./views/Setup";
import { loadSetup } from "./state/setup";

/** Everything the pages need; also run again when the live stream comes back after a break. */
async function loadState(): Promise<void> {
  const cars = await getJson<CarState[]>("/api/state");
  setCars(cars);
  if (!vin() || !cars.some((c) => c.vin === vin())) setVin(cars[0]?.vin);
}

async function boot(): Promise<Info> {
  await migrateOldToken();
  const info = await getJson<Info>("/api/info");
  setInfo(info);
  await loadState();
  addEvents((await getJson<LiveEvent[]>("/api/events/recent")).reverse());
  void loadModelInfo();
  void loadSettings();
  void loadSetup(false);
  return info;
}

function Shell(props: ParentProps) {
  const carName = () => (vin() ? state.cars[vin()!.toUpperCase()]?.name : undefined);
  return (
    <div class="shell">
      <Nav carName={carName()} />
      <div>
        <div class="mobile-top">
          <b>{carName() ?? "Tesla Bridge"}</b>
          <div class="conn-mini"><span class={`dot ${connected() ? "ok" : "bad"}`} /><span>{carStatus().text}</span></div>
        </div>
        <main>{props.children}</main>
      </div>
    </div>
  );
}

export function App() {
  const [ready, { refetch }] = createResource(boot);
  onMount(() => {
    void loadUpdate();
    const timer = setInterval(loadUpdate, 3 * 3600 * 1000);
    onCleanup(() => clearInterval(timer));
  });
  // Live events once we're in (maybe only after the login form)
  let stop: (() => void) | undefined;
  createEffect(() => {
    if (!ready()) return;
    stop?.();
    stop = connect(EVENTS_URL, () => { void loadState().catch(() => undefined); });
  });
  onCleanup(() => stop?.());

  return (
    <>
      <Switch>
        <Match when={needsLogin()}>
          <Login onDone={() => { void refetch(); }} />
        </Match>
        <Match when={ready.error}>
          <div class="panel login"><h1>Tesla Bridge</h1><p class="muted">{String(ready.error?.message ?? ready.error)}</p></div>
        </Match>
        <Match when={ready()}>
          <HashRouter root={Shell}>
            <Route path="/overview" component={Overview} />
            <Route path="/controls" component={Controls} />
            <Route path="/data" component={Data} />
            <Route path="/usage" component={Usage} />
            <Route path="/settings" component={Settings} />
            <Route path="/setup" component={Setup} />
            <Route path="*" component={() => <Navigate href="/overview" />} />
          </HashRouter>
        </Match>
      </Switch>
      <Toast />
    </>
  );
}
