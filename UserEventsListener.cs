﻿using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using OpenMod.API.Eventing;
using OpenMod.API.Users;
using OpenMod.Core.Users.Events;
using OpenMod.Unturned.Users;
using Pustalorc.PlayerInfoLib.Unturned.Database;
using Pustalorc.PlayerInfoLib.Unturned.SteamWebApiClasses;
using Steamworks;

namespace Pustalorc.PlayerInfoLib.Unturned
{
    public class UserEventsListener : IEventListener<UserConnectedEvent>, IEventListener<UserDisconnectedEvent>
    {
        private readonly IPlayerInfoRepository m_PlayerInfoRepository;
        private readonly IConfiguration m_Configuration;

        public UserEventsListener(IPlayerInfoRepository playerInfoRepository, IConfiguration configuration)
        {
            m_PlayerInfoRepository = playerInfoRepository;
            m_Configuration = configuration;
        }

        public async Task HandleEventAsync(object sender, UserConnectedEvent @event)
        {
            if (!(@event.User is UnturnedUser player)) return;

            var playerId = player.SteamPlayer.playerID;
            var steamId = player.SteamId;
            var pfpHash = await GetProfilePictureHashAsync(steamId);
            var groupName = await GetSteamGroupNameAsync(playerId.group);
            var hwid = string.Join("", playerId.hwid);
            SteamGameServerNetworking.GetP2PSessionState(steamId, out var sessionState);
            var ip = sessionState.m_nRemoteIP == 0 ? uint.MinValue : sessionState.m_nRemoteIP;

            var pData = await m_PlayerInfoRepository.FindPlayerAsync(steamId.ToString(), UserSearchMode.Id);
            var server = await m_PlayerInfoRepository.GetCurrentServerAsync() ?? await m_PlayerInfoRepository.CheckAndRegisterCurrentServerAsync();

            if (pData == null)
            {
                pData = m_PlayerInfoRepository.BuildPlayerData(steamId.m_SteamID, player.DisplayName,
                    playerId.playerName, hwid, ip,
                    pfpHash, player.Player.quests.groupID.m_SteamID, playerId.group.m_SteamID, groupName, 0,
                    DateTime.Now, server);

                await m_PlayerInfoRepository.AddPlayerDataAsync(pData);
            }
            else
            {
                pData.ProfilePictureHash = pfpHash;
                pData.CharacterName = player.DisplayName;
                pData.Hwid = hwid;
                pData.Ip = ip;
                pData.LastLoginGlobal = DateTime.Now;
                pData.LastQuestGroupId = player.Player.quests.groupID.m_SteamID;
                pData.SteamGroup = playerId.group.m_SteamID;
                pData.SteamGroupName = groupName;
                pData.SteamName = playerId.playerName;
                pData.Server = server;
                pData.ServerId = server.Id;

                await m_PlayerInfoRepository.SaveChangesAsync();
            }
        }

        public async Task HandleEventAsync(object sender, UserDisconnectedEvent @event)
        {
            if (!(@event.User is UnturnedUser player)) return;

            var playerId = player.SteamPlayer.playerID;
            var steamId = player.SteamId;
            var pfpHash = await GetProfilePictureHashAsync(steamId);
            var groupName = await GetSteamGroupNameAsync(playerId.group);
            var hwid = string.Join("", playerId.hwid);
            SteamGameServerNetworking.GetP2PSessionState(steamId, out var sessionState);
            var ip = sessionState.m_nRemoteIP == 0 ? uint.MinValue : sessionState.m_nRemoteIP;

            var pData = await m_PlayerInfoRepository.FindPlayerAsync(player.SteamId.ToString(), UserSearchMode.Id);
            var server = await m_PlayerInfoRepository.GetCurrentServerAsync() ?? await m_PlayerInfoRepository.CheckAndRegisterCurrentServerAsync();

            if (pData == null)
            {
                pData = m_PlayerInfoRepository.BuildPlayerData(steamId.m_SteamID, player.DisplayName,
                    playerId.playerName, hwid, ip,
                    pfpHash, player.Player.quests.groupID.m_SteamID, playerId.group.m_SteamID, groupName, 0,
                    DateTime.Now, server);

                await m_PlayerInfoRepository.AddPlayerDataAsync(pData);
            }
            else
            {
                pData.ProfilePictureHash = pfpHash;
                pData.CharacterName = player.DisplayName;
                pData.Hwid = hwid;
                pData.Ip = ip;
                pData.LastLoginGlobal = DateTime.Now;
                pData.LastQuestGroupId = player.Player.quests.groupID.m_SteamID;
                pData.SteamGroup = playerId.group.m_SteamID;
                pData.SteamGroupName = groupName;
                pData.SteamName = playerId.playerName;
                pData.TotalPlaytime += DateTime.Now.Subtract(pData.LastLoginGlobal).TotalSeconds;
                pData.Server = server;
                pData.ServerId = server.Id;

                await m_PlayerInfoRepository.SaveChangesAsync();
            }
        }

        [ItemNotNull]
        private async Task<string> GetProfilePictureHashAsync(CSteamID user)
        {
            var apiKey = m_Configuration["steamWebApiKey"];
            if (string.IsNullOrEmpty(apiKey))
                return "";

            using var web = new WebClient();
            var result =
                await web.DownloadStringTaskAsync(
                    $"http://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={apiKey}&steamids={user.m_SteamID}");

            var deserialized = JsonConvert.DeserializeObject<PlayerSummaries>(result);

            return deserialized.response.players
                       .FirstOrDefault(k => k.steamid.Equals(user.ToString(), StringComparison.Ordinal))?.avatarhash ??
                   "";
        }

        [ItemNotNull]
        private static async Task<string> GetSteamGroupNameAsync(CSteamID groupId)
        {
            using var web = new WebClient();
            var result =
                await web.DownloadStringTaskAsync("http://steamcommunity.com/gid/" + groupId +
                                                  "/memberslistxml?xml=1");

            if (!result.Contains("<groupName>") || !result.Contains("</groupName>")) return "";

            var start = result.IndexOf("<groupName>", 0, StringComparison.Ordinal) + "<groupName>".Length;
            var end = result.IndexOf("</groupName>", start, StringComparison.Ordinal);

            var data = result.Substring(start, end - start);
            data = data.Trim();
            data = data.Replace("<![CDATA[", "").Replace("]]>", "");
            return data;
        }
    }
}