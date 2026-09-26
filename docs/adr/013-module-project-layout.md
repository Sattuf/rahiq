# ADR-013: مشروعان لكل وحدة بدل خمسة

- **الحالة:** مقبول. يعدّل تفصيلًا في architecture.md §3.

## القرار
لكل وحدة:
- `Rahiq.Modules.X.Contracts`: الواجهات العامة، DTOs، والأحداث التي تعبر الحدود.
- `Rahiq.Modules.X`: فيه المجلدات `Domain/` و `Application/` و `Infrastructure/` و `Presentation/`.

الحدود بين الطبقات داخل الوحدة تُفرض باختبارات NetArchTest على مستوى الـ namespace: `Domain` لا يعرف EF Core ولا ASP.NET ولا `Application`. `Application` لا يعرف `Infrastructure` ولا `Presentation`.

## لماذا
أحد عشر وحدة × خمسة مشاريع = 55 مشروعًا، وبطء بناء وضوضاء بلا مكسب لمطوّر واحد. الحماية الحقيقية (منع وحدة من لمس كيانات أخرى) باقية بمراجع المشاريع.
