// The caustics shader is plain WebGL (no three.js), so pages that use it stay light.
// The 3D bottle lives in "@rahiq/three/bottle" and must only be loaded with a dynamic import (Law 11).
export { Caustics } from "./Caustics";
export type { CausticsProps } from "./Caustics";
