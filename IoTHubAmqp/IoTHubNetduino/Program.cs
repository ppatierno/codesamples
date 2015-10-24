using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using SecretLabs.NETMF.Hardware;
using SecretLabs.NETMF.Hardware.Netduino;
using Amqp;
using Amqp.Framing;
using System.Text;

namespace IoTHubAmqp
{
    public class Program
    {
        private const string HOST = "[IOT_HUB_NAME].azure-devices.net";
        private const int PORT = 5671;
        private const string DEVICE_ID = "[DEVICE_ID]";
        private const string DEVICE_KEY = "[DEVICE_KEY]";

        private static Address address;
        private static Connection connection;
        private static Session session;

        private static Thread receiverThread;

        private static AutoResetEvent networkAvailableEvent = new AutoResetEvent(false);
        private static AutoResetEvent networkAddressChangedEvent = new AutoResetEvent(false);

        public static void Main()
        {
            // write your code here

            // wait for DHCP-allocated IP address
            // while (IPAddress.GetDefaultLocalAddress() == IPAddress.Any) ;

            // wait for network connectivity
            // while (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) ;

            Microsoft.SPOT.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged;
            Microsoft.SPOT.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged;

            networkAvailableEvent.WaitOne();
            Debug.Print("link is up!");
            networkAddressChangedEvent.WaitOne();
            Debug.Print("address acquired: " + Microsoft.SPOT.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()[0].IPAddress);

            Debug.Print("\r\n*** GET NETWORK INTERFACE SETTINGS ***");
            Microsoft.SPOT.Net.NetworkInformation.NetworkInterface[] networkInterfaces = Microsoft.SPOT.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            Debug.Print("Found " + networkInterfaces.Length + " network interfaces.");

            // get date/time via NTP
            DateTime dateTime = MFToolkit.Net.Ntp.NtpClient.GetNetworkTime();
            Utility.SetLocalTime(dateTime);

            Amqp.Trace.TraceLevel = Amqp.TraceLevel.Frame | Amqp.TraceLevel.Verbose;
#if NETMF
            Amqp.Trace.TraceListener = (f, a) => Debug.Print(DateTime.Now.ToString("[hh:ss.fff]") + " " + Fx.Format(f, a));
#else
            Amqp.Trace.TraceListener = (f, a) => System.Diagnostics.Trace.WriteLine(DateTime.Now.ToString("[hh:ss.fff]") + " " + Fx.Format(f, a));
#endif
            address = new Address(HOST, PORT, null, null);
            connection = new Connection(address);
            
            string audience = Fx.Format("/devices/{0}/events", DEVICE_ID);
            string resourceUri = Fx.Format("{0}/devices/{1}", HOST, DEVICE_ID);

            string sasToken = GetSharedAccessSignature(null, DEVICE_KEY, resourceUri, new TimeSpan(1, 0, 0));
            bool cbs = PutCbsToken(connection, HOST, sasToken, audience);

            if (cbs)
            {
                session = new Session(connection);

                SendEvent();
                receiverThread = new Thread(ReceiveCommands);
                receiverThread.Start();
            }

            // just as example ...
            // the application ends only after received a command or timeout on receiving
            receiverThread.Join();

            session.Close();
            connection.Close();

            Thread.Sleep(Timeout.Infinite);
        }

        static void NetworkChange_NetworkAddressChanged(object sender, EventArgs e)
        {
            Debug.Print("NetworkAddressChanged");
            networkAddressChangedEvent.Set();
        }

        static void NetworkChange_NetworkAvailabilityChanged(object sender, Microsoft.SPOT.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
        {
            Debug.Print("NetworkAvailabilityChanged " + e.IsAvailable);
            if (e.IsAvailable)
            {
                networkAvailableEvent.Set();
            }
        }

        static private void SendEvent()
        {
            string entity = Fx.Format("/devices/{0}/messages/events", DEVICE_ID);

            SenderLink senderLink = new SenderLink(session, "sender-link", entity);

            var messageValue = Encoding.UTF8.GetBytes("i am a message.");
            Message message = new Message()
            {
                BodySection = new Data() { Binary = messageValue }
            };

            senderLink.Send(message);
            senderLink.Close();
        }

        static private void ReceiveCommands()
        {
            string entity = Fx.Format("/devices/{0}/messages/deviceBound", DEVICE_ID);

            ReceiverLink receiveLink = new ReceiverLink(session, "receive-link", entity);

            Message received = receiveLink.Receive();
            if (received != null)
                receiveLink.Accept(received);

            receiveLink.Close();
        }

        static private bool PutCbsToken(Connection connection, string host, string shareAccessSignature, string audience)
        {
            bool result = true;
            Session session = new Session(connection);

            string cbsReplyToAddress = "cbs-reply-to";
            var cbsSender = new SenderLink(session, "cbs-sender", "$cbs");
            var cbsReceiver = new ReceiverLink(session, cbsReplyToAddress, "$cbs");

            // construct the put-token message
            var request = new Message(shareAccessSignature);
            request.Properties = new Properties();
            request.Properties.MessageId = Guid.NewGuid().ToString();
            request.Properties.ReplyTo = cbsReplyToAddress;
            request.ApplicationProperties = new ApplicationProperties();
            request.ApplicationProperties["operation"] = "put-token";
            request.ApplicationProperties["type"] = "azure-devices.net:sastoken";
            request.ApplicationProperties["name"] = audience;
            cbsSender.Send(request);

            // receive the response
            var response = cbsReceiver.Receive();
            if (response == null || response.Properties == null || response.ApplicationProperties == null)
            {
                result = false;
            }
            else
            {
                int statusCode = (int)response.ApplicationProperties["status-code"];
                string statusCodeDescription = (string)response.ApplicationProperties["status-description"];
                if (statusCode != (int)202 && statusCode != (int)200) // !Accepted && !OK
                {
                    result = false;
                }
            }

            // the sender/receiver may be kept open for refreshing tokens
            cbsSender.Close();
            cbsReceiver.Close();
            session.Close();

            return result;
        }

        private static readonly long UtcReference = (new DateTime(1970, 1, 1, 0, 0, 0, 0)).Ticks;

        static string GetSharedAccessSignature(string keyName, string sharedAccessKey, string resource, TimeSpan tokenTimeToLive)
        {
            // http://msdn.microsoft.com/en-us/library/azure/dn170477.aspx
            // the canonical Uri scheme is http because the token is not amqp specific
            // signature is computed from joined encoded request Uri string and expiry string

#if NETMF
            // needed in .Net Micro Framework to use standard RFC4648 Base64 encoding alphabet
            System.Convert.UseRFC4648Encoding = true;
#endif
            string expiry = ((long)(DateTime.UtcNow - new DateTime(UtcReference, DateTimeKind.Utc) + tokenTimeToLive).TotalSeconds()).ToString();
            string encodedUri = HttpUtility.UrlEncode(resource);

            byte[] hmac = SHA.computeHMAC_SHA256(Convert.FromBase64String(sharedAccessKey), Encoding.UTF8.GetBytes(encodedUri + "\n" + expiry));
            string sig = Convert.ToBase64String(hmac);

            if (keyName != null)
            {
                return Fx.Format(
                "SharedAccessSignature sr={0}&sig={1}&se={2}&skn={3}",
                encodedUri,
                HttpUtility.UrlEncode(sig),
                HttpUtility.UrlEncode(expiry),
                HttpUtility.UrlEncode(keyName));
            }
            else
            {
                return Fx.Format(
                    "SharedAccessSignature sr={0}&sig={1}&se={2}",
                    encodedUri,
                    HttpUtility.UrlEncode(sig),
                    HttpUtility.UrlEncode(expiry));
            }
        }
    }
}
