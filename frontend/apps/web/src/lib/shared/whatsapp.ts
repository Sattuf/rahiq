/**
 * Ordering by WhatsApp: a wa.me link with the order already written, in the visitor's language. The conversation
 * that follows lands in the Conversations inbox (ADR-019), where the order, shipping and payment are confirmed.
 * Works without JavaScript wherever the text is known on the server.
 */
export function whatsappLink(number: string, text: string): string {
  return `https://wa.me/${number.replace(/\D/g, "")}?text=${encodeURIComponent(text)}`;
}
