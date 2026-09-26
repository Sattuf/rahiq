# نموذج البيانات (Data Model)

PostgreSQL. مخطط لكل وحدة. مفاتيح `uuid v7`. الأوقات `timestamptz` UTC. المال `bigint` بأصغر وحدة (القروش/kuruş) + `char(3)` للعملة.

## 1. الكتالوج

```sql
CREATE TABLE catalog.products (
  id uuid PRIMARY KEY,
  section text NOT NULL CHECK (section IN ('perfume','honey','shared')),
  type text NOT NULL,                    -- perfume, attar, discovery_set, honey, comb_honey, nuts_in_honey, honey_blend, gift_box, bundle
  slug text NOT NULL UNIQUE,
  status text NOT NULL,
  attributes jsonb NOT NULL,             -- يُتحقق منه بمخطط JSON لكل type في طبقة Application
  warnings text[] NOT NULL DEFAULT '{}', -- رموز من قاموس ثابت
  allergens text[] NOT NULL DEFAULT '{}',
  created_at timestamptz NOT NULL DEFAULT now(),
  xmin_version xid
);
CREATE INDEX ix_products_section_status ON catalog.products (section, status);
CREATE INDEX ix_products_attributes ON catalog.products USING gin (attributes jsonb_path_ops);

CREATE TABLE catalog.product_translations (
  product_id uuid REFERENCES catalog.products(id) ON DELETE CASCADE,
  locale text NOT NULL,
  name text NOT NULL, short_description text, story text, usage text,
  seo_title text, seo_description text,
  PRIMARY KEY (product_id, locale)
);

CREATE TABLE catalog.variants (
  id uuid PRIMARY KEY,
  product_id uuid NOT NULL REFERENCES catalog.products(id),
  sku text NOT NULL UNIQUE,
  volume_ml int, weight_g int,
  gtin text,
  shipping_weight_g int NOT NULL,
  shipping_class text NOT NULL,          -- standard, fragile, liquid, flammable
  is_sample boolean NOT NULL DEFAULT false,
  sort_order int NOT NULL DEFAULT 0,
  CHECK (volume_ml IS NOT NULL OR weight_g IS NOT NULL)
);

CREATE TABLE catalog.notes (             -- قاموس نوتات العطور
  id text PRIMARY KEY,                   -- 'bergamot', 'taif-rose', 'oud'
  family text, icon_asset_id uuid, names jsonb NOT NULL  -- {"ar":"..","tr":"..","en":".."}
);
```

**لماذا `jsonb` للخصائص لا جداول EAV؟** خصائص كل نوع معروفة ومحدودة، والتحقق بمخطط JSON لكل نوع في الكود أوضح من عشرات الجداول. الفهرس GIN يكفي للفلترة، والبحث الفعلي في Meilisearch.

## 2. المخزون

```sql
CREATE TABLE inventory.batches (
  id uuid PRIMARY KEY,
  variant_id uuid NOT NULL,
  code text NOT NULL,                    -- المطبوع على الملصق
  qty_received int NOT NULL CHECK (qty_received > 0),
  qty_on_hand int NOT NULL CHECK (qty_on_hand >= 0),
  qty_reserved int NOT NULL DEFAULT 0 CHECK (qty_reserved >= 0),
  produced_at date, best_before date,    -- best_before إلزامي للغذاء (يُفرض في المجال)
  origin jsonb,                          -- {"region": "...", "altitude": 1200, "season": "2026-09"}
  lab_report_asset_id uuid,
  lab_summary jsonb,
  public_token text UNIQUE,              -- للرابط العام في QR، غير قابل للتخمين
  UNIQUE (variant_id, code),
  CHECK (qty_reserved <= qty_on_hand)
);
CREATE INDEX ix_batches_fefo ON inventory.batches (variant_id, best_before NULLS LAST)
  WHERE qty_on_hand > qty_reserved;

CREATE TABLE inventory.reservations (
  id uuid PRIMARY KEY,
  checkout_id uuid NOT NULL,
  batch_id uuid NOT NULL REFERENCES inventory.batches(id),
  qty int NOT NULL CHECK (qty > 0),
  status text NOT NULL,                  -- held, committed, released
  expires_at timestamptz NOT NULL
);
CREATE INDEX ix_reservations_expiry ON inventory.reservations (expires_at) WHERE status = 'held';

CREATE TABLE inventory.movements (      -- سجل غير قابل للتعديل لكل حركة
  id bigserial PRIMARY KEY, batch_id uuid NOT NULL, delta int NOT NULL,
  reason text NOT NULL,                  -- received, sold, returned, damaged, adjustment
  ref_id uuid, actor_id uuid, at timestamptz NOT NULL DEFAULT now()
);
```

القيد `CHECK (qty_reserved <= qty_on_hand)` هو خط الدفاع الأخير ضد البيع الزائد: حتى لو أخطأ الكود، قاعدة البيانات ترفض.

## 3. التسعير

```sql
CREATE TABLE pricing.prices (
  variant_id uuid NOT NULL, currency char(3) NOT NULL,
  amount bigint NOT NULL CHECK (amount >= 0),
  valid_from timestamptz NOT NULL, valid_to timestamptz,
  PRIMARY KEY (variant_id, currency, valid_from)
);
-- أدنى سعر خلال 30 يومًا يُحسب من هذا الجدول، لا يُدخل يدويًا

CREATE TABLE pricing.tax_rates (category text, rate_bp int, valid_from date, PRIMARY KEY (category, valid_from));
-- rate_bp: نقاط أساس (1000 = 10%)

CREATE TABLE pricing.coupons (
  id uuid PRIMARY KEY, code citext UNIQUE NOT NULL,
  kind text NOT NULL,                    -- percent, fixed, free_shipping
  value bigint NOT NULL, min_subtotal bigint, section text,
  max_uses int, used_count int NOT NULL DEFAULT 0, per_customer_limit int,
  starts_at timestamptz, ends_at timestamptz, active boolean NOT NULL DEFAULT true,
  CHECK (max_uses IS NULL OR used_count <= max_uses)
);
```

## 4. الطلبات (اللقطة المجمدة)

```sql
CREATE TABLE ordering.orders (
  id uuid PRIMARY KEY,
  number text NOT NULL UNIQUE,           -- RHQ-26-000123 مقروء للبشر
  customer_id uuid, email citext NOT NULL, phone text,
  status text NOT NULL,
  currency char(3) NOT NULL,
  subtotal bigint NOT NULL, discount bigint NOT NULL, shipping bigint NOT NULL,
  tax bigint NOT NULL, total bigint NOT NULL,
  shipping_address jsonb NOT NULL, billing_address jsonb NOT NULL,
  is_gift boolean NOT NULL DEFAULT false, gift_message text, hide_prices boolean NOT NULL DEFAULT false,
  locale text NOT NULL,
  contract_doc_hash text,                -- بصمة عقد البيع عن بعد المقبول
  placed_at timestamptz, idempotency_key text UNIQUE,
  CHECK (total = subtotal - discount + shipping)  -- الضريبة ضمن الأسعار الشاملة
);

CREATE TABLE ordering.order_lines (
  id uuid PRIMARY KEY, order_id uuid NOT NULL REFERENCES ordering.orders(id),
  variant_id uuid NOT NULL, sku text NOT NULL,
  name_snapshot text NOT NULL,           -- بلغة الطلب
  warnings_snapshot text[] NOT NULL,
  unit_price bigint NOT NULL, qty int NOT NULL CHECK (qty > 0),
  tax_rate_bp int NOT NULL, line_total bigint NOT NULL,
  batch_allocations jsonb                -- [{batchId, code, qty}] بعد التثبيت
);

CREATE TABLE ordering.order_status_history (
  order_id uuid, status text, note text, actor_id uuid, at timestamptz DEFAULT now()
);
```

## 5. الدفع والشحن

```sql
CREATE TABLE payments.payments (
  id uuid PRIMARY KEY, order_id uuid NOT NULL,
  provider text NOT NULL, provider_ref text,
  method text NOT NULL,                  -- card, cod
  amount bigint NOT NULL, currency char(3) NOT NULL,
  installments int NOT NULL DEFAULT 1,
  status text NOT NULL,                  -- pending, succeeded, failed, refunded, partially_refunded
  raw_last_event jsonb, created_at timestamptz DEFAULT now()
);
CREATE TABLE payments.webhook_events (
  provider text, event_id text, received_at timestamptz DEFAULT now(), processed_at timestamptz,
  payload jsonb, PRIMARY KEY (provider, event_id)   -- يمنع معالجة الحدث مرتين
);
CREATE TABLE shipping.shipments (
  id uuid PRIMARY KEY, order_id uuid NOT NULL, carrier text NOT NULL,
  tracking_number text, label_asset_id uuid, status text NOT NULL, events jsonb
);
```

## 6. الزبائن والموافقات

```sql
CREATE TABLE customers.consents (
  customer_id uuid, channel text,        -- email, sms, call
  purpose text,                          -- marketing
  granted boolean, source text, at timestamptz, iys_synced_at timestamptz
);
```
سجل إضافة فقط: كل تغيير موافقة صف جديد، لإثبات الموافقة عند السؤال.

## 7. قواعد عامة
- الترجمات في جداول `*_translations` (إضافة لغة لا تغير المخطط).
- التزامن التفاؤلي بـ `xmin` للمنتجات والطلبات في لوحة الإدارة.
- الترحيلات كـ SQL مراجَع، تطبق قبل النشر، ومتوافقة مع النسخة السابقة.
- لا حذف فعلي للطلبات والمدفوعات والحركات أبدًا (متطلبات محاسبية).
