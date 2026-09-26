import { revalidateTag } from "next/cache";
import { type NextRequest, NextResponse } from "next/server";
import { config } from "@/lib/server/config";

/** Called by the API on ProductPublished / PriceChanged (architecture.md §6). */
export async function POST(request: NextRequest) {
  if (request.headers.get("x-revalidate-secret") !== config.revalidateSecret) {
    return NextResponse.json({ ok: false }, { status: 401 });
  }

  const { tags } = (await request.json()) as { tags?: string[] };
  for (const tag of (tags ?? []).slice(0, 50)) {
    revalidateTag(tag, "max");
  }

  return NextResponse.json({ ok: true });
}
