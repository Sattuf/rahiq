# GATE المرحلة 5: الهيكل الماشي

> من PLAN.md: دفعة تجريبية كاملة ناجحة على Staging عبر Sandbox مزود الدفع.

| البند | الحالة | الدليل |
|---|---|---|
| المسار كاملًا محليًا: منتج ← سلة ← بطاقة (Sandbox، 3 أقساط) ← webhook ← طلب مؤكد ← بريد | ✅ | Playwright journey 1، و T03 |
| الدفع عند الاستلام | ✅ | journey 2 |
| السجلات المنظمة (Serilog إلى Seq)، وفحوص الصحة | ✅ | `/health/live`، `/health/ready`، و `Health_and_security_headers` |
| صور الإنتاج والنشر | ✅ مكتوبة | `infra/docker/*.Dockerfile`، `docker-compose.prod.yml`، `.github/workflows/release.yml`، `infra/scripts/deploy.sh` |
| **دفعة على Staging عبر iyzico Sandbox** | ⏳ | تحتاج: خادمًا، ونطاقًا، ومفاتيح iyzico Sandbox، وتشغيل Docker. المحول `IyzicoPaymentProvider` مكتوب ولم يُجرَّب مع iyzico الحقيقي |
