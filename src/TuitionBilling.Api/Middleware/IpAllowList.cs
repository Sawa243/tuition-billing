using System.Net;

namespace TuitionBilling.Api.Middleware;

/// <summary>
/// Проверка адреса отправителя уведомления по списку сетей провайдера.
/// Вынесено из контроллера отдельно, чтобы разбор масок можно было проверить
/// тестами, не поднимая приложение.
/// </summary>
public static class IpAllowList
{
    public static bool IsAllowed(IPAddress? address, IReadOnlyList<string> allowed)
    {
        // Пустой список — проверка выключена. Так можно только при локальной разработке.
        if (allowed.Count == 0)
        {
            return true;
        }

        if (address is null)
        {
            return false;
        }

        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return allowed.Any(range => Matches(normalized, range));
    }

    public static bool Matches(IPAddress address, string range)
    {
        var slash = range.IndexOf('/');
        if (slash < 0)
        {
            return IPAddress.TryParse(range, out var single) && single.Equals(address);
        }

        if (!IPAddress.TryParse(range[..slash], out var network) || !int.TryParse(range[(slash + 1)..], out var prefix))
        {
            return false;
        }

        if (network.AddressFamily != address.AddressFamily)
        {
            return false;
        }

        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        if (prefix < 0 || prefix > networkBytes.Length * 8)
        {
            return false;
        }

        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != addressBytes[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (networkBytes[fullBytes] & mask) == (addressBytes[fullBytes] & mask);
    }
}
