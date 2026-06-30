namespace nexthire_api.Helpers;

public static class MessengerFunctionUrlHelper
{
  public static string GetMessengerBaseUrl(IConfiguration configuration)
  {
    var whatsAppUrl = configuration["MessengerFunction:WhatsAppUrl"]
      ?? configuration["MessengerFunction:Url"];

    if (string.IsNullOrWhiteSpace(whatsAppUrl))
      throw new InvalidOperationException("MessengerFunction WhatsApp URL is not configured.");

    if (!Uri.TryCreate(whatsAppUrl.Trim(), UriKind.Absolute, out var uri))
      throw new InvalidOperationException("MessengerFunction WhatsApp URL is not a valid absolute URI.");

    return $"{uri.Scheme}://{uri.Authority}";
  }

  public static string GetWhatsAppRoutesUrl(IConfiguration configuration)
  {
    return $"{GetMessengerBaseUrl(configuration).TrimEnd('/')}/api/messages/whatsapp/routes";
  }
}
