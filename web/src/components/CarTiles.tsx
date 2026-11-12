import { Show } from "solid-js";
import { Tile } from "./Tile";
import { t } from "../i18n";
import { val, busy, motion } from "../state/selection";
import { send, sendConfirmed } from "../api/commands";
import { toast } from "./Toast";

const onOff = (on: unknown) => (on ? t("on") : t("off"));

/** The car's quick actions: compact on the Car page, everything on Controls. */
export function CarTiles(props: { compact?: boolean }) {
  const locked = () => val<boolean>("locked");
  const lockState = () => {
    const m = motion("locked");
    return m === "LOCKING" ? t("locking") : m === "UNLOCKING" ? t("unlocking") : locked() === false ? t("q.lock") : t("q.unlock");
  };
  const toggle = (key: string, cmd: string) => () => void send(cmd, val(key) ? "off" : "on");

  return (
    <>
      <Tile icon={locked() === false ? "unlock" : "lock"} label={locked() === false ? t("unlocked") : t("locked")} state={lockState()}
        active={locked() === false} kind="open" pending={busy("locked")}
        onClick={() => (locked() === false ? void send("lock", "LOCK") : sendConfirmed(t("confirm.unlock"), "lock", "UNLOCK"))} />
      <Show when={props.compact}>
        <Tile icon="fan" label={t("c.climateOn")} state={onOff(val("climate_on"))} active={!!val("climate_on")}
          pending={busy("climate_on")} onClick={toggle("climate_on", "climate")} />
      </Show>
      <Tile icon="frunk" label={t("c.frunk")} state={val("frunk_open") ? t("car.open") : t("c.open")} active={!!val("frunk_open")} kind="open"
        pending={busy("frunk_open")}
        onClick={() => (val("frunk_open") ? toast(t("frunkManual")) : sendConfirmed(t("confirm.frunk"), "frunk", "OPEN"))} />
      <Tile icon="trunk" label={t("c.trunk")} state={val("trunk_open") ? t("c.close") : t("c.open")} active={!!val("trunk_open")} kind="open"
        pending={busy("trunk_open")}
        onClick={() => (val("trunk_open") ? void send("trunk", "CLOSE") : sendConfirmed(t("confirm.trunkOpen"), "trunk", "OPEN"))} />
      <Tile icon="plug" label={t("c.port")} state={val("charge_port_open") ? t("c.close") : t("c.open")} active={!!val("charge_port_open")} kind="open"
        pending={busy("charge_port_open")} onClick={() => void send("charge_port_door", val("charge_port_open") ? "CLOSE" : "OPEN")} />
      <Show when={!props.compact}>
        <Tile icon="window" label={t("c.windows")} state={val("windows_open") ? t("c.close") : t("c.vent")} active={!!val("windows_open")} kind="open"
          pending={busy("windows_open")} onClick={() => void send("windows", val("windows_open") ? "CLOSE" : "OPEN")} />
        <Tile icon="shield" label={t("c.sentry")} state={onOff(val("sentry_mode"))} active={!!val("sentry_mode")}
          pending={busy("sentry_mode")} onClick={toggle("sentry_mode", "sentry_mode")} />
        <Tile icon="home" label={t("c.homelink")} state={t("c.press")} onClick={() => void send("homelink")} />
        <Tile icon="guest" label={t("c.guest")} state={onOff(val("guest_mode"))} active={!!val("guest_mode")}
          pending={busy("guest_mode")} onClick={toggle("guest_mode", "guest_mode")} />
        <Tile icon="valet" label={t("c.valet")} state={onOff(val("valet_mode"))} active={!!val("valet_mode")}
          pending={busy("valet_mode")} onClick={toggle("valet_mode", "valet_mode")} />
        <Tile icon="key" label={t("c.keyless")} state={t("c.press")} onClick={() => void send("keyless_driving")} />
        <Tile icon="cloud" label={t("c.fart")} state={t("c.press")} onClick={() => void send("play_fart")} />
      </Show>
      <Tile icon="sun" label={t("c.flash")} state={props.compact ? "" : t("c.press")} onClick={() => void send("flash_lights")} />
      <Tile icon="horn" label={t("c.honk")} state={props.compact ? "" : t("c.press")} onClick={() => void send("honk_horn")} />
      <Show when={!props.compact || val("online") !== true}>
        <Tile icon="bolt" label={t("c.wake")} state={props.compact ? "" : val("online") ? t("car.awake") : t("c.press")}
          onClick={() => void send("wake")} />
      </Show>
    </>
  );
}
