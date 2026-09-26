import Link from "next/link";

export type FilterGroup = { key: string; label: string; options: { code: string; name: string }[] };

/**
 * Filters as plain links (they work without JavaScript and are shareable). The current choice is marked with
 * aria-current; choosing it again clears it.
 */
export function FilterBar({ base, groups, current, clearLabel, anyLabel }: {
  base: string;
  groups: FilterGroup[];
  current: Record<string, string | undefined>;
  clearLabel: string;
  anyLabel: string;
}) {
  const href = (key: string, code?: string) => {
    const params = new URLSearchParams(Object.entries(current).filter(([, v]) => v) as [string, string][]);
    if (code && current[key] !== code) params.set(key, code);
    else params.delete(key);
    const query = params.toString();
    return `${base}${query ? `?${query}` : ""}`;
  };
  const active = Object.values(current).some(Boolean);

  return (
    <div className="filters">
      {groups.map((group) => (
        <div key={group.key} className="filter-group" role="group" aria-label={group.label}>
          <span className="filter-label">{group.label}</span>
          <div className="chips">
            <Link className="chip" href={href(group.key)} aria-current={!current[group.key] ? "true" : undefined}>
              {anyLabel}
            </Link>
            {group.options.map((o) => (
              <Link key={o.code} className="chip" href={href(group.key, o.code)} aria-current={current[group.key] === o.code ? "true" : undefined}>
                {o.name}
              </Link>
            ))}
          </div>
        </div>
      ))}
      {active && (
        <Link className="btn btn-quiet" href={base}>
          {clearLabel}
        </Link>
      )}
    </div>
  );
}
