# سجل القرارات المعمارية

كل قرار في ملف. التغيير = ADR جديد يلغي القديم صراحة، لا تعديل صامت.

| القرار | العنوان | الحالة |
|---|---|---|
| [ADR-000](000-custom-build-vs-platform.md) | بناء مخصص أم منصة جاهزة؟ (القرار الكبير، بصدق) | مقبول افتراضيًا بتفويض (ADR-011)، التوقيع معلّق |
| [ADR-001](001-modular-monolith.md) | Modular Monolith بمستأجر واحد | مقبول (من حزمة الخطة) |
| [ADR-002](002-nextjs-frontend.md) | Next.js للواجهة | مقبول (من حزمة الخطة) |
| [ADR-003](003-thin-controllers.md) | Controllers رقيقة | مقبول (من حزمة الخطة) |
| [ADR-004](004-postgresql-integer-money.md) | PostgreSQL، والمال أعداد صحيحة | مقبول (من حزمة الخطة) |
| [ADR-005](005-hosted-payment-form.md) | نموذج دفع مستضاف لدى المزود | مقبول (من حزمة الخطة) |
| [ADR-006](006-signed-webhook-is-truth.md) | الإشعار الموقّع مصدر حقيقة الدفع | مقبول (من حزمة الخطة) |
| [ADR-007](007-reserve-at-checkout.md) | الحجز عند الدفع لا عند الإضافة للسلة | مقبول (من حزمة الخطة) |
| [ADR-008](008-meilisearch.md) | Meilisearch للبحث | مقبول (من حزمة الخطة) |
| [ADR-009](009-mediator-library.md) | مكتبة الوسيط | مقبول (من حزمة الخطة) |
| [ADR-010](010-warnings-as-data.md) | التحذيرات والادعاءات كبيانات مقيدة | مقبول (من حزمة الخطة) |
| [ADR-011](011-delegated-defaults.md) | القرارات المفوّضة (الافتراضات المعتمدة للبدء) | مقبول مؤقتًا |
| [ADR-012](012-composed-dbcontext-sql-migrations.md) | DbContext واحد مُركّب وترحيلات SQL | مقبول |
| [ADR-013](013-module-project-layout.md) | مشروعان لكل وحدة | مقبول |
| [ADR-014](014-auth-bff-otp.md) | المصادقة: OTP للزبائن، TOTP للإدارة، BFF | مقبول |
| [ADR-015](015-contracts-as-hashed-html.md) | العقود كمستند HTML مجمّد ببصمة | مقبول |
| [ADR-016](016-coupon-held-at-checkout.md) | حجز استخدام القسيمة عند الدفع | مقبول |
| [ADR-017](017-order-fees-and-leaf-lines.md) | رسم الاستلام والأسطر الورقية | مقبول |
| [ADR-018](018-frontend-runtime-choices.md) | ترجمة داخلية، CSP بـ nonce، وميزانية الأداء | مقبول، وبند الميزانية مفتوح |
| [ADR-019](019-conversations-and-agent.md) | المحادثات والقنوات والمساعد الذكي | مقبول |
