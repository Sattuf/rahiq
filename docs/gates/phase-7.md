# GATE المرحلة 7: مجموعة التجارة (الأهم في المشروع)

> من PLAN.md: 50 زبونًا على آخر 3 قطع لا يبيعون أكثر من 3؛ webhook مكرر لا ينشئ طلبين؛ فشل الدفع يحرر الحجز؛ مجموع الطلب يطابق مجموع المزود قرشًا بقرش.

**الحالة: ✅ خضراء، 13/13 على PostgreSQL حقيقي** (`backend/tests/Rahiq.CommerceTests`، testing.md §2)

| # | الاختبار |
|---|---|
| 1 | `T01_fifty_buyers_for_the_last_three_units_buy_exactly_three` |
| 2 | `T02_an_expired_reservation_returns_the_stock_and_cancels_the_order` |
| 3 | `T03_a_webhook_delivered_five_times_confirms_invoices_and_mails_once` |
| 4 | `T04_a_forged_webhook_is_rejected_and_changes_nothing` |
| 5 | `T05_a_success_for_a_different_amount_confirms_nothing_and_alerts` |
| 6 | `T06a_a_late_payment_completes_the_order_when_stock_is_still_there` |
| 6 | `T06b_a_late_payment_is_refunded_automatically_when_the_stock_is_gone` |
| 7 | `T07_a_double_click_places_one_order` |
| 8 | `T08_a_thousand_random_carts_add_up_to_the_last_kurus` |
| 9 | `T09_a_ten_use_coupon_is_used_ten_times_under_load` |
| 10 | `T10_the_earliest_expiring_sellable_batch_ships_first` |
| 11 | `T11_an_order_keeps_its_name_price_and_documents_after_catalog_changes` |
| 12 | `T12_the_previous_price_shown_is_the_lowest_of_the_last_30_days` |

- **التشغيل:** `RAHIQ_TEST_POSTGRES=<conn> dotnet test backend/tests/Rahiq.CommerceTests`، أو بلا متغير عبر Testcontainers (يحتاج Docker).
- **في CI:** وظيفة `backend` تشغلها على خدمة postgres:17 مع كل PR.
- **ما لا تغطيه:** سلوك iyzico الحقيقي (المرحلة 5). المجموعة تعمل على مزود Sandbox داخلي يحاكي عقد الـ webhook الموقّع.
