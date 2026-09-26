# GATE المرحلة 1: الأساس التجاري والقانوني

> من PLAN.md: قائمة الامتثال مكتملة، أو كل بند فيها له موعد محدد ومسؤول. لا إطلاق قبل اكتمالها.

**الحالة: ⏳ مفتوحة. بيد صاحب المشروع والمحاسب والمحامي، لا بيد الكود.**

القائمة بـ 25 بندًا في [../compliance-checklist.md](../compliance-checklist.md).

ما جهّزه الكود ليعمل فور اكتمال الأوراق:

| البند القانوني | ما في الكود | الدليل |
|---|---|---|
| عقد البيع عن بعد، والمعلومات المسبقة | يُولَّدان ويُجمَّدان في الطلب ببصمة SHA-256 (ADR-015) | `Documents/Templates/ContractTexts.cs`، و T11 |
| بيانات البائع (MERSİS، ETBİS، KEP، الضريبة) | إعدادات `Seller__*`، والنصوص معلَّمة "مسودة" حتى `DraftLegalTexts=false` | `infra/compose/prod.env.example` |
| حق العدول، واستثناء الأغذية المفتوحة | منطق الإرجاع، ومختبر | `Honey_cannot_be_returned_on_withdrawal…` |
| السعر السابق = أدنى سعر خلال 30 يومًا | محسوب من تاريخ الأسعار، لا يُكتب يدويًا | T12 |
| KVKK: موافقة الكوكيز، وتصدير البيانات | لا تحليلات قبل الموافقة، و `GET /api/me/export` | `CookieBanner`، Customers |
| الادعاءات العلاجية الممنوعة | `ClaimsGuard` بثلاث لغات يمنع النشر | `Admin_needs_a_second_factor_and_forbidden_claims_block_publishing` |
| الفاتورة الإلكترونية (e-Arşiv) | واجهة مزود مع بديل Sandbox | `Documents/Application/InvoicingApplication.cs` |

**⏳ لإغلاق هذه البوابة:** يكتب صاحب المشروع لكل بند في compliance-checklist.md المسؤول والموعد.
