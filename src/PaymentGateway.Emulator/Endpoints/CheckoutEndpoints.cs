using System.Globalization;
using System.Net;
using PaymentGateway.Emulator.Contracts;
using PaymentGateway.Emulator.Infrastructure;

namespace PaymentGateway.Emulator.Endpoints;

/// <summary>
/// Страница оплаты. Ключевая деталь для отчёта: реквизиты карты вводятся здесь,
/// на стороне шлюза, и в биллинг не попадают никогда — поэтому университет
/// как торгово-сервисное предприятие проходит по самой лёгкой анкете PCI DSS.
/// </summary>
public static class CheckoutEndpoints
{
    private const string SuccessCard = "4111111111111111";
    private const string DeclinedCard = "4000000000000002";

    public static void MapCheckoutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/checkout/{id}", (string id, PaymentStore store) =>
        {
            var payment = store.Find(id);
            return payment is null
                ? Results.Content(Page("Платёж не найден", "<p class=\"note\">Такой операции у шлюза нет.</p>"), "text/html; charset=utf-8", null, 404)
                : Results.Content(CheckoutPage(payment), "text/html; charset=utf-8");
        })
        .ExcludeFromDescription();

        app.MapPost("/checkout/{id}", async (string id, HttpRequest request, PaymentStore store, PaymentFlow flow) =>
        {
            var payment = store.Find(id);
            if (payment is null)
            {
                return Results.NotFound();
            }

            if (payment.Status != GatewayPaymentStatus.Pending)
            {
                return Redirect(payment);
            }

            var form = await request.ReadFormAsync();
            var card = new string(form["card"].ToString().Where(char.IsDigit).ToArray());
            switch (card)
            {
                case SuccessCard:
                    flow.Pay(payment);
                    break;
                case DeclinedCard:
                    flow.Cancel(payment, "payment_network", "insufficient_funds");
                    break;
                default:
                    return Results.Content(
                        CheckoutPage(payment, "Карта не принята. Учебный шлюз понимает только тестовые номера, они указаны ниже."),
                        "text/html; charset=utf-8");
            }

            return Redirect(payment);
        })
        .ExcludeFromDescription();

        app.MapGet("/checkout/{id}/abort", (string id, PaymentStore store, PaymentFlow flow) =>
        {
            var payment = store.Find(id);
            if (payment is null)
            {
                return Results.NotFound();
            }

            if (payment.Status == GatewayPaymentStatus.Pending)
            {
                flow.Cancel(payment, "payer", "canceled_by_payer");
            }

            return Redirect(payment);
        })
        .ExcludeFromDescription();
    }

    private static IResult Redirect(GatewayPayment payment)
    {
        if (string.IsNullOrWhiteSpace(payment.ReturnUrl))
        {
            return Results.Content(
                Page("Оплата завершена", $"<p class=\"note\">Статус операции: <b>{payment.Status}</b>. Страницу можно закрыть.</p>"),
                "text/html; charset=utf-8");
        }

        var separator = payment.ReturnUrl.Contains('?') ? "&" : "?";
        return Results.Redirect($"{payment.ReturnUrl}{separator}payment_id={WebUtility.UrlEncode(payment.Id)}");
    }

    private static string CheckoutPage(GatewayPayment payment, string? error = null)
    {
        var amount = payment.Amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
        var description = WebUtility.HtmlEncode(payment.Description ?? "Оплата");
        var errorBlock = error is null ? string.Empty : $"<div class=\"error\">{WebUtility.HtmlEncode(error)}</div>";

        var body = $"""
            <div class="amount">{amount} &#8381;</div>
            <div class="purpose">{description}</div>
            {errorBlock}
            <form method="post" action="/checkout/{payment.Id}">
              <label>Номер карты
                <input name="card" inputmode="numeric" autocomplete="off" placeholder="0000 0000 0000 0000" required>
              </label>
              <div class="row">
                <label>Срок
                  <input name="expiry" placeholder="12/29" required>
                </label>
                <label>CVC
                  <input name="cvc" placeholder="123" required>
                </label>
              </div>
              <button type="submit">Оплатить</button>
            </form>
            <a class="abort" href="/checkout/{payment.Id}/abort">Отменить и вернуться в личный кабинет</a>
            <div class="note">
              Учебный эмулятор платёжного шлюза: деньги не списываются, реквизиты карты
              никуда не передаются и не сохраняются.<br>
              Тестовые карты — <code>4111 1111 1111 1111</code> успешная оплата,
              <code>4000 0000 0000 0002</code> отказ банка.
            </div>
            """;

        return Page("Оплата обучения", body);
    }

    private static string Page(string title, string body) => $$"""
        <!doctype html>
        <html lang="ru">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{{title}}</title>
          <style>
            :root { color-scheme: light dark; }
            body { margin: 0; min-height: 100vh; display: grid; place-items: center;
                   font: 16px/1.5 "Segoe UI", system-ui, sans-serif; background: #eef1f6; color: #15181f; }
            .card { width: min(420px, 92vw); background: #fff; border-radius: 16px; padding: 28px;
                    box-shadow: 0 18px 50px rgba(20, 30, 60, .12); }
            h1 { font-size: 15px; font-weight: 600; letter-spacing: .04em; text-transform: uppercase;
                 color: #6b7280; margin: 0 0 20px; }
            .amount { font-size: 34px; font-weight: 700; }
            .purpose { color: #6b7280; margin: 4px 0 22px; }
            label { display: block; font-size: 13px; color: #6b7280; margin-bottom: 14px; }
            input { width: 100%; box-sizing: border-box; margin-top: 6px; padding: 11px 13px; font-size: 16px;
                    border: 1px solid #d3d8e0; border-radius: 9px; background: #fbfcfe; color: inherit; }
            input:focus { outline: 2px solid #2f6feb; border-color: transparent; }
            .row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
            button { width: 100%; padding: 13px; font-size: 16px; font-weight: 600; color: #fff;
                     background: #2f6feb; border: 0; border-radius: 9px; cursor: pointer; }
            button:hover { background: #2559c7; }
            .abort { display: block; text-align: center; margin: 14px 0 0; color: #6b7280; font-size: 14px; }
            .error { background: #fdecec; color: #a12020; border-radius: 9px; padding: 10px 13px;
                     margin-bottom: 16px; font-size: 14px; }
            .note { margin-top: 22px; padding-top: 18px; border-top: 1px solid #eceff4;
                    color: #8a93a3; font-size: 12.5px; }
            code { background: #f1f3f7; padding: 1px 5px; border-radius: 4px; white-space: nowrap; }
            @media (prefers-color-scheme: dark) {
              body { background: #12151c; color: #e7eaf0; }
              .card { background: #1b1f29; box-shadow: none; }
              input { background: #141822; border-color: #2c3341; }
              .note { border-color: #262c38; }
              code { background: #262c38; }
            }
          </style>
        </head>
        <body>
          <main class="card">
            <h1>Платёжный шлюз</h1>
            {{body}}
          </main>
        </body>
        </html>
        """;
}
