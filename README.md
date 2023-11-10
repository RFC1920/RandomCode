# RandomCode

To send email from a plugin, you can use something like this:

```cs
    private void EmailReport(string filename = "")
    {
        if (config.mailServer?.Length > 0 && config.mailPort > 0
            && config.mailFrom?.Length > 0 && config.mailTo?.Length > 0)
        {
            SmtpClient smtpClient = new SmtpClient(config.mailServer)
            {
                Port = config.mailPort,
                EnableSsl = config.mailTLS
            };
            if (config.mailUser?.Length > 0 && config.mailPassword?.Length > 0)
            {
                smtpClient.Credentials = new NetworkCredential(config.mailUser, config.mailPassword);
            }

            MailMessage mailMessage = new MailMessage
            {
                From = new MailAddress(config.mailFrom),
                Subject = $"SUBJECT OF MESSAGE",
                Body = "<h1>See attached</h1>",
                IsBodyHtml = true
            };
            mailMessage.To.Add(config.mailTo);
            if (config.mailCc?.Length > 0)
            {
                mailMessage.CC.Add(config.mailCc);
            }
            if (filename.Length > 0)
            {
                Attachment att = new Attachment($"{filename}.json");
                mailMessage.Attachments.Add(att);
            }
            smtpClient.SendMailAsync(mailMessage);
        }
    }
```

To post some data to a site from within a plugin:

```cs
    private async void PostMessage()
    {
        Dictionary<string, string> serverRequest = new Dictionary<string, string>()
        {
            { "server", ConVar.Server.hostname },
            { "ip", ConVar.Server.ip },
            { "port", ConVar.Server.port.ToString() },
            { "queryport", ConVar.Server.queryport.ToString() }
        };
        string requestString = JsonConvert.SerializeObject(serverRequest, Formatting.Indented);

        using (HttpClient httpClient = new HttpClient())
        using (HttpRequestMessage httpReq = new HttpRequestMessage(HttpMethod.Post, "https://your.site.here/code.php"))
        {
            httpReq.Headers.Add("UserAgent", Filename);
            httpReq.Content = new StringContent(requestString, Encoding.UTF8, "application/json");
            using (HttpResponseMessage httpResponse = await httpClient.SendAsync(httpReq))
            {
                if (httpResponse?.IsSuccessStatusCode == true)
                {
                    Puts("Successful post!");
                }
            }
        }
    }
```
