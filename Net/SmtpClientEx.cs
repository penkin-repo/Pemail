using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Pemail.Models;

namespace Pemail.Net
{
    public class SmtpClientEx
    {
        public static byte[] BuildMimeMessage(string fromDisplay, string fromEmail, string toList, string ccList, string subject, string textBody, string inReplyTo, string references, List<string> attachmentPaths)
        {
            string boundary = "----Pemail_" + Guid.NewGuid().ToString("N");
            string msgId = "<" + Guid.NewGuid().ToString("N") + "@pemail.local>";
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("From: {0} <{1}>\r\n", EncodeHeader(fromDisplay), fromEmail);
            sb.AppendFormat("To: {0}\r\n", toList.Trim());
            if (!string.IsNullOrWhiteSpace(ccList)) sb.AppendFormat("Cc: {0}\r\n", ccList.Trim());
            sb.AppendFormat("Subject: {0}\r\n", EncodeHeader(subject ?? string.Empty));
            DateTimeOffset now = DateTimeOffset.Now;
            string tzSign = now.Offset < TimeSpan.Zero ? "-" : "+";
            TimeSpan tzAbs = now.Offset.Duration();
            string rfcDate = now.ToString("ddd, dd MMM yyyy HH:mm:ss ", System.Globalization.CultureInfo.InvariantCulture)
                + tzSign + tzAbs.Hours.ToString("D2") + tzAbs.Minutes.ToString("D2");
            sb.AppendFormat("Date: {0}\r\n", rfcDate);
            sb.AppendFormat("Message-ID: {0}\r\n", msgId);
            if (!string.IsNullOrWhiteSpace(inReplyTo)) sb.AppendFormat("In-Reply-To: {0}\r\n", inReplyTo.Trim());
            if (!string.IsNullOrWhiteSpace(references)) sb.AppendFormat("References: {0}\r\n", references.Trim());
            sb.Append("MIME-Version: 1.0\r\n");

            bool hasAtt = attachmentPaths != null && attachmentPaths.Count > 0;
            if (!hasAtt)
            {
                sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
                sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
                sb.Append(WrapBase64(Encoding.UTF8.GetBytes(textBody ?? string.Empty)));
                return Encoding.ASCII.GetBytes(sb.ToString());
            }

            sb.AppendFormat("Content-Type: multipart/mixed; boundary=\"{0}\"\r\n\r\n", boundary);
            sb.AppendFormat("--{0}\r\n", boundary);
            sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(WrapBase64(Encoding.UTF8.GetBytes(textBody ?? string.Empty)));
            sb.Append("\r\n");

            foreach (string path in attachmentPaths)
            {
                if (!File.Exists(path)) continue;
                string fileName = Path.GetFileName(path);
                byte[] fileBytes = File.ReadAllBytes(path);
                sb.AppendFormat("--{0}\r\n", boundary);
                sb.AppendFormat("Content-Type: application/octet-stream; name=\"{0}\"\r\n", EncodeHeader(fileName));
                sb.AppendFormat("Content-Disposition: attachment; filename=\"{0}\"\r\n", EncodeHeader(fileName));
                sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
                sb.Append(WrapBase64(fileBytes));
                sb.Append("\r\n");
            }

            sb.AppendFormat("--{0}--\r\n", boundary);
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        public static void Send(AccountConfig account, List<string> recipients, byte[] rawMime)
        {
            string password = account.GetPassword();
            using (TcpClient tcp = new TcpClient())
            {
                tcp.ReceiveTimeout = 60000;
                tcp.SendTimeout = 60000;
                tcp.Connect(account.SmtpHost, account.SmtpPort);

                Stream stream = tcp.GetStream();
                if (account.SmtpImplicitTls)
                {
                    SslStream ssl = new SslStream(stream, false);
                    ssl.AuthenticateAsClient(account.SmtpHost);
                    stream = ssl;
                }

                StreamReader reader = new StreamReader(stream, Encoding.ASCII);
                StreamWriter writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

                ExpectCode(reader, 220);
                writer.WriteLine("EHLO pemail.local");
                ExpectCode(reader, 250);

                if (!account.SmtpImplicitTls)
                {
                    writer.WriteLine("STARTTLS");
                    ExpectCode(reader, 220);
                    SslStream ssl = new SslStream(tcp.GetStream(), false);
                    ssl.AuthenticateAsClient(account.SmtpHost);
                    stream = ssl;
                    reader = new StreamReader(stream, Encoding.ASCII);
                    writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                    writer.WriteLine("EHLO pemail.local");
                    ExpectCode(reader, 250);
                }

                writer.WriteLine("AUTH LOGIN");
                ExpectCode(reader, 334);
                writer.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(account.Email)));
                ExpectCode(reader, 334);
                writer.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(password)));
                ExpectCode(reader, 235);

                writer.WriteLine("MAIL FROM:<" + account.Email + ">");
                ExpectCode(reader, 250);

                foreach (string rcpt in recipients)
                {
                    string addr = ExtractEmailAddress(rcpt);
                    if (string.IsNullOrEmpty(addr)) continue;
                    writer.WriteLine("RCPT TO:<" + addr + ">");
                    ExpectCode(reader, 250);
                }

                writer.WriteLine("DATA");
                ExpectCode(reader, 354);

                string mimeText = Encoding.ASCII.GetString(rawMime);
                string dotStuffed = mimeText.Replace("\r\n.", "\r\n..");
                if (dotStuffed.StartsWith(".")) dotStuffed = "." + dotStuffed;
                writer.Write(dotStuffed);
                if (!dotStuffed.EndsWith("\r\n")) writer.Write("\r\n");
                writer.WriteLine(".");
                ExpectCode(reader, 250);

                writer.WriteLine("QUIT");
            }
        }

        public static List<string> ParseRecipients(string input)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrWhiteSpace(input)) return list;
            string[] parts = input.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                string addr = ExtractEmailAddress(p);
                if (!string.IsNullOrEmpty(addr)) list.Add(addr);
            }
            return list;
        }

        public static string ExtractEmailAddress(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            int lt = raw.IndexOf('<');
            int gt = raw.IndexOf('>');
            if (lt >= 0 && gt > lt) return raw.Substring(lt + 1, gt - lt - 1).Trim();
            return raw.Trim();
        }

        private static void ExpectCode(StreamReader reader, int expectedCode)
        {
            string lastLine = null;
            while (true)
            {
                string line = reader.ReadLine();
                if (line == null) throw new IOException("SMTP сервер закрыл соединение.");
                lastLine = line;
                if (line.Length < 4 || line[3] != '-') break;
            }
            int code;
            if (lastLine.Length < 3 || !int.TryParse(lastLine.Substring(0, 3), out code) || code != expectedCode)
            {
                throw new IOException("Ошибка SMTP (ожидался " + expectedCode + "): " + lastLine);
            }
        }

        private static string EncodeHeader(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            bool asciiOnly = true;
            foreach (char c in text)
            {
                if (c > 127) { asciiOnly = false; break; }
            }
            if (asciiOnly) return text;
            return "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "?=";
        }

        private static string WrapBase64(byte[] data)
        {
            string b64 = Convert.ToBase64String(data);
            StringBuilder sb = new StringBuilder(b64.Length + b64.Length / 30);
            for (int i = 0; i < b64.Length; i += 76)
            {
                int len = Math.Min(76, b64.Length - i);
                sb.Append(b64, i, len);
                sb.Append("\r\n");
            }
            return sb.ToString();
        }
    }
}
