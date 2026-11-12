import icons from "./icons.json";

export type IconName = keyof typeof icons;

/** Our own static line icons (icons.json, from the first dashboard). */
export function Icon(props: { name: IconName }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"
      aria-hidden="true" innerHTML={icons[props.name]} />
  );
}
