using System.Net;
using System.Net.Mail;

sealed class EmailNotificationService
{
    private readonly RuntimeSettings _settings;
    private readonly ILogger<EmailNotificationService> _logger;

    public EmailNotificationService(RuntimeSettings settings, ILogger<EmailNotificationService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool Send(IEnumerable<string> recipients, string subject, string body)
    {
        var targetRecipients = recipients
            .Select(r => r?.Trim() ?? string.Empty)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targetRecipients.Count == 0)
        {
            return false;
        }

        if (!IsConfigured())
        {
            return false;
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_settings.SmtpFrom),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };

            foreach (var recipient in targetRecipients)
            {
                message.To.Add(recipient);
            }

            using var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
            {
                EnableSsl = _settings.SmtpUseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            if (!string.IsNullOrWhiteSpace(_settings.SmtpUser))
            {
                client.Credentials = new NetworkCredential(_settings.SmtpUser, _settings.SmtpPassword ?? string.Empty);
            }

            client.Send(message);
            return true;
        }
        catch (SmtpFailedRecipientsException ex)
        {
            _logger.LogError(ex, "SMTP recipients rejected for email notification");
            return false;
        }
        catch (SmtpException ex)
        {
            _logger.LogError(ex, "SMTP error while sending email notification");
            return false;
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Invalid email format in notification recipients");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Invalid SMTP operation while sending notification");
            return false;
        }
    }

    private bool IsConfigured()
    {
        return !string.IsNullOrWhiteSpace(_settings.SmtpHost)
            && !string.IsNullOrWhiteSpace(_settings.SmtpFrom)
            && _settings.SmtpPort > 0;
    }
}
