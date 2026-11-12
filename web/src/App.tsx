import { createEffect, createResource, createSignal, Match, onCleanup, onMount, Switch, type ParentProps } from "solid-js";
import { HashRouter, Route, Navigate } from "@solidjs/router";
import { getJson, needsLogin, eventsUrl } from "./api/client";
import type { CarState, Info, LiveEvent } from "./api/types";
import { addEvents, connect, setCars, setInfo, state } from "./state/live";
import { loadUpdate } from "./state/update";
import { Nav } from "./components/Nav";
import { Login } from "./components/Login";
import { Placeholder } from "./views/Placeholder";

const [vin, setVin] = createSignal<string>();
export { vin };

async function boot(): Promise<Info> {
  const info = await getJson<Info>("/api/info");
  setInfo(info);
  const cars = await getJson<CarState[]>("/api/state");
  setCars(cars);
  setVin(cars[0]?.vin);
  addEvents((await getJson<LiveEvent[]>("/api/events/recent")).reverse());
  return info;
}

function Shell(props: ParentProps) {
  const carName = () => (vin() ? state.cars[vin()!.toUpperCase()]?.name : undefined);
  return (
    <div class="shell">
      <Nav carName={carName()} />
      <div><main>{props.children}</main></div>
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
  // Live events once we're in (the token may have come from the login form)
  let stop: (() => void) | undefined;
  createEffect(() => {
    if (!ready()) return;
    stop?.();
    stop = connect(eventsUrl());
  });
  onCleanup(() => stop?.());

  return (
    <Switch>
      <Match when={needsLogin()}>
        <Login onDone={() => { void refetch(); }} />
      </Match>
      <Match when={ready.error}>
        <div class="panel login"><h1>Tesla Bridge</h1><p class="muted">{String(ready.error?.message ?? ready.error)}</p></div>
      </Match>
      <Match when={ready()}>
        <HashRouter root={Shell}>
          <Route path="/overview" component={() => <Placeholder view="overview" phase="2.2" />} />
          <Route path="/controls" component={() => <Placeholder view="controls" phase="2.2" />} />
          <Route path="/data" component={() => <Placeholder view="data" phase="2.3" />} />
          <Route path="/usage" component={() => <Placeholder view="usage" phase="2.3" />} />
          <Route path="/settings" component={() => <Placeholder view="settings" phase="2.4" />} />
          <Route path="/setup" component={() => <Placeholder view="setup" phase="2.4" />} />
          <Route path="*" component={() => <Navigate href="/overview" />} />
        </HashRouter>
      </Match>
    </Switch>
  );
}
