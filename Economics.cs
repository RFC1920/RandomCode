using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Economics", "Wulf/RFC1920", "3.9.2")]
    [Description("Basic economics system and economy API")]
    public class Economics : CovalencePlugin
    {
        #region Configuration

        private Configuration config;

        private class Configuration
        {
            [JsonProperty("Allow negative balance for accounts")]
            public bool AllowNegativeBalance;

            [JsonProperty("Balance limit for accounts (0 to disable)")]
            public int BalanceLimit;

            [JsonProperty("Maximum balance for accounts (0 to disable)")] // TODO: From version 3.8.6; remove eventually
            private int BalanceLimitOld { set { BalanceLimit = value; } }

            [JsonProperty("Negative balance limit for accounts (0 to disable)")]
            public int NegativeBalanceLimit;

            [JsonProperty("Remove unused accounts")]
            public bool RemoveUnused = true;

            [JsonProperty("Log transactions to file")]
            public bool LogTransactions;

            [JsonProperty("Starting account balance (0 or higher)")]
            public int StartingBalance = 1000;

            [JsonProperty("Starting money amount (0 or higher)")] // TODO: From version 3.8.6; remove eventually
            private int StartingBalanceOld { set { StartingBalance = value; } }

            [JsonProperty("Wipe balances on new save file")]
            public bool WipeOnNewSave;

            [JsonProperty("Store data in sqlite instead of data files")]
            public bool useSQLite;

            [JsonProperty("Store data in MySQL instead of data files")]
            public bool useMySQL;

            [JsonProperty("MySQL configuration, if using MySQL")]
            public MySQLConfig mysql = new MySQLConfig();

            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonConvert.DeserializeObject<Dictionary<string, object>>(ToJson());

            public VersionNumber Version;
        }

        private class MySQLConfig
        {
            public string server;
            public string user;
            public string pass;
            public string database;
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null)
                {
                    throw new JsonException();
                }

                if (!config.ToDictionary().Keys.SequenceEqual(Config.ToDictionary(x => x.Key, x => x.Value).Keys))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch
            {
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
            config.Version = Version;
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            LogWarning($"Configuration changes saved to {Name}.json");
            Config.WriteObject(config, true);
        }

        #endregion Configuration

        #region Stored Data

        private DynamicConfigFile data;
        private StoredData storedData;
        private bool changed;

        private SQLiteConnection sqlConnection;
        private MySqlConnection mysqlConnection;
        private string connStr;

        private class StoredData
        {
            public readonly Dictionary<string, double> Balances = new Dictionary<string, double>();
        }

        private void SaveData()
        {
            if (changed)
            {
                Puts("Saving balances for players...");
                Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);
            }
        }

        private void OnServerSave() => SaveData();

        private void Unload() => SaveData();

        #endregion Stored Data

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["CommandBalance"] = "balance",
                ["CommandDeposit"] = "deposit",
                ["CommandSetBalance"] = "SetBalance",
                ["CommandTransfer"] = "transfer",
                ["CommandWithdraw"] = "withdraw",
                ["CommandWipe"] = "ecowipe",
                ["DataSaved"] = "Economics data saved!",
                ["DataWiped"] = "Economics data wiped!",
                ["DepositedToAll"] = "Deposited {0:C} total ({1:C} each) to {2} player(s)",
                ["LogDeposit"] = "{0:C} deposited to {1}",
                ["LogSetBalance"] = "{0:C} set as balance for {1}",
                ["LogTransfer"] = "{0:C} transferred to {1} from {2}",
                ["LogWithdrawl"] = "{0:C} withdrawn from {1}",
                ["NegativeBalance"] = "Balance can not be negative!",
                ["NotAllowed"] = "You are not allowed to use the '{0}' command",
                ["NoPlayersFound"] = "No players found with name or ID '{0}'",
                ["PlayerBalance"] = "Balance for {0}: {1:C}",
                ["PlayerLacksMoney"] = "'{0}' does not have enough money!",
                ["PlayersFound"] = "Multiple players were found, please specify: {0}",
                ["ReceivedFrom"] = "You have received {0} from {1}",
                ["SetBalanceForAll"] = "Balance set to {0:C} for {1} player(s)",
                ["TransactionFailed"] = "Transaction failed! Make sure amount is above 0",
                ["TransferredTo"] = "{0} transferred to {1}",
                ["TransferredToAll"] = "Transferred {0:C} total ({1:C} each) to {2} player(s)",
                ["TransferToSelf"] = "You can not transfer money yourself!",
                ["UsageBalance"] = "{0} - check your balance",
                ["UsageBalanceOthers"] = "{0} <player name or id> - check balance of a player",
                ["UsageDeposit"] = "{0} <player name or id> <amount> - deposit amount to player",
                ["UsageSetBalance"] = "Usage: {0} <player name or id> <amount> - set balance for player",
                ["UsageTransfer"] = "Usage: {0} <player name or id> <amount> - transfer money to player",
                ["UsageWithdraw"] = "Usage: {0} <player name or id> <amount> - withdraw money from player",
                ["UsageWipe"] = "Usage: {0} - wipe all economics data",
                ["YouLackMoney"] = "You do not have enough money!",
                ["YouLostMoney"] = "You lost: {0:C}",
                ["YouReceivedMoney"] = "You received: {0:C}",
                ["YourBalance"] = "Your balance is: {0:C}",
                ["WithdrawnForAll"] = "Withdrew {0:C} total ({1:C} each) from {2} player(s)",
                ["ZeroAmount"] = "Amount cannot be zero"
            }, this);
        }

        #endregion Localization

        #region Initialization

        private const string permissionBalance = "economics.balance";
        private const string permissionDeposit = "economics.deposit";
        private const string permissionDepositAll = "economics.depositall";
        private const string permissionSetBalance = "economics.setbalance";
        private const string permissionSetBalanceAll = "economics.setbalanceall";
        private const string permissionTransfer = "economics.transfer";
        private const string permissionTransferAll = "economics.transferall";
        private const string permissionWithdraw = "economics.withdraw";
        private const string permissionWithdrawAll = "economics.withdrawall";
        private const string permissionWipe = "economics.wipe";

        private void Init()
        {
            // Register universal chat/console commands
            AddLocalizedCommand(nameof(CommandBalance));
            AddLocalizedCommand(nameof(CommandDeposit));
            AddLocalizedCommand(nameof(CommandSetBalance));
            AddLocalizedCommand(nameof(CommandTransfer));
            AddLocalizedCommand(nameof(CommandWithdraw));
            AddLocalizedCommand(nameof(CommandWipe));

            // Register permissions for commands
            permission.RegisterPermission(permissionBalance, this);
            permission.RegisterPermission(permissionDeposit, this);
            permission.RegisterPermission(permissionDepositAll, this);
            permission.RegisterPermission(permissionSetBalance, this);
            permission.RegisterPermission(permissionSetBalanceAll, this);
            permission.RegisterPermission(permissionTransfer, this);
            permission.RegisterPermission(permissionTransferAll, this);
            permission.RegisterPermission(permissionWithdraw, this);
            permission.RegisterPermission(permissionWithdrawAll, this);
            permission.RegisterPermission(permissionWipe, this);

            bool emptydb = true;
            if (config.useSQLite)
            {
                DynamicConfigFile dataFile = Interface.Oxide.DataFileSystem.GetDatafile(Name + "/economics");
                dataFile.Save();
                connStr = $"Data Source={Interface.Oxide.DataDirectory}{Path.DirectorySeparatorChar}{Name}{Path.DirectorySeparatorChar}Economics.db;";
                sqlConnection = new SQLiteConnection(connStr);
                sqlConnection.Open();
                emptydb = CheckDB();
            }
            else if (config.useMySQL)
            {
                DynamicConfigFile dataFile = Interface.Oxide.DataFileSystem.GetDatafile(Name + "/economics");
                dataFile.Save();
                connStr = $"server={config.mysql.server};uid={config.mysql.user};pwd={config.mysql.pass};database={config.mysql.database};";
                mysqlConnection = new MySqlConnection(connStr);
                mysqlConnection.Open();
                emptydb = CheckDB();
            }

            if ((!config.useMySQL && !config.useSQLite) || emptydb)
            {
                // Load existing data and migrate old data format
                data = Interface.Oxide.DataFileSystem.GetFile(Name);
                try
                {
                    Dictionary<ulong, double> temp = data.ReadObject<Dictionary<ulong, double>>();
                    try
                    {
                        storedData = new StoredData();
                        foreach (KeyValuePair<ulong, double> old in temp)
                        {
                            if (!storedData.Balances.ContainsKey(old.Key.ToString()))
                            {
                                storedData.Balances.Add(old.Key.ToString(), old.Value);
                            }
                        }
                        changed = true;
                    }
                    catch
                    {
                        // Ignored
                    }
                }
                catch
                {
                    storedData = data.ReadObject<StoredData>();
                    changed = true;
                }

                List<string> playerData = new List<string>(storedData.Balances.Keys);

                // Check for and set any balances over maximum allowed
                if (config.BalanceLimit > 0)
                {
                    foreach (string p in playerData)
                    {
                        if (storedData.Balances[p] > config.BalanceLimit)
                        {
                            storedData.Balances[p] = config.BalanceLimit;
                            changed = true;
                        }
                    }
                }

                // Check for and remove any inactive player balance data
                if (config.RemoveUnused)
                {
                    foreach (string p in playerData)
                    {
                        if (storedData.Balances[p].Equals(config.StartingBalance))
                        {
                            storedData.Balances.Remove(p);
                            changed = true;
                        }
                    }
                }
            }

            if (config.useSQLite)
            {
                if (emptydb)
                {
                    // Migrate/import the datafile, if present
                    foreach (KeyValuePair<string, double> s in storedData.Balances)
                    {
                        using (SQLiteConnection c = new SQLiteConnection(connStr))
                        {
                            c.Open();
                            string query = $"INSERT INTO balances VALUES ('{s.Key}', {s.Value})";
                            using (SQLiteCommand us = new SQLiteCommand(query, c))
                            {
                                us.ExecuteNonQuery();
                            }
                        }
                    }
                }
                else if (config.RemoveUnused)
                {
                    Puts("Reading data from sqlite");
                    // Read the storedData from SQLite
                    storedData = new StoredData();
                    using (SQLiteConnection c = new SQLiteConnection(connStr))
                    {
                        c.Open();
                        using (SQLiteCommand rm = new SQLiteCommand("SELECT DISTINCT playerid, value FROM balances", c))
                        using (SQLiteDataReader rd = rm.ExecuteReader())
                        {
                            while (rd.Read())
                            {
                                string pid = rd.GetString(0);
                                double val = rd.GetDouble(1);
                                storedData.Balances.Add(pid, val);
                            }
                        }
                    }

                    // Check for and remove any inactive player balance data
                    foreach (string p in new List<string>(storedData.Balances.Keys))
                    {
                        if (storedData.Balances[p].Equals(config.StartingBalance))
                        {
                            using (SQLiteConnection c = new SQLiteConnection(connStr))
                            {
                                c.Open();
                                using (SQLiteCommand rm = new SQLiteCommand($"DELETE FROM balances WHERE playerid='{p}'", c))
                                {
                                    rm.ExecuteNonQuery();
                                }
                            }
                        }
                    }
                    storedData = new StoredData();
                }
            }
            else if (config.useMySQL)
            {
                if (emptydb)
                {
                    // Migrate/import the datafile, if present
                    foreach (KeyValuePair<string, double> s in storedData.Balances)
                    {
                        using (MySqlConnection c = new MySqlConnection(connStr))
                        {
                            c.Open();
                            string query = $"INSERT INTO balances VALUES ('{s.Key}', {s.Value})";
                            using (MySqlCommand us = new MySqlCommand(query, c))
                            {
                                us.ExecuteNonQuery();
                            }
                        }
                    }
                }
                else if (config.RemoveUnused)
                {
                    Puts("Reading data from MySQL");
                    storedData = new StoredData();
                    using (MySqlConnection c = new MySqlConnection(connStr))
                    {
                        c.Open();
                        using (MySqlCommand rm = new MySqlCommand("SELECT DISTINCT playerid, value FROM balances", c))
                        using (MySqlDataReader rd = rm.ExecuteReader())
                        {
                            while (rd.Read())
                            {
                                string pid = rd.GetString(0);
                                double val = rd.GetDouble(1);
                                storedData.Balances.Add(pid, val);
                            }
                        }
                    }

                    // Check for and remove any inactive player balance data
                    foreach (string p in new List<string>(storedData.Balances.Keys))
                    {
                        if (storedData.Balances[p].Equals(config.StartingBalance))
                        {
                            using (MySqlConnection c = new MySqlConnection(connStr))
                            {
                                c.Open();
                                using (MySqlCommand rm = new MySqlCommand($"DELETE FROM balances WHERE playerid='{p}'", c))
                                {
                                    rm.ExecuteNonQuery();
                                }
                            }
                        }
                    }
                    storedData = new StoredData();
                }
            }
        }

        private void OnNewSave()
        {
            if (config.WipeOnNewSave)
            {
                storedData.Balances.Clear();
                changed = true;
                Interface.Call("OnEconomicsDataWiped");
            }
        }

        private bool CheckDB()
        {
            // Return true if db was empty or non-existent
            bool found = false;
            if (config.useSQLite)
            {
                using (SQLiteConnection c = new SQLiteConnection(connStr))
                {
                    c.Open();
                    using (SQLiteCommand r = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name='balances'", c))
                    using (SQLiteDataReader rentry = r.ExecuteReader())
                    {
                        while (rentry.Read()) { found = true; }
                    }
                }
                if (!found)
                {
                    SQLiteCommand ct = new SQLiteCommand("DROP TABLE IF EXISTS balances", sqlConnection);
                    ct.ExecuteNonQuery();
                    ct = new SQLiteCommand("CREATE TABLE balances (playerid varchar(32), value DOUBLE DEFAULT 0)", sqlConnection);
                    ct.ExecuteNonQuery();
                    return true;
                }
            }
            else if (config.useMySQL)
            {
                using (MySqlConnection c = new MySqlConnection(connStr))
                {
                    c.Open();
                    using (MySqlCommand r = new MySqlCommand($"SELECT * FROM information_schema.tables WHERE table_schema='{config.mysql.database}' AND table_name='balances' LIMIT 1;", c))
                    using (MySqlDataReader rentry = r.ExecuteReader())
                    {
                        while (rentry.Read()) { found = true; }
                    }
                }
                if (!found)
                {
                    MySqlCommand ct = new MySqlCommand("DROP TABLE IF EXISTS balances", mysqlConnection);
                    ct.ExecuteNonQuery();
                    ct = new MySqlCommand("CREATE TABLE balances (playerid varchar(32), value DOUBLE DEFAULT 0)", mysqlConnection);
                    ct.ExecuteNonQuery();
                    return true;
                }
            }

            return !found;
        }

        #endregion Initialization

        #region API Methods

        private double Balance(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                LogWarning("Balance method called without a valid player ID");
                return 0.0;
            }

            double playerData;
            if (config.useSQLite)
            {
                using (SQLiteConnection c = new SQLiteConnection(connStr))
                {
                    c.Open();
                    using (SQLiteCommand rm = new SQLiteCommand($"SELECT value FROM balances WHERE playerid='{playerId}'", c))
                    using (SQLiteDataReader rd = rm.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            double val = rd.GetDouble(0);
                            return val > config.StartingBalance ? val : config.StartingBalance;
                        }
                    }
                }
            }
            else if (config.useMySQL)
            {
                using (MySqlConnection c = new MySqlConnection(connStr))
                {
                    c.Open();
                    using (MySqlCommand rm = new MySqlCommand($"SELECT value FROM balances WHERE playerid='{playerId}'", c))
                    {
                        using (MySqlDataReader rd = rm.ExecuteReader())
                        {
                            while (rd.Read())
                            {
                                double val = rd.GetDouble(0);
                                return val > config.StartingBalance ? val : config.StartingBalance;
                            }
                        }
                    }
                }
            }
            return storedData.Balances.TryGetValue(playerId, out playerData) ? playerData : config.StartingBalance;
        }

        private double Balance(ulong playerId) => Balance(playerId.ToString());

        private bool Deposit(string playerId, double amount)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                LogWarning("Deposit method called without a valid player ID");
                return false;
            }

            if (amount > 0 && SetBalance(playerId, amount + Balance(playerId)))
            {
                Interface.Call("OnEconomicsDeposit", playerId, amount);

                if (config.LogTransactions)
                {
                    LogToFile("transactions", $"[{DateTime.Now}] {GetLang("LogDeposit", null, amount, playerId)}", this);
                }

                return true;
            }

            return false;
        }

        private bool Deposit(ulong playerId, double amount) => Deposit(playerId.ToString(), amount);

        private bool SetBalance(string playerId, double amount)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                LogWarning("SetBalance method called without a valid player ID");
                return false;
            }

            if (amount >= 0 || config.AllowNegativeBalance)
            {
                amount = Math.Round(amount, 2);
                if (config.BalanceLimit > 0 && amount > config.BalanceLimit)
                {
                    amount = config.BalanceLimit;
                }
                else if (config.AllowNegativeBalance && config.NegativeBalanceLimit < 0 && amount < config.NegativeBalanceLimit)
                {
                    amount = config.NegativeBalanceLimit;
                }

                if (config.useSQLite)
                {
                    using (SQLiteConnection c = new SQLiteConnection(connStr))
                    {
                        c.Open();
                        string query = $"UPDATE balances SET value = {amount} WHERE playerid='{playerId}'";
                        using (SQLiteCommand us = new SQLiteCommand(query, c))
                        {
                            us.ExecuteNonQuery();
                        }
                    }
                }
                else if (config.useMySQL)
                {
                    using (MySqlConnection c = new MySqlConnection(connStr))
                    {
                        c.Open();
                        string query = $"UPDATE balances SET value = {amount} WHERE playerid='{playerId}'";
                        using (MySqlCommand us = new MySqlCommand(query, c))
                        {
                            us.ExecuteNonQuery();
                        }
                    }
                }
                else
                {
                    storedData.Balances[playerId] = amount;
                    changed = true;
                }

                Interface.Call("OnEconomicsBalanceUpdated", playerId, amount);
                Interface.CallDeprecatedHook("OnBalanceChanged", "OnEconomicsBalanceUpdated", new System.DateTime(2022, 7, 1), playerId, amount);

                if (config.LogTransactions)
                {
                    LogToFile("transactions", $"[{DateTime.Now}] {GetLang("LogSetBalance", null, amount, playerId)}", this);
                }

                return true;
            }

            return false;
        }

        private bool SetBalance(ulong playerId, double amount) => SetBalance(playerId.ToString(), amount);

        private bool Transfer(string playerId, string targetId, double amount)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                LogWarning("Transfer method called without a valid player ID");
                return false;
            }

            if (Withdraw(playerId, amount) && Deposit(targetId, amount))
            {
                Interface.Call("OnEconomicsTransfer", playerId, targetId, amount);

                if (config.LogTransactions)
                {
                    LogToFile("transactions", $"[{DateTime.Now}] {GetLang("LogTransfer", null, amount, targetId, playerId)}", this);
                }

                return true;
            }

            return false;
        }

        private bool Transfer(ulong playerId, ulong targetId, double amount)
        {
            return Transfer(playerId.ToString(), targetId.ToString(), amount);
        }

        private bool Withdraw(string playerId, double amount)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                LogWarning("Withdraw method called without a valid player ID");
                return false;
            }

            if (amount >= 0 || config.AllowNegativeBalance)
            {
                double balance = Balance(playerId);
                if ((balance >= amount || (config.AllowNegativeBalance && balance + amount > config.NegativeBalanceLimit)) && SetBalance(playerId, balance - amount))
                {
                    Interface.Call("OnEconomicsWithdrawl", playerId, amount);

                    if (config.LogTransactions)
                    {
                        LogToFile("transactions", $"[{DateTime.Now}] {GetLang("LogWithdrawl", null, amount, playerId)}", this);
                    }

                    return true;
                }
            }

            return false;
        }

        private bool Withdraw(ulong playerId, double amount) => Withdraw(playerId.ToString(), amount);

        #endregion API Methods

        #region Commands

        #region Balance Command

        private void CommandBalance(IPlayer player, string command, string[] args)
        {
            if (args?.Length > 0)
            {
                if (!player.HasPermission(permissionBalance))
                {
                    Message(player, "NotAllowed", command);
                    return;
                }

                IPlayer target = FindPlayer(args[0], player);
                if (target == null)
                {
                    Message(player, "UsageBalance", command);
                    return;
                }

                Message(player, "PlayerBalance", target.Name, Balance(target.Id));
                return;
            }

            if (player.IsServer)
            {
                Message(player, "UsageBalanceOthers", command);
            }
            else
            {
                Message(player, "YourBalance", Balance(player.Id));
            }
        }

        #endregion Balance Command

        #region Deposit Command

        private void CommandDeposit(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission(permissionDeposit))
            {
                Message(player, "NotAllowed", command);
                return;
            }

            if (args == null || args.Length <= 1)
            {
                Message(player, "UsageDeposit", command);
                return;
            }

            double amount;
            double.TryParse(args[1], out amount);
            if (amount <= 0)
            {
                Message(player, "ZeroAmount");
                return;
            }

            if (args[0] == "*")
            {
                if (!player.HasPermission(permissionDepositAll))
                {
                    Message(player, "NotAllowed", command);
                    return;
                }

                int receivers = 0;
                if (config.useSQLite)
                {
                    using (SQLiteConnection c = new SQLiteConnection(connStr))
                    {
                        c.Open();
                        string query = $"UPDATE balances SET value=value + {amount}";
                        using (SQLiteCommand us = new SQLiteCommand(query, c))
                        {
                            us.ExecuteNonQuery();
                        }

                        using (SQLiteCommand rm = new SQLiteCommand("SELECT COUNT(*) FROM balances", c))
                        using (SQLiteDataReader rd = rm.ExecuteReader())
                        {
                            while (rd.Read())
                            {
                                receivers = rd.GetInt32(0);
                            }
                        }
                    }
                    Message(player, "DepositedToAll", amount * receivers, amount, receivers);
                    return;
                }
                else if (config.useMySQL)
                {
                    using (MySqlConnection c = new MySqlConnection(connStr))
                    {
                        c.Open();
                        string query = $"UPDATE balances SET value=value + {amount}";
                        using (MySqlCommand us = new MySqlCommand(query, c))
                        {
                            us.ExecuteNonQuery();
                        }

                        using (MySqlCommand rm = new MySqlCommand("SELECT COUNT(*) FROM balances", c))
                        using (MySqlDataReader rd = rm.ExecuteReader())
                        {
                            while (rd.Read())
                            {
                                receivers = rd.GetInt32(0);
                            }
                        }
                    }
                    Message(player, "DepositedToAll", amount * receivers, amount, receivers);
                    return;
                }
                foreach (string targetId in storedData.Balances.Keys.ToList())
                {
                    if (Deposit(targetId, amount))
                    {
                        receivers++;
                    }
                }
                Message(player, "DepositedToAll", amount * receivers, amount, receivers);
            }
            else
            {
                IPlayer target = FindPlayer(args[0], player);
                if (target == null)
                {
                    return;
                }

                if (Deposit(target.Id, amount))
                {
                    Message(player, "PlayerBalance", target.Name, Balance(target.Id));
                }
                else
                {
                    Message(player, "TransactionFailed", target.Name);
                }
            }
        }

        #endregion Deposit Command

        #region Set Balance Command

        private void CommandSetBalance(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission(permissionSetBalance))
            {
                Message(player, "NotAllowed", command);
                return;
            }

            if (args == null || args.Length <= 1)
            {
                Message(player, "UsageSetBalance", command);
                return;
            }

            double amount;
            double.TryParse(args[1], out amount);

            if (amount < 0)
            {
                Message(player, "NegativeBalance");
                return;
            }

            if (args[0] == "*")
            {
                if (!player.HasPermission(permissionSetBalanceAll))
                {
                    Message(player, "NotAllowed", command);
                    return;
                }

                int receivers = 0;
                if (config.useSQLite)
                {
                    using (SQLiteConnection c = new SQLiteConnection(connStr))
                    {
                        c.Open();
                        string query = $"UPDATE balances SET value={amount}";
                        using (SQLiteCommand us = new SQLiteCommand(query, c))
                        {
                            us.ExecuteNonQuery();
                        }

                        using (SQLiteCommand rm = new SQLiteCommand("SELECT COUNT(*) FROM balances", c))
                        using (SQLiteDataReader rd = rm.ExecuteReader())
                        {
                            while (rd.Read())
                            {
                                receivers = rd.GetInt32(0);
                            }
                        }
                    }
                    Message(player, "SetBalanceForAll", amount, receivers);
                    return;
                }
                else if (config.useMySQL)
                {
                    using (MySqlConnection c = new MySqlConnection(connStr))
                    {
                        c.Open();
                        string query = $"UPDATE balances SET value={amount}";
                        using (MySqlCommand us = new MySqlCommand(query, c))
                        {
                            us.ExecuteNonQuery();
                        }

                        using (MySqlCommand rm = new MySqlCommand("SELECT COUNT(*) FROM balances", c))
                        using (MySqlDataReader rd = rm.ExecuteReader())
                        {
                            while (rd.Read())
                            {
                                receivers = rd.GetInt32(0);
                            }
                        }
                    }
                    Message(player, "SetBalanceForAll", amount, receivers);
                    return;
                }
                foreach (string targetId in storedData.Balances.Keys.ToList())
                {
                    if (SetBalance(targetId, amount))
                    {
                        receivers++;
                    }
                }
                Message(player, "SetBalanceForAll", amount, receivers);
            }
            else
            {
                IPlayer target = FindPlayer(args[0], player);
                if (target == null)
                {
                    return;
                }

                if (SetBalance(target.Id, amount))
                {
                    Message(player, "PlayerBalance", target.Name, Balance(target.Id));
                }
                else
                {
                    Message(player, "TransactionFailed", target.Name);
                }
            }
        }

        #endregion Set Balance Command

        #region Transfer Command

        private void CommandTransfer(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission(permissionTransfer))
            {
                Message(player, "NotAllowed", command);
                return;
            }

            if (args == null || args.Length <= 1)
            {
                Message(player, "UsageTransfer", command);
                return;
            }

            double amount;
            double.TryParse(args[1], out amount);

            if (amount <= 0)
            {
                Message(player, "ZeroAmount");
                return;
            }

            if (args[0] == "*")
            {
                if (!player.HasPermission(permissionTransferAll))
                {
                    Message(player, "NotAllowed", command);
                    return;
                }

                if (!Withdraw(player.Id, amount))
                {
                    Message(player, "YouLackMoney");
                    return;
                }

                int receivers = players.Connected.Count();
                double splitAmount = amount /= receivers;

                foreach (IPlayer target in players.Connected)
                {
                    if (Deposit(target.Id, splitAmount))
                    {
                        if (target.IsConnected)
                        {
                            Message(target, "ReceivedFrom", splitAmount, player.Name);
                        }
                    }
                }
                Message(player, "TransferedToAll", amount, splitAmount, receivers);
            }
            else
            {
                IPlayer target = FindPlayer(args[0], player);
                if (target == null)
                {
                    return;
                }

                if (target.Equals(player))
                {
                    Message(player, "TransferToSelf");
                    return;
                }

                if (!Withdraw(player.Id, amount))
                {
                    Message(player, "YouLackMoney");
                    return;
                }

                if (Deposit(target.Id, amount))
                {
                    Message(player, "TransferredTo", amount, target.Name);
                    Message(target, "ReceivedFrom", amount, player.Name);
                }
                else
                {
                    Message(player, "TransactionFailed", target.Name);
                }
            }
        }

        #endregion Transfer Command

        #region Withdraw Command

        private void CommandWithdraw(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission(permissionWithdraw))
            {
                Message(player, "NotAllowed", command);
                return;
            }

            if (args == null || args.Length <= 1)
            {
                Message(player, "UsageWithdraw", command);
                return;
            }

            double amount;
            double.TryParse(args[1], out amount);

            if (amount <= 0)
            {
                Message(player, "ZeroAmount");
                return;
            }

            if (args[0] == "*")
            {
                if (!player.HasPermission(permissionWithdrawAll))
                {
                    Message(player, "NotAllowed", command);
                    return;
                }

                int receivers = 0;
                if (config.useSQLite)
                {
                    using (SQLiteConnection c = new SQLiteConnection(connStr))
                    {
                        c.Open();
                        string query = $"UPDATE balances SET value=value - {amount}";
                        using (SQLiteCommand us = new SQLiteCommand(query, c))
                        {
                            us.ExecuteNonQuery();
                        }

                        using (SQLiteCommand rm = new SQLiteCommand("SELECT COUNT(*) FROM balances", c))
                        using (SQLiteDataReader rd = rm.ExecuteReader())
                        {
                            while (rd.Read())
                            {
                                receivers = rd.GetInt32(0);
                            }
                        }
                    }
                    Message(player, "WithdrawnForAll", amount * receivers, amount, receivers);
                    return;
                }
                else if (config.useMySQL)
                {
                    using (MySqlConnection c = new MySqlConnection(connStr))
                    {
                        c.Open();
                        string query = $"UPDATE balances SET value=value - {amount}";
                        using (MySqlCommand us = new MySqlCommand(query, c))
                        {
                            us.ExecuteNonQuery();
                        }

                        using (MySqlCommand rm = new MySqlCommand("SELECT COUNT(*) FROM balances", c))
                        using (MySqlDataReader rd = rm.ExecuteReader())
                        {
                            while (rd.Read())
                            {
                                receivers = rd.GetInt32(0);
                            }
                        }
                    }
                    Message(player, "WithdrawnForAll", amount * receivers, amount, receivers);
                    return;
                }

                foreach (string targetId in storedData.Balances.Keys.ToList())
                {
                    if (Withdraw(targetId, amount))
                    {
                        receivers++;
                    }
                }
                Message(player, "WithdrawnForAll", amount * receivers, amount, receivers);
            }
            else
            {
                IPlayer target = FindPlayer(args[0], player);
                if (target == null)
                {
                    return;
                }

                if (Withdraw(target.Id, amount))
                {
                    Message(player, "PlayerBalance", target.Name, Balance(target.Id));
                }
                else
                {
                    Message(player, "YouLackMoney", target.Name);
                }
            }
        }

        #endregion Withdraw Command

        #region Wipe Command

        private void CommandWipe(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission(permissionWipe))
            {
                Message(player, "NotAllowed", command);
                return;
            }

            if (config.useSQLite)
            {
                using (SQLiteConnection c = new SQLiteConnection(connStr))
                {
                    c.Open();
                    using (SQLiteCommand us = new SQLiteCommand("DELETE FROM balances", c))
                    {
                        us.ExecuteNonQuery();
                    }
                }
                Message(player, "DataWiped");
                Interface.Call("OnEconomicsDataWiped", player);
                return;
            }
            else if (config.useMySQL)
            {
                using (MySqlConnection c = new MySqlConnection(connStr))
                {
                    c.Open();
                    using (MySqlCommand us = new MySqlCommand("DELETE FROM balances", c))
                    {
                        us.ExecuteNonQuery();
                    }
                }
                Message(player, "DataWiped");
                Interface.Call("OnEconomicsDataWiped", player);
                return;
            }

            storedData = new StoredData();
            changed = true;
            SaveData();

            Message(player, "DataWiped");
            Interface.Call("OnEconomicsDataWiped", player);
        }

        #endregion Wipe Command

        #endregion Commands

        #region Helpers

        private IPlayer FindPlayer(string playerNameOrId, IPlayer player)
        {
            IPlayer[] foundPlayers = players.FindPlayers(playerNameOrId).ToArray();
            if (foundPlayers.Length > 1)
            {
                Message(player, "PlayersFound", string.Join(", ", foundPlayers.Select(p => p.Name).Take(10).ToArray()).Truncate(60));
                return null;
            }

            IPlayer target = foundPlayers.Length == 1 ? foundPlayers[0] : null;
            if (target == null)
            {
                Message(player, "NoPlayersFound", playerNameOrId);
                return null;
            }

            return target;
        }

        private void AddLocalizedCommand(string command)
        {
            foreach (string language in lang.GetLanguages(this))
            {
                foreach (KeyValuePair<string, string> message in lang.GetMessages(language, this))
                {
                    if (message.Key.Equals(command) && !string.IsNullOrEmpty(message.Value))
                    {
                        AddCovalenceCommand(message.Value, command);
                    }
                }
            }
        }

        private string GetLang(string langKey, string playerId = null, params object[] args)
        {
            return string.Format(lang.GetMessage(langKey, this, playerId), args);
        }

        private void Message(IPlayer player, string textOrLang, params object[] args)
        {
            if (player.IsConnected)
            {
                string message = GetLang(textOrLang, player.Id, args);
                player.Reply(message != textOrLang ? message : textOrLang);
            }
        }

        #endregion Helpers
    }
}

#region Extension Methods

namespace Oxide.Plugins.EconomicsExtensionMethods
{
    public static class ExtensionMethods
    {
        public static T Clamp<T>(this T val, T min, T max) where T : IComparable<T>
        {
            if (val.CompareTo(min) < 0)
            {
                return min;
            }
            else if (val.CompareTo(max) > 0)
            {
                return max;
            }
            else
            {
                return val;
            }
        }
    }
}

#endregion Extension Methods
