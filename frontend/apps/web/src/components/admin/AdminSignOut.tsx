"use client";

import { bff } from "@/lib/client/bff";
import { useLocale } from "@/lib/client/i18n";

export function AdminSignOut({ label }: { label: string }) {
  const locale = useLocale();
  return (
    <button
      type="button"
      className="btn btn-quiet"
      onClick={async () => {
        await bff("auth/logout", { method: "POST" });
        window.location.assign(`/${locale}/admin/login`);
      }}
    >
      {label}
    </button>
  );
}
