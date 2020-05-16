﻿using System.Collections.Generic;
using System.Linq;
using I18N.West;
using MySql.Data.MySqlClient;
using PlayerInfoLibrary.Configuration;
using Pustalorc.Libraries.MySqlConnectorWrapper;
using Pustalorc.Libraries.MySqlConnectorWrapper.Queries;
using Pustalorc.Libraries.MySqlConnectorWrapper.TableStructure;
using SDG.Unturned;
using Steamworks;
using Logger = Rocket.Core.Logging.Logger;

namespace PlayerInfoLibrary.Database
{
    public sealed class DatabaseManager : ConnectorWrapper<PlayerInfoLibConfig>
    {
        private Dictionary<Query, Query> _createTableQueries;
        private Query _getPlayerDataQuery;

        private Dictionary<Query, Query> CreateTableQueries => _createTableQueries ??= new Dictionary<Query, Query>
        {
            {
                new Query($"SHOW TABLES LIKE '{Configuration.TableNamePlayers}';", EQueryType.Scalar),
                new Query(
                    $"CREATE TABLE `{Configuration.TableNamePlayers}` (`SteamID` BIGINT UNSIGNED NOT NULL, `SteamName` VARCHAR(255) COLLATE utf8_unicode_ci NOT NULL, `CharName` VARCHAR(255) COLLATE utf8_unicode_ci NOT NULL, `LastQuestGroupId` BIGINT UNSIGNED NOT NULL DEFAULT 0, `LastQuestGroupName` VARCHAR(255) COLLATE utf8_unicode_ci NOT NULL DEFAULT 'N/A', `HWID` varchar(255) COLLATE utf8_unicode_ci NOT NULL, `IP` INT UNSIGNED NOT NULL DEFAULT 4294967295, `LastLoginGlobal` BIGINT(32) NOT NULL, `TotalPlaytime` BIGINT UNSIGNED NOT NULL, `LastServerId` SMALLINT UNSIGNED NOT NULL, PRIMARY KEY (`SteamID`), KEY `LastServerId` (`LastServerId`), KEY `HWID` (`HWID`), KEY `IP` (`IP`));",
                    EQueryType.NonQuery)
            },
            {
                new Query($"SHOW TABLES LIKE '{Configuration.TableNameInstances}';", EQueryType.Scalar),
                new Query(
                    $"CREATE TABLE `{Configuration.TableNameInstances}` (`ServerID` smallint(5) unsigned NOT NULL AUTO_INCREMENT, `ServerInstance` varchar(128) COLLATE utf8_unicode_ci NOT NULL, `ServerName` varchar(60) COLLATE utf8_unicode_ci NOT NULL, PRIMARY KEY(`ServerID`), UNIQUE KEY `ServerInstance` (`ServerInstance`));",
                    EQueryType.NonQuery)
            }
        };

        public Query GetPlayerDataQuery => _getPlayerDataQuery ??= new Query(
            $"SELECT t1.SteamID, t1.SteamName, t1.CharName, t1.LastQuestGroupId, t1.LastQuestGroupName, t1.HWID, t1.IP, t1.LastLoginGlobal, t1.TotalPlaytime, t2.ServerID AS LastServerID, t2.ServerName AS LastServerName FROM `{Configuration.TableNamePlayers}` as t1 LEFT JOIN `{Configuration.TableNameInstances}` as t2 ON t1.LastServerId=t2.ServerID;",
            EQueryType.Reader, PlayerDataFetched, true);

        private List<PlayerData> _allPlayerData = new List<PlayerData>();
        private readonly object _memory = new object();

        public bool Initialized { get; private set; }
        public ushort InstanceId { get; private set; }

        internal DatabaseManager(PlayerInfoLibConfig config) : base(config)
        {
            new CP1250();

            var output = ExecuteTransaction(CreateTableQueries.Keys.ToArray()).ToList();
            var execute = (from queryResult in output
                where queryResult.Output == null
                select CreateTableQueries[queryResult.Query]).ToArray();

            if (execute.Length > 0)
                ExecuteTransaction(execute);

            for (var i = 0; i < 10; i++)
            {
                if (GetInstanceId())
                    break;

                if (i != 9) continue;

                Logger.LogError("Unable to get instance Id.");
                return;
            }

            ExecuteQuery(GetPlayerDataQuery);

            Initialized = true;
        }

        private void PlayerDataFetched(QueryOutput queryOutput)
        {
            if (queryOutput.Query.QueryType != EQueryType.Reader || !(queryOutput.Output is List<Row> rows)) return;

            lock (_memory)
            {
                _allPlayerData = (from row in rows select BuildPlayerData(row)).ToList();
            }
        }

        private bool GetInstanceId()
        {
            var output = ExecuteQuery(new Query(
                $"SELECT `ServerID`, `ServerName` FROM `{Configuration.TableNameInstances}` WHERE `ServerInstance` = @instance;",
                EQueryType.Reader, queryParameters: new MySqlParameter("@instance", Provider.serverID.ToLower())));

            var rows = (List<Row>) output.Output;
            if (rows.Count <= 0)
            {
                ExecuteQuery(new Query(
                    $"INSERT INTO `{Configuration.TableNameInstances}` (`ServerInstance`, `ServerName`) VALUES (@instance, @name);",
                    EQueryType.NonQuery, null, false, new MySqlParameter("@instance", Provider.serverID.ToLower()),
                    new MySqlParameter("name", Provider.serverName)));
                return false;
            }

            var row = rows[0];
            if (row["ServerID"] == null)
            {
                ExecuteQuery(new Query(
                    $"INSERT INTO `{Configuration.TableNameInstances}` (`ServerInstance`, `ServerName`) VALUES (@instance, @name);",
                    EQueryType.NonQuery, null, false, new MySqlParameter("@instance", Provider.serverID.ToLower()),
                    new MySqlParameter("name", Provider.serverName)));
                return false;
            }

            InstanceId = ushort.Parse(row["ServerID"].ToString());

            if (row["ServerName"].ToString() != Provider.serverName)
                ExecuteQuery(new Query(
                    $"UPDATE `{Configuration.TableNameInstances}` SET `ServerName` = @servername WHERE `ServerID` = {InstanceId};",
                    EQueryType.NonQuery, queryParameters: new MySqlParameter("name", Provider.serverName)));

            return true;
        }

        public void SetInstanceName(string newName, QueryCallback callback)
        {
            if (!Initialized) return;

            RequestQueryExecute(false,
                new Query(
                    $"UPDATE `{Configuration.TableNameInstances}` SET `ServerInstance` = @name WHERE `ServerID` = @instance;",
                    EQueryType.NonQuery, callback, false, new MySqlParameter("@name", newName),
                    new MySqlParameter("@instance", InstanceId)));
        }

        public PlayerData QueryById(CSteamID steamId)
        {
            if (Initialized) return _allPlayerData.FirstOrDefault(k => k.SteamId == steamId);

            Logger.LogError("Error: Cant load player info from DB, plugin hasn't initialized properly.");
            return null;
        }

        public List<PlayerData> QueryByName(string playerName, QueryType queryType)
        {
            if (!Initialized)
            {
                Logger.LogError("Error: Cant load player info from DB, plugin hasn't initialized properly.");
                return new List<PlayerData>();
            }

            if (!string.IsNullOrEmpty(playerName.Trim()))
                return _allPlayerData.Where(k =>
                {
                    switch (queryType)
                    {
                        case QueryType.Both:
                            return k.SteamName.ToLowerInvariant().Contains(playerName.ToLowerInvariant()) ||
                                   k.CharacterName.ToLowerInvariant().Contains(playerName.ToLowerInvariant());
                        case QueryType.CharName:
                            return k.CharacterName.ToLowerInvariant().Contains(playerName.ToLowerInvariant());
                        case QueryType.SteamName:
                            return k.SteamName.ToLowerInvariant().Contains(playerName.ToLowerInvariant());
                        case QueryType.Ip:
                            return k.Ip == Parser.getUInt32FromIP(playerName);
                        default:
                            return false;
                    }
                }).ToList();

            Logger.LogWarning("Warning: Need at least one character in the player name.");
            return new List<PlayerData>();
        }

        private PlayerData BuildPlayerData(Row row)
        {
            return new PlayerData(new CSteamID(ulong.Parse(row["SteamID"].ToString())),row["SteamName"].ToString(), row["CharName"].ToString(), ulong.Parse(row["LastQuestGroupId"].ToString()), row["LastQuestGroupName"].ToString(), uint.Parse(row["IP"].ToString()),row["HWID"].ToString(), row["LastServerName"].ToString(), InstanceId, ulong.Parse(row["TotalPlaytime"].ToString()), long.Parse(row["LastLoginGlobal"].ToString()).FromTimeStamp());
        }

        public void RemoveInstance(ushort instanceId, QueryCallback callback)
        {
            if (!Initialized) return;

            RequestQueryExecute(false, new Query($"DELETE FROM `{Configuration.TableNameInstances}` WHERE ServerID = {instanceId};",EQueryType.NonQuery, callback));
        }

        public void SaveToDb(PlayerData pdata)
        {
            if (!Initialized)
            {
                Logger.LogError("Error: Cant save player info, plugin hasn't initialized properly.");
                return;
            }

            if (!pdata.IsValid())
            {
                Logger.LogError("Error: Invalid player data information.");
                return;
            }
            
            RequestQueryExecute(false,new Query($"INSERT INTO `{Configuration.TableNamePlayers}` (`SteamID`, `SteamName`, `CharName`, `LastQuestGroupId`, `LastQuestGroupName`, `HWID`, `IP`, `LastLoginGlobal`, `TotalPlaytime`, `LastServerId`) " +$"VALUES (@steamid, @steamname, @charname, @groupid, @groupname, @hwid, @ip, @lastloginglobal, @totalplaytime, @lastinstanceid) ON DUPLICATE KEY UPDATE `SteamName` = VALUES(`SteamName`), `CharName` = VALUES(`CharName`), `LastQuestGroupId` = VALUES(`LastQuestGroupId`), `LastQuestGroupName` = VALUES(`LastQuestGroupName`), `HWID` = VALUES(`HWID`), `IP` = VALUES(`IP`), `LastLoginGlobal` = VALUES(`LastLoginglobal`), `TotalPlaytime` = VALUES(`TotalPlaytime`), `LastServerId` = VALUES(`LastServerId`);",EQueryType.NonQuery, output =>{var indexOf = _allPlayerData.FindIndex(k => k.SteamId == pdata.SteamId);lock (_memory){if (indexOf < 0)_allPlayerData.Add(pdata);else _allPlayerData[indexOf] = pdata;}}, false, new MySqlParameter("@steamid", pdata.SteamId), new MySqlParameter("@steamname", pdata.SteamName.Truncate(200)), new MySqlParameter("@charname", pdata.CharacterName.Truncate(200)), new MySqlParameter("@groupid", pdata.LastQuestGroupId), new MySqlParameter("@groupname", pdata.GroupName),new MySqlParameter("@hwid", pdata.Hwid), new MySqlParameter("@ip", pdata.Ip), new MySqlParameter("@lastinstanceid", pdata.ServerId), new MySqlParameter("@lastloginglobal", pdata.LastLoginGlobal.ToTimeStamp()), new MySqlParameter("@totalplaytime", pdata.TotalPlaytime)));
        }
    }
}