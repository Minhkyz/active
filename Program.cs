using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarbleRaceWebSocketBot
{
    public class MarbleRaceClient
    {
        private const int MaxStakePerSpot = 3000;
        private const double EscalateFactor = 8;
        private const double TpPercent = 1;
        private const double TpFixed = 15000;

        private readonly int _baseStake;
        private readonly string _discordWebhookUrl = "";

        private readonly Dictionary<string, int> _realStake = new();
        private readonly HashSet<string> _chosenThisRound = new();
        private readonly List<SimulationTracker> _simulations;
        private readonly HashSet<SimulationTracker> _activeLiveSignals = new();

        private static int _msgSeq;

        private string _currentGameId = string.Empty;
        private string _currentNumber = string.Empty;

        private bool _hasPlacedForThisRound;
        private bool _tpTriggered;
        private bool _isLiveBetting;

        private int _winStreak;
        private int _realWins;

        private long _sumTotalLive;
        private long _sumTotalSim;

        private double _currentBalance;
        private double _firstBalance;
        private bool _isFirstBalance = true;

        private static string NextMsgId(string pfx) => $"{pfx}-{Interlocked.Increment(ref _msgSeq)}";

        private static readonly string[] AllSpots =
        {
            "Marble_1", "Marble_2", "Marble_3", "Marble_4", "Marble_5", "Marble_6"
        };

        public MarbleRaceClient(int baseStake = 10, string discordWebhookUrl = "")
        {
            _baseStake = baseStake <= 0 ? 10 : baseStake;
            _discordWebhookUrl = discordWebhookUrl?.Trim() ?? string.Empty;

            foreach (var spot in AllSpots)
            {
                _realStake[spot] = _baseStake;
            }

            _simulations = Enumerable.Range(0, 10)
                .Select(i => new SimulationTracker($"Sim{i + 1}", AllSpots, i))
                .ToList();
        }

        private void ResetAllToBase()
        {
            foreach (var s in AllSpots)
            {
                _realStake[s] = _baseStake;
            }
            _winStreak = 0;
        }

        private async Task NotifyResetAsync(string reason)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($" - {reason} → RESET all stakes to base ({_baseStake}).");
            Console.ResetColor();

            if (!_isLiveBetting) return;

            await PostDiscordEmbedAsync(
                title: "Marble — RESET",
                desc: $"**Reason:** {reason}",
                kind: "LOSS",
                fields: new[]
                {
                    ("Stakes reset", $"All to `{_baseStake}`", false),
                    ("Balance", _isFirstBalance ? "`-`" : $"`{Math.Round(_currentBalance, 2)}`", true),
                    ("P/L Total", _isFirstBalance ? "`-`" : $"`{Math.Round(_currentBalance - _firstBalance, 2)}`", true)
                }
            );
        }

        private static int ColorFor(string kind) => kind switch
        {
            "BET" => 0x3498DB,
            "WIN" => 0x2ECC71,
            "LOSS" => 0xE74C3C,
            "BAL" => 0x9B59B6,
            _ => 0x95A5A6
        };

        private async Task PostDiscordEmbedAsync(string title, string desc, string kind,
            IEnumerable<(string name, string value, bool inline)> fields)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_discordWebhookUrl)) return;

                var payload = new
                {
                    embeds = new[]
                    {
                        new
                        {
                            title,
                            description = desc,
                            color = ColorFor(kind),
                            fields = fields.Select(f => new { f.name, f.value, f.inline }).ToArray(),
                            footer = new { text = "MarbleRace Bot" },
                            timestamp = DateTime.UtcNow.ToString("o")
                        }
                    }
                };

                using var http = new HttpClient();
                var json = JsonSerializer.Serialize(payload);
                var resp = await http.PostAsync(_discordWebhookUrl,
                    new StringContent(json, Encoding.UTF8, "application/json"));
                resp.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Discord webhook error: " + ex.Message);
            }
        }

        private async Task CheckTakeProfitAsync()
        {
            if (_isFirstBalance || _tpTriggered) return;

            var profit = _currentBalance - _firstBalance;
            var hitPercent = TpPercent > 0 && profit >= _firstBalance * TpPercent;
            var hitFixed = TpFixed > 0 && profit >= TpFixed;

            if (!hitPercent && !hitFixed) return;

            _tpTriggered = true;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"💰 TAKE PROFIT hit! Profit={profit:F2}. Bot will pause betting.");
            Console.ResetColor();

            if (!_isLiveBetting) return;

            await PostDiscordEmbedAsync(
                title: "Marble — TAKE PROFIT",
                desc: $"**Profit:** `{profit:F2}`  •  **Start:** `{_firstBalance:F2}`  •  **Now:** `{_currentBalance:F2}`",
                kind: "BAL",
                fields: new[]
                {
                    ("TP %", TpPercent > 0 ? $"`{TpPercent:P0}`" : "`-`", true),
                    ("TP Fixed", TpFixed > 0 ? $"`{TpFixed:F2}`" : "`-`", true)
                }
            );
        }

        private async Task SendFetchBalanceAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var payload = new
            {
                id = NextMsgId("fetchbal"),
                type = "fetchBalance",
                args = new { }
            };
            await SendAsync(ws, JsonSerializer.Serialize(payload), ct);
        }

        public async Task RunAsync(string websocketUrl, CancellationToken ct = default)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndListenAsync(websocketUrl, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }

                Console.WriteLine("Connection closed. Reconnecting in 3 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }

        private async Task ConnectAndListenAsync(string websocketUrl, CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(websocketUrl), ct);
            Console.WriteLine("Connected to WebSocket");

            await SendFetchBalanceAsync(ws, ct);
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    try
                    {
                        await SendFetchBalanceAsync(ws, ct);
                    }
                    catch
                    {
                        // ignored
                    }

                    await Task.Delay(30000, ct);
                }
            }, ct);

            var buffer = new byte[64 * 1024];
            var sb = new StringBuilder();

            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text) continue;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                var text = sb.ToString();
                sb.Clear();
                await HandleTextAsync(text, ws, ct);
            }
        }

        private async Task HandleTextAsync(string text, ClientWebSocket ws, CancellationToken ct)
        {
            if (await TryProcessAsync(text, ws, ct)) return;

            foreach (var chunk in SplitConcatenatedJson(text))
            {
                if (await TryProcessAsync(chunk, ws, ct)) continue;
            }
        }

        private async Task<bool> TryProcessAsync(string s, ClientWebSocket ws, CancellationToken ct)
        {
            try
            {
                await ProcessMessageAsync(s, ws, ct);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static IEnumerable<string> SplitConcatenatedJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) yield break;

            var depth = 0;
            var inString = false;
            var prev = '\0';
            var sb = new StringBuilder();

            foreach (var c in raw)
            {
                sb.Append(c);
                if (c == '"' && prev != '\\') inString = !inString;

                if (!inString)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;

                    if (depth == 0 && sb.Length > 0)
                    {
                        yield return sb.ToString();
                        sb.Clear();
                    }
                }

                prev = c;
            }

            if (sb.Length > 0) yield return sb.ToString();
        }

        private async Task ProcessMessageAsync(string message, ClientWebSocket ws, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (root.TryGetProperty("log", out var logEl) &&
                    logEl.ValueKind == JsonValueKind.Object &&
                    logEl.TryGetProperty("type", out var ltypeEl) &&
                    logEl.TryGetProperty("value", out var valEl))
                {
                    var ltype = ltypeEl.GetString();

                    if (valEl.TryGetProperty("balance", out var balEl2) &&
                        balEl2.TryGetDouble(out var bal2))
                    {
                        if (_isFirstBalance) _firstBalance = bal2;
                        _currentBalance = bal2;
                        _isFirstBalance = false;

                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.WriteLine($" - Balance: {_currentBalance} | P/L: {_currentBalance - _firstBalance}");
                        Console.ResetColor();
                    }

                    if (ltype == "CLIENT_BET_ACCEPTED")
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine(" - CLIENT_BET_ACCEPTED");
                        Console.ResetColor();
                    }
                    else if (ltype == "CLIENT_BALANCE_UPDATED")
                    {
                        Console.ForegroundColor = ConsoleColor.DarkMagenta;
                        Console.WriteLine(" - CLIENT_BALANCE_UPDATED");
                        Console.ResetColor();
                    }
                    else if (ltype == "CLIENT_BET_CHIP")
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                        Console.WriteLine(" - CLIENT_BET_CHIP");
                        Console.ResetColor();
                    }

                    await CheckTakeProfitAsync();
                    return;
                }

                if (root.TryGetProperty("type", out var t1) &&
                    t1.GetString() == "balanceUpdated" &&
                    root.TryGetProperty("args", out var a1) &&
                    a1.TryGetProperty("balance", out var balEl) &&
                    balEl.TryGetDouble(out var balv))
                {
                    if (_isFirstBalance) _firstBalance = balv;
                    _currentBalance = balv;
                    _isFirstBalance = false;

                    await CheckTakeProfitAsync();
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine($" - Balance: {_currentBalance} | P/L: {_currentBalance - _firstBalance}");
                    Console.ResetColor();
                    return;
                }

                if (root.TryGetProperty("type", out var typeEl) &&
                    typeEl.GetString() == "marblerace.tableState" &&
                    root.TryGetProperty("args", out var args) &&
                    args.TryGetProperty("gameId", out var gidEl))
                {
                    _currentGameId = gidEl.GetString() ?? string.Empty;
                    _currentNumber = args.TryGetProperty("number", out var numEl) ? numEl.GetString() ?? string.Empty : string.Empty;

                    if (!args.TryGetProperty("phaseData", out var pd) ||
                        !pd.TryGetProperty("type", out var ptypeEl)) return;

                    var ptype = ptypeEl.GetString() ?? string.Empty;

                    switch (ptype)
                    {
                        case "BetsOpen":
                            await HandleBetsOpenPhaseAsync(ws, ct);
                            break;
                        case "GameFinalized":
                            await HandleGameFinalizedAsync(pd);
                            break;
                    }
                }
            }
            catch (JsonException jx)
            {
                Console.WriteLine("JSON parsing error: " + jx.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Process error: " + ex.Message);
            }
        }

        private async Task HandleBetsOpenPhaseAsync(ClientWebSocket ws, CancellationToken ct)
        {
            RunSimulationsForRound();

            if (_tpTriggered)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine("⏸️ Paused (TP hit). No bets.");
                Console.ResetColor();
                _isLiveBetting = false;
                _activeLiveSignals.Clear();
                _hasPlacedForThisRound = false;
                _chosenThisRound.Clear();
                return;
            }

            var readySignals = _simulations.Where(s => s.SignalActive).ToList();

            if (_isLiveBetting && readySignals.Count == 0 && _activeLiveSignals.Count == 0)
            {
                _isLiveBetting = false;
            }

            if (!_isLiveBetting && readySignals.Count > 0)
            {
                foreach (var sim in readySignals) _activeLiveSignals.Add(sim);
                _isLiveBetting = true;
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"🔁 Live betting triggered by {readySignals.Count} simulation(s).");
                Console.ResetColor();
            }
            else if (_isLiveBetting)
            {
                foreach (var sim in readySignals)
                {
                    _activeLiveSignals.Add(sim);
                }
            }

            if (!_isLiveBetting || _activeLiveSignals.Count == 0)
            {
                _hasPlacedForThisRound = false;
                _chosenThisRound.Clear();
                return;
            }

            await PlaceLiveBetAsync(ws, _currentGameId, ct);
        }

        private async Task HandleGameFinalizedAsync(JsonElement phaseData)
        {
            var winningSpots = new List<string>();
            if (phaseData.TryGetProperty("winningSpots", out var wsArr) && wsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in wsArr.EnumerateArray())
                {
                    if (it.ValueKind == JsonValueKind.String)
                    {
                        winningSpots.Add(it.GetString() ?? string.Empty);
                    }
                }
            }

            foreach (var sim in _simulations)
            {
                var simWin = sim.ProcessResult(winningSpots);
                if (simWin && sim.SignalActive)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                    Console.WriteLine($"[SIM-{sim.Name}] Win → reset loss streak.");
                    Console.ResetColor();
                }
            }

            _activeLiveSignals.RemoveWhere(sim => !sim.SignalActive);

            if (!_isLiveBetting || !_hasPlacedForThisRound)
            {
                return;
            }

            var win = HasWin(winningSpots, _chosenThisRound);

            if (win)
            {
                _winStreak++;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($" - WIN (streak={_winStreak}). Number={_currentNumber} GameId={_currentGameId}");
                Console.ResetColor();

                _realWins++;
                await PostDiscordEmbedAsync(
                    title: "Marble — WIN",
                    desc: $"**Round:** `{_currentNumber}` (`{_currentGameId}`)\n**WinStreak:** `{_winStreak}` • **Wins(total):** `{_realWins}`",
                    kind: "WIN",
                    fields: new[]
                    {
                        ("Stakes now", $"1:{_realStake["Marble_1"]} 2:{_realStake["Marble_2"]} 3:{_realStake["Marble_3"]} 4:{_realStake["Marble_4"]} 5:{_realStake["Marble_5"]} 6:{_realStake["Marble_6"]}", false),
                        ("Balance", _isFirstBalance ? "`-`" : $"`{Math.Round(_currentBalance, 2)}`", true),
                        ("P/L Total", _isFirstBalance ? "`-`" : $"`{Math.Round(_currentBalance - _firstBalance, 2)}`", true),
                        ("Total sum LIVE", $"`{_sumTotalLive:N0}`", true)
                    }
                );

                ResetAllToBase();
                foreach (var sim in _activeLiveSignals)
                {
                    sim.ResetAfterLiveWin();
                }

                _activeLiveSignals.Clear();
                _isLiveBetting = false;
            }
            else
            {
                _winStreak = 0;

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($" - LOSS. Number={_currentNumber} GameId={_currentGameId}");
                Console.ResetColor();

                var exceeded = false;
                foreach (var s in _chosenThisRound)
                {
                    var next = (int)Math.Ceiling(_realStake[s] * EscalateFactor);
                    if (next > MaxStakePerSpot) exceeded = true;
                    _realStake[s] = Math.Min(next, MaxStakePerSpot);
                }

                if (exceeded)
                {
                    ResetAllToBase();
                    await NotifyResetAsync($"Stake exceeded MaxStakePerSpot ({MaxStakePerSpot})");
                    foreach (var sim in _activeLiveSignals) sim.ResetAfterLiveWin();
                    _activeLiveSignals.Clear();
                    _isLiveBetting = false;
                }
                else
                {
                    await PostDiscordEmbedAsync(
                        title: "Marble — LOSS",
                        desc: $"**Round:** `{_currentNumber}` (`{_currentGameId}`)",
                        kind: "LOSS",
                        fields: new[]
                        {
                            ("Escalated", string.Join(", ", _chosenThisRound.Select(s => $"{s}={_realStake[s]}")), false),
                            ("Balance", _isFirstBalance ? "`-`" : $"`{Math.Round(_currentBalance, 2)}`", true),
                            ("P/L Total", _isFirstBalance ? "`-`" : $"`{Math.Round(_currentBalance - _firstBalance, 2)}`", true),
                            ("Total sum LIVE", $"`{_sumTotalLive:N0}`", true)
                        }
                    );
                }
            }

            _hasPlacedForThisRound = false;
            _chosenThisRound.Clear();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($" - Stakes now: 1:{_realStake["Marble_1"]} 2:{_realStake["Marble_2"]} 3:{_realStake["Marble_3"]} 4:{_realStake["Marble_4"]} 5:{_realStake["Marble_5"]} 6:{_realStake["Marble_6"]}");
            Console.ResetColor();
        }

        private void RunSimulationsForRound()
        {
            long roundTotal = 0;
            foreach (var sim in _simulations)
            {
                sim.PrepareRound(_baseStake);
                roundTotal += sim.TotalStakeThisRound;

                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"[SIM-{sim.Name}] Picks: {string.Join(",", sim.CurrentPicks)} | Zero: {sim.ZeroSpot} | LossStreak={sim.ConsecutiveLosses}");
                Console.ResetColor();
            }

            _sumTotalSim += roundTotal;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[SIM] Tổng cộng dồn Total (SIM) từ đầu phiên: {_sumTotalSim:N0}");
            Console.ResetColor();
        }

        private async Task PlaceLiveBetAsync(ClientWebSocket ws, string gameId, CancellationToken ct)
        {
            var scores = new Dictionary<string, int>();
            foreach (var sim in _activeLiveSignals)
            {
                foreach (var spot in sim.CurrentPicks)
                {
                    scores[spot] = scores.TryGetValue(spot, out var current) ? current + 1 : 1;
                }
            }

            var ordered = scores
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .Select(kv => kv.Key)
                .ToList();

            if (ordered.Count < 5)
            {
                foreach (var spot in AllSpots)
                {
                    if (!ordered.Contains(spot)) ordered.Add(spot);
                    if (ordered.Count == 5) break;
                }
            }

            var selected = ordered.Take(5).ToList();
            var zeroSpot = AllSpots.First(spot => !selected.Contains(spot));

            _chosenThisRound.Clear();
            foreach (var spot in selected) _chosenThisRound.Add(spot);

            var chips = new Dictionary<string, int>();
            var total = 0;

            foreach (var spot in selected)
            {
                var amount = _realStake[spot];
                if (amount <= 0) amount = _baseStake;
                chips[spot] = amount;
                total += amount;
            }

            _hasPlacedForThisRound = true;

            var payloadChip = new
            {
                log = new
                {
                    type = "CLIENT_BET_CHIP",
                    value = new
                    {
                        type = "chip",
                        codes = chips,
                        amount = total,
                        chips,
                        gridView = "mainGrid",
                        chipStack = new[] { 2, 10, 20, 40, 100, 500, 2000, 10000 },
                        currency = "VN2",
                        gameType = "marblerace",
                        channel = "PCMac",
                        orientation = "landscape",
                        gameDimensions = new { width = 1284, height = 722.25 },
                        gameId
                    }
                }
            };
            await SendAsync(ws, JsonSerializer.Serialize(payloadChip), ct);
            await Task.Delay(60, ct);

            var payloadSet = new
            {
                id = NextMsgId("mr-set"),
                type = "marblerace.setChips",
                args = new
                {
                    chips,
                    gameId,
                    gridView = "mainGrid",
                    betTags = new { openMwTables = 1, mwLayout = 8 }
                }
            };
            await SendAsync(ws, JsonSerializer.Serialize(payloadSet), ct);

            _sumTotalLive += total;

            var picksList = string.Join(", ", selected);
            var amtList = string.Join(", ", selected.Select(s => $"{s}={_realStake[s]}"));

            await PostDiscordEmbedAsync(
                title: "Marble — BET",
                desc: $"**Round:** `{_currentNumber}` (`{gameId}`)\n**Picks:** `{picksList}`\n**Zero:** `{zeroSpot}`",
                kind: "BET",
                fields: new[]
                {
                    ("Amounts", $"`{amtList}`", false),
                    ("Total", $"`{total}`", true),
                    ("Balance", _isFirstBalance ? "`-`" : $"`{Math.Round(_currentBalance, 2)}`", true)
                }
            );

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($" - BET GameId {gameId} ({_currentNumber}) → Picks: {picksList} | Zero: {zeroSpot} | Total={total}");
            Console.ResetColor();
        }

        private bool HasWin(List<string> winningSpots, HashSet<string> chosen)
        {
            return winningSpots.Any(chosen.Contains);
        }

        private async Task SendAsync(ClientWebSocket ws, string msg, CancellationToken ct)
        {
            var data = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, ct);
        }

        private class SimulationTracker
        {
            private readonly string[] _allSpots;
            private readonly int _offset;

            private int _roundCounter;

            public SimulationTracker(string name, string[] allSpots, int offset)
            {
                Name = name;
                _allSpots = allSpots;
                _offset = offset;
                CurrentPicks = new HashSet<string>();
            }

            public string Name { get; }
            public HashSet<string> CurrentPicks { get; private set; }
            public string ZeroSpot { get; private set; } = string.Empty;
            public int ConsecutiveLosses { get; private set; }
            public int TotalStakeThisRound { get; private set; }
            public bool SignalActive => ConsecutiveLosses >= 2;

            public void PrepareRound(int baseStake)
            {
                _roundCounter++;
                var zeroIndex = (_roundCounter + _offset) % _allSpots.Length;
                ZeroSpot = _allSpots[zeroIndex];
                CurrentPicks = new HashSet<string>(_allSpots.Where(s => s != ZeroSpot));
                TotalStakeThisRound = CurrentPicks.Count * baseStake;
            }

            public bool ProcessResult(IEnumerable<string> winningSpots)
            {
                var win = winningSpots.Any(CurrentPicks.Contains);
                ConsecutiveLosses = win ? 0 : ConsecutiveLosses + 1;
                return win;
            }

            public void ResetAfterLiveWin()
            {
                ConsecutiveLosses = 0;
            }
        }
    }

    internal class Program
    {
        private static async Task<bool> CheckActivationAsync()
        {
            const string activationUrl = "https://raw.githubusercontent.com/Minhkyz/active/refs/heads/main/check";
            using var client = new HttpClient();
            try
            {
                var s = await client.GetStringAsync(activationUrl);
                return s.Trim() == "1";
            }
            catch
            {
                Console.WriteLine("Activation check failed 0w0");
                return false;
            }
        }

        private static async Task Main(string[] args)
        {
            var ok = await CheckActivationAsync();
            if (!ok)
            {
                Console.WriteLine("The application is not activated. Exiting in 10 seconds...");
                await Task.Delay(10000);
                Environment.Exit(0);
            }

            Console.Write("Enter Discord Webhook URL (optional, press Enter to skip): ");
            var webhook = Console.ReadLine()?.Trim();

            Console.WriteLine("Activation confirmed. Starting ...\n");

            Console.Write("Enter WSS URL: ");
            var wss = Console.ReadLine();

            var baseStake = PromptBaseStake();
            var client = new MarbleRaceClient(baseStake, webhook);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            await client.RunAsync(wss!, cts.Token);
        }

        private static int PromptBaseStake()
        {
            Console.Write("Base stake per marble (default 10): ");
            var s = Console.ReadLine();
            if (int.TryParse(s, out var v) && v > 0 && v <= 5000) return v;
            Console.WriteLine("Invalid. Using 10.");
            return 10;
        }
    }
}
