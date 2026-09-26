"use client";

import { formatDate } from "@rahiq/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAction } from "@/components/admin/ui";
import { bff } from "@/lib/client/bff";
import { useInboxHub } from "@/lib/client/hub";
import { useLocale, useT } from "@/lib/client/i18n";

type Summary = {
  id: string;
  channel: string;
  contactName?: string | null;
  status: string;
  mode: "agent" | "human";
  assignedStaffId?: string | null;
  handoffReason?: string | null;
  unread: number;
  lastMessageAt: string;
  lastPreview?: string | null;
};
type Message = { id: string; direction: "in" | "out"; author: "customer" | "agent" | "staff" | "system"; kind: string; body: string; status: string; error?: string | null; createdAt: string };
type Detail = {
  conversation: Summary;
  contact: { id: string; channel: string; name?: string | null; phone?: string | null; locale?: string | null };
  replyWindow: { open: boolean; closesAt?: string | null };
  messages: Message[];
};

const CHANNEL_LABEL: Record<string, string> = { telegram: "Telegram", whatsapp: "WhatsApp", instagram: "Instagram", messenger: "Messenger" };
const FILTERS = [
  ["", "inbox.filterAll"],
  ["handoff", "inbox.filterWaiting"],
  ["mine", "inbox.filterMine"],
  ["agent", "inbox.filterAgent"],
] as const;

/** The shared inbox: every chat channel in one list, live, with "take over" to silence the assistant for a thread. */
export default function AdminConversations() {
  const t = useT();
  const locale = useLocale();
  const client = useQueryClient();
  const [filter, setFilter] = useState("");
  const [closed, setClosed] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);

  const onChanged = useCallback(
    (id: string) => {
      void client.invalidateQueries({ queryKey: ["admin", "conversations"] });
      if (id === "*" || id === selected) void client.invalidateQueries({ queryKey: ["admin", "conversation", id === "*" ? selected : id] });
    },
    [client, selected],
  );
  const live = useInboxHub(onChanged);

  // Polling is only the safety net for when the live connection is down.
  const { data: list } = useQuery({
    queryKey: ["admin", "conversations", filter, closed],
    queryFn: () => bff<Summary[]>(`admin/conversations?filter=${filter}&closed=${closed}`),
    refetchInterval: live ? false : 15_000,
  });

  return (
    <>
      <div className="admin-head">
        <h1>{t("inbox.title")}</h1>
        <span className={`inbox-live ${live ? "is-live" : ""}`} role="status">
          {live ? t("inbox.live") : t("inbox.offline")}
        </span>
      </div>
      <div className="admin-toolbar">
        <div className="chips">
          {FILTERS.map(([value, label]) => (
            <button key={value} type="button" className="chip" aria-pressed={filter === value} onClick={() => setFilter(value)}>
              {t(label)}
            </button>
          ))}
        </div>
        <label className="small">
          <input type="checkbox" checked={closed} onChange={(e) => setClosed(e.target.checked)} /> {t("inbox.showClosed")}
        </label>
      </div>
      <div className="inbox">
        <ul className="inbox-list" aria-label={t("inbox.title")}>
          {list?.length === 0 && <li className="muted small inbox-empty">{t("inbox.empty")}</li>}
          {list?.map((c) => (
            <li key={c.id}>
              <button type="button" className="inbox-item" aria-current={selected === c.id} onClick={() => setSelected(c.id)}>
                <span className="inbox-item-top">
                  <strong>{c.contactName ?? t("inbox.unnamed")}</strong>
                  {c.unread > 0 && <span className="inbox-unread">{c.unread}</span>}
                </span>
                <span className="muted small">
                  {CHANNEL_LABEL[c.channel] ?? c.channel} · {formatDate(c.lastMessageAt, locale, true)}
                </span>
                <span className="inbox-preview small">{c.lastPreview}</span>
                <span className={`inbox-mode small ${c.mode === "human" && !c.assignedStaffId ? "is-waiting" : ""}`}>
                  {c.mode === "agent" ? t("inbox.modeAgent") : c.assignedStaffId ? t("inbox.modeHuman") : t("inbox.handoff", { reason: c.handoffReason ?? "" })}
                  {c.status === "closed" ? ` · ${t("inbox.close")}` : ""}
                </span>
              </button>
            </li>
          ))}
        </ul>
        {selected ? <Thread key={selected} id={selected} live={live} /> : <p className="muted inbox-thread">{t("inbox.pick")}</p>}
      </div>
    </>
  );
}

function Thread({ id, live }: { id: string; live: boolean }) {
  const t = useT();
  const locale = useLocale();
  const client = useQueryClient();
  const { run, busy, error } = useAction();
  const [text, setText] = useState("");
  const bottom = useRef<HTMLDivElement>(null);
  const { data } = useQuery({
    queryKey: ["admin", "conversation", id],
    queryFn: () => bff<Detail>(`admin/conversations/${id}`),
    refetchInterval: live ? false : 10_000,
  });
  const refresh = () => {
    void client.invalidateQueries({ queryKey: ["admin", "conversation", id] });
    void client.invalidateQueries({ queryKey: ["admin", "conversations"] });
  };
  const post = (path: string, body?: unknown) => run(() => bff(`admin/conversations/${id}/${path}`, { method: "POST", body }), refresh);

  const unread = data?.conversation.unread ?? 0;
  useEffect(() => {
    if (unread > 0) void bff(`admin/conversations/${id}/read`, { method: "POST" });
  }, [id, unread]);
  const count = data?.messages.length ?? 0;
  useEffect(() => bottom.current?.scrollIntoView({ block: "end" }), [count]);

  if (!data) return <div className="inbox-thread" aria-busy="true" />;
  const c = data.conversation;
  const send = () => {
    const value = text.trim();
    if (!value) return;
    void run(() => bff(`admin/conversations/${id}/messages`, { method: "POST", body: { text: value } }), () => {
      setText("");
      refresh();
    });
  };

  return (
    <section className="inbox-thread" aria-label={data.contact.name ?? t("inbox.unnamed")}>
      <header className="inbox-thread-head">
        <div>
          <strong>{data.contact.name ?? t("inbox.unnamed")}</strong>
          <p className="muted small">
            {CHANNEL_LABEL[c.channel] ?? c.channel}
            {data.contact.phone ? ` · ${t("inbox.phone")}: ${data.contact.phone}` : ""}
            {" · "}
            {c.mode === "agent" ? t("inbox.modeAgent") : t("inbox.modeHuman")}
          </p>
        </div>
        <div className="admin-actions">
          {c.mode === "agent" ? (
            <button type="button" className="btn" disabled={busy} onClick={() => post("take-over")}>
              {t("inbox.takeOver")}
            </button>
          ) : (
            <button type="button" className="btn btn-quiet" disabled={busy} onClick={() => post("release")}>
              {t("inbox.release")}
            </button>
          )}
          {c.status === "open" && (
            <button type="button" className="btn btn-quiet" disabled={busy} onClick={() => post("close")}>
              {t("inbox.close")}
            </button>
          )}
        </div>
      </header>
      {error}
      <ol className="inbox-messages">
        {data.messages.map((m) => (
          <li key={m.id} className={`bubble bubble-${m.author}`}>
            <span className="bubble-meta small">
              {t(`inbox.${m.author === "system" ? "note" : m.author}`)} · {formatDate(m.createdAt, locale, true)}
              {m.status === "pending" && ` · ${t("inbox.pending")}`}
              {m.status === "failed" && ` · ${t("inbox.failed")}${m.error ? ` (${m.error})` : ""}`}
            </span>
            <span className="bubble-body">{m.body}</span>
          </li>
        ))}
      </ol>
      <div ref={bottom} />
      <form
        className="inbox-reply"
        onSubmit={(e) => {
          e.preventDefault();
          send();
        }}
      >
        <label className="small" htmlFor="inbox-reply">
          {data.replyWindow.open
            ? data.replyWindow.closesAt
              ? t("inbox.windowUntil", { time: formatDate(data.replyWindow.closesAt, locale, true) })
              : t("inbox.reply")
            : t("inbox.windowClosed")}
        </label>
        <textarea
          id="inbox-reply"
          className="input"
          rows={3}
          maxLength={4000}
          value={text}
          disabled={!data.replyWindow.open}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) send();
          }}
        />
        <div className="admin-actions">
          <button type="submit" className="btn btn-buy" disabled={busy || !data.replyWindow.open || !text.trim()}>
            {t("inbox.send")}
          </button>
          {c.mode === "agent" && <span className="muted small">{t("inbox.replyHint")}</span>}
        </div>
      </form>
    </section>
  );
}
