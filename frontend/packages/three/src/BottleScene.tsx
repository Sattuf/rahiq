"use client";

import { Environment, Lightformer, MeshTransmissionMaterial, PerformanceMonitor } from "@react-three/drei";
import { Canvas, useFrame } from "@react-three/fiber";
import { useMemo, useRef, useState } from "react";
import * as THREE from "three";

/** A lathe profile for a flat, shouldered perfume bottle (x = radius, y = height). */
function bottleProfile() {
  const points = [
    [0, 0], [0.62, 0], [0.7, 0.05], [0.72, 0.2], [0.72, 1.05], [0.66, 1.2], [0.42, 1.34], [0.2, 1.4], [0.18, 1.55], [0, 1.55],
  ];
  return points.map(([x, y]) => new THREE.Vector2(x, y));
}

function liquidProfile() {
  const points = [[0, 0.08], [0.6, 0.08], [0.64, 0.2], [0.64, 0.9], [0, 0.9]];
  return points.map(([x, y]) => new THREE.Vector2(x, y));
}

function Bottle({ liquid, quality }: { liquid: string; quality: number }) {
  const group = useRef<THREE.Group>(null);
  const glass = useMemo(() => new THREE.LatheGeometry(bottleProfile(), 64), []);
  const inner = useMemo(() => new THREE.LatheGeometry(liquidProfile(), 48), []);

  useFrame((state, delta) => {
    if (group.current) {
      group.current.rotation.y += delta * 0.18;
      group.current.position.y = -0.8 + Math.sin(state.clock.elapsedTime * 0.6) * 0.03;
    }
  });

  return (
    <group ref={group} position={[0, -0.8, 0]}>
      <mesh geometry={inner} scale={[0.98, 1, 0.98]}>
        <meshPhysicalMaterial color={liquid} transmission={0.6} roughness={0.15} thickness={0.6} transparent opacity={0.85} />
      </mesh>
      <mesh geometry={glass}>
        <MeshTransmissionMaterial
          samples={quality > 0.7 ? 6 : 3}
          resolution={quality > 0.7 ? 512 : 256}
          thickness={0.4}
          roughness={0.04}
          ior={1.5}
          chromaticAberration={0.04}
          anisotropy={0.1}
          distortion={0.1}
          distortionScale={0.2}
          temporalDistortion={0.05}
          backside
        />
      </mesh>
      <mesh position={[0, 1.66, 0]}>
        <cylinderGeometry args={[0.24, 0.24, 0.24, 32]} />
        <meshStandardMaterial color="#2A1F17" metalness={0.6} roughness={0.35} />
      </mesh>
    </group>
  );
}

/**
 * The full "light through liquid" moment (frontend-experience.md §2): a real transmissive bottle. Loaded with a dynamic
 * import after the LCP, only on capable devices; everything it says is also plain text in the DOM (aria-hidden).
 */
export default function BottleScene({ liquid = "#7A4A2A", className }: { liquid?: string; className?: string }) {
  const [quality, setQuality] = useState(1);
  return (
    <div className={className} aria-hidden="true">
      <Canvas dpr={[1, 1.5]} camera={{ position: [0, 0.2, 4.2], fov: 32 }} gl={{ antialias: true, powerPreference: "high-performance" }}>
        <PerformanceMonitor onDecline={() => setQuality(0.5)} onIncline={() => setQuality(1)} />
        <ambientLight intensity={0.4} />
        <directionalLight position={[-3, 4, -2]} intensity={2.2} color="#B9C6F0" />
        <Bottle liquid={liquid} quality={quality} />
        <Environment resolution={256}>
          <Lightformer form="rect" intensity={3} color="#B9C6F0" position={[-4, 2, -3]} scale={[4, 6, 1]} />
          <Lightformer form="rect" intensity={1.5} color="#FFC766" position={[4, 1, 2]} scale={[2, 4, 1]} />
          <Lightformer form="ring" intensity={2} color="#ffffff" position={[0, 4, 0]} scale={2} />
        </Environment>
      </Canvas>
    </div>
  );
}
