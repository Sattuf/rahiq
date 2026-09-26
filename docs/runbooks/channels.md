# إعداد قنوات المحادثة والمساعد الذكي

> التصميم في [ADR-019](../adr/019-conversations-and-agent.md). كل الأسرار في `/etc/rahiq/prod.env` (انظر `infra/compose/prod.env.example`)، لا في git.

## 0. المساعد (Claude)
1. أنشئ مفتاح API في console.anthropic.com، وضعه في `Agent__ApiKey` (أو `ANTHROPIC_API_KEY`)، و `Agent__Enabled=true`.
2. بدون مفتاح: كل محادثة جديدة تذهب مباشرة لقائمة "بانتظار موظف". لا يضيع شيء.
3. للتجربة محليًا: `ANTHROPIC_API_KEY=... dotnet run --project src/Rahiq.Api` (المساعد مفعّل في Development).

## 1. تيليجرام (ابدأ به: مجاني وفوري)
1. في تيليجرام افتح `@BotFather` ← `/newbot` ← انسخ التوكن إلى `Channels__Telegram__BotToken`.
2. ولّد سرًا عشوائيًا (32 حرفًا من A-Z a-z 0-9 _ -) في `Channels__Telegram__WebhookSecret`.
3. تأكد أن `Store__PublicApiUrl` عنوان HTTPS عام، ثم: `dotnet Rahiq.Api.dll telegram-webhook` (أو `docker compose run --rm api telegram-webhook`).
4. محليًا تحتاج نفقًا عامًا (مثل `cloudflared tunnel --url http://localhost:5080`) وضع عنوانه في `Store:PublicApiUrl` قبل الخطوة 3.
5. اكتب للبوت. تظهر المحادثة في **المحادثات** في لوحة الإدارة خلال ثانية.

## 2. ماسنجر وإنستغرام (webhook واحد: `/webhooks/meta`)
1. **ابدأ التوثيق في Meta Business مبكرًا** (Business Verification + App Review لصلاحيات `pages_messaging` و `instagram_manage_messages`، و `human_agent` إن أردت الرد حتى 7 أيام). قد يأخذ أسابيع.
2. في developers.facebook.com: تطبيق من نوع Business. انسخ **App Secret** إلى `Channels__Meta__AppSecret`.
3. اختر `Channels__Meta__VerifyToken` عشوائيًا.
4. Webhooks ← Page: العنوان `https://api.../webhooks/meta`، ورمز التحقق نفسه، والاشتراك في `messages` و `messaging_postbacks`. كرّر لـ Instagram.
5. رمز صفحة دائم (System User) في `Channels__Meta__PageAccessToken`. لإنستغرام رمز خاص اختياري في `InstagramAccessToken`.

## 3. واتساب (`/webhooks/whatsapp`)
1. في نفس التطبيق (أو تطبيق آخر): أضف WhatsApp، وسجّل رقمًا، وانسخ **Phone number ID** إلى `Channels__WhatsApp__PhoneNumberId`.
2. رمز System User دائم بصلاحية `whatsapp_business_messaging` في `Channels__WhatsApp__AccessToken`، والـ App Secret في `Channels__WhatsApp__AppSecret`.
3. Webhook: `https://api.../webhooks/whatsapp` مع `Channels__WhatsApp__VerifyToken`، واشترك في `messages`.
4. **الرسوم والنافذة:** واتساب يحسب الرسوم على المحادثات. الرد الحر مسموح فقط خلال 24 ساعة من آخر رسالة للعميل. بعدها يُغلق مربع الرد في اللوحة ويجب أن يكتب العميل أولًا.

## 4. لوحة المحادثات
- القائمة: **بانتظار موظف** = سلّمها المساعد ولم يستلمها أحد. ابدأ بها.
- **استلام المحادثة**: يسكت المساعد في هذه المحادثة. الرد من الموظف يستلمها تلقائيًا أيضًا.
- **إعادتها للمساعد**: يجيب المساعد عن الرسائل القادمة فقط، لا عن القديمة.
- "مباشر" بجانب العنوان يعني أن التحديث لحظي؛ إن ظهر "جارٍ إعادة الاتصال" فالصفحة تتحدث كل 15 ثانية.
- الصلاحيات: `conversations.view` و `conversations.reply` (المالك والمدير والدعم). **على الموظفين الحاليين تسجيل الدخول من جديد** ليحصلوا عليها.

## 5. عند المشاكل
| العرض | السبب المرجح |
|---|---|
| الـ webhook يرد 404 | أسرار القناة غير مضبوطة في البيئة |
| 401 وسجل "bad signature" | App Secret أو سر تيليجرام خطأ |
| رسائل "Not delivered: … window is closed" | انتهت نافذة 24 ساعة (واتساب/ماسنجر/إنستغرام) |
| "graph 400 code 10/200" | صلاحية التطبيق غير معتمدة من Meta بعد |
| كل المحادثات تذهب للموظفين بسبب `agent_disabled` | `Agent__Enabled` أو المفتاح ناقص |
| `agent_unavailable` | فشل الاتصال بـ Claude 4 مرات؛ راجع السجلات في Seq |
