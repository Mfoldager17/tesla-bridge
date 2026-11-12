// 3D view of the car for the dashboard, using the owner's own .glb model (Settings → 3D model).
// Loaded only when a model exists. three.js is bundled under /vendor/three (MIT).
//
// The model works best with these names (all optional – any glTF shows, it just won't animate):
//   nodes      door_fl, door_fr, door_rl, door_rr   (rotate around Y to open)
//              wheel_fl, wheel_fr, wheel_rl, wheel_rr (spin around X when driving)
//   materials  paint, jamb (body colour) · seats, trim2 (interior colour)
//   axes       front of the car towards +Z, driver side (left) towards +X, wheels on Y = 0
import * as T from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { RoomEnvironment } from "three/addons/environments/RoomEnvironment.js";
import { MeshoptDecoder } from "three/addons/libs/meshopt_decoder.module.js";

export const PAINTS = [
  ["white", "#e9ecef", 0.35, 0.28], ["grey", "#4b5057", 0.55, 0.3], ["silver", "#b4b8bc", 0.75, 0.25],
  ["black", "#0e1013", 0.45, 0.22], ["red", "#8a0d14", 0.5, 0.25], ["blue", "#24476e", 0.55, 0.27],
];
export const INTERIORS = [["black", "#16181c"], ["white", "#e4dfd7"]];

// Hotspots: position on the car (metres, model space) and the direction they face, so they hide behind the car.
const HOTSPOTS = [
  { id: "frunk", pos: [0, 0.95, 1.9], nrm: [0, 1, 0.4], click: true },
  { id: "trunk", pos: [0, 1.02, -2.12], nrm: [0, 0.4, -1], click: true },
  { id: "port", pos: [0.94, 0.92, -1.95], nrm: [1, 0, 0], click: true },
  { id: "fl", pos: [0.97, 0.82, 0.35], nrm: [1, 0, 0] },
  { id: "rl", pos: [0.97, 0.82, -0.75], nrm: [1, 0, 0] },
  { id: "fr", pos: [-0.97, 0.82, 0.35], nrm: [-1, 0, 0] },
  { id: "rr", pos: [-0.97, 0.82, -0.75], nrm: [-1, 0, 0] },
];

/**
 * @param {HTMLElement} wrap        container (position: relative, has a size)
 * @param {ArrayBuffer} glb         the model
 * @param {{labels: Record<string,string>, onHotspot: (id:string)=>void}} opts
 */
export async function mount(wrap, glb, opts) {
  const R = new T.WebGLRenderer({ antialias: true, alpha: true });
  R.setPixelRatio(Math.min(devicePixelRatio, 1.75));
  R.toneMapping = T.NeutralToneMapping;
  R.toneMappingExposure = 1.05;
  R.outputColorSpace = T.SRGBColorSpace;
  wrap.prepend(R.domElement);

  const S = new T.Scene();
  S.environment = new T.PMREMGenerator(R).fromScene(new RoomEnvironment(), 0.04).texture;
  const C = new T.PerspectiveCamera(24, 1, 0.1, 100);
  const O = new OrbitControls(C, R.domElement);
  Object.assign(O, { enableDamping: true, enablePan: false, minPolarAngle: 0.44, maxPolarAngle: 1.54, autoRotate: !matchMedia("(prefers-reduced-motion: reduce)").matches, autoRotateSpeed: -0.55 });
  O.target.set(0, 0.75, 0);
  let idleSince = 0;
  O.addEventListener("start", () => { O.autoRotate = false; idleSince = 0; });
  O.addEventListener("end", () => { idleSince = performance.now(); });

  // soft shadow under the car
  const sc = document.createElement("canvas"); sc.width = sc.height = 256;
  { const g = sc.getContext("2d"), gr = g.createRadialGradient(128, 128, 10, 128, 128, 128);
    gr.addColorStop(0, "rgba(0,0,0,.6)"); gr.addColorStop(0.55, "rgba(0,0,0,.35)"); gr.addColorStop(1, "rgba(0,0,0,0)"); g.fillStyle = gr; g.fillRect(0, 0, 256, 256); }
  const shadow = new T.Mesh(new T.PlaneGeometry(3.1, 6), new T.MeshBasicMaterial({ map: new T.CanvasTexture(sc), transparent: true, depthWrite: false }));
  shadow.rotation.x = -Math.PI / 2; shadow.position.y = 0.003; S.add(shadow);

  // warm/cool glow in the cabin when climate is on
  const gc = document.createElement("canvas"); gc.width = gc.height = 128;
  { const g = gc.getContext("2d"), gr = g.createRadialGradient(64, 64, 0, 64, 64, 64);
    gr.addColorStop(0, "rgba(255,255,255,1)"); gr.addColorStop(0.4, "rgba(255,255,255,.45)"); gr.addColorStop(1, "rgba(255,255,255,0)"); g.fillStyle = gr; g.fillRect(0, 0, 128, 128); }
  const glow = new T.Sprite(new T.SpriteMaterial({ map: new T.CanvasTexture(gc), color: 0xff8a3c, transparent: true, opacity: 0, blending: T.AdditiveBlending, depthWrite: false }));
  glow.scale.set(1.9, 1.1, 1); glow.position.set(0, 1.05, -0.15); S.add(glow);
  const cabin = new T.PointLight(0xff8a3c, 0, 3.2, 1.6); cabin.position.set(0, 1.1, -0.1); S.add(cabin);

  const loader = new GLTFLoader(); loader.setMeshoptDecoder(MeshoptDecoder);
  const gltf = await loader.parseAsync(glb, "");
  const car = gltf.scene;
  car.traverse((o) => { if (o.isMesh && o.material.transparent) { o.material.envMap = S.environment; o.material.envMapIntensity = 0.12; } });
  S.add(car);

  const doors = {}, wheels = {}, ang = { fl: 0, fr: 0, rl: 0, rr: 0 };
  for (const k of ["fl", "fr", "rl", "rr"]) { doors[k] = car.getObjectByName("door_" + k); wheels[k] = car.getObjectByName("wheel_" + k); }
  const mats = {};
  car.traverse((o) => { if (o.isMesh) (mats[o.material.name] ||= new Set()).add(o.material); });
  const eachMat = (n, f) => mats[n]?.forEach(f);

  // hotspots
  const els = HOTSPOTS.map((h) => {
    const el = document.createElement(h.click ? "button" : "div");
    el.className = "hs" + (h.click ? "" : " door") + (h.id === "port" ? " port" : "");
    if (h.click) { el.type = "button"; el.onclick = () => opts.onHotspot?.(h.id); }
    el.innerHTML = "<span></span>";
    wrap.append(el);
    return { ...h, el };
  });
  const setLabels = (labels) => els.forEach((h) => { const l = labels[h.id] || ""; h.el.querySelector("span").textContent = l; if (h.click) h.el.setAttribute("aria-label", l); });
  setLabels(opts.labels || {});

  // camera fit
  const fit = () => {
    const w = wrap.clientWidth, h = wrap.clientHeight; if (!w || !h) return null;
    R.setSize(w, h, false); C.aspect = w / h;
    const tn = Math.tan((12 * Math.PI) / 180), d = Math.max(2.75 / (tn * C.aspect), 1.55 / tn);
    C.updateProjectionMatrix(); O.minDistance = d * 0.62; O.maxDistance = d * 1.5; return d;
  };
  const ro = new ResizeObserver(fit); ro.observe(wrap);
  { const d = fit() || 6.5, th = -0.6, ph = 1.26; C.position.set(d * Math.sin(ph) * Math.sin(th), 0.75 + d * Math.cos(ph), d * Math.sin(ph) * Math.cos(th)); O.update(); }
  wrap.classList.add("ready");

  let state = {}, spin = 0, glowK = 0, t0 = performance.now();
  const OPEN = (55 * Math.PI) / 180, heat = new T.Color(0xff8a3c), cool = new T.Color(0x5fc5ff);
  const v = new T.Vector3(), n = new T.Vector3(), cd = new T.Vector3();
  R.setAnimationLoop(() => {
    const now = performance.now(), dt = Math.min(0.05, (now - t0) / 1000); t0 = now;
    if (!wrap.offsetParent || document.hidden) return;   // not on screen: don't spend the GPU
    if (!O.autoRotate && idleSince && now - idleSince > 6000 && !matchMedia("(prefers-reduced-motion: reduce)").matches) O.autoRotate = true;
    for (const k in doors) {
      const d = doors[k]; if (!d) continue;
      ang[k] += ((state.doors?.[k] ? OPEN : 0) - ang[k]) * Math.min(1, dt * 3.2);
      d.rotation.y = (k.endsWith("l") ? -1 : 1) * ang[k];
    }
    spin += Math.min(Math.max(0, +state.speed || 0) / 3.6 / 0.36, 7) * dt * (state.reverse ? -1 : 1);
    for (const k in wheels) if (wheels[k]) wheels[k].rotation.x = spin;
    glowK += ((state.climate ? 1 : 0) - glowK) * Math.min(1, dt * 1.5);
    const col = state.climate === "cool" ? cool : heat, pulse = 0.78 + 0.22 * Math.sin(now / 650);
    cabin.color.copy(col); glow.material.color.copy(col); cabin.intensity = glowK * 5 * pulse; glow.material.opacity = glowK * 0.75 * pulse;
    O.update(); R.render(S, C);
    const w = wrap.clientWidth, h = wrap.clientHeight;
    for (const hs of els) {
      v.set(...hs.pos); n.set(...hs.nrm).normalize(); cd.copy(C.position).sub(v).normalize();
      const visible = n.dot(cd) > 0.05; v.project(C);
      hs.el.style.transform = `translate(${(v.x * 0.5 + 0.5) * w - 11}px,${(-v.y * 0.5 + 0.5) * h - 11}px)`;
      hs.el.classList.toggle("hid", !visible);
    }
  });

  return {
    /** {doors:{fl,fr,rl,rr}, open:{frunk,trunk,port,fl,...}, charging, speed, reverse, climate:"heat"|"cool"|null} */
    update(s) {
      state = s;
      for (const hs of els) {
        hs.el.classList.toggle("open", !!s.open?.[hs.id]);
        if (hs.id === "port") hs.el.classList.toggle("charging", !!s.charging);
      }
    },
    setLabels,
    setColors({ paint, interior }) {
      const p = PAINTS.find((x) => x[0] === paint) || PAINTS[0];
      for (const n of ["paint", "jamb"]) eachMat(n, (m) => { m.color.set(p[1]); m.metalness = p[2]; m.roughness = p[3]; });
      const white = interior === "white";
      eachMat("seats", (m) => { m.color.set(white ? "#e4dfd7" : "#16181c"); m.roughness = white ? 0.55 : 0.62; });
      eachMat("trim2", (m) => m.color.set(white ? "#d6d1c9" : "#30343b"));
    },
    dispose() { R.setAnimationLoop(null); ro.disconnect(); R.dispose(); els.forEach((h) => h.el.remove()); R.domElement.remove(); },
  };
}
