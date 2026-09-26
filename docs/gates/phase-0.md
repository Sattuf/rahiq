# GATE المرحلة 0: الإعداد والقرار الكبير

> من PLAN.md: الخدمات تعمل محليًا، CI أخضر على مستودع فارغ، ADR-000 موقّع.

| البند | الحالة | الدليل |
|---|---|---|
| المستودع: `backend/` و `frontend/` و `infra/` و `docs/` | ✅ | بنية المستودع (architecture.md §3) |
| الخلفية تُبنى بلا تحذيرات (TreatWarningsAsErrors) | ✅ | `dotnet build`: 0 تحذيرات، 0 أخطاء |
| اختبارات البنية والصحة | ✅ | `dotnet test`: 3/3 ناجحة |
| الواجهة: lint و typecheck و build | ✅ | `pnpm lint && pnpm typecheck && pnpm build` |
| فحص التبعيات | ✅ | `pnpm audit`: لا ثغرات؛ `dotnet list package --vulnerable`: لا ثغرات |
| docker compose (PostgreSQL، Redis، Meilisearch، MinIO، Seq) | ⏳ مكتوب، لم يُشغَّل | Docker غير مثبت على جهاز التطوير. بعد تثبيته: `infra/scripts/dev-up.ps1` ثم `GET /health/ready` |
| CI أخضر على GitHub | ⏳ | `.github/workflows/ci.yml` جاهز، ويعمل بعد الدفع إلى `main` |
| ADR-000 موقّع | ⏳ بانتظار صاحب المشروع | [docs/adr/000-custom-build-vs-platform.md](../adr/000-custom-build-vs-platform.md) |
| حساب Sandbox لدى مزود الدفع (iyzico أو PayTR) | ⏳ بانتظار صاحب المشروع | |
| حساب Sandbox لدى شركة الشحن | ⏳ بانتظار صاحب المشروع | |
