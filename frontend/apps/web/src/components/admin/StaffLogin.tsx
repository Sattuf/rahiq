"use client";

import { ApiError } from "@rahiq/api-client";
import QRCode from "qrcode";
import { useState } from "react";
import { bff } from "@/lib/client/bff";
import { useErrorText, useLocale, useT } from "@/lib/client/i18n";

type LoginResult = { enrollmentTicket?: string | null; profile?: unknown };

/** Password, then TOTP; on first sign-in the second factor must be set up before anything else (security.md §1). */
export function StaffLogin() {
  const t = useT();
  const locale = useLocale();
  const errorText = useErrorText();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [needCode, setNeedCode] = useState(false);
  const [ticket, setTicket] = useState<string | null>(null);
  const [qr, setQr] = useState<string | null>(null);
  const [secret, setSecret] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const done = () => window.location.assign(`/${locale}/admin`);
  const fail = (e: unknown) => {
    const c = e instanceof ApiError ? e.code : undefined;
    if (c === "auth.totp_required") {
      setNeedCode(true);
      return;
    }
    setError(errorText(c === "auth.invalid" ? "auth.invalid" : c));
  };

  const login = async (event: React.FormEvent) => {
    event.preventDefault();
    setError(null);
    try {
      const result = await bff<LoginResult>("auth/staff/login", { method: "POST", body: { email, password, totpCode: code || null } });
      if (result.enrollmentTicket) {
        setTicket(result.enrollmentTicket);
        const enrollment = await bff<{ secret: string; provisioningUri: string }>("auth/staff/totp/start", { method: "POST", body: { ticket: result.enrollmentTicket } });
        setSecret(enrollment.secret);
        setQr(await QRCode.toDataURL(enrollment.provisioningUri, { margin: 1, width: 220 }));
        return;
      }
      done();
    } catch (e) {
      fail(e);
    }
  };

  const confirm = async (event: React.FormEvent) => {
    event.preventDefault();
    setError(null);
    try {
      await bff("auth/staff/totp/confirm", { method: "POST", body: { ticket, code } });
      done();
    } catch (e) {
      fail(e);
    }
  };

  return (
    <div className="admin-login">
      <h1>{t("admin.signIn")}</h1>
      {!ticket ? (
        <form onSubmit={login} className="stack">
          <div className="field">
            <label htmlFor="email">{t("admin.email")}</label>
            <input id="email" className="input" type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
          </div>
          <div className="field">
            <label htmlFor="password">{t("admin.password")}</label>
            <input id="password" className="input" type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
          </div>
          {needCode && (
            <div className="field">
              <label htmlFor="totp">{t("admin.totp")}</label>
              <input id="totp" className="input otp" inputMode="numeric" autoComplete="one-time-code" maxLength={6} required value={code} onChange={(e) => setCode(e.target.value)} autoFocus />
            </div>
          )}
          <button className="btn btn-buy" type="submit">
            {t("account.verify")}
          </button>
        </form>
      ) : (
        <form onSubmit={confirm} className="stack">
          <h2>{t("admin.enrollTitle")}</h2>
          <p className="muted">{t("admin.enrollText")}</p>
          {/* eslint-disable-next-line @next/next/no-img-element */}
          {qr && <img src={qr} alt="" width={220} height={220} />}
          {secret && <code className="secret">{secret}</code>}
          <div className="field">
            <label htmlFor="enroll-code">{t("admin.totp")}</label>
            <input id="enroll-code" className="input otp" inputMode="numeric" maxLength={6} required value={code} onChange={(e) => setCode(e.target.value)} />
          </div>
          <button className="btn btn-buy" type="submit">
            {t("admin.save")}
          </button>
        </form>
      )}
      {error && (
        <p className="field-error" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}
