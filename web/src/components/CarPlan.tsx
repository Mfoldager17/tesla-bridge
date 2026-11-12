import { t } from "../i18n";
import { val, busy } from "../state/selection";

/** The drawing: the car from above, front to the right, with doors, windows, frunk, trunk, port and lock. */
export function CarPlan(props: { name?: string }) {
  const on = (key: string) => !!val(key);
  const locked = () => val<boolean>("locked");
  return (
    <svg viewBox="0 0 420 200" role="img">
      <title>{[props.name, locked() ? t("locked") : locked() === false ? t("unlocked") : ""].filter(Boolean).join(", ")}</title>
      <g class="wheel">
        <rect x="84" y="12" width="50" height="16" rx="6" /><rect x="84" y="172" width="50" height="16" rx="6" />
        <rect x="292" y="12" width="50" height="16" rx="6" /><rect x="292" y="172" width="50" height="16" rx="6" />
      </g>
      <rect class="body" x="20" y="22" width="380" height="156" rx="64" />
      <rect class="glass" x="268" y="44" width="40" height="112" rx="14" />
      <rect class="glass" x="150" y="44" width="112" height="112" rx="10" />
      <rect class="glass" x="102" y="50" width="40" height="100" rx="12" />
      <rect class="glass" classList={{ open: on("window_driver_front_open") }} x="212" y="34" width="54" height="7" rx="3" />
      <rect class="glass" classList={{ open: on("window_driver_rear_open") }} x="152" y="34" width="54" height="7" rx="3" />
      <rect class="glass" classList={{ open: on("window_passenger_front_open") }} x="212" y="159" width="54" height="7" rx="3" />
      <rect class="glass" classList={{ open: on("window_passenger_rear_open") }} x="152" y="159" width="54" height="7" rx="3" />
      <path class="part" classList={{ open: on("door_driver_front_open") }} d="M214 23 H 268" />
      <path class="part" classList={{ open: on("door_driver_rear_open") }} d="M150 23 H 206" />
      <path class="part" classList={{ open: on("door_passenger_front_open") }} d="M214 177 H 268" />
      <path class="part" classList={{ open: on("door_passenger_rear_open") }} d="M150 177 H 206" />
      <path class="part" classList={{ open: on("frunk_open") }} d="M340 34 Q 396 100 340 166" />
      <path class="part" classList={{ open: on("trunk_open") }} d="M78 34 Q 22 100 78 166" />
      <circle class="port" classList={{ open: on("charge_port_open") && !on("charge_cable_connected"), plugged: on("charge_cable_connected") }} cx="64" cy="40" r="6" />
      <g class="lock-badge" classList={{ unlocked: locked() === false, pending: busy("locked") }} transform="translate(206 100)">
        <circle r="19" />
        <path d={locked() === false ? "M-6 -2 V-7 a6 6 0 0 1 11 -3" : "M-6 -2 V-7 a6 6 0 0 1 12 0 V-2"} />
        <path d="M-8 -2 h16 v12 h-16 z" />
      </g>
    </svg>
  );
}
