using Discord.WebSocket;
using Discord;
using MongoDB.Driver;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System;
using MongoDB.Driver.Core.Events;

namespace ademir.mineadmin
{
    internal class Program
    {
        private string? mongoServer { get => Environment.GetEnvironmentVariable("MongoServer"); }
        private string? ademirAuth { get => Environment.GetEnvironmentVariable("AdemirAuth"); }
        private string? guildIdStr { get => Environment.GetEnvironmentVariable("GuildId"); }
        private string? channelIdStr { get => Environment.GetEnvironmentVariable("ChannelId"); }
        private string? adminIdStr { get => Environment.GetEnvironmentVariable("AdminId"); }
        private string? serverTapHost { get => Environment.GetEnvironmentVariable("ServerTapHost"); }
        private string? serverTapKey { get => Environment.GetEnvironmentVariable("ServerTapKey"); }
        private string? discordWebHookUrl { get => Environment.GetEnvironmentVariable("DiscordWebHookUrl"); }

        public static Task Main(string[] args) => new Program().MainAsync();

        public async Task MainAsync()
        {
            DotEnv.Load();
            var wl = new List<string> { };
            ulong guildId = ulong.Parse(guildIdStr!);
            ulong logChannelId = ulong.Parse(channelIdStr!);
            var adminIds = adminIdStr!.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(a => ulong.Parse(a.Trim()));
            var servertaphost = serverTapHost;
            var server_tap_key = serverTapKey;
            var mongodbstring = mongoServer;
            var dicordbottoken = ademirAuth;

            var websocketuri = new Uri($"ws://{servertaphost}");
            var servertapuri = new Uri($"http://{servertaphost}");
            var consolews = new Uri(websocketuri, "/v1/ws/console");
            var execuri = new Uri(servertapuri, "/v1/server/exec");

            var config = new DiscordSocketConfig()
            {
                GatewayIntents = GatewayIntents.All
            };
            DiscordSocketClient _client = new DiscordSocketClient(config);
            await _client.LoginAsync(TokenType.Bot, dicordbottoken);
            await _client.StartAsync();
            
            var mongo = new MongoClient(mongodbstring);
            var db = mongo.GetDatabase("mineadm");
            var whitelist = db.GetCollection<WhiteListEntry>("whitelist");

            var loadWhiteList = async () =>
            {
                wl = await whitelist
                .Find(a => a.MinecraftUserName != "").Project(a => a.MinecraftUserName).ToListAsync();
            };
            await loadWhiteList();
            Func<string, Task> runCommand = async (command) =>
            {
                using (var h = new HttpClient())
                {
                    h.DefaultRequestHeaders.Add("key", server_tap_key);
                    await h.PostAsync(execuri,
                    new FormUrlEncodedContent(new Dictionary<string, string> {
                    { "command", command },
                    { "time", "90" }
                    }));
                }
            };

            await runCommand("whitelist off");

            await loadWhiteList();
            var reCmd = new Regex(@"^wl (\S+)$");
            _client.MessageReceived += async (msg) =>
            {
                if (msg.Content != null)
                {
                    var match = reCmd.Match(msg.Content);

                    if (match.Success && adminIds.Contains(msg.Author?.Id ?? 0))
                    {
                        var fullname = match.Groups[1].Value;
                        var uname = fullname;
                        if (uname.StartsWith('.'))
                            uname = uname.Substring(1);

                        await whitelist.ReplaceOneAsync(
                            a => a.MinecraftUserName == uname,
                            new WhiteListEntry { MinecraftUserName = uname }
                            , new ReplaceOptions { IsUpsert = true });

                        await runCommand($"gamemode survival {fullname}");
                        await loadWhiteList();
                    }
                }
            };

            bool ready = false;
            _client.Ready += async () => {
                ready = true;
            };
            do
            {
                await Task.Delay(1000);
                Console.WriteLine("Esperando server.");
            }
            while (!ready);

            var guild = _client.GetGuild(guildId);
            var lastTimeLog = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            CancellationTokenSource source = new CancellationTokenSource();
            while (true)
                using (var ws = new ClientWebSocket())
                {
                    var cookies = new System.Net.CookieContainer();

                    cookies.Add(websocketuri, new System.Net.Cookie("x-servertap-key", server_tap_key));
                    ws.Options.Cookies = cookies;
                    try
                    {
                        await ws.ConnectAsync(consolews, CancellationToken.None);
                        byte[] buffer = new byte[1024];

                        while (ws.State == WebSocketState.Open)
                        {
                            try
                            {
                                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                                if (result.MessageType == WebSocketMessageType.Close)
                                {
                                    try
                                    {
                                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                                    }
                                    catch (IOException ex)
                                    {
                                        // ignore ero ao fechar
                                    }
                                    continue;
                                }
                                else
                                {
                                    var payload = Encoding.UTF8.GetString(buffer, 0, result.Count);
                                    try
                                    {
                                        var log = JsonSerializer.Deserialize<JsonElement>(payload);
                                        var logname = log.GetProperty("loggerName").GetString();

                                        var message = log.GetProperty("message").GetString();
                                        var timestampMillis = log.GetProperty("timestampMillis").GetInt64();
                                        var ts = DateTimeOffset.FromUnixTimeMilliseconds(timestampMillis);

                                        if (logname == "net.minecraft.server.MinecraftServer")
                                        {
                                            if (lastTimeLog < timestampMillis)
                                            {
                                                Console.WriteLine($"[{ts.ToLocalTime()}] {message}");

                                                var channel = guild.GetTextChannel(logChannelId);

                                                var re = Regex.Match(message!, @"^(\S+) (left|joined|was|has|fell|starved|suffocated|died|drowned|walked|tried|hit|blew|burned|went|discovered|experienced|froze|withered)");

                                                using (var http = new HttpClient(new HttpClientHandler
                                                {
                                                    AllowAutoRedirect = true,
                                                    MaxAutomaticRedirections = 2
                                                }))
                                                {

                                                    if (re.Success)
                                                    {
                                                        var fullname = re.Groups[1].Value;
                                                        if (fullname.StartsWith("\u001b[93m"))
                                                            fullname = fullname[5..];

                                                        var uname = fullname;

                                                        if (uname.StartsWith('.'))
                                                            uname = uname[1..];

                                                        var adv = re.Groups[2].Value;

                                                        if (adv == "joined")
                                                        {
                                                            await Task.Delay(5000);
                                                            if (!wl.Contains(uname, StringComparer.InvariantCultureIgnoreCase))
                                                            {
                                                                await runCommand($"gamemode spectator {fullname}");
                                                            }
                                                            else
                                                            {
                                                                await runCommand($"gamemode survival {fullname}");
                                                            }
                                                        }

                                                        var uidreq = await http.GetFromJsonAsync<JsonElement>($"https://api.geysermc.org/v2/utils/uuid/bedrock_or_java/{fullname}?prefix=.");
                                                        var uid = uidreq.GetProperty("id").GetString();

                                                        var post = http.PostAsJsonAsync(discordWebHookUrl, new
                                                        {
                                                            content = message,
                                                            username = uname,
                                                            avatar_url = $"https://mc-heads.net/head/{uid}"
                                                        }).Result;
                                                    }
                                                    else
                                                    {

                                                    }
                                                }
                                            }
                                        }
                                        else
                                        {
                                            Console.WriteLine($"[{ts.ToLocalTime()}][{logname}] {message}");
                                        }
                                    }
                                    catch
                                    (Exception ex)
                                    {
                                    }
                                }
                            }
                            catch (WebSocketException ex)
                            {
                                continue;
                            }
                        }
                    }
                    catch (WebSocketException ex)
                    {
                        continue;
                    }
                }
        }
    }
}
