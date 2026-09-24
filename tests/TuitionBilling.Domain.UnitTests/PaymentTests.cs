namespace TuitionBilling.Domain.UnitTests;

public class PaymentTests
{
    private static Payment NewPayment(decimal amount = 40_000m) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), amount, Guid.NewGuid().ToString(), "Оплата обучения, 1 семестр");

    private static Payment SucceededPayment(decimal amount = 40_000m)
    {
        var payment = NewPayment(amount);
        payment.ApplyStatus(PaymentStatus.Succeeded, DateTimeOffset.UtcNow);
        return payment;
    }

    [Theory]
    [InlineData(PaymentStatus.WaitingForCapture)]
    [InlineData(PaymentStatus.Succeeded)]
    [InlineData(PaymentStatus.Canceled)]
    public void ApplyStatus_FromPending_IsApplied(PaymentStatus next)
    {
        var payment = NewPayment();

        var outcome = payment.ApplyStatus(next, DateTimeOffset.UtcNow);

        Assert.Equal(PaymentTransitionOutcome.Applied, outcome);
        Assert.Equal(next, payment.Status);
    }

    [Fact]
    public void ApplyStatus_SameStatusTwice_IsRecognisedAsDuplicate()
    {
        var payment = SucceededPayment();

        var outcome = payment.ApplyStatus(PaymentStatus.Succeeded, DateTimeOffset.UtcNow);

        Assert.Equal(PaymentTransitionOutcome.AlreadyInThatState, outcome);
    }

    // Из-за повторной доставки уведомление может прийти не по порядку.
    // Откатывать конечный статус нельзя, но и падать тут не за что.
    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.WaitingForCapture)]
    [InlineData(PaymentStatus.Canceled)]
    public void ApplyStatus_AfterTerminalStatus_IsIgnored(PaymentStatus late)
    {
        var payment = SucceededPayment();

        var outcome = payment.ApplyStatus(late, DateTimeOffset.UtcNow);

        Assert.Equal(PaymentTransitionOutcome.Ignored, outcome);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
    }

    [Fact]
    public void ApplyStatus_Succeeded_RecordsCaptureTime()
    {
        var payment = NewPayment();
        var moment = new DateTimeOffset(2026, 9, 20, 10, 15, 0, TimeSpan.Zero);

        payment.ApplyStatus(PaymentStatus.Succeeded, moment);

        Assert.Equal(moment, payment.CapturedAt);
    }

    [Fact]
    public void ApplyStatus_Canceled_KeepsReason()
    {
        var payment = NewPayment();

        payment.ApplyStatus(PaymentStatus.Canceled, DateTimeOffset.UtcNow, "Недостаточно средств");

        Assert.Equal("Недостаточно средств", payment.CancellationReason);
    }

    [Fact]
    public void AttachToGateway_Twice_Throws()
    {
        var payment = NewPayment();
        payment.AttachToGateway("pay_1", "http://localhost/checkout/pay_1");

        Assert.Throws<DomainException>(() => payment.AttachToGateway("pay_2", "http://localhost/checkout/pay_2"));
    }

    [Fact]
    public void StartRefund_OnPendingPayment_Throws()
    {
        var payment = NewPayment();

        Assert.Throws<DomainException>(() => payment.StartRefund(1_000m, "Отчисление", Guid.NewGuid().ToString()));
    }

    [Fact]
    public void StartRefund_AboveRefundableAmount_Throws()
    {
        var payment = SucceededPayment();
        payment.RegisterRefund(30_000m);

        Assert.Throws<DomainException>(() => payment.StartRefund(10_000.01m, "Отчисление", Guid.NewGuid().ToString()));
    }

    [Fact]
    public void RegisterRefund_ReducesRefundableAmount()
    {
        var payment = SucceededPayment();

        payment.RegisterRefund(15_000m);

        Assert.Equal(25_000m, payment.RefundableAmount);
    }

    [Fact]
    public void Constructor_WithoutIdempotencyKey_Throws()
    {
        Assert.Throws<DomainException>(() =>
            new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m, " ", "Оплата"));
    }
}
