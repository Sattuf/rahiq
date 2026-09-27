import { whatsappLink } from "@/lib/shared/whatsapp";

/** The floating WhatsApp button on store pages: a plain link, no JavaScript. */
export function WhatsAppButton({ number, label, text }: { number: string; label: string; text: string }) {
  return (
    <a className="wa-float" href={whatsappLink(number, text)} target="_blank" rel="noopener" aria-label={label} title={label}>
      <WhatsAppIcon />
    </a>
  );
}

export function WhatsAppIcon({ size = 26 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 32 32" aria-hidden="true" focusable="false">
      <path
        fill="currentColor"
        d="M16 3C8.8 3 3 8.7 3 15.8c0 2.5.7 4.9 2 7L3 29l6.4-2a13.2 13.2 0 0 0 6.6 1.7c7.2 0 13-5.7 13-12.8S23.2 3 16 3Zm0 23.4c-2.1 0-4.1-.6-5.9-1.7l-.4-.2-3.8 1.2 1.2-3.7-.3-.4a10.4 10.4 0 0 1-1.7-5.8C5.1 10 10 5.3 16 5.3S26.9 10 26.9 15.8 22 26.4 16 26.4Zm6-7.8c-.3-.2-1.9-1-2.2-1-.3-.1-.5-.2-.7.2-.2.3-.8 1-1 1.2-.2.2-.4.2-.7.1-.3-.2-1.4-.5-2.6-1.6-1-.9-1.6-1.9-1.8-2.2-.2-.3 0-.5.1-.7l.5-.6.3-.5c.1-.2 0-.4 0-.6l-1-2.4c-.3-.6-.5-.5-.7-.5h-.6c-.2 0-.6.1-.9.4-.3.3-1.1 1.1-1.1 2.7s1.2 3.1 1.3 3.3c.2.2 2.3 3.5 5.5 4.9.8.3 1.4.5 1.9.7.8.2 1.5.2 2.1.1.6-.1 1.9-.8 2.2-1.5.3-.8.3-1.4.2-1.5-.1-.2-.3-.3-.6-.4Z"
      />
    </svg>
  );
}
