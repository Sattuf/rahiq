# ADR-012: DbContext واحد مُركّب، ومخطط لكل وحدة، وترحيلات SQL

- **الحالة:** مقبول

## السياق
الدفع يلمس عدة وحدات في لحظة واحدة: حجز المخزون (Inventory)، وإنشاء الطلب (Ordering)، وحجز استخدام القسيمة (Pricing)، وسجل الدفع (Payments). الخطة تطلب أن يتم الحجز "في معاملة واحدة"، وأن تكون الترحيلات "SQL مراجَعًا".

## القرار
- `RahiqDbContext` واحد في `Rahiq.Infrastructure.Common`. كل وحدة تساهم بتهيئة كياناتها عبر `IModelContributor`، وكل جدول في مخطط وحدته (`catalog.*`، `inventory.*`...).
- **العزل بالمشاريع لا بالـ DbContext:** مشروع الوحدة لا يرجع إلا إلى `*.Contracts` للوحدات الأخرى، فلا يستطيع لمس كياناتها. اختبارات البنية تفرض ذلك.
- معاملة واحدة لكل Command (سلوك `TransactionBehavior`)، ويشارك فيها SQL الخام (Dapper) للحجز الذري.
- الترحيلات ملفات SQL مرقمة في `backend/db/migrations`، يطبقها `SqlMigrator` بقفل استشاري (advisory lock) قبل النشر (`dotnet Rahiq.Api.dll migrate`). لا EF Migrations.

## المرفوض
- DbContext لكل وحدة: يتطلب معاملات موزعة أو Saga لكل عملية دفع، وهو تعقيد بلا فائدة لمتجر واحد.
- EF Migrations: الخطة تطلب SQL مراجعًا، وقيود CHECK والفهارس الجزئية أوضح في SQL.
