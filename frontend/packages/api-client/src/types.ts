// Mirrors the API's DTOs. Money is always an integer in minor units with its currency (Law 5).

export type Section = "perfume" | "honey" | "shared";

export type Media = { id: string; url: string; width: number; height: number; role: string; alt?: string | null };

export type Highlights = {
  concentration?: string | null;
  families: string[];
  gender?: string | null;
  seasons: string[];
  topNotes: string[];
  floralSource?: string | null;
  texture?: string | null;
  regionCode?: string | null;
};

export type ProductCard = {
  id: string;
  slug: string;
  section: Section;
  type: string;
  name: string;
  shortDescription?: string | null;
  image?: Media | null;
  price?: number | null;
  previousPrice?: number | null;
  currency: string;
  priceVaries: boolean;
  available: boolean;
  hasSample: boolean;
  isFeatured: boolean;
  sizes: string[];
  highlights: Highlights;
};

export type Variant = {
  id: string;
  sku: string;
  label: string;
  volumeMl?: number | null;
  weightG?: number | null;
  isSample: boolean;
  price?: number | null;
  previousPrice?: number | null;
  availability: "in_stock" | "low" | "out";
  gtin?: string | null;
};

export type LabSummary = {
  moisture?: number;
  hmf?: number;
  diastase?: number;
  pollen?: { name: string; percent: number };
  lab?: string;
  reportDate?: string;
};

export type BatchCard = {
  variantId: string;
  code: string;
  token: string;
  producedAt?: string | null;
  bestBefore?: string | null;
  origin?: Record<string, unknown> | null;
  labSummary?: LabSummary | null;
  analysed: boolean;
};

export type GiftOption = { variantId: string; productId: string; slug: string; name: string; label: string; section: Section; price?: number | null; image?: Media | null };

export type GiftSlot = { code: string; required: boolean; allowedSections: Section[]; options: GiftOption[] };

export type ProductDetail = {
  card: ProductCard;
  story?: string | null;
  usage?: string | null;
  seoTitle?: string | null;
  seoDescription?: string | null;
  attributes: Record<string, unknown>;
  warnings: { code: string; text: string }[];
  allergens: { code: string; name: string }[];
  variants: Variant[];
  media: Media[];
  batches: BatchCard[];
  giftSlots: GiftSlot[];
  bundleParts: { variantId: string; name: string; label: string; qty: number }[];
  crystallizationNote?: string | null;
  locales: string[];
  updatedAt: string;
};

export type TaxonomyEntry = { code: string; name: string; family?: string | null };

export type Taxonomy = {
  sections: TaxonomyEntry[];
  productTypes: TaxonomyEntry[];
  concentrations: TaxonomyEntry[];
  families: TaxonomyEntry[];
  genders: TaxonomyEntry[];
  seasons: TaxonomyEntry[];
  notes: TaxonomyEntry[];
  floralSources: TaxonomyEntry[];
  textures: TaxonomyEntry[];
  allergens: TaxonomyEntry[];
  warnings: TaxonomyEntry[];
  regionCodes: string[];
  crystallizationNote: string;
};

export type PublicBatch = {
  productName: string;
  slug: string;
  variantLabel: string;
  variantId: string;
  code: string;
  producedAt?: string | null;
  bestBefore?: string | null;
  origin?: Record<string, unknown> | null;
  labSummary?: LabSummary | null;
  analysed: boolean;
  isCurrentBatch: boolean;
  available: boolean;
};

export type CartItem = {
  lineId: string;
  variantId: string;
  productId: string;
  slug: string;
  section: Section;
  name: string;
  label: string;
  qty: number;
  unitPrice: number;
  total: number;
  image?: string | null;
  isSample: boolean;
  giftMessage?: string | null;
  components: { variantId: string; name: string; label: string }[];
  priceChanged?: { was: number; now: number } | null;
  availability: "ok" | "low" | "short" | "out" | "unavailable";
  maxQty: number;
};

export type Cart = {
  newToken?: string | null;
  items: CartItem[];
  itemCount: number;
  currency: string;
  subtotal: number;
  discount: number;
  total: number;
  couponCode?: string | null;
  couponError?: string | null;
  freeShippingThreshold?: number | null;
  remainingForFreeShipping?: number | null;
  problems: { variantId: string; code: string }[];
};

export type Address = {
  fullName: string;
  phone: string;
  provinceCode: number;
  provinceName?: string;
  district: string;
  neighbourhood?: string | null;
  line1: string;
  line2?: string | null;
  postalCode?: string | null;
  companyName?: string | null;
  taxOffice?: string | null;
  taxNumber?: string | null;
};

export type QuoteLine = {
  itemIndex: number;
  variantId: string;
  sku: string;
  section: Section;
  name: string;
  variantLabel: string;
  warnings: string[];
  isSample: boolean;
  shippingClass: string;
  groupId?: string | null;
  groupLabel?: string | null;
  unitPrice: number;
  qty: number;
  discount: number;
  taxRateBp: number;
  taxAmount: number;
  lineTotal: number;
  returnable: boolean;
};

export type Quote = {
  currency: string;
  lines: QuoteLine[];
  subtotal: number;
  discount: number;
  couponDiscount: number;
  shipping: number;
  codFee: number;
  tax: number;
  total: number;
  appliedCoupon?: string | null;
  couponError?: string | null;
  freeShippingApplied: boolean;
  freeShippingThreshold?: number | null;
  shippingOptions: { method: string; amount: number; isFree: boolean }[];
  problems: { variantId: string; code: string }[];
  codAvailable: boolean;
  hasFlammable: boolean;
};

export type CheckoutView = {
  id: string;
  email?: string | null;
  phone?: string | null;
  shippingAddress?: Address | null;
  billingAddress?: Address | null;
  isGift: boolean;
  giftMessage?: string | null;
  hidePrices: boolean;
  shippingMethod?: string | null;
  marketingConsent: boolean;
  quote: Quote;
  codAvailable: boolean;
  codUnavailableReason?: string | null;
  contracts?: { preInformationHtml: string; distanceSalesHtml: string; hash: string } | null;
  readyToPay: boolean;
};

export type PayResult = { number: string; paymentMethod: "card" | "cod"; redirectUrl?: string | null; total: number; currency: string };

export type OrderStatus = { number: string; status: string; paymentMethod: string; paymentStatus?: string | null; total: number; currency: string };

export type OrderLine = {
  id: string;
  variantId: string;
  sku: string;
  name: string;
  variantLabel: string;
  section: Section;
  qty: number;
  unitPrice: number;
  discount: number;
  lineTotal: number;
  taxRateBp: number;
  warnings: string[];
  groupLabel?: string | null;
  returnable: boolean;
  batches: { batchId: string; code: string; qty: number }[];
};

export type OrderView = {
  id: string;
  number: string;
  status: string;
  paymentMethod: string;
  paymentStatus?: string | null;
  installments: number;
  currency: string;
  subtotal: number;
  discount: number;
  shipping: number;
  codFee: number;
  tax: number;
  total: number;
  email: string;
  phone?: string | null;
  shippingAddress: Address;
  isGift: boolean;
  giftMessage?: string | null;
  hidePrices: boolean;
  locale: string;
  placedAt: string;
  lines: OrderLine[];
  history: { status: string; note?: string | null; at: string }[];
  trackingNumber?: string | null;
  canRequestReturn: boolean;
  fraudFlags: string[];
};

export type OrderSummary = { id: string; number: string; status: string; total: number; currency: string; placedAt: string; items: number; firstItem: string };

export type Province = { code: number; name: string };

export type Page = { id: string; slug: string; locale: string; kind: string; title: string; summary?: string | null; bodyMarkdown: string; status: string; updatedAt: string };

export type GuideQuestion = { code: string; prompt: string; options: { code: string; label: string }[] };

export type Profile = { id: string; email: string; name?: string | null; phone?: string | null; locale: string; marketingEmail: boolean; marketingSms: boolean };

export type Problem = { status: number; code: string; detail?: string; errors?: Record<string, string[]> };
