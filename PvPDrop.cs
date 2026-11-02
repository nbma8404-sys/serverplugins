using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;

namespace PvPDrop
{
    [ApiVersion(2, 1)]
    public class PvPDropPlugin : TerrariaPlugin
    {
        public override string Name => "PvPDrop";
        public override string Author => "You";
        public override string Description => "On PvP death, drops only the chosen inventory slots.";
        public override Version Version => new Version(1, 1, 0);

        private const string ImmunePermission = "pvpdrop.immune";
        private static readonly string ConfigPath = Path.Combine(TShock.SavePath, "pvpdrop.json");

        // Slot numbers (edit them in tshock/pvpdrop.json, no rebuild needed).
        // Numbering: 0-9 hotbar, 10-19 / 20-29 / 30-39 / 40-49 storage rows,
        // counted from the row right above the hotbar upward (mobile layout).
        // Default = column 6 of all 4 storage rows + the two slots beside it in the row above the hotbar.
        private class Config
        {
            public List<int> Slots = new List<int> { 14, 15, 16, 25, 35, 45 };
        }

        private Config _config = new Config();

        public PvPDropPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            LoadConfig();
            GetDataHandlers.KillMe += OnKillMe;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                GetDataHandlers.KillMe -= OnKillMe;
            base.Dispose(disposing);
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    _config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(ConfigPath)) ?? new Config();
                }
                else
                {
                    File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(_config, Formatting.Indented));
                }
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("PvPDrop config error: " + ex.Message);
            }
        }

        private void OnKillMe(object sender, GetDataHandlers.KillMeEventArgs e)
        {
            try
            {
                // Only when killed by another player
                if (!e.Pvp) return;

                var ply = e.Player;
                if (ply == null || !ply.Active) return;
                if (ply.HasPermission(ImmunePermission)) return;

                var tp = ply.TPlayer;
                IEntitySource source = null;
                var dropped = new List<int>();

                foreach (int i in _config.Slots)
                {
                    if (i < 0 || i >= 50) continue; // main inventory only

                    var item = tp.inventory[i];
                    if (item == null || item.type <= 0 || item.stack <= 0) continue;

                    int idx = Item.NewItem(source, (int)tp.position.X, (int)tp.position.Y,
                        tp.width, tp.height, item.type, item.stack, false, item.prefix);

                    if (idx >= 0 && idx < Main.maxItems)
                        NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, idx);

                    item.TurnToAir();
                    NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, ply.Index, i);
                    dropped.Add(i);
                }

                TShock.Log.ConsoleInfo("[PvPDrop] " + ply.Name + " died to a player. Dropped slots: " +
                    (dropped.Count == 0 ? "none (those slots were empty)" : string.Join(",", dropped)));

                if (Main.ServerSideCharacter)
                    ply.SaveServerCharacter();
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("[PvPDrop] error: " + ex);
            }
        }
    }
}
