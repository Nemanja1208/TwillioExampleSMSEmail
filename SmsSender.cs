using System;
using System.Collections.Generic;
using System.Text;

namespace Flashy
{
    using Twilio;
    using Twilio.Rest.Api.V2010.Account;
    using Twilio.Types;

    public sealed class SmsSender
    {
        private readonly PhoneNumber _from;

        public SmsSender(string accountSid, string authToken, string fromNumber)
        {
            TwilioClient.Init(accountSid, authToken);
            _from = new PhoneNumber(fromNumber);
        }

        public async Task<string> SendAsync(string to, string message)
        {
            var sms = await MessageResource.CreateAsync(
                to: new PhoneNumber(to),
                from: _from,
                body: message);

            return sms.Sid;
        }
    }
}
