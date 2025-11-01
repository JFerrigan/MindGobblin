using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

// TODO: Fix errors when transitioning between pong-lobbies.html and pong-duel.html.
// TODO: handle browser events that navigate away from pong-duel.html like page refresh, 
// changing open up another URL while in duel and coming back, clicking the back button,
// etc. Right now, the only clean way to leave pong-duel.html is by clicking the leave
// button.
// TODO: no touch controls atm
namespace JakeServer.Hubs
{
    public class PongGameHub : Hub
    {
        #region Definitions and Initializations
        public class Duel
        {
            public string LobbyId = "";
            public string LeftConn = "";
            public string RightConn = "";
            public float leftY, rightY;
            public float ballX, ballY;
            public float ballVX, ballVY;
            public float ballSpeed;
            public int scoreLeft = 0, scoreRight = 0;
            public bool BallInPlay = false;
            public bool Serving = true;
            public int ServeDir = -1;
            public CancellationTokenSource cts = new();
            public Task? RunTask = null;
        }

        private class ConnectionInfo
        {
            public string? ContextType { get; set; }
            public string? LobbyId { get; set; }
            public string? Side { get; set; }
        }

        // Joey would love these dependency injections lol
        private readonly JakeServerDbContext _db;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHubContext<PongGameHub> _hubContext;
        public PongGameHub(JakeServerDbContext db, IServiceScopeFactory scopeFactory, IHubContext<PongGameHub> hubContext)
        {
            _db = db;
            _scopeFactory = scopeFactory;
            _hubContext = hubContext;
        }

        private static readonly ConcurrentDictionary<string, ConnectionInfo> ConnectionContexts = new();

        private static ConcurrentDictionary<string, bool> ActiveConnections = new();
        private bool LobbyCreationInProgress = false;

        static ConcurrentDictionary<string, Duel> Duels = new();

        const int WIDTH = 900;
        const int HEIGHT = 500;
        const float PADDLE_HEIGHT = (float)(HEIGHT * 0.20); // slightly bigger than client to give player some buffer
        const float BALL_RADIUS = 9f;
        const float BALL_START_SPEED = 5.0f;
        const float TICK_HZ = 30f;
        const float BALL_SPEED_INCREMENT = 0.25f;
        const int WIN_SCORE = 5;
        const float MAX_BOUNCE_ANGLE = (float)(Math.PI / 3);
        #endregion

        #region Lobbies and Duels Management
        private void CleanupOrphanedDuels(IEnumerable<PongGameLobby> activeLobbies)
        {
            var activeLobbyIds = activeLobbies.Select(l => l.LobbyId).ToHashSet();

            foreach (var kvp in Duels)
            {
                var duel = kvp.Value;

                bool lobbyMissing = !activeLobbyIds.Contains(duel.LobbyId);
                bool bothDisconnected =
                    (string.IsNullOrEmpty(duel.LeftConn) || !IsConnectionActive(duel.LeftConn)) &&
                    (string.IsNullOrEmpty(duel.RightConn) || !IsConnectionActive(duel.RightConn));

                if (lobbyMissing || bothDisconnected)
                {
                    duel.cts.Cancel();
                    Duels.TryRemove(kvp.Key, out _);
                }
            }
        }

        public async Task<object[]> ListLobbies()
        {
            // Load candidate lobbies
            var lobbies = await _db.PongGameLobbies.ToListAsync();

            object[] arr;

            if (LobbyCreationInProgress)
            {
                arr = lobbies.Select(l => new
                {
                    lobbyId = l.LobbyId,
                    lobbyName = l.LobbyName,
                    players = (l.HostConnectionId != null ? 1 : 0) + (l.ChallengerConnectionId != null ? 1 : 0)
                }).ToArray();
            }
            else
            {
                // Clean up orphaned or stale lobbies
                var toRemove = new List<PongGameLobby>();

                foreach (var l in lobbies)
                {
                    bool hostDisconnected = string.IsNullOrEmpty(l.HostConnectionId) || !IsConnectionActive(l.HostConnectionId);
                    bool challengerDisconnected = string.IsNullOrEmpty(l.ChallengerConnectionId) || !IsConnectionActive(l.ChallengerConnectionId);

                    // For stale timeout check
                    bool isStale = (DateTime.UtcNow - l.CreatedUtc) > TimeSpan.FromMinutes(10);

                    // If both players disconnected or it’s stale
                    if ((hostDisconnected && challengerDisconnected) || isStale)
                    {
                        toRemove.Add(l);
                    }
                }

                if (toRemove.Count > 0)
                {
                    _db.PongGameLobbies.RemoveRange(toRemove);
                    await _db.SaveChangesAsync();
                }

                // Clean up orphaned or stale duels
                CleanupOrphanedDuels(lobbies);

                // Return remaining active lobbies
                arr = lobbies
                    .Except(toRemove)
                    .Select(l => new
                    {
                        lobbyId = l.LobbyId,
                        lobbyName = l.LobbyName,
                        players = (l.HostConnectionId != null ? 1 : 0) + (l.ChallengerConnectionId != null ? 1 : 0)
                    })
                    .ToArray();
            }
            return arr;
        }

        public async Task<string> CreateLobby(string lobbyName)
        {
            // Set this flag so we don't accidentally clean up
            // brand-new lobbies and duels that are just created
            LobbyCreationInProgress = true;

            // Create initial lobby row in db with given lobbyName
            var lobby = new PongGameLobby();
            lobby.LobbyName = lobbyName;
            _db.PongGameLobbies.Add(lobby);
            await _db.SaveChangesAsync();

            // Create initial lobby object
            var duel = new Duel
            {
                LobbyId = lobby.LobbyId,
                leftY = (HEIGHT - PADDLE_HEIGHT) / 2,
                rightY = (HEIGHT - PADDLE_HEIGHT) / 2,
                ballX = WIDTH / 2,
                ballY = HEIGHT / 2,
                ballSpeed = BALL_START_SPEED,
                ballVX = BALL_START_SPEED,
                ballVY = 0
            };
            Duels[lobby.LobbyId] = duel;

            // Send back Matched event (which will fire CheckDuel() upon page transition to pong-duel.html)
            await Clients.Caller.SendAsync("Matched", lobby.LobbyId, "Left");

            return lobby.LobbyId;
        }

        public async Task<bool> CheckDuel(string lobbyId, string side)
        {
            if (!Duels.TryGetValue(lobbyId, out var duel))
                return false;

            var lobby = await _db.PongGameLobbies.FirstOrDefaultAsync(l => l.LobbyId == lobbyId);

            // Fill out Duel object depending which side connected
            if (string.IsNullOrEmpty(duel.LeftConn) && side == "Left")
            {
                duel.LeftConn = Context.ConnectionId;
                if (lobby is not null)
                {
                    lobby.HostConnectionId = duel.LeftConn;
                    await _db.SaveChangesAsync();
                    await Clients.All.SendAsync("LobbiesUpdated");
                }
                await Groups.AddToGroupAsync(duel.LeftConn, lobbyId);
            }
            else if (string.IsNullOrEmpty(duel.RightConn) && side == "Right")
            {
                duel.RightConn = Context.ConnectionId;
                if (lobby is not null)
                {
                    lobby.ChallengerConnectionId = duel.RightConn;
                    await _db.SaveChangesAsync();
                    await Clients.All.SendAsync("LobbiesUpdated");
                }
                await Groups.AddToGroupAsync(duel.RightConn, lobbyId);
                CheckWinOrReset(duel, true);
                // Set the inital serve message (rightchallenger serve upon game start)
                SetServingSide(duel, "Right");
            }

            // Flag to say lobby and duel creation done, clean-up process can proceed if need be
            LobbyCreationInProgress = false;

            // Start game loop only when both connections exist
            if (!string.IsNullOrEmpty(duel.LeftConn) && !string.IsNullOrEmpty(duel.RightConn))
            {
                if (duel.RunTask == null || duel.RunTask.IsCompleted)
                {
                    duel.cts = new CancellationTokenSource();
                    duel.RunTask = Task.Run(() => RunDuelLoop(duel, duel.cts.Token));
                }
            }

            return true;
        }

        public async Task<bool> JoinLobby(string lobbyId)
        {
            var lobby = await _db.PongGameLobbies.FirstOrDefaultAsync(l => l.LobbyId == lobbyId);
            if (lobby == null || lobby.IsFull)
                return false;

            // If host joined first, somehow bypassing CreateLobby()...
            if (string.IsNullOrEmpty(lobby.ChallengerConnectionId) && string.IsNullOrEmpty(lobby.HostConnectionId))
            {
                // Ideally, this code shouldn't be hit if CreateLobby() does its job...
                await Clients.Caller.SendAsync("Matched", lobby.LobbyId, "Left");
                return true;
            }

            // Challenger joining, sending matched event
            if (string.IsNullOrEmpty(lobby.ChallengerConnectionId))
            {
                await Clients.Caller.SendAsync("Matched", lobby.LobbyId, "Right");
                return true;
            }

            return false;
        }

        public async Task LeaveLobby(string lobbyId, string side)
        {
            // Load the lobby from DB
            var lobby = await _db.PongGameLobbies.FirstOrDefaultAsync(l => l.LobbyId == lobbyId);
            if (lobby == null)
                return;

            // Handle lobby clean-up
            if (side == "Left")
            {
                // Host leaving — remove the lobby entirely
                _db.PongGameLobbies.Remove(lobby);
            }
            else
            {
                // Challenger leaving — clear challenger slot but keep lobby alive
                lobby.ChallengerConnectionId = null;
                _db.PongGameLobbies.Update(lobby);
            }

            await _db.SaveChangesAsync();
            await Clients.All.SendAsync("LobbiesUpdated");

            // Handle duel clean-up
            if (Duels.TryGetValue(lobbyId, out var d))
            {
                if (side == "Left")
                {
                    // Host left — tear down duel completely
                    d.cts.Cancel();
                    Duels.TryRemove(lobbyId, out _);

                    // Send event before we remove the user from group
                    await Clients.Group(lobbyId).SendAsync("LobbyClosing", "Host has left the game.");

                    // Remove both players from SignalR group
                    await Groups.RemoveFromGroupAsync(d.LeftConn, lobbyId);
                    await Groups.RemoveFromGroupAsync(d.RightConn, lobbyId);
                }
                else
                {
                    // Challenger left — keep duel open, but remove their connection
                    if (!string.IsNullOrEmpty(d.RightConn))
                    {
                        CheckWinOrReset(d, true);
                        await Clients.Group(lobbyId).SendAsync("DuelEnded", "Opponent left the match.");
                        await Groups.RemoveFromGroupAsync(d.RightConn, lobbyId);
                        d.RightConn = "";
                    }
                }
            }
        }
        #endregion

        #region Connection Management
        public override Task OnConnectedAsync()
        {
            var httpContext = Context.GetHttpContext();
            var contextType = httpContext?.Request.Query["context"].ToString();
            var lobbyId = httpContext?.Request.Query["lobbyId"].ToString();
            var side = httpContext?.Request.Query["side"].ToString();

            // Track connection details
            ConnectionContexts[Context.ConnectionId] = new ConnectionInfo
            {
                ContextType = contextType,
                LobbyId = lobbyId,
                Side = side
            };

            // Only mark duel connections as active
            if (string.Equals(contextType, "duel", StringComparison.OrdinalIgnoreCase))
                ActiveConnections[Context.ConnectionId] = true;

            Console.WriteLine($"[Connect] {Context.ConnectionId} ({contextType}) Lobby={lobbyId} Side={side}");

            return base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            // Always remove from context tracking
            ConnectionContexts.TryRemove(Context.ConnectionId, out var info);

            // Only remove from ActiveConnections if this was a duel connection
            if (info?.ContextType == "duel")
                ActiveConnections.TryRemove(Context.ConnectionId, out _);

            if (info?.ContextType == "duel" && !string.IsNullOrEmpty(info.LobbyId))
            {
                // Disconnection logic
                var connId = Context.ConnectionId;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000); // wait 3 seconds to confirm it’s a true disconnect
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<JakeServerDbContext>();

                    // Check if the same player has reconnected
                    bool reconnected = db.PongGameLobbies.Any(l =>
                        l.HostConnectionId == connId || l.ChallengerConnectionId == connId);
                    if (reconnected) return;

                    var affectedLobbies = await db.PongGameLobbies
                        .Where(l => l.HostConnectionId == connId || l.ChallengerConnectionId == connId)
                        .ToListAsync();

                    // Clean up lobby
                    foreach (var lobby in affectedLobbies)
                    {
                        db.PongGameLobbies.Remove(lobby);
                        await db.SaveChangesAsync();
                        await Clients.All.SendAsync("LobbiesUpdated");
                    }

                    // Clean up duel
                    foreach (var kv in Duels)
                    {
                        var d = kv.Value;
                        bool leftGone = string.IsNullOrEmpty(d.LeftConn) || d.LeftConn == connId || !IsConnectionActive(d.LeftConn);
                        bool rightGone = string.IsNullOrEmpty(d.RightConn) || d.RightConn == connId || !IsConnectionActive(d.RightConn);

                        if (leftGone && rightGone)
                        {
                            d.cts.Cancel();
                            Duels.TryRemove(kv.Key, out _);
                            await Clients.Group(d.LobbyId).SendAsync("DuelEnded", "Opponent disconnected");
                        }
                    }
                });
            }
            await base.OnDisconnectedAsync(exception);
        }

        private static bool IsConnectionActive(string connectionId)
        {
            return ActiveConnections.ContainsKey(connectionId);
        }
        #endregion

        #region Game Logic
        public Task SendInput(string lobbyId, float paddleY, string side)
        {
            if (Duels.TryGetValue(lobbyId, out var d))
            {
                if (side == "Left") d.leftY = paddleY;
                else d.rightY = paddleY;
            }
            return Task.CompletedTask;
        }

        private async Task RunDuelLoop(Duel d, CancellationToken token)
        {
            var interval = TimeSpan.FromSeconds(1.0 / TICK_HZ);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Don't run our duel loop if either side is missing, and reset UI in case
                    if (string.IsNullOrEmpty(d.LeftConn) || string.IsNullOrEmpty(d.RightConn))
                    {
                        CheckWinOrReset(d, true);
                        break;
                    }

                    // Only run step logic to update duel object when ball is in play
                    if (d.BallInPlay)
                    {
                        Step(d, 1f / TICK_HZ);
                    }

                    await _hubContext.Clients.Group(d.LobbyId).SendAsync("StateUpdate", new
                    {
                        ballX = d.ballX,
                        ballY = d.ballY,
                        leftY = d.leftY,
                        rightY = d.rightY,
                        scoreLeft = d.scoreLeft,
                        scoreRight = d.scoreRight,
                        isReset = !d.BallInPlay // true when waiting for next serve
                    });
                    await Task.Delay(interval, token);
                }
            }
            catch (TaskCanceledException) { }
        }

        private void Step(Duel d, float dt)
        {
            // move ball
            d.ballX += d.ballVX * dt * 60f; // match frame scaling
            d.ballY += d.ballVY * dt * 60f;

            // top/bottom bounce
            if (d.ballY - BALL_RADIUS <= 0)
            {
                d.ballY = BALL_RADIUS;
                d.ballVY = -d.ballVY;
            }
            else if (d.ballY + BALL_RADIUS >= HEIGHT)
            {
                d.ballY = HEIGHT - BALL_RADIUS;
                d.ballVY = -d.ballVY;
            }

            float paddleW = 14;
            float pxL = 24;
            float pxR = WIDTH - 24 - paddleW;

            // left paddle
            if (d.ballX - BALL_RADIUS <= pxL + paddleW &&
                d.ballX - BALL_RADIUS >= pxL &&
                d.ballY >= d.leftY - 2 && d.ballY <= d.leftY + PADDLE_HEIGHT + 2)
            {
                float relative = (d.ballY - (d.leftY + PADDLE_HEIGHT / 2)) / (PADDLE_HEIGHT / 2f);
                float bounceAngle = relative * MAX_BOUNCE_ANGLE;

                d.ballSpeed = Math.Min(50f, d.ballSpeed + BALL_SPEED_INCREMENT);
                d.ballVX = (float)(Math.Cos(bounceAngle) * d.ballSpeed);
                d.ballVY = (float)(Math.Sin(bounceAngle) * d.ballSpeed);
                if (d.ballVX < 0) d.ballVX = -d.ballVX;

                d.ballX = pxL + paddleW + BALL_RADIUS + 0.5f;
            }

            // right paddle
            if (d.ballX + BALL_RADIUS >= pxR &&
                d.ballX + BALL_RADIUS <= pxR + paddleW &&
                d.ballY >= d.rightY - 2 && d.ballY <= d.rightY + PADDLE_HEIGHT + 2)
            {
                float relative = (d.ballY - (d.rightY + PADDLE_HEIGHT / 2)) / (PADDLE_HEIGHT / 2f);
                float bounceAngle = relative * MAX_BOUNCE_ANGLE;

                d.ballSpeed = Math.Min(20f, d.ballSpeed + BALL_SPEED_INCREMENT);
                d.ballVX = -(float)(Math.Cos(bounceAngle) * d.ballSpeed);
                d.ballVY = (float)(Math.Sin(bounceAngle) * d.ballSpeed);
                if (d.ballVX > 0) d.ballVX = -d.ballVX;

                d.ballX = pxR - BALL_RADIUS - 0.5f;
            }

            // scoring
            if (d.ballX + BALL_RADIUS < 0)
            {
                d.scoreRight++;
                d.ServeDir = 1;
                SetServingSide(d, "Left");
                CheckWinOrReset(d);
            }
            else if (d.ballX - BALL_RADIUS > WIDTH)
            {
                d.scoreLeft++;
                d.ServeDir = -1;
                SetServingSide(d, "Right");
                CheckWinOrReset(d);
            }
        }

        public async Task ServeBall(string lobbyId)
        {
            if (!Duels.TryGetValue(lobbyId, out var d)) return;

            if (!d.BallInPlay && d.Serving)
            {
                ResetBall(d, d.ServeDir > 0);
                await _hubContext.Clients.Group(lobbyId).SendAsync("BallServed");

                d.BallInPlay = true;
                d.Serving = false;

                await _hubContext.Clients.Group(lobbyId).SendAsync("StateUpdate", new
                {
                    ballX = d.ballX,
                    ballY = d.ballY,
                    leftY = d.leftY,
                    rightY = d.rightY,
                    scoreLeft = d.scoreLeft,
                    scoreRight = d.scoreRight,
                    isReset = !d.BallInPlay

                });
            }
        }

        private void ResetBall(Duel d, bool towardRight)
        {
            d.ballX = WIDTH / 2;
            d.ballY = HEIGHT / 2;
            d.ballSpeed = BALL_START_SPEED;
            float angle = (float)(new Random().NextDouble() * 0.6 - 0.3);
            d.ballVX = (float)((towardRight ? 1 : -1) * d.ballSpeed * Math.Cos(angle));
            d.ballVY = (float)(d.ballSpeed * Math.Sin(angle));
            d.BallInPlay = false;
            d.Serving = true;
            d.ServeDir = towardRight ? 1 : -1;
        }

        private void CheckWinOrReset(Duel d, bool forceReset = false)
        {
            if (d.scoreLeft >= WIN_SCORE || d.scoreRight >= WIN_SCORE || forceReset)
            {
                if (!forceReset)
                {
                    string winner = d.scoreLeft >= WIN_SCORE ? "Left Player (←)" : "Right Player (→)";
                    _ = _hubContext.Clients.Group(d.LobbyId).SendAsync("VictoryReached", $"{winner} wins!");
                }
                else
                {
                    _ = _hubContext.Clients.Group(d.LobbyId).SendAsync("ForceReset");
                }

                // Reset scores and positions
                d.scoreLeft = 0;
                d.scoreRight = 0;
                d.leftY = (HEIGHT - PADDLE_HEIGHT) / 2;
                d.rightY = (HEIGHT - PADDLE_HEIGHT) / 2;
                d.ballSpeed = BALL_START_SPEED;
            }

            // Set win flag for next serving player
            if (d.scoreLeft >= WIN_SCORE)
            {
                SetServingSide(d, "Right");
            }
            else if (d.scoreRight >= WIN_SCORE)
            {
                SetServingSide(d, "Left");
            }

            // Normal score event — pause until next serve
            d.ballVX = 0;
            d.ballVY = 0;
            d.ballX = WIDTH / 2;
            d.ballY = HEIGHT / 2;
            d.BallInPlay = false;
            d.Serving = true;
        }

        private void SetServingSide(Duel d, string side)
        {
            _ = _hubContext.Clients.Group(d.LobbyId).SendAsync("SideServing", side);
        }
        #endregion
    }
}
