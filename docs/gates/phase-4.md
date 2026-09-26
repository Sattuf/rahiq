# GATE المرحلة 4: نموذج المجال

> من PLAN.md: كل قاعدة تجارية لها مكان واحد في الكود واختبار واحد على الأقل. اختبارات المال والحجز 100% تغطية.

| القاعدة | مكانها الوحيد | الاختبار |
|---|---|---|
| المال بالقرش، والتوزيع يضع الباقي في آخر سطر | `SharedKernel/Money.cs` | Domain.Tests، و T08 (1000 سلة) |
| الضريبة مشمولة في السعر | `Money.IncludedTax` | Domain.Tests |
| الحجز لا يتجاوز المتاح | `InventoryService` مع قيد `qty_reserved <= qty_on_hand` | T01، و `The_database_itself_refuses_to_reserve_more_than_is_on_hand` |
| الصرف FEFO، وأدنى صلاحية 60 يومًا | `Batch`، `InventoryService` | T10، Domain.Tests |
| آلة حالات الطلب | `Ordering/Domain/Order.cs` | Domain.Tests |
| القسيمة: حدود الاستخدام والشروط | `Pricing/Domain` (ADR-016) | T09، Domain.Tests |
| العيّنات: 3 كحد أقصى | `Cart/Domain` | Domain.Tests |
| التحذيرات والادعاءات كبيانات | `ClaimsGuard`، taxonomy.json | Domain.Tests (الجذر العربي، والتليين التركي) |
| مجموع الطلب | قيد في قاعدة البيانات (ADR-017) | T08 |

**النتيجة الحالية:** Domain.Tests 159/159، ArchitectureTests 58/58.

**قياس التغطية:** CI يجمعها (`XPlat Code Coverage`). ⏳ نسبة 100% لملفات المال والحجز تُقرأ من أول تقرير على CI. لم تُقس محليًا.
