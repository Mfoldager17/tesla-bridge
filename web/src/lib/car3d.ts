// The car in 3D on the Car page (three.js). Loaded only when it's shown, as its own chunk.
//
// Any glTF model shows; it animates when its parts have these names:
//   nodes      door_fl, door_fr, door_rl, door_rr   (rotate around Y to open)
//              wheel_fl, wheel_fr, wheel_rl, wheel_rr (spin around X when driving)
//   materials  paint, jamb (body colour) · seats, trim2 (interior colour)
//   axes       front of the car towards +Z, driver's side (left) towards +X, wheels on Y = 0, metres
import * as T from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { RoomEnvironment } from "three/addons/environments/RoomEnvironment.js";
import { MeshoptDecoder } from "three/addons/libs/meshopt_decoder.module.js";

type Side = "fl" | "fr" | "rl" | "rr";
const SIDES: Side[] = ["fl", "fr", "rl", "rr"];

/** [name, colour, metalness, roughness] */
export const PAINTS: [name: string, color: string, metalness: number, roughness: number][] = [
  ["white", "#e9ecef", 0.35, 0.28], ["grey", "#4b5057", 0.55, 0.3], ["silver", "#b4b8bc", 0.75, 0.25],
  ["black", "#0e1013", 0.45, 0.22], ["red", "#8a0d14", 0.5, 0.25], ["blue", "#24476e", 0.55, 0.27],
];
export const INTERIORS: [name: string, color: string][] = [["black", "#16181c"], ["white", "#e4dfd7"]];

export interface Car3DState {
  doors: Record<Side, boolean>;
  open: Record<"frunk" | "trunk" | "port" | Side, boolean>;
  windows?: Record<Side, boolean>;
  charging: boolean;
  speed: number;
  reverse: boolean;
  climate: "heat" | "cool" | null;
}

export interface Car3D {
  update(state: Car3DState): void;
  setLabels(labels: Record<string, string>): void;
  setColors(colors: { paint: string; interior: string }): void;
  dispose(): void;
}

interface Hotspot {
  id: keyof Car3DState["open"];
  /** position on the car (metres, model space) */
  pos: [number, number, number];
  /** the way it faces – it hides when that side is turned away */
  nrm: [number, number, number];
  /** tapping it opens the frunk, trunk or charge port */
  click?: boolean;
}

const HOTSPOTS: Hotspot[] = [
  { id: "frunk", pos: [0, 0.95, 1.9], nrm: [0, 1, 0.4], click: true },
  { id: "trunk", pos: [0, 1.02, -2.12], nrm: [0, 0.4, -1], click: true },
  { id: "port", pos: [0.94, 0.92, -1.95], nrm: [1, 0, 0], click: true },
  { id: "fl", pos: [0.97, 0.82, 0.35], nrm: [1, 0, 0] },
  { id: "rl", pos: [0.97, 0.82, -0.75], nrm: [1, 0, 0] },
  { id: "fr", pos: [-0.97, 0.82, 0.35], nrm: [-1, 0, 0] },
  { id: "rr", pos: [-0.97, 0.82, -0.75], nrm: [-1, 0, 0] },
];

const DOOR_OPEN = (55 * Math.PI) / 180;
const reducedMotion = () => matchMedia("(prefers-reduced-motion: reduce)").matches;

/** A soft radial gradient on a canvas – the shadow under the car and the glow in the cabin. */
function radial(size: number, stops: [number, string][]): HTMLCanvasElement {
  const c = document.createElement("canvas");
  c.width = c.height = size;
  const g = c.getContext("2d")!;
  const r = size / 2;
  const gr = g.createRadialGradient(r, r, 0, r, r, r);
  for (const [at, color] of stops) gr.addColorStop(at, color);
  g.fillStyle = gr;
  g.fillRect(0, 0, size, size);
  return c;
}

/**
 * Shows the model in wrap (position: relative, with a size) and keeps it animated until
 * dispose(). Hotspot labels are HTML elements laid over the canvas.
 */
export async function mount(
  wrap: HTMLElement,
  glb: ArrayBuffer,
  opts: { labels: Record<string, string>; onHotspot?: (id: string) => void },
): Promise<Car3D> {
  const renderer = new T.WebGLRenderer({ antialias: true, alpha: true });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 1.75));
  renderer.toneMapping = T.NeutralToneMapping;
  renderer.toneMappingExposure = 1.05;
  renderer.outputColorSpace = T.SRGBColorSpace;
  wrap.prepend(renderer.domElement);

  const scene = new T.Scene();
  scene.environment = new T.PMREMGenerator(renderer).fromScene(new RoomEnvironment(), 0.04).texture;
  const camera = new T.PerspectiveCamera(24, 1, 0.1, 100);
  const controls = new OrbitControls(camera, renderer.domElement);
  Object.assign(controls, {
    enableDamping: true, enablePan: false, minPolarAngle: 0.44, maxPolarAngle: 1.54,
    autoRotate: !reducedMotion(), autoRotateSpeed: -0.55,
  });
  controls.target.set(0, 0.75, 0);
  // Turning stops when you drag the car, and starts again after six quiet seconds.
  let idleSince = 0;
  controls.addEventListener("start", () => { controls.autoRotate = false; idleSince = 0; });
  controls.addEventListener("end", () => { idleSince = performance.now(); });

  const shadow = new T.Mesh(
    new T.PlaneGeometry(3.1, 6),
    new T.MeshBasicMaterial({
      map: new T.CanvasTexture(radial(256, [[0.04, "rgba(0,0,0,.6)"], [0.55, "rgba(0,0,0,.35)"], [1, "rgba(0,0,0,0)"]])),
      transparent: true, depthWrite: false,
    }),
  );
  shadow.rotation.x = -Math.PI / 2;
  shadow.position.y = 0.003;
  scene.add(shadow);

  // A warm or cool glow in the cabin while the climate is on.
  const glow = new T.Sprite(new T.SpriteMaterial({
    map: new T.CanvasTexture(radial(128, [[0, "rgba(255,255,255,1)"], [0.4, "rgba(255,255,255,.45)"], [1, "rgba(255,255,255,0)"]])),
    color: 0xff8a3c, transparent: true, opacity: 0, blending: T.AdditiveBlending, depthWrite: false,
  }));
  glow.scale.set(1.9, 1.1, 1);
  glow.position.set(0, 1.05, -0.15);
  scene.add(glow);
  const cabin = new T.PointLight(0xff8a3c, 0, 3.2, 1.6);
  cabin.position.set(0, 1.1, -0.1);
  scene.add(cabin);

  const loader = new GLTFLoader();
  loader.setMeshoptDecoder(MeshoptDecoder);
  const car = (await loader.parseAsync(glb, "")).scene;
  const materials = new Map<string, Set<T.MeshStandardMaterial>>();
  car.traverse((o) => {
    if (!(o instanceof T.Mesh)) return;
    const m = o.material as T.MeshStandardMaterial;
    if (m.transparent) { m.envMap = scene.environment; m.envMapIntensity = 0.12; }
    if (!materials.has(m.name)) materials.set(m.name, new Set());
    materials.get(m.name)!.add(m);
  });
  scene.add(car);
  const eachMaterial = (name: string, f: (m: T.MeshStandardMaterial) => void) => materials.get(name)?.forEach(f);

  const doors = Object.fromEntries(SIDES.map((k) => [k, car.getObjectByName("door_" + k)])) as Record<Side, T.Object3D | undefined>;
  const wheels = SIDES.map((k) => car.getObjectByName("wheel_" + k)).filter((w): w is T.Object3D => !!w);
  const doorAngle: Record<Side, number> = { fl: 0, fr: 0, rl: 0, rr: 0 };

  const hotspots = HOTSPOTS.map((h) => {
    const el = document.createElement(h.click ? "button" : "div");
    el.className = "hs" + (h.click ? "" : " door") + (h.id === "port" ? " port" : "");
    if (el instanceof HTMLButtonElement) {
      el.type = "button";
      el.onclick = () => opts.onHotspot?.(h.id);
    }
    el.append(document.createElement("span"));
    wrap.append(el);
    return { ...h, el };
  });
  const setLabels = (labels: Record<string, string>) => {
    for (const h of hotspots) {
      const label = labels[h.id] ?? "";
      h.el.querySelector("span")!.textContent = label;
      if (h.click) h.el.setAttribute("aria-label", label);
    }
  };
  setLabels(opts.labels);

  // Frame the whole car whatever the shape of the box.
  const fit = (): number | null => {
    const w = wrap.clientWidth, h = wrap.clientHeight;
    if (!w || !h) return null;
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    const tan = Math.tan((12 * Math.PI) / 180);
    const distance = Math.max(2.75 / (tan * camera.aspect), 1.55 / tan);
    camera.updateProjectionMatrix();
    controls.minDistance = distance * 0.62;
    controls.maxDistance = distance * 1.5;
    return distance;
  };
  const resize = new ResizeObserver(() => fit());
  resize.observe(wrap);
  {
    const d = fit() ?? 6.5, theta = -0.6, phi = 1.26;
    camera.position.set(d * Math.sin(phi) * Math.sin(theta), 0.75 + d * Math.cos(phi), d * Math.sin(phi) * Math.cos(theta));
    controls.update();
  }
  wrap.classList.add("ready");

  let state: Car3DState | null = null;
  let spin = 0, glowLevel = 0, last = performance.now();
  const heat = new T.Color(0xff8a3c), cool = new T.Color(0x5fc5ff);
  const v = new T.Vector3(), n = new T.Vector3(), toCamera = new T.Vector3();

  renderer.setAnimationLoop(() => {
    const now = performance.now(), dt = Math.min(0.05, (now - last) / 1000);
    last = now;
    if (!wrap.offsetParent || document.hidden) return;   // not on screen: don't spend the GPU
    if (!controls.autoRotate && idleSince && now - idleSince > 6000 && !reducedMotion()) controls.autoRotate = true;

    for (const k of SIDES) {
      const door = doors[k];
      if (!door) continue;
      doorAngle[k] += ((state?.doors[k] ? DOOR_OPEN : 0) - doorAngle[k]) * Math.min(1, dt * 3.2);
      door.rotation.y = (k.endsWith("l") ? -1 : 1) * doorAngle[k];
    }
    // km/h → wheel turns (0.36 m radius), capped so it stays a wheel and not a blur
    spin += Math.min(Math.max(0, state?.speed ?? 0) / 3.6 / 0.36, 7) * dt * (state?.reverse ? -1 : 1);
    for (const w of wheels) w.rotation.x = spin;

    glowLevel += ((state?.climate ? 1 : 0) - glowLevel) * Math.min(1, dt * 1.5);
    const color = state?.climate === "cool" ? cool : heat, pulse = 0.78 + 0.22 * Math.sin(now / 650);
    cabin.color.copy(color);
    glow.material.color.copy(color);
    cabin.intensity = glowLevel * 5 * pulse;
    glow.material.opacity = glowLevel * 0.75 * pulse;

    controls.update();
    renderer.render(scene, camera);

    // Hotspots follow the car, and hide on the side turned away from you.
    const w = wrap.clientWidth, h = wrap.clientHeight;
    for (const hs of hotspots) {
      v.set(...hs.pos);
      n.set(...hs.nrm).normalize();
      toCamera.copy(camera.position).sub(v).normalize();
      const visible = n.dot(toCamera) > 0.05;
      v.project(camera);
      hs.el.style.transform = `translate(${(v.x * 0.5 + 0.5) * w - 11}px,${(-v.y * 0.5 + 0.5) * h - 11}px)`;
      hs.el.classList.toggle("hid", !visible);
    }
  });

  return {
    update(s) {
      state = s;
      for (const hs of hotspots) {
        hs.el.classList.toggle("open", !!s.open[hs.id]);
        if (hs.id === "port") hs.el.classList.toggle("charging", s.charging);
      }
    },
    setLabels,
    setColors({ paint, interior }) {
      const p = PAINTS.find((x) => x[0] === paint) ?? PAINTS[0];
      for (const name of ["paint", "jamb"]) {
        eachMaterial(name, (m) => { m.color.set(p[1]); m.metalness = p[2]; m.roughness = p[3]; });
      }
      const white = interior === "white";
      eachMaterial("seats", (m) => { m.color.set(white ? "#e4dfd7" : "#16181c"); m.roughness = white ? 0.55 : 0.62; });
      eachMaterial("trim2", (m) => m.color.set(white ? "#d6d1c9" : "#30343b"));
    },
    dispose() {
      renderer.setAnimationLoop(null);
      resize.disconnect();
      renderer.dispose();
      for (const h of hotspots) h.el.remove();
      renderer.domElement.remove();
    },
  };
}
