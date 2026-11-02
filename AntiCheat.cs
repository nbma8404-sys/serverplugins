using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat
{
    [ApiVersion(2, 1)]
    public class AntiCheatPlugin : TerrariaPlugin
    {
        public override string Name => "AntiCheat";
        public override string Author => "You";
        public override string Description => "Warns, removes items and bans cheaters.";
        public override Version Version => new Version(1, 0, 0);

        // Give these to your admin group:
        //   /group addperm admin anticheat.immune   (admins are never checked)
        //   /group addperm admin anticheat.notify   (admins see alerts in chat)
        private const string ImmunePermission = "anticheat.immune";
        private const string NotifyPermission = "anticheat.notify";

        private static readonly string ConfigPath = Path.Combine(TShock.SavePath, "anticheat.json");

        // All numbers can be edited in tshock/anticheat.json (no rebuild needed)
        private class Config
        {
            // INSTANT violations (damage, max life, block spam): ban right away, no warning
            // HARD violations (illegal stack): warning + item removed on 1st time, ban from this strike on
            public int BanAtHardStrike = 2;
            // SOFT violations (can false-trigger): warning on 1st time, kick from this strike on (never ban)
            public int KickAtSoftStrike = 2;

            public bool CheckStacks = true;        // item stack bigger than the item's real maximum
            public bool CheckDamage = true;        // damage to NPCs above MaxNpcDamage
            public bool CheckMaxLife = true;       // max life above MaxLifeAllowed (crude god mode)
            public bool CheckTileSpam = true;      // too many block edits per second
            public bool CheckItemDropSpam = true;  // too many item drops in 3 seconds
            public bool CheckSpeed = true;         // moving far too fast (speed hack)

            public int MaxNpcDamage = 5000;
            public int MaxLifeAllowed = 500;
            public int MaxTileEditsPerSecond = 300;
            public int MaxItemDropsPer3Sec = 20;
            public int MaxMovePerHalfSecond = 1500; // in pixels (16 pixels = 1 block)
        }

        private class State
        {
            public DateTime TileStart = DateTime.UtcNow;
            public int TileCount;
            public bool TileFlagged;
            public DateTime DropStart = DateTime.UtcNow;
            public int DropCount;
            public bool DropFlagged;
            public bool HasLast;
            public float LastX;
            public float LastY;
            public int SpeedHits;
        }

        private Config _config = new Config();
        private readonly State[] _states = new State[256];
        private readonly Dictionary<string, int> _hard = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _soft = new Dictionary<string, int>();
        private readonly object _lock = new object();
        private int _tick;

        public AntiCheatPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            LoadConfig();
            ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
            ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
            GetDataHandlers.NPCStrike += OnNpcStrike;
            GetDataHandlers.TileEdit += OnTileEdit;
            GetDataHandlers.ItemDrop += OnItemDrop;
            GetDataHandlers.PlayerHP += OnPlayerHp;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
                GetDataHandlers.NPCStrike -= OnNpcStrike;
                GetDataHandlers.TileEdit -= OnTileEdit;
                GetDataHandlers.ItemDrop -= OnItemDrop;
                GetDataHandlers.PlayerHP -= OnPlayerHp;
            }
            base.Dispose(disposing);
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                    _config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(ConfigPath)) ?? new Config();
                else
                    File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(_config, Formatting.Indented));
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("AntiCheat config error: " + ex.Message);
            }
        }

        private State GetState(TSPlayer ply)
        {
            int i = ply.Index;
            if (_states[i] == null) _states[i] = new State();
            return _states[i];
        }

        private void OnLeave(LeaveEventArgs args)
        {
            if (args.Who >= 0 && args.Who < _states.Length)
                _states[args.Who] = null;
        }

        private bool IsChecked(TSPlayer ply)
        {
            return ply != null && ply.Active && !ply.HasPermission(ImmunePermission);
        }

        // ---------- Periodic scan (every 0.5 second): stacks + speed ----------
        private void OnUpdate(EventArgs args)
        {
            if (++_tick % 30 != 0) return;

            foreach (var ply in TShock.Players)
            {
                if (!IsChecked(ply) || ply.TPlayer == null) continue;
                var tp = ply.TPlayer;
                var st = GetState(ply);

                if (_config.CheckStacks)
                {
                    for (int i = 0; i < 59; i++)
                    {
                        var it = tp.inventory[i];
                        if (it == null || it.type <= 0 || it.stack <= 0) continue;
                        if (it.stack > it.maxStack)
                        {
                            string name = it.Name;
                            int stack = it.stack;
                            it.TurnToAir();
                            NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, ply.Index, i);
                            Violation(ply, "illegal stack: " + stack + " x " + name, Level.Hard);
                        }
                    }
                }

                if (_config.CheckSpeed)
                {
                    if (tp.dead)
                    {
                        st.HasLast = false;
                        st.SpeedHits = 0;
                        continue;
                    }

                    float x = tp.position.X;
                    float y = tp.position.Y;
                    if (st.HasLast)
                    {
                        float dx = x - st.LastX;
                        float dy = y - st.LastY;
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        if (dist > _config.MaxMovePerHalfSecond) st.SpeedHits++;
                        else st.SpeedHits = 0;

                        if (st.SpeedHits >= 3)
                        {
                            st.SpeedHits = 0;
                            Violation(ply, "speed hack", Level.Soft);
                        }
                    }
                    st.LastX = x;
                    st.LastY = y;
                    st.HasLast = true;
                }
            }
        }

        // ---------- Damage to NPCs ----------
        private void OnNpcStrike(object sender, GetDataHandlers.NPCStrikeEventArgs e)
        {
            if (!_config.CheckDamage || e.Handled) return;
            var ply = e.Player;
            if (!IsChecked(ply)) return;

            if (e.Damage > _config.MaxNpcDamage)
            {
                e.Handled = true;
                Violation(ply, "abnormal damage: " + e.Damage, Level.Instant);
            }
        }

        // ---------- Block spam ----------
        private void OnTileEdit(object sender, GetDataHandlers.TileEditEventArgs e)
        {
            if (!_config.CheckTileSpam || e.Handled) return;
            var ply = e.Player;
            if (!IsChecked(ply)) return;

            var st = GetState(ply);
            var now = DateTime.UtcNow;
            if ((now - st.TileStart).TotalSeconds >= 1.0)
            {
                st.TileStart = now;
                st.TileCount = 0;
                st.TileFlagged = false;
            }
            st.TileCount++;
            if (st.TileCount > _config.MaxTileEditsPerSecond)
            {
                e.Handled = true;
                if (!st.TileFlagged)
                {
                    st.TileFlagged = true;
                    Violation(ply, "block spam", Level.Instant);
                }
            }
        }

        // ---------- Item spawn / drop spam ----------
        private void OnItemDrop(object sender, GetDataHandlers.ItemDropEventArgs e)
        {
            if (!_config.CheckItemDropSpam || e.Handled) return;
            var ply = e.Player;
            if (!IsChecked(ply)) return;

            var st = GetState(ply);
            var now = DateTime.UtcNow;
            if ((now - st.DropStart).TotalSeconds >= 3.0)
            {
                st.DropStart = now;
                st.DropCount = 0;
                st.DropFlagged = false;
            }
            st.DropCount++;
            if (st.DropCount > _config.MaxItemDropsPer3Sec)
            {
                e.Handled = true;
                if (!st.DropFlagged)
                {
                    st.DropFlagged = true;
                    Violation(ply, "item spawn/drop spam", Level.Soft);
                }
            }
        }

        // ---------- Crude god mode (impossible max life) ----------
        private void OnPlayerHp(object sender, GetDataHandlers.PlayerHPEventArgs e)
        {
            if (!_config.CheckMaxLife || e.Handled) return;
            var ply = e.Player;
            if (!IsChecked(ply)) return;

            if (e.Max > _config.MaxLifeAllowed)
            {
                e.Handled = true;
                Violation(ply, "impossible max life: " + e.Max, Level.Instant);
            }
        }

        // ---------- Punishment ----------
        private enum Level { Soft, Hard, Instant }

        private void Ban(TSPlayer ply, string what)
        {
            Commands.HandleCommand(TSPlayer.Server, "/ban add \"" + ply.Name + "\" \"AntiCheat: " + what + "\"");
        }

        private void Violation(TSPlayer ply, string what, Level level)
        {
            if (level == Level.Instant)
            {
                Notify("[AntiCheat] " + ply.Name + " (" + ply.IP + "): " + what + "  -> BANNED");
                Ban(ply, what);
                return;
            }

            bool hard = level == Level.Hard;
            string key = ply.IP;
            int n;
            lock (_lock)
            {
                var dict = hard ? _hard : _soft;
                dict.TryGetValue(key, out n);
                n++;
                dict[key] = n;
            }

            Notify("[AntiCheat] " + ply.Name + " (" + ply.IP + "): " + what + "  strike #" + n + (hard ? " [HARD]" : " [SOFT]"));

            if (hard)
            {
                if (n >= _config.BanAtHardStrike)
                    Ban(ply, what);
                else
                    ply.SendErrorMessage("WARNING: cheating detected (" + what + "). The item was removed. Doing it again = permanent ban.");
            }
            else
            {
                if (n >= _config.KickAtSoftStrike)
                    ply.Kick("AntiCheat: " + what, true);
                else
                    ply.SendErrorMessage("WARNING: suspicious activity (" + what + "). Doing it again = kick.");
            }
        }

        private void Notify(string msg)
        {
            TShock.Log.ConsoleInfo(msg);
            foreach (var p in TShock.Players)
            {
                if (p != null && p.Active && p.HasPermission(NotifyPermission))
                    p.SendWarningMessage(msg);
            }
        }
    }
}
