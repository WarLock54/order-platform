namespace OrderPlatform.PaymentService.Infrastructure;

/// <summary>
/// Gerçek bir dış ödeme sağlayıcısı (Stripe/iyzico/PayTR vb.) entegre
/// edilseydi kullanılacak typed client. C# raporunun risk #4 bulgusu --
/// "new HttpClient()" ile doğrudan client oluşturma anti-pattern'i -- burada
/// IHttpClientFactory + Polly retry/circuit-breaker policy'leriyle (bkz.
/// Program.cs AddHttpClient&lt;PaymentGatewayClient&gt;) baştan doğru
/// kuruluyor.
///
/// NOT: Bu portföy projesinde gerçek bir dış ödeme sağlayıcısı yok (Proje
/// 1'deki payment-service de aynı şekilde ödemeyi "simüle" ediyordu); bu
/// sınıf sadece doğru DI/resilience kurulumunu göstermek için var,
/// ProcessPaymentConsumer şu an bunu çağırmıyor. Gerçek bir entegrasyonda
/// tek yapılması gereken, consumer içindeki "ödemeyi simüle et" satırını
/// `await gatewayClient.ChargeAsync(...)` ile değiştirmek olurdu.
/// </summary>
public class PaymentGatewayClient
{
    private readonly HttpClient _httpClient;

    public PaymentGatewayClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<bool> ChargeAsync(Guid customerId, long amountCents, string currency, CancellationToken ct)
    {
        // Gerçek entegrasyonda: await _httpClient.PostAsJsonAsync("/charges", ...)
        // Polly policy'leri (Program.cs'te AddHttpClient'a eklenen) bu
        // çağrının başarısız olması durumunda otomatik retry/circuit
        // breaker uygular -- Proje 1'deki pkg/resilience'ın .NET karşılığı.
        return Task.FromResult(true);
    }
}
