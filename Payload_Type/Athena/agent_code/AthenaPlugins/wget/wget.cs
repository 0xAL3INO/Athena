﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text;

namespace Athena
{
    public static class Plugin
    {
        public static string Execute(Dictionary<string, object> args)
        {
            try
            {
                if (args.ContainsKey("url"))
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(args["url"].ToString());   
                    
                    if (args.ContainsKey("cookies"))
                    {
                        if (!String.IsNullOrEmpty((string)args["cookies"]) && args["cookies"].ToString().StartsWith('{'))
                        {
                            Dictionary<string,string> cookies = JsonSerializer.Deserialize<Dictionary<string,string>>((string)args["cookies"]);
                            CookieContainer cc = new CookieContainer();
                            foreach(var kvp in cookies)
                            {
                                Cookie c = new Cookie(kvp.Key, kvp.Value);
                                cc.Add(c);
                            }
                            req.CookieContainer = cc;
                             
                        }
                    }
                    if (args.ContainsKey("headers"))
                    {
                        if (!String.IsNullOrEmpty((string)args["headers"]) && args["headers"].ToString().StartsWith('{'))
                        {
                            Dictionary<string, string> headers = JsonSerializer.Deserialize<Dictionary<string, string>>((string)args["headers"]);

                            foreach(var kvp in headers)
                            {
                                if (kvp.Key.ToLower() == "host")
                                {
                                    req.Host = kvp.Value;
                                }
                                else
                                {
                                    req.Headers.Add(kvp.Key, kvp.Value);
                                }
                            }

                        }
                    }

                    try
                    {
                        if (args.ContainsKey("method"))
                        {
                            switch (args["method"].ToString().ToLower())
                            {
                                case "get":
                                    return Get(req);
                                    break;
                                case "post":
                                    if (args.ContainsKey("body"))
                                    {
                                        return Post(req, args["body"].ToString());
                                    }
                                    break;
                                default:
                                    return Get(req);
                                    break;
                            }
                        }
                        else
                        {
                            return Get(req);
                        }
                    }
                    catch (Exception e)
                    {
                        return e.Message;
                    }
                    //Will this ever fire?
                    return Get(req);
                }
                else
                {
                    return "A url needs to be specified";
                }
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }
        public static string Get(HttpWebRequest req)
        {
            try
            {
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (HttpWebResponse response = (HttpWebResponse)req.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }
        public static string Post(HttpWebRequest req, string data)
        {
            try
            {
                byte[] dataBytes = Encoding.UTF8.GetBytes(data);

                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                req.ContentLength = dataBytes.Length;
                //req.ContentType = contentType;
                //req.Method = method;

                using (Stream requestBody = req.GetRequestStream())
                {
                    requestBody.Write(dataBytes, 0, dataBytes.Length);
                }

                using (HttpWebResponse response = (HttpWebResponse)req.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch(Exception e)
            {
                return e.Message;
            }
        }
    }
}

