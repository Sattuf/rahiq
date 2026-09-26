"use client";

import * as Dialog from "@radix-ui/react-dialog";
import type { Locale } from "@rahiq/i18n/core";
import Link from "next/link";
import { useState } from "react";
import { useT } from "@/lib/client/i18n";

export function MobileMenu({ locale, links }: { locale: Locale; links: { href: string; label: string }[] }) {
  const t = useT();
  const [open, setOpen] = useState(false);
  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger className="btn btn-quiet show-sm" aria-label={t("nav.menu")}>
        <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
          <path d="M4 7h16M4 12h16M4 17h16" />
        </svg>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="overlay" />
        <Dialog.Content className="drawer drawer-start">
          <div className="drawer-head">
            <Dialog.Title className="display">{t("nav.menu")}</Dialog.Title>
            <Dialog.Close className="btn btn-quiet">{t("nav.close")}</Dialog.Close>
          </div>
          <Dialog.Description className="sr-only">{t("nav.menu")}</Dialog.Description>
          <nav className="mobile-nav">
            {[...links, { href: `/${locale}/account`, label: t("nav.account") }, { href: `/${locale}/track`, label: t("nav.track") }].map((l) => (
              <Link key={l.href} href={l.href} onClick={() => setOpen(false)}>
                {l.label}
              </Link>
            ))}
          </nav>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
