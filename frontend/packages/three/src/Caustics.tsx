"use client";

import { useEffect, useRef } from "react";

const VERTEX = `
attribute vec2 a_pos;
void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }
`;

// Rippling light through liquid (brand-identity.md §3): an iterated, slowly distorted interference pattern.
const FRAGMENT = `
precision mediump float;
uniform vec2 u_res;
uniform float u_time;
uniform vec3 u_tint;
uniform float u_intensity;
#define TAU 6.28318530718
#define MAX_ITER 5
void main() {
  float time = u_time * 0.25 + 23.0;
  vec2 uv = gl_FragCoord.xy / u_res.xy * vec2(u_res.x / u_res.y, 1.0) * 0.9;
  vec2 p = mod(uv * TAU, TAU) - 250.0;
  vec2 i = p;
  float c = 1.0;
  float inten = 0.005;
  for (int n = 0; n < MAX_ITER; n++) {
    float t = time * (1.0 - (3.5 / float(n + 1)));
    i = p + vec2(cos(t - i.x) + sin(t + i.y), sin(t - i.y) + cos(t + i.x));
    c += 1.0 / length(vec2(p.x / (sin(i.x + t) / inten), p.y / (cos(i.y + t) / inten)));
  }
  c /= float(MAX_ITER);
  c = 1.17 - pow(c, 1.4);
  float v = clamp(pow(abs(c), 8.0), 0.0, 1.0) * u_intensity;
  gl_FragColor = vec4(u_tint * v, v);
}
`;

function hexToRgb(hex: string): [number, number, number] {
  const n = parseInt(hex.replace("#", ""), 16);
  return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
}

export type CausticsProps = {
  tint: string;
  intensity: number;
  className?: string;
  /** Freeze on one frame (reduced motion). The pattern stays; the motion goes. */
  still?: boolean;
};

/**
 * Full-bleed caustic light (frontend-experience.md §2, light implementation). Rendered at half resolution, paused
 * off-screen and in hidden tabs, one static frame under reduced motion; without WebGL nothing renders and the
 * CSS fallback behind it remains (Law 11).
 */
export function Caustics({ tint, intensity, className, still }: CausticsProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const gl = canvas.getContext("webgl", { premultipliedAlpha: true, antialias: false, alpha: true, powerPreference: "low-power" });
    if (!gl) {
      canvas.remove();
      return;
    }

    const compile = (type: number, source: string) => {
      const shader = gl.createShader(type)!;
      gl.shaderSource(shader, source);
      gl.compileShader(shader);
      return shader;
    };
    const program = gl.createProgram()!;
    gl.attachShader(program, compile(gl.VERTEX_SHADER, VERTEX));
    gl.attachShader(program, compile(gl.FRAGMENT_SHADER, FRAGMENT));
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
      canvas.remove();
      return;
    }

    gl.useProgram(program);
    const buffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    const position = gl.getAttribLocation(program, "a_pos");
    gl.enableVertexAttribArray(position);
    gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);
    const uRes = gl.getUniformLocation(program, "u_res");
    const uTime = gl.getUniformLocation(program, "u_time");
    gl.uniform3fv(gl.getUniformLocation(program, "u_tint"), hexToRgb(tint));
    gl.uniform1f(gl.getUniformLocation(program, "u_intensity"), intensity);

    const reduce = window.matchMedia("(prefers-reduced-motion: reduce)");
    let frame = 0;
    let visible = true;
    let lost = false;
    const start = performance.now();

    const resize = () => {
      const scale = 0.5; // Half resolution: the pattern is soft anyway.
      canvas.width = Math.max(1, Math.floor(canvas.clientWidth * scale));
      canvas.height = Math.max(1, Math.floor(canvas.clientHeight * scale));
      gl.viewport(0, 0, canvas.width, canvas.height);
      gl.uniform2f(uRes, canvas.width, canvas.height);
    };

    const draw = (now: number) => {
      if (lost) return;
      gl.uniform1f(uTime, (now - start) / 1000);
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    };

    const loop = (now: number) => {
      draw(now);
      frame = requestAnimationFrame(loop);
    };

    const run = () => {
      cancelAnimationFrame(frame);
      if (still || reduce.matches || !visible || document.hidden) {
        draw(start + 12_000); // One calm frame.
        return;
      }

      frame = requestAnimationFrame(loop);
    };

    const observer = new IntersectionObserver(([entry]) => {
      visible = entry.isIntersecting;
      run();
    });
    const onLost = (event: Event) => {
      event.preventDefault();
      lost = true;
      cancelAnimationFrame(frame);
    };

    resize();
    observer.observe(canvas);
    window.addEventListener("resize", resize);
    document.addEventListener("visibilitychange", run);
    reduce.addEventListener("change", run); // Respected even if it changes while the page is open.
    canvas.addEventListener("webglcontextlost", onLost);
    run();

    return () => {
      cancelAnimationFrame(frame);
      observer.disconnect();
      window.removeEventListener("resize", resize);
      document.removeEventListener("visibilitychange", run);
      reduce.removeEventListener("change", run);
      canvas.removeEventListener("webglcontextlost", onLost);
    };
  }, [tint, intensity, still]);

  return <canvas ref={canvasRef} className={className} aria-hidden="true" />;
}
