using System.Globalization;
using Rahiq.Modules.Notifications.Infrastructure;

namespace Rahiq.Modules.Notifications.Application;

/// <summary>Transactional e-mails in the customer's language, in the brand voice: warm, exact, no filler.</summary>
internal static class EmailTemplates
{
    private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
    {
        ["tr"] = new()
        {
            ["otp.subject"] = "Giriş kodunuz: {0}",
            ["otp.body"] = "Rahiq'e giriş kodunuz <strong style=\"font-size:22px;letter-spacing:4px\">{0}</strong>. Kod 10 dakika geçerlidir. Bu isteği siz yapmadıysanız bu e-postayı yok sayabilirsiniz.",
            ["confirmed.subject"] = "Siparişiniz alındı · {0}",
            ["confirmed.body"] = "Teşekkürler. {0} numaralı siparişiniz onaylandı; bir iş günü içinde hazırlayıp kargoya veriyoruz. Ön bilgilendirme formu ve mesafeli satış sözleşmesi ektedir.",
            ["cod.note"] = "Ödemeyi kapıda yapacaksınız: {0}.",
            ["cancelled.subject"] = "Siparişiniz iptal edildi · {0}",
            ["cancelled.body"] = "{0} numaralı siparişiniz iptal edildi. Ödeme alındıysa tutarın tamamı kartınıza iade edilir; bankanıza göre birkaç gün sürebilir.",
            ["cancelled.unpaid"] = "{0} numaralı siparişinizin ödemesi tamamlanmadı, bu yüzden sipariş iptal edildi. Sepetinizdeki ürünleri yeniden deneyebilirsiniz.",
            ["shipped.subject"] = "Siparişiniz yolda · {0}",
            ["shipped.body"] = "{0} numaralı siparişiniz kargoya verildi. Takip numarası: <strong>{1}</strong>.",
            ["delivered.subject"] = "Teslim edildi · {0}",
            ["delivered.body"] = "{0} numaralı siparişiniz teslim edildi. Bal zamanla kristalleşebilir; bu doğaldır ve bozulduğu anlamına gelmez.",
            ["return.subject"] = "İade talebiniz · {0}",
            ["return.requested"] = "{0} numaralı sipariş için iade talebinizi aldık. Bir iş günü içinde size döneceğiz.",
            ["return.resolved"] = "{0} numaralı siparişteki iade talebinizin durumu: {1}.",
            ["refund.subject"] = "İadeniz yapıldı · {0}",
            ["refund.body"] = "{0} numaralı sipariş için {1} iade edildi.",
            ["invoice.subject"] = "e-Arşiv faturanız · {0}",
            ["invoice.body"] = "{0} numaralı siparişinizin faturası kesildi: {1}. Hesabınızdan veya sipariş takip sayfasından indirebilirsiniz.",
            ["track"] = "Siparişimi görüntüle",
            ["footer"] = "Bu e-posta siparişinizle ilgili bilgilendirmedir.",
        },
        ["ar"] = new()
        {
            ["otp.subject"] = "رمز الدخول: {0}",
            ["otp.body"] = "رمز الدخول إلى رحيق هو <strong style=\"font-size:22px;letter-spacing:4px\">{0}</strong>. صالح لمدة 10 دقائق. إن لم تطلبه فتجاهل هذه الرسالة.",
            ["confirmed.subject"] = "وصلنا طلبك · {0}",
            ["confirmed.body"] = "شكرًا لك. تأكد طلبك رقم {0}، ونجهّزه ونسلمه للشحن خلال يوم عمل. مرفق نموذج المعلومات المسبقة وعقد البيع عن بعد.",
            ["cod.note"] = "ستدفع عند الاستلام: {0}.",
            ["cancelled.subject"] = "أُلغي طلبك · {0}",
            ["cancelled.body"] = "أُلغي طلبك رقم {0}. إن كان المبلغ قد دُفع فسيعاد كاملًا إلى بطاقتك، وقد يستغرق ذلك بضعة أيام حسب البنك.",
            ["cancelled.unpaid"] = "لم يكتمل الدفع للطلب رقم {0} فأُلغي. يمكنك المحاولة مجددًا من السلة.",
            ["shipped.subject"] = "طلبك في الطريق · {0}",
            ["shipped.body"] = "سُلّم طلبك رقم {0} لشركة الشحن. رقم التتبع: <strong>{1}</strong>.",
            ["delivered.subject"] = "تم التسليم · {0}",
            ["delivered.body"] = "وصل طلبك رقم {0}. قد يتبلور العسل مع الوقت، وهذا طبيعي ولا يعني أنه فسد.",
            ["return.subject"] = "طلب الإرجاع · {0}",
            ["return.requested"] = "وصلنا طلب الإرجاع للطلب رقم {0}، وسنرد عليك خلال يوم عمل.",
            ["return.resolved"] = "حالة طلب الإرجاع للطلب رقم {0}: {1}.",
            ["refund.subject"] = "تمت إعادة المبلغ · {0}",
            ["refund.body"] = "أُعيد {1} عن الطلب رقم {0}.",
            ["invoice.subject"] = "فاتورتك الإلكترونية · {0}",
            ["invoice.body"] = "صدرت فاتورة الطلب رقم {0}: {1}. يمكنك تنزيلها من حسابك أو صفحة تتبع الطلب.",
            ["track"] = "عرض طلبي",
            ["footer"] = "هذه رسالة معلومات تخص طلبك.",
        },
        ["en"] = new()
        {
            ["otp.subject"] = "Your sign-in code: {0}",
            ["otp.body"] = "Your Rahiq sign-in code is <strong style=\"font-size:22px;letter-spacing:4px\">{0}</strong>. It is valid for 10 minutes. If you did not ask for it, ignore this e-mail.",
            ["confirmed.subject"] = "We have your order · {0}",
            ["confirmed.body"] = "Thank you. Order {0} is confirmed; we prepare it and hand it to the carrier within one business day. The pre-contractual information form and the distance sales contract are attached.",
            ["cod.note"] = "You will pay on delivery: {0}.",
            ["cancelled.subject"] = "Your order was cancelled · {0}",
            ["cancelled.body"] = "Order {0} was cancelled. If you were charged, the full amount goes back to your card; depending on your bank this can take a few days.",
            ["cancelled.unpaid"] = "The payment for order {0} was not completed, so the order was cancelled. You can try again from your cart.",
            ["shipped.subject"] = "Your order is on its way · {0}",
            ["shipped.body"] = "Order {0} is with the carrier. Tracking number: <strong>{1}</strong>.",
            ["delivered.subject"] = "Delivered · {0}",
            ["delivered.body"] = "Order {0} was delivered. Honey may crystallise over time; that is natural and does not mean it has spoiled.",
            ["return.subject"] = "Your return · {0}",
            ["return.requested"] = "We received your return request for order {0} and will reply within one business day.",
            ["return.resolved"] = "Your return for order {0} is now: {1}.",
            ["refund.subject"] = "Refund issued · {0}",
            ["refund.body"] = "{1} was refunded for order {0}.",
            ["invoice.subject"] = "Your e-invoice · {0}",
            ["invoice.body"] = "The invoice for order {0} was issued: {1}. You can download it from your account or the order tracking page.",
            ["track"] = "View my order",
            ["footer"] = "This is a service message about your order.",
        },
    };

    public static string T(string locale, string key, params object[] args)
    {
        var table = Strings.GetValueOrDefault(locale) ?? Strings["tr"];
        var template = table.GetValueOrDefault(key) ?? Strings["tr"][key];
        return string.Format(CultureInfo.InvariantCulture, template, args.Select(a => a is string s ? Html.E(s) : a).ToArray());
    }

    public static string Money(long minor, string currency) => $"{(minor / 100m).ToString("N2", CultureInfo.InvariantCulture)} {currency}";

    /// <summary>One layout for every e-mail: stone background, amber ink, the drop accent on the single button.</summary>
    public static string Layout(string locale, string bodyHtml, string? buttonUrl = null)
    {
        var dir = locale == "ar" ? "rtl" : "ltr";
        var button = buttonUrl is null ? string.Empty
            : $"<p style=\"margin:28px 0\"><a href=\"{Html.E(buttonUrl)}\" style=\"background:#8A5412;color:#fff;text-decoration:none;padding:12px 22px;border-radius:10px;display:inline-block\">{T(locale, "track")}</a></p>";
        return $"""
            <!doctype html><html lang="{locale}" dir="{dir}"><meta charset="utf-8">
            <body style="margin:0;background:#ECE9E3;font-family:system-ui,-apple-system,Segoe UI,sans-serif;color:#2A1F17">
            <div style="max-width:560px;margin:0 auto;padding:32px 20px">
            <p style="font-size:22px;letter-spacing:.08em;margin:0 0 24px">RAHIQ · رحيق</p>
            <div style="background:#fff;border-radius:4px;padding:28px;line-height:1.7">{bodyHtml}{button}</div>
            <p style="font-size:12px;color:#6E655C;margin-top:20px">{T(locale, "footer")}</p>
            </div></body></html>
            """;
    }
}
