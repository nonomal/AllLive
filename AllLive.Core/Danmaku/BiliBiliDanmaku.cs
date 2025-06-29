﻿using AllLive.Core.Helper;
using AllLive.Core.Interface;
using AllLive.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using System.Web;
using WebSocketSharp;
/*
* 哔哩哔哩弹幕实现
* 参考文档：https://github.com/lovelyyoshino/Bilibili-Live-API/blob/master/API.WebSocket.md
*/
namespace AllLive.Core.Danmaku
{
    public class BiliDanmakuArgs
    {
        public int RoomId { get; set; }
        public long UserId { get; set; } = 0;
        public string Cookie { get; set; }
    }
    public enum SslProtocolsHack
    {
        Tls = 192,
        Tls11 = 768,
        Tls12 = 3072
    }
    public class BiliBiliDanmaku : ILiveDanmaku
    {
        public event EventHandler<LiveMessage> NewMessage;
        public event EventHandler<string> OnClose;
        public int HeartbeatTime => 60 * 1000;
        private int roomId = 0;
        //private readonly string ServerUrl = "wss://broadcastlv.chat.bilibili.com/sub";
        Timer timer;
        WebSocket ws;
        private DanmuInfo danmuInfo;
        private string buvid;
        private BiliDanmakuArgs Args;

        public BiliBiliDanmaku()
        {

        }
        private async void Ws_OnOpen(object sender, EventArgs e)
        {
            await Task.Run(() =>
            {
                //发送进房信息
                ws.Send(EncodeData(JsonConvert.SerializeObject(new
                {
                    roomid = roomId,
                    uid = Args.UserId,
                    protover = 2,
                    key = danmuInfo.token,
                    platform = "web",
                    type = 2,
                    buvid,
                }), 7));

            });
            timer.Start();

        }
        private void Ws_OnMessage(object sender, MessageEventArgs e)
        {
            try
            {
                ParseData(e.RawData);
            }
            catch (Exception)
            {
            }
        }

        private void Ws_OnClose(object sender, CloseEventArgs e)
        {
            // https://github.com/sta/websocket-sharp/issues/219
            var sslProtocolHack = (System.Security.Authentication.SslProtocols)(SslProtocolsHack.Tls12 | SslProtocolsHack.Tls11 | SslProtocolsHack.Tls);
            //TlsHandshakeFailure
            if (e.Code == 1015 && ws.SslConfiguration.EnabledSslProtocols != sslProtocolHack)
            {
                ws.SslConfiguration.EnabledSslProtocols = sslProtocolHack;
                ws.Connect();
            }
            else
            {
                OnClose?.Invoke(this, e.Reason);
            }
            
        }

        private void Ws_OnError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            OnClose?.Invoke(this, e.Message);
        }

        public async Task Start(object args)
        {
            var _args = args as BiliDanmakuArgs;
            Args = _args;
            roomId = Args.RoomId;

            var _buvid = await GetBuvid();
            buvid = _buvid;
            var info = await GetDanmuInfo(roomId);
            if (info == null)
            {
                SendSystemMessage("获取弹幕信息失败");
                return;
            }
            danmuInfo = info;
            var host = info.host_list.Last();
            ws = new WebSocket($"wss://{host.host}/sub");
          
            if (!string.IsNullOrEmpty(Args.Cookie))
            {
                ws.CustomHeaders = new Dictionary<string, string>() {
                 
                    {"Cookie", Args.Cookie},
                };
            }
            ws.Compression = CompressionMethod.Deflate;
            ws.OnOpen += Ws_OnOpen;
            ws.OnError += Ws_OnError;
            ws.OnMessage += Ws_OnMessage;
            ws.OnClose += Ws_OnClose;
            timer = new Timer(HeartbeatTime);
            timer.Elapsed += Timer_Elapsed;
            await Task.Run(() =>
            {
                ws.Connect();
            });
        }


        private void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            Heartbeat();
        }

        public async void Heartbeat()
        {
            await Task.Run(() =>
            {
                ws.Send(EncodeData("", 2));
            });
        }
        public async Task Stop()
        {
            timer.Stop();
            await Task.Run(() =>
            {
                ws.Close();
            });
        }

        private void ParseData(byte[] data)
        {
            //协议版本。
            //0为JSON，可以直接解析；
            //1为房间人气值,Body为Int32；
            //2为zlib压缩过Buffer，需要解压再处理
            //3为brotli压缩过Buffer，需要解压再处理
            int protocolVersion = BitConverter.ToInt32(new byte[4] { data[7], data[6], 0, 0 }, 0);
            //操作类型。
            //3=心跳回应，内容为房间人气值；
            //5=通知，弹幕、广播等全部信息；
            //8=进房回应，空
            int operation = BitConverter.ToInt32(data.Skip(8).Take(4).Reverse().ToArray(), 0);

            //内容
            var body = data.Skip(16).ToArray();
            if (operation == 3)
            {
                var online = BitConverter.ToInt32(body.Reverse().ToArray(), 0);
                NewMessage?.Invoke(this, new LiveMessage()
                {
                    Data = online,
                    Type = LiveMessageType.Online,
                });
            }
            else if (operation == 5)
            {

                if (protocolVersion == 2)//|| protocolVersion == 3
                {
                    body = DecompressData(body, protocolVersion);
                }
                var text = Encoding.UTF8.GetString(body);
                //可能有多条数据，做个分割
                var textLines = Regex.Split(text, "[\x00-\x1f]+").Where(x => x.Length > 2 && x[0] == '{').ToArray();
                foreach (var item in textLines)
                {
                    ParseMessage(item);
                }
            }
        }

        private void ParseMessage(string jsonMessage)
        {
            try
            {
                var obj = JObject.Parse(jsonMessage);
                var cmd = obj["cmd"].ToString();
                if (cmd.Contains("DANMU_MSG"))
                {
                    if (obj["info"] != null && obj["info"].ToArray().Length != 0)
                    {
                        var message = obj["info"][1].ToString();
                        var color = obj["info"][0][3].ToInt32();
                        if (obj["info"][2] != null && obj["info"][2].ToArray().Length != 0)
                        {
                            var username = obj["info"][2][1].ToString();
                            NewMessage?.Invoke(this, new LiveMessage()
                            {
                                Type = LiveMessageType.Chat,
                                Message = message,
                                UserName = username,
                                Color = color == 0 ? DanmakuColor.White : new DanmakuColor(color),
                            });
                        }
                    }
                }
                else if (cmd.Contains("SUPER_CHAT_MESSAGE"))
                {
                    if (obj["data"].Type == JTokenType.Null)
                    {
                        return;
                    }
                    var message = new LiveSuperChatMessage()
                    {
                        BackgroundBottomColor = obj["data"]["background_bottom_color"].ToString(),
                        BackgroundColor = obj["data"]["background_color"].ToString(),
                        EndTime = Utils.TimestampToDateTime(obj["data"]["end_time"].ToInt64()),
                        StartTime = Utils.TimestampToDateTime(obj["data"]["start_time"].ToInt64()),
                        Face = $"{obj["data"]["user_info"]["face"]}@200w.jpg",
                        Message = obj["data"]["message"].ToString(),
                        Price = obj["data"]["price"].ToInt32(),
                        UserName = obj["data"]["user_info"]["uname"].ToString(),
                    };
                    NewMessage?.Invoke(this, new LiveMessage()
                    {
                        Color = DanmakuColor.White,
                        Message = message.Message,
                        Type = LiveMessageType.SuperChat,
                        Data = message,
                        UserName = message.UserName,
                    });

                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }

        }

        /// <summary>
        /// 对数据进行编码
        /// </summary>
        /// <param name="msg">文本内容</param>
        /// <param name="action">2=心跳，7=进房</param>
        /// <returns></returns>
        private byte[] EncodeData(string msg, int action)
        {
            var data = Encoding.UTF8.GetBytes(msg);
            //头部长度固定16
            var length = data.Length + 16;
            var buffer = new byte[length];
            using (var ms = new MemoryStream(buffer))
            {

                //数据包长度
                var b = BitConverter.GetBytes(buffer.Length).ToArray().Reverse().ToArray();
                ms.Write(b, 0, 4);
                //数据包头部长度,固定16
                b = BitConverter.GetBytes(16).Reverse().ToArray();
                ms.Write(b, 2, 2);
                //协议版本，0=JSON,1=Int32,2=Buffer
                b = BitConverter.GetBytes(0).Reverse().ToArray(); ;
                ms.Write(b, 0, 2);
                //操作类型
                b = BitConverter.GetBytes(action).Reverse().ToArray(); ;
                ms.Write(b, 0, 4);
                //数据包头部长度,固定1
                b = BitConverter.GetBytes(1).Reverse().ToArray(); ;
                ms.Write(b, 0, 4);
                //数据
                ms.Write(data, 0, data.Length);
                var _bytes = ms.ToArray();
                ms.Flush();
                return _bytes;
            }

        }


        /// <summary>
        /// 解码数据
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        private byte[] DecompressData(byte[] data, int protocolVersion)
        {
            if (protocolVersion == 3)
            {
                return DecompressDataWithBrotli(data);
            }
            using (MemoryStream outBuffer = new MemoryStream())
            using (System.IO.Compression.DeflateStream compressedzipStream = new System.IO.Compression.DeflateStream(new MemoryStream(data, 2, data.Length - 2), System.IO.Compression.CompressionMode.Decompress))
            {
                byte[] block = new byte[1024];
                while (true)
                {
                    int bytesRead = compressedzipStream.Read(block, 0, block.Length);
                    if (bytesRead <= 0)
                        break;
                    else
                        outBuffer.Write(block, 0, bytesRead);
                }
                compressedzipStream.Close();
                return outBuffer.ToArray();
            }


        }
        /// <summary>
        /// 解压数据 (使用Brotli)
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        private byte[] DecompressDataWithBrotli(byte[] data)
        {

            //using (var decompressedStream = new BrotliStream(new MemoryStream(data), CompressionMode.Decompress))
            //{
            //    using (var outBuffer = new MemoryStream())
            //    {
            //        var block = new byte[1024];
            //        while (true)
            //        {
            //            var bytesRead = decompressedStream.Read(block, 0, block.Length);
            //            if (bytesRead <= 0)
            //                break;
            //            outBuffer.Write(block, 0, bytesRead);
            //        }
            //        return outBuffer.ToArray();
            //    }
            //}
            throw new NotImplementedException();


        }
        private async Task<string> GetBuvid()
        {
            try
            {
                if (!string.IsNullOrEmpty(Args.Cookie) && Args.Cookie.Contains("buvid3"))
                {
                    var regex = new Regex("buvid3=(.*?);");
                    var match = regex.Match(Args.Cookie);
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }

                var result = await HttpUtil.GetString($"https://api.bilibili.com/x/frontend/finger/spi",
                    headers: string.IsNullOrEmpty(Args.Cookie) ? null : new Dictionary<string, string>
                    {
                        { "cookie", Args.Cookie }
                    }
                  );
                var obj = JObject.Parse(result);

                return obj["data"]["b_3"].ToString();
            }
            catch (Exception)
            {
                return "";
            }
        }

        private async Task<DanmuInfo> GetDanmuInfo(int roomId)
        {
            try
            {
                var url = $"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo";
                var query = $"id={roomId}";
                query = await GetWbiSign(query);

                var result = await HttpUtil.GetString($"{url}?{query}",
                    headers: string.IsNullOrEmpty(Args.Cookie) ? null : new Dictionary<string, string>
                    {
                        { "cookie", Args.Cookie }
                    });
                var obj = JObject.Parse(result);
                var info = obj["data"].ToObject<DanmuInfo>();
                return info;
            }
            catch (Exception ex)
            {
                SendSystemMessage(ex.Message);
            }
            return null;
        }

        private void SendSystemMessage(string msg)
        {
            NewMessage(this, new LiveMessage()
            {
                Type = LiveMessageType.Chat,
                UserName = "系统",
                Message = msg
            });
        }

        private string _imgKey;
        private string _subKey;
        private int[] mixinKeyEncTab = new int[] {
            46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
            33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
            61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
            36, 20, 34, 44, 52
        };
        private async Task<(string, string)> GetWbiKeys()
        {
            if (_imgKey != null && _subKey != null)
            {
                return (_imgKey, _subKey);
            }
            // 获取最新的 img_key 和 sub_key
            var response = await HttpUtil.GetString(
                "https://api.bilibili.com/x/web-interface/nav",
                 headers: string.IsNullOrEmpty(Args.Cookie) ? null : new Dictionary<string, string>
                    {
                        { "cookie", Args.Cookie }
                    });
            var obj = JObject.Parse(response);

            var imgUrl = obj["data"]["wbi_img"]["img_url"].ToString();
            var subUrl = obj["data"]["wbi_img"]["sub_url"].ToString();
            var imgKey = imgUrl.Substring(imgUrl.LastIndexOf('/') + 1).Split('.')[0];
            var subKey = subUrl.Substring(subUrl.LastIndexOf('/') + 1).Split('.')[0];

            _imgKey = imgKey;
            _subKey = subKey;

            return (imgKey, subKey);
        }

        private string GetMixinKey(string origin)
        {
            // 对 imgKey 和 subKey 进行字符顺序打乱编码
            return mixinKeyEncTab.Aggregate("", (s, i) => s + origin[i]).Substring(0, 32);
        }

        public async Task<string> GetWbiSign(string url)
        {
            var (imgKey, subKey) = await GetWbiKeys();

            // 为请求参数进行 wbi 签名
            var mixinKey = GetMixinKey(imgKey + subKey);
            var currentTime = (long)Math.Round(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds);

            var queryString = HttpUtility.ParseQueryString(url);

            var queryParams = queryString.Cast<string>().ToDictionary(k => k, v => queryString[v]);
            queryParams["wts"] = currentTime + ""; // 添加 wts 字段
            queryParams = queryParams.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value); // 按照 key 重排参数
                                                                                                  // 过滤 value 中的 "!'()*" 字符
            queryParams = queryParams.ToDictionary(x => x.Key, x => string.Join("", x.Value.ToString().Where(c => "!'()*".Contains(c) == false)));

            var query = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));

            var wbi_sign = Utils.ToMD5($"{query}{mixinKey}");

            return $"{query}&w_rid={wbi_sign}";
        }

    }



    class DanmuInfo
    {
        public string group { get; set; }
        public int business_id { get; set; }
        public double refresh_row_factor { get; set; }
        public int refresh_rate { get; set; }
        public int max_delay { get; set; }
        public string token { get; set; }
        public List<DanmuInfoHostList> host_list { get; set; }
    }

    class DanmuInfoHostList
    {
        public string host { get; set; }
        public int port { get; set; }
        public int wss_port { get; set; }
        public int ws_port { get; set; }
    }
}
