import { type Locale, translator } from "@rahiq/i18n";
import type { Metadata } from "next";
import { Account } from "@/components/account/Account";
import { readSession } from "@/lib/server/session";

export const metadata: Metadata = { robots: { index: false } };

export default async function AccountPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = (await params).locale as Locale;
  const t = translator(locale);
  const session = await readSession();
  return (
    <div className="container narrow section">
      <h1>{t("account.title")}</h1>
      <Account signedIn={session?.kind === "customer"} />
    </div>
  );
}
