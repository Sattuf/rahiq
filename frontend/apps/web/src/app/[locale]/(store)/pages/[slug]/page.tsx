import type { Page } from "@rahiq/api-client";
import type { Locale } from "@rahiq/i18n";
import type { Metadata } from "next";
import { notFound } from "next/navigation";
import Markdown from "react-markdown";
import { apiGetOrNull } from "@/lib/server/api";

type Props = { params: Promise<{ locale: string; slug: string }> };

async function load(locale: Locale, slug: string) {
  return apiGetOrNull<Page>(`/api/content/pages/${encodeURIComponent(slug)}`, { locale, tags: ["content", `page:${slug}`], revalidate: 600 });
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { locale, slug } = await params;
  const page = await load(locale as Locale, slug);
  return page ? { title: page.title, description: page.summary ?? undefined } : {};
}

/** Editorial and legal pages. Markdown only; raw HTML in content is never rendered (security.md §7). */
export default async function ContentPage({ params }: Props) {
  const { locale, slug } = await params;
  const page = await load(locale as Locale, slug);
  if (!page) notFound();
  return (
    <article className="container narrow section prose" lang={page.locale}>
      <p className="eyebrow">{page.kind}</p>
      <h1>{page.title}</h1>
      <Markdown skipHtml>{page.bodyMarkdown}</Markdown>
    </article>
  );
}
