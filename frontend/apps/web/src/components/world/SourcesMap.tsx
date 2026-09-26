import Link from "next/link";

type Region = { code: string; name: string; count: number };

// A hand-drawn, schematic Türkiye in seven regions (not a heavy interactive map, frontend-experience.md §3.4).
const SHAPES: Record<string, { d: string; x: number; y: number }> = {
  marmara: { d: "M40 70c14-22 44-30 70-22 10 16 6 34-8 44-26 8-50 4-62-22z", x: 72, y: 70 },
  black_sea: { d: "M118 44c50-18 130-22 196-10 10 14 4 30-12 36-60 8-124 10-176 4-12-10-14-22-8-30z", x: 214, y: 50 },
  aegean: { d: "M30 104c16-6 40-4 58 6 8 28 4 58-16 76-22 4-38-8-44-30-4-20-4-38 2-52z", x: 58, y: 146 },
  central_anatolia: { d: "M104 98c44-10 106-8 148 6 10 26 6 54-10 72-48 10-100 10-136 0-12-24-14-52-2-78z", x: 178, y: 136 },
  mediterranean: { d: "M84 196c40 8 104 12 166 4 10 14 8 30-4 40-54 14-116 10-160-4-10-14-10-30-2-40z", x: 166, y: 216 },
  eastern_anatolia: { d: "M264 84c40-6 84 0 114 14 10 30 6 64-10 86-40 8-78 6-104-6-10-30-10-64 0-94z", x: 320, y: 132 },
  southeastern_anatolia: { d: "M262 196c30-8 70-10 106-2 8 14 6 30-6 40-34 10-70 10-98 2-8-14-10-28-2-40z", x: 314, y: 216 },
};

export function SourcesMap({ title, hint, regions, base, current, importedLabel }: {
  title: string;
  hint: string;
  regions: Region[];
  base: string;
  current?: string;
  importedLabel?: string;
}) {
  const imported = regions.find((r) => r.code === "imported");
  return (
    <section className="section sources" aria-labelledby="sources-title">
      <div className="container sources-grid">
        <div>
          <h2 id="sources-title">{title}</h2>
          <p className="muted">{hint}</p>
          <ul className="sources-list">
            {regions.filter((r) => r.count > 0).map((r) => (
              <li key={r.code}>
                <Link href={`${base}?region=${r.code}`} aria-current={current === r.code ? "true" : undefined} className="chip">
                  {r.name} · {r.count}
                </Link>
              </li>
            ))}
          </ul>
        </div>
        <svg viewBox="0 0 400 260" className="sources-map" role="img" aria-label={title}>
          {Object.entries(SHAPES).map(([code, shape]) => {
            const region = regions.find((r) => r.code === code);
            const has = (region?.count ?? 0) > 0;
            const body = (
              <>
                <path d={shape.d} className={`map-region${has ? " has" : ""}${current === code ? " current" : ""}`} />
                <text x={shape.x} y={shape.y} textAnchor="middle" className="map-label">
                  {region?.name}
                </text>
              </>
            );
            return has ? (
              <a key={code} href={`${base}?region=${code}`} aria-label={region?.name}>
                {body}
              </a>
            ) : (
              <g key={code}>{body}</g>
            );
          })}
          {imported && imported.count > 0 && (
            <a href={`${base}?region=imported`}>
              <text x="360" y="252" textAnchor="end" className="map-label map-imported">
                + {importedLabel ?? imported.name}
              </text>
            </a>
          )}
        </svg>
      </div>
    </section>
  );
}
