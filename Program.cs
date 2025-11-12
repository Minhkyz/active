using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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

        private readonly HashSet<string> _chosenThisRound = new();
        private readonly List<SimulationTracker> _simulations;
        private readonly HashSet<SimulationTracker> _activeLiveSignals = new();
        private readonly Dictionary<string, int> _lastLiveBet = new();
        private readonly WebMonitor? _monitor;

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

        internal static readonly string[] AllSpots =
        {
            "Marble_1", "Marble_2", "Marble_3", "Marble_4", "Marble_5", "Marble_6"
        };

        public MarbleRaceClient(int baseStake = 10, string discordWebhookUrl = "", WebMonitor? monitor = null)
        {
            _baseStake = baseStake <= 0 ? 10 : baseStake;
            _discordWebhookUrl = discordWebhookUrl?.Trim() ?? string.Empty;
            _monitor = monitor;

            _simulations = Enumerable.Range(0, 10)
                .Select(i => new SimulationTracker($"Sim{i + 1}", AllSpots, i, _baseStake, MaxStakePerSpot, EscalateFactor))
                .ToList();

            if (_monitor != null)
            {
                foreach (var sim in _simulations)
                {
                    _monitor.RegisterSimulation(sim.Name);
                }
            }
        }

        private void ResetAllToBase()
        {
            _lastLiveBet.Clear();
            foreach (var sim in _simulations)
            {
                sim.ResetToBase();
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
                var stakeSnapshot = FormatStakeSnapshot(_lastLiveBet);
                await PostDiscordEmbedAsync(
                    title: "Marble — WIN",
                    desc: $"**Round:** `{_currentNumber}` (`{_currentGameId}`)\n**WinStreak:** `{_winStreak}` • **Wins(total):** `{_realWins}`",
                    kind: "WIN",
                    fields: new[]
                    {
                        ("Stake snapshot", $"`{stakeSnapshot}`", false),
                        ("Balance", _isFirstBalance ? "`-`" : $"`{Math.Round(_currentBalance, 2)}`", true),
                        ("P/L Total", _isFirstBalance ? "`-`" : $"`{Math.Round(_currentBalance - _firstBalance, 2)}`", true),
                        ("Total sum LIVE", $"`{_sumTotalLive:N0}`", true)
                    }
                );

            }
            else
            {
                _winStreak = 0;

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($" - LOSS. Number={_currentNumber} GameId={_currentGameId}");
                Console.ResetColor();

                var stakeSnapshot = FormatStakeSnapshot(_lastLiveBet);
                await PostDiscordEmbedAsync(
                    title: "Marble — LOSS",
                    desc: $"**Round:** `{_currentNumber}` (`{_currentGameId}`)",
                    kind: "LOSS",
                    fields: new[]
                    {
                        ("Stake snapshot", $"`{stakeSnapshot}`", false),
                        ("Balance", _isFirstBalance ? "`-`" : $"`{Math.Round(_currentBalance, 2)}`", true),
                        ("P/L Total", _isFirstBalance ? "`-`" : $"`{Math.Round(_currentBalance - _firstBalance, 2)}`", true),
                        ("Total sum LIVE", $"`{_sumTotalLive:N0}`", true)
                    }
                );

            }

            _hasPlacedForThisRound = false;
            _chosenThisRound.Clear();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" - Simulation stakes snapshot:");
            foreach (var sim in _simulations.OrderBy(s => s.Name))
            {
                Console.WriteLine($"   {sim.Name}: Stake={sim.CurrentStakePerSpot} | Losses={sim.ConsecutiveLosses} | Zero={sim.ZeroSpot}");
            }
            Console.ResetColor();

            var reachedCap = _activeLiveSignals.Any(sim => sim.CurrentStakePerSpot >= MaxStakePerSpot);
            if (reachedCap)
            {
                await NotifyResetAsync($"Simulation stake reached MaxStakePerSpot ({MaxStakePerSpot})");
                ResetAllToBase();
                _activeLiveSignals.Clear();
                _isLiveBetting = false;
            }

            if (_activeLiveSignals.Count == 0)
            {
                _isLiveBetting = false;
            }
        }

        private void RunSimulationsForRound()
        {
            long roundTotal = 0;
            foreach (var sim in _simulations)
            {
                var snapshot = sim.PrepareRound(_currentGameId, _currentNumber);
                roundTotal += sim.TotalStakeThisRound;

                _monitor?.RecordPrediction(snapshot);

                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine(
                    $"[SIM-{sim.Name}] Stake={sim.CurrentStakePerSpot} | LossStreak={sim.ConsecutiveLosses} | Zero: {sim.ZeroSpot} | Picks: {string.Join(",", snapshot.Picks)}");
                Console.ResetColor();
            }

            _sumTotalSim += roundTotal;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[SIM] Tổng cộng dồn Total (SIM) từ đầu phiên: {_sumTotalSim:N0}");
            Console.ResetColor();
        }

        private async Task PlaceLiveBetAsync(ClientWebSocket ws, string gameId, CancellationToken ct)
        {
            var aggregated = new Dictionary<string, int>();
            foreach (var sim in _activeLiveSignals)
            {
                foreach (var kv in sim.CurrentProposedStakes)
                {
                    aggregated[kv.Key] = aggregated.TryGetValue(kv.Key, out var current)
                        ? current + kv.Value
                        : kv.Value;
                }
            }

            if (aggregated.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine(" - No aggregated stakes available for live bet.");
                Console.ResetColor();
                return;
            }

            var ordered = aggregated
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

            var selectedAmounts = selected.ToDictionary(
                spot => spot,
                spot => aggregated.TryGetValue(spot, out var value) ? value : 0);

            var minStake = selectedAmounts.Values.DefaultIfEmpty(0).Min();
            if (minStake > 0 && selectedAmounts.Values.Any(v => v > minStake))
            {
                foreach (var spot in selected)
                {
                    selectedAmounts[spot] = Math.Max(0, selectedAmounts[spot] - minStake);
                }
            }

            var chips = new Dictionary<string, int>();
            var total = 0;
            var capped = false;

            _chosenThisRound.Clear();
            _lastLiveBet.Clear();

            var fullSnapshot = new Dictionary<string, int>();
            foreach (var spot in AllSpots)
            {
                fullSnapshot[spot] = 0;
            }

            foreach (var spot in selected)
            {
                var adjustedAmount = selectedAmounts[spot];
                if (adjustedAmount <= 0) continue;

                if (adjustedAmount > MaxStakePerSpot) capped = true;
                var finalAmount = Math.Min(MaxStakePerSpot, adjustedAmount);
                chips[spot] = finalAmount;
                _chosenThisRound.Add(spot);
                fullSnapshot[spot] = finalAmount;
                total += finalAmount;
            }

            fullSnapshot[zeroSpot] = 0;

            foreach (var kv in fullSnapshot)
            {
                _lastLiveBet[kv.Key] = kv.Value;
            }

            if (chips.Count == 0 || total <= 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine(" - Combined signals resulted in zero stake. Skipping live bet this round.");
                Console.ResetColor();
                _hasPlacedForThisRound = false;
                return;
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

            _monitor?.RecordLiveBet(fullSnapshot, gameId, _currentNumber);

            var picksList = string.Join(", ", chips.Keys);
            var amtList = string.Join(", ", chips.Select(kv => $"{kv.Key}={kv.Value}"));

            await PostDiscordEmbedAsync(
                title: "Marble — BET",
                desc: $"**Round:** `{_currentNumber}` (`{gameId}`)\n**Zero:** `{zeroSpot}`",
                kind: "BET",
                fields: new[]
                {
                    ("Amounts", chips.Count > 0 ? $"`{amtList}`" : "`-`", false),
                    ("Total", $"`{total}`", true),
                    ("Balance", _isFirstBalance ? "`-`" : $"`{Math.Round(_currentBalance, 2)}`", true)
                }
            );

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($" - BET GameId {gameId} ({_currentNumber}) → Picks: {picksList} | Zero: {zeroSpot} | Total={total}");
            Console.ResetColor();

            if (capped)
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($"   ⚠️ Some combined signals exceeded MaxStakePerSpot={MaxStakePerSpot}. Amounts capped.");
                Console.ResetColor();
            }
        }

        private static string FormatStakeSnapshot(IReadOnlyDictionary<string, int> stakes)
        {
            return string.Join(", ", AllSpots.Select(spot =>
            {
                var amount = stakes.TryGetValue(spot, out var value) ? value : 0;
                return $"{spot}={amount}";
            }));
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
            private readonly int _baseStake;
            private readonly int _maxStake;
            private readonly double _escalateFactor;

            private int _roundCounter;
            private readonly Dictionary<string, int> _currentProposedStakes = new();

            public SimulationTracker(string name, string[] allSpots, int offset, int baseStake, int maxStake, double escalateFactor)
            {
                Name = name;
                _allSpots = allSpots;
                _offset = offset;
                _baseStake = baseStake;
                _maxStake = maxStake;
                _escalateFactor = escalateFactor;
                CurrentPicks = new HashSet<string>();
                CurrentStakePerSpot = _baseStake;
            }

            public string Name { get; }
            public HashSet<string> CurrentPicks { get; private set; }
            public IReadOnlyDictionary<string, int> CurrentProposedStakes => _currentProposedStakes;
            public string ZeroSpot { get; private set; } = string.Empty;
            public int ConsecutiveLosses { get; private set; }
            public int TotalStakeThisRound { get; private set; }
            public int CurrentStakePerSpot { get; private set; }
            public bool SignalActive => ConsecutiveLosses >= 2;
            public SimulationPredictionSnapshot PrepareRound(string gameId, string roundNumber)
            {
                _roundCounter++;
                var zeroIndex = (_roundCounter + _offset) % _allSpots.Length;
                ZeroSpot = _allSpots[zeroIndex];
                CurrentPicks = new HashSet<string>(_allSpots.Where(s => s != ZeroSpot));
                _currentProposedStakes.Clear();
                foreach (var spot in CurrentPicks)
                {
                    _currentProposedStakes[spot] = CurrentStakePerSpot;
                }
                TotalStakeThisRound = _currentProposedStakes.Values.Sum();

                var snapshot = new SimulationPredictionSnapshot(
                    Name,
                    gameId,
                    roundNumber,
                    ZeroSpot,
                    new Dictionary<string, int>(_currentProposedStakes),
                    CurrentPicks.ToArray(),
                    CurrentStakePerSpot,
                    DateTime.UtcNow);

                LastSnapshot = snapshot;
                return snapshot;
            }

            public bool ProcessResult(IEnumerable<string> winningSpots)
            {
                var win = winningSpots.Any(CurrentPicks.Contains);
                if (win)
                {
                    ConsecutiveLosses = 0;
                    CurrentStakePerSpot = _baseStake;
                }
                else
                {
                    ConsecutiveLosses++;
                    var escalated = (int)Math.Ceiling(CurrentStakePerSpot * _escalateFactor);
                    CurrentStakePerSpot = Math.Min(_maxStake, Math.Max(_baseStake, escalated));
                }

                return win;
            }

            public void ResetToBase()
            {
                ConsecutiveLosses = 0;
                CurrentStakePerSpot = _baseStake;
            }

            public SimulationPredictionSnapshot? LastSnapshot { get; private set; }
        }
    }

    public record SimulationPredictionSnapshot(
        string Simulation,
        string GameId,
        string RoundNumber,
        string ZeroSpot,
        IReadOnlyDictionary<string, int> Stakes,
        IReadOnlyCollection<string> Picks,
        int StakePerSpot,
        DateTime TimestampUtc);

    public record LiveBetSnapshot(
        string GameId,
        string RoundNumber,
        DateTime TimestampUtc,
        IReadOnlyDictionary<string, int> Amounts);

    public class WebMonitor
    {
        private readonly string[] _spots;
        private readonly int _historyLimit;
        private readonly int _port;
        private readonly Dictionary<string, LimitedQueue<SimulationPredictionSnapshot>> _history = new();
        private readonly object _sync = new();
        private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private LiveBetSnapshot? _lastLiveBet;
        private HttpListener? _listener;
        private bool _started;

        public WebMonitor(string[] spots, int historyLimit = 20, int port = 8080)
        {
            _spots = spots;
            _historyLimit = historyLimit;
            _port = port;
        }

        public void RegisterSimulation(string simulation)
        {
            lock (_sync)
            {
                if (!_history.ContainsKey(simulation))
                {
                    _history[simulation] = new LimitedQueue<SimulationPredictionSnapshot>(_historyLimit);
                }
            }
        }

        public void RecordPrediction(SimulationPredictionSnapshot snapshot)
        {
            lock (_sync)
            {
                if (!_history.TryGetValue(snapshot.Simulation, out var queue))
                {
                    queue = new LimitedQueue<SimulationPredictionSnapshot>(_historyLimit);
                    _history[snapshot.Simulation] = queue;
                }

                queue.Enqueue(snapshot);
            }
        }

        public void RecordLiveBet(IReadOnlyDictionary<string, int> amounts, string gameId, string? roundNumber)
        {
            lock (_sync)
            {
                var copy = amounts.ToDictionary(kv => kv.Key, kv => kv.Value);
                _lastLiveBet = new LiveBetSnapshot(gameId, roundNumber ?? string.Empty, DateTime.UtcNow, copy);
            }
        }

        public Task StartAsync(CancellationToken ct)
        {
            lock (_sync)
            {
                if (_started) return Task.CompletedTask;
                _started = true;
            }

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://*:{_port}/");
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                lock (_sync)
                {
                    _started = false;
                }

                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($"Web monitor failed to start: {ex.Message}");
                Console.ResetColor();
                return Task.CompletedTask;
            }

            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"🌐 Web monitor available at http://localhost:{_port}/");
            Console.ResetColor();

            ct.Register(() =>
            {
                try
                {
                    _listener?.Stop();
                }
                catch
                {
                    // ignored
                }
            });

            return Task.Run(() => ListenLoopAsync(ct), ct);
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            if (_listener == null) return;

            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext? context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleContextAsync(context), ct);
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url?.AbsolutePath ?? "/";
                if (string.IsNullOrWhiteSpace(path)) path = "/";
                if (path != "/") path = path.TrimEnd('/');

                switch (path)
                {
                    case "/":
                        await WriteHtmlAsync(context.Response);
                        break;
                    case "/data":
                        await WriteJsonAsync(context.Response);
                        break;
                    default:
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        break;
                }
            }
            catch
            {
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch
                {
                    // ignored
                }
            }
        }

        private async Task WriteHtmlAsync(HttpListenerResponse response)
        {
            const string html = @"<!DOCTYPE html>
<html lang=\"en\">
<head>
    <meta charset=\"utf-8\" />
    <title>Marble Race Monitor</title>
    <style>
        body { font-family: 'Segoe UI', Tahoma, sans-serif; background: #0c1320; color: #f0f3ff; margin: 0; padding: 24px; }
        h1, h2 { margin-top: 0; }
        a { color: #5ad1ff; }
        table { border-collapse: collapse; width: 100%; margin-bottom: 24px; }
        th, td { border: 1px solid #1f2a3d; padding: 6px 8px; font-size: 13px; vertical-align: top; }
        th { background: #142033; }
        .grid { display: grid; grid-template-columns: repeat(3, minmax(160px, 1fr)); gap: 12px; max-width: 540px; }
        .square { background: #182844; border-radius: 10px; padding: 12px; box-shadow: 0 0 8px rgba(0,0,0,0.35); }
        .square .label { font-size: 14px; text-transform: uppercase; letter-spacing: .08em; color: #9cb3d1; }
        .square .value { font-size: 26px; font-weight: 600; margin-top: 6px; }
        .timestamp { color: #9cb3d1; font-size: 12px; }
        .entries { margin-bottom: 18px; }
        code { background: rgba(255,255,255,0.08); padding: 2px 4px; border-radius: 4px; }
    </style>
</head>
<body>
    <h1>Marble Race Monitor</h1>
    <p class=\"timestamp\">Auto-refreshing every 2 seconds.</p>

    <section>
        <h2>Last Live Bet</h2>
        <div id=\"bet-grid\" class=\"grid\"></div>
        <p id=\"bet-meta\" class=\"timestamp\"></p>
    </section>

    <section>
        <h2>Simulation Predictions (last 20)</h2>
        <div id=\"predictions\"></div>
    </section>

    <script>
        async function loadData() {
            try {
                const res = await fetch('/data');
                if (!res.ok) throw new Error('HTTP ' + res.status);
                const data = await res.json();
                renderBet(data);
                renderPredictions(data);
            } catch (err) {
                console.error('Failed to load data', err);
            }
        }

        function renderBet(data) {
            const container = document.getElementById('bet-grid');
            container.innerHTML = '';
            const meta = document.getElementById('bet-meta');
            meta.textContent = '';

            if (!data.lastLiveBet) {
                container.innerHTML = '<p>No live bet recorded yet.</p>';
                return;
            }

            data.order.forEach(spot => {
                const amount = data.lastLiveBet.amounts[spot] ?? 0;
                const div = document.createElement('div');
                div.className = 'square';
                div.innerHTML = `<div class="label">${spot}</div><div class="value">${amount.toLocaleString()}</div>`;
                container.appendChild(div);
            });

            const when = new Date(data.lastLiveBet.timestampUtc).toLocaleString();
            meta.textContent = `Round ${data.lastLiveBet.roundNumber || '-'} • Game ${data.lastLiveBet.gameId || '-'} • Updated ${when}`;
        }

        function renderPredictions(data) {
            const host = document.getElementById('predictions');
            host.innerHTML = '';

            data.predictions.forEach(sim => {
                const wrapper = document.createElement('div');
                wrapper.className = 'entries';
                const title = document.createElement('h3');
                title.textContent = sim.simulation;
                wrapper.appendChild(title);

                if (!sim.entries.length) {
                    const empty = document.createElement('p');
                    empty.textContent = 'No predictions yet.';
                    wrapper.appendChild(empty);
                } else {
                    const table = document.createElement('table');
                    const thead = document.createElement('thead');
                    thead.innerHTML = '<tr><th>When</th><th>Round</th><th>Zero</th><th>Stake/spot</th><th>Picks</th></tr>';
                    table.appendChild(thead);
                    const tbody = document.createElement('tbody');

                    sim.entries.forEach(entry => {
                        const row = document.createElement('tr');
                        const when = new Date(entry.timestampUtc).toLocaleTimeString();
                        row.innerHTML = `<td>${when}</td><td>${entry.roundNumber || '-'}</td><td>${entry.zeroSpot}</td><td>${entry.stakePerSpot.toLocaleString()}</td><td><code>${entry.picks.join(', ')}</code></td>`;
                        tbody.appendChild(row);
                    });

                    table.appendChild(tbody);
                    wrapper.appendChild(table);
                }

                host.appendChild(wrapper);
            });
        }

        loadData();
        setInterval(loadData, 2000);
    </script>
</body>
</html>";

            response.ContentType = "text/html; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

        private async Task WriteJsonAsync(HttpListenerResponse response)
        {
            var snapshot = CreateSnapshot();
            var json = JsonSerializer.Serialize(snapshot, _jsonOptions);

            response.ContentType = "application/json; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

        private object CreateSnapshot()
        {
            lock (_sync)
            {
                var predictions = _history
                    .OrderBy(kv => kv.Key)
                    .Select(kv => new
                    {
                        simulation = kv.Key,
                        entries = kv.Value.ToArray()
                            .OrderByDescending(entry => entry.TimestampUtc)
                            .Take(_historyLimit)
                            .Select(entry => new
                            {
                                entry.GameId,
                                entry.RoundNumber,
                                entry.ZeroSpot,
                                entry.StakePerSpot,
                                entry.TimestampUtc,
                                entry.Picks,
                                entry.Stakes
                            })
                    });

                object? liveBet = null;
                if (_lastLiveBet != null)
                {
                    liveBet = new
                    {
                        _lastLiveBet.GameId,
                        _lastLiveBet.RoundNumber,
                        _lastLiveBet.TimestampUtc,
                        _lastLiveBet.Amounts
                    };
                }

                return new
                {
                    generatedAt = DateTime.UtcNow,
                    order = _spots,
                    predictions,
                    lastLiveBet = liveBet
                };
            }
        }
    }

    internal class LimitedQueue<T>
    {
        private readonly Queue<T> _queue;
        private readonly int _capacity;

        public LimitedQueue(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _queue = new Queue<T>(_capacity);
        }

        public void Enqueue(T item)
        {
            if (_queue.Count == _capacity)
            {
                _queue.Dequeue();
            }

            _queue.Enqueue(item);
        }

        public T[] ToArray() => _queue.ToArray();
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

            using var cts = new CancellationTokenSource();
            var monitor = new WebMonitor(MarbleRaceClient.AllSpots);
            _ = monitor.StartAsync(cts.Token);

            var client = new MarbleRaceClient(baseStake, webhook, monitor);
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
