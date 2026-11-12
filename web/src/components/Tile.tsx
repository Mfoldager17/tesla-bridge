import { Icon, type IconName } from "./Icon";

/** A square button: icon, label and state. Active is accent ("on") or warning ("open"); pending is purple. */
export function Tile(props: {
  icon: IconName;
  label: string;
  state?: string;
  active?: boolean;
  kind?: "on" | "open";
  pending?: boolean;
  onClick?: () => void;
}) {
  return (
    <button type="button" class="tile"
      classList={{ [props.kind ?? "on"]: !!props.active, pending: !!props.pending }}
      aria-pressed={!!props.active} onClick={() => props.onClick?.()}>
      <span class="ic"><Icon name={props.icon} /></span>
      <b>{props.label}</b>
      <small>{props.state || " "}</small>
    </button>
  );
}
