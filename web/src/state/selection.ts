import { createSignal } from "solid-js";
import { isPending, value } from "./live";

/** The car shown (VIN). Several cars: picked in the menu. */
const [vin, setVin] = createSignal<string>();
export { vin, setVin };

/** Values of the shown car. */
export const val = <T = any>(key: string): T | undefined => value<T>(vin(), key); // eslint-disable-line @typescript-eslint/no-explicit-any
export const pending = (key: string) => isPending(vin(), key);
/** LOCKING / UNLOCKING / OPENING / CLOSING while the car carries out a lock/cover command */
export const motion = (key: string) => val<string>(`${key}_motion`);
/** Waiting for the car: optimistic value, or a lock/cover still moving. */
export const busy = (key: string) => pending(key) || /ING$/i.test(motion(key) ?? "");
export const chargingNow = () => ["charging", "starting"].includes(val<string>("charging_state") ?? "");
