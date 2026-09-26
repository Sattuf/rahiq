"use client";

import { HttpTransportType, HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { useEffect, useRef, useState } from "react";
import { bff } from "./bff";

/** Public API origin: the live inbox connects to it directly (WebSockets cannot pass through the BFF route handlers). */
export const apiOrigin = (process.env.NEXT_PUBLIC_API_ORIGIN ?? "http://localhost:5080").replace(/\/$/, "");

/**
 * Live inbox updates (ADR-019). The hub only says "conversation X changed"; the caller re-reads it through the BFF.
 * Each (re)connect asks the BFF for a fresh two-minute hub token, so the admin's real session never reaches the browser.
 * Calls onChanged("*") after a reconnect, since updates may have been missed while offline.
 */
export function useInboxHub(onChanged: (conversationId: string) => void): boolean {
  const [live, setLive] = useState(false);
  const handler = useRef(onChanged);
  useEffect(() => {
    handler.current = onChanged;
  }, [onChanged]);

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl(`${apiOrigin}/hubs/conversations`, {
        withCredentials: false,
        // WebSockets only, no negotiate round-trip: with several API instances behind a round-robin proxy, a negotiate
        // on one instance and a socket on another would fail. One long-lived socket needs no sticky sessions.
        skipNegotiation: true,
        transport: HttpTransportType.WebSockets,
        accessTokenFactory: async () => (await bff<{ token: string }>("admin/conversations/hub-token", { method: "POST" })).token,
      })
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 30_000])
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on("conversationChanged", (id: string) => handler.current(id));
    connection.onreconnecting(() => setLive(false));
    connection.onreconnected(() => {
      setLive(true);
      handler.current("*");
    });
    connection.onclose(() => setLive(false));

    let stopped = false;
    let retry: ReturnType<typeof setTimeout> | undefined;
    const start = async () => {
      try {
        await connection.start();
        if (!stopped) setLive(true);
      } catch {
        if (!stopped) retry = setTimeout(start, 10_000); // API restarting or token refused: try again; polling covers the gap.
      }
    };
    void start();

    return () => {
      stopped = true;
      clearTimeout(retry);
      void connection.stop();
    };
  }, []);

  return live;
}
