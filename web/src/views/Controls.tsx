import { createSignal, For, Show, type JSX } from "solid-js";
import { t, type Key } from "../i18n";
import { val, pending, busy, chargingNow } from "../state/selection";
import { settings } from "../state/settings";
import { send } from "../api/commands";
import { hm, nf } from "../lib/format";
import { Icon } from "../components/Icon";
import { CarTiles } from "../components/CarTiles";

const LVL = ["off", "low", "medium", "high"] as const;

/**
 * A value you step with − / + (temperature, amps): shown at once, sent after a short pause so several taps
 * become one command, and kept until the car has had time to report it back.
 */
function useDraft(key: string, cmd: string) {
  const [draft, setDraft] = createSignal<number | null>(null);
  let timer: ReturnType<typeof setTimeout> | undefined;
  const set = (next: number, delay = 650) => {
    setDraft(next);
    clearTimeout(timer);
    timer = setTimeout(async () => {
      await send(cmd, next);
      setTimeout(() => setDraft(null), 1500);
    }, delay);
  };
  const value = () => draft() ?? val<number>(key);
  const waiting = () => draft() != null || pending(key);
  return { value, waiting, set };
}

function Chip(props: { label: string; active?: boolean; pending?: boolean; onClick: () => void }) {
  return (
    <button type="button" class="chip" classList={{ pending: !!props.pending }} aria-pressed={!!props.active} onClick={props.onClick}>
      {props.label}
    </button>
  );
}

function Card(props: { class: string; title: string; sub?: JSX.Element; action?: JSX.Element; children: JSX.Element; on?: boolean }) {
  return (
    <section class={`card ${props.class}`} classList={{ on: !!props.on }}>
      <header>
        <div><h2>{props.title}</h2><Show when={props.sub}><span class="muted">{props.sub}</span></Show></div>
        {props.action}
      </header>
      {props.children}
    </section>
  );
}

function Climate() {
  const on = () => !!val("climate_on");
  const temp = useDraft("driver_temp_setting", "driver_temp");
  const inside = () => val<number>("inside_temp");
  const mode = () => (on() && (temp.value() ?? 21) < (inside() ?? 20) ? "cool" : "heat");
  const keeper = () => val<string>("climate_keeper_mode") ?? "off";
  const cop = () => val<string>("cabin_overheat_protection");
  const copT = () => val<number>("cop_temp_limit");
  const step = (d: number) => temp.set(Math.min(28, Math.max(15, Math.round(((temp.value() ?? 21) + d) * 2) / 2)));
  return (
    <section class={`card climate ${mode()}`} classList={{ on: on() }}>
      <header>
        <div><h2>{t("c.climate")}</h2><span class="muted">{t("f.inside")} {inside() ?? "–"}° · {t("f.outside")} {val("outside_temp") ?? "–"}°</span></div>
        <button type="button" class="power" classList={{ pending: busy("climate_on") }} aria-pressed={on()}
          aria-label={on() ? t("q.climateOff") : t("q.climateOn")} onClick={() => void send("climate", on() ? "off" : "on")}>
          <Icon name="power" />
        </button>
      </header>
      <div class="temp">
        <button type="button" class="round" aria-label="−0.5 °C" onClick={() => step(-0.5)}>−</button>
        <div class="tv num" classList={{ pending: temp.waiting() }}>
          {temp.value() == null ? "–" : nf(temp.value()!, temp.value()! % 1 ? 1 : 0)}<small>°C</small>
        </div>
        <button type="button" class="round" aria-label="+0.5 °C" onClick={() => step(0.5)}>+</button>
      </div>
      <div class="sect"><div class="lbl">{t("c.keeper")}</div><div class="chips">
        <For each={[["off", t("lvl.off")], ["keep", t("c.keep")], ["dog", t("c.dog")], ["camp", t("c.camp")]]}>{([k, l]) => (
          <Chip label={l} active={keeper() === k} pending={pending("climate_keeper_mode") && keeper() === k} onClick={() => void send("climate_keeper", k)} />
        )}</For>
      </div></div>
      <div class="sect"><div class="lbl">{t("c.defrost")}</div><div class="chips">
        <Chip label={t("off")} active={val("defrost") !== true} onClick={() => void send("defrost", "off")} />
        <Chip label={t("on")} active={val("defrost") === true} pending={pending("defrost")} onClick={() => void send("defrost", "on")} />
      </div></div>
      <div class="sect"><div class="lbl">{t("c.cop")}</div><div class="chips">
        <For each={[["off", t("lvl.off")], ["on", t("on")], ["fan_only", t("c.fanOnly")]]}>{([k, l]) => (
          <Chip label={l} active={cop() === k} pending={pending("cabin_overheat_protection") && cop() === k} onClick={() => void send("cabin_overheat_protection", k)} />
        )}</For>
        <span style={{ width: "8px" }} />
        <For each={[30, 35, 40]}>{(c) => (
          <Chip label={`${c}°`} active={copT() === c} pending={pending("cop_temp_limit") && copT() === c} onClick={() => void send("cop_temp", c)} />
        )}</For>
      </div></div>
    </section>
  );
}

function SeatButton(props: { label: string; key: string; cmd: string }) {
  const v = () => val<number>(props.key) ?? 0;
  const next = () => (v() === 0 ? 3 : v() - 1);   // like the car: high → medium → low → off
  return (
    <button type="button" class="seat" classList={{ lit: v() > 0, pending: busy(props.key) }}
      aria-label={`${props.label}: ${t(`lvl.${LVL[v()]}` as Key)}`} onClick={() => void send(props.cmd, LVL[next()])}>
      <Icon name="seat" />
      <span class="bars"><For each={[1, 2, 3]}>{(i) => <i classList={{ on: v() >= i }} />}</For></span>
      <span>{props.label}</span>
    </button>
  );
}

function WheelButton() {
  const v = () => { const raw = val<number>("steering_wheel_heat_level"); return raw == null || raw === 0 ? 0 : raw === 1 ? 1 : 3; };
  return (
    <button type="button" class="seat" classList={{ lit: v() > 0, pending: busy("steering_wheel_heat_level") }} aria-label={t("c.wheel")}
      onClick={() => void send("steering_wheel_heater", v() === 0 ? "high" : v() === 3 ? "low" : "off")}>
      <Icon name="wheel" />
      <span class="bars"><i classList={{ on: v() >= 1 }} /><i classList={{ on: v() >= 3 }} /></span>
      <span>{t("c.wheel")}</span>
    </button>
  );
}

function Seats() {
  const hint = () => (val("climate_on") === false
    ? t(settings()?.climate_for_seat_heat === false ? "c.seatsClimateOff" : "c.seatsNeedClimate")
    : t("c.seatsHint"));
  const auto = (label: string, key: string, cmd: string) => (
    <Chip label={label} active={!!val(key)} pending={pending(key)} onClick={() => void send(cmd, val(key) ? "off" : "on")} />
  );
  return (
    <Card class="seats" title={t("c.seats")} sub={hint()}>
      <div class="seatmap">
        <div class="row front">
          <SeatButton label={t("c.seatFL")} key="seat_heater_left" cmd="seat_heater_left" />
          <SeatButton label={t("c.seatFR")} key="seat_heater_right" cmd="seat_heater_right" />
        </div>
        <div class="row">
          <SeatButton label={t("c.seatRL")} key="seat_heater_rear_left" cmd="seat_heater_rear_left" />
          <SeatButton label={t("c.seatRC")} key="seat_heater_rear_center" cmd="seat_heater_rear_center" />
          <SeatButton label={t("c.seatRR")} key="seat_heater_rear_right" cmd="seat_heater_rear_right" />
        </div>
        <div class="row"><WheelButton /></div>
      </div>
      <div class="sect"><div class="lbl">{t("c.auto")}</div><div class="chips">
        {auto(t("c.autoL"), "auto_seat_climate_left", "auto_seat_climate_left")}
        {auto(t("c.autoR"), "auto_seat_climate_right", "auto_seat_climate_right")}
        {auto(t("c.wheelAuto"), "auto_steering_wheel_heat", "auto_steering_wheel_heater")}
      </div></div>
    </Card>
  );
}

function Charging() {
  const plugged = () => !!val("charge_cable_connected");
  const amps = useDraft("charge_current_request", "charge_current");
  const limit = useDraft("charge_limit", "charge_limit");
  const [sliding, setSliding] = createSignal<number | null>(null);
  const shownLimit = () => sliding() ?? limit.value();
  const power = () => (val<number>("ac_charging_power") ?? 0) + (val<number>("dc_charging_power") ?? 0);
  const status = (): JSX.Element => {
    const cs = val<string>("charging_state");
    if (cs === "charging") return <><span class="state on">{t("charging")}</span>{power() ? ` · ${nf(power(), 1)} kW` : ""}{val("time_to_full_charge") ? ` · ${hm(val<number>("time_to_full_charge"))}` : ""}</>;
    if (cs === "starting") return <span class="state on">{t("charging.starting")}</span>;
    if (cs === "complete") return t("charging.done");
    return plugged() ? t("charging.plugged") : t("charging.unplugged");
  };
  const stepAmps = (d: number) => amps.set(Math.min(32, Math.max(5, (amps.value() ?? 16) + d)));
  return (
    <Card class="charge" title={t("c.charging")} sub={status()} action={
      <Show when={plugged() || chargingNow()}>
        <button type="button" class={`cta ${chargingNow() ? "stop" : "go"}`} onClick={() => void send("charge", chargingNow() ? "off" : "on")}>
          {chargingNow() ? t("c.stopCharge") : t("c.startCharge")}
        </button>
      </Show>
    }>
      <div class="charge-head">
        <span class="big num">{val<number>("battery_level") != null ? Math.round(val<number>("battery_level")!) : "–"}<small> %</small></span>
        <span class="muted">{val("est_range") != null ? t("range", nf(val<number>("est_range")!)) : ""}</span>
      </div>
      <div class="sect">
        <div class="lbl">{t("c.limit")} <b classList={{ st: limit.waiting(), pending: limit.waiting() }}>{shownLimit() ?? "–"} %</b></div>
        <input type="range" class="range" min="50" max="100" step="1" value={shownLimit() ?? 80} aria-label={t("c.limit")}
          style={{ "--p": `${(((shownLimit() ?? 80) - 50) / 50) * 100}%` }}
          onInput={(e) => setSliding(Number(e.currentTarget.value))}
          onChange={(e) => { limit.set(Number(e.currentTarget.value), 0); setSliding(null); }} />
        <div class="range-scale"><span>50 %</span><span>{t("c.daily")}</span><span>{t("c.trip")}</span><span>100 %</span></div>
      </div>
      <div class="sect" style={{ display: "flex", "align-items": "center", "justify-content": "space-between", gap: "12px", "flex-wrap": "wrap" }}>
        <div class="lbl" style={{ margin: 0 }}>{t("c.amps")}</div>
        <div class="stepper">
          <button type="button" class="round" aria-label="−1 A" onClick={() => stepAmps(-1)}>−</button>
          <span class="v" classList={{ st: amps.waiting(), pending: amps.waiting() }}>{amps.value() ?? "–"}<small>A</small></span>
          <button type="button" class="round" aria-label="+1 A" onClick={() => stepAmps(1)}>+</button>
        </div>
      </div>
      <div class="sect"><div class="chips">
        <Chip label={val("charge_port_open") ? t("q.portClose") : t("q.port")} active={!!val("charge_port_open")} pending={busy("charge_port_open")}
          onClick={() => void send("charge_port_door", val("charge_port_open") ? "CLOSE" : "OPEN")} />
        <Show when={plugged()}><Chip label={t("c.unlockCable")} onClick={() => void send("charge_cable_lock", "UNLOCK")} /></Show>
      </div></div>
    </Card>
  );
}

export function Controls() {
  return (
    <section class="view">
      <h1>{t("nav.controls")}</h1>
      <p class="lede">{t("controls.lede")}</p>
      <div class="cgrid">
        <Climate />
        <Seats />
        <Charging />
        <Card class="cartiles" title={t("c.car")} sub={t("c.carHint")}>
          <div class="tiles"><CarTiles /></div>
        </Card>
      </div>
    </section>
  );
}
