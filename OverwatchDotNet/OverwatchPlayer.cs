﻿using AngleSharp;
using AngleSharp.Dom;
using OverwatchAPI.Internal;
using OverwatchAPI.intrnl;
using System;
using System.Threading.Tasks;
using static OverwatchAPI.OverwatchAPIHelpers;

namespace OverwatchAPI
{
    public sealed class OverwatchPlayer : IDisposable
    {
        /// <summary>
        /// The players Battletag with Discriminator - e.g. "SomeUser#1234"
        /// </summary>
        public string Username { get; }

        /// <summary>
        /// The PlayOverwatch profile of the player - This is only available if the user has set a region
        /// </summary>
        public string ProfileURL { get; private set; }

        /// <summary>
        /// The Player Level of the player
        /// </summary>
        public ushort PlayerLevel { get; private set; }

        /// <summary>
        /// The Competitive Rank of the player
        /// </summary>
        public ushort CompetitiveRank { get; private set; }
        
        /// <summary>
        /// The Competitive Rank Image of the player
        /// </summary>
        public string CompetitiveRankImg { get; private set; }

        /// <summary>
        /// The player's region - EU/US/None
        /// </summary>
        public Region Region { get; private set; } 

        /// <summary>
        /// The player's platform - PC/XBL/PSN
        /// </summary>
        public Platform Platform { get; private set; }

        /// <summary>
        /// The players quick-play stats.
        /// </summary>
        public OverwatchStats CasualStats { get; private set; }

        /// <summary>
        /// The players competitive stats.
        /// </summary>
        public OverwatchStats CompetitiveStats { get; private set; }

        /// <summary>
        /// The players achievements.
        /// </summary>
        public OverwatchAchievements Achievements { get; private set; }

        /// <summary>
        /// The last time the profile was downloaded from PlayOverwatch.
        /// </summary>
        public DateTime ProfileLastDownloaded { get; private set; }

        /// <summary>
        /// A direct link to the users profile portrait.
        /// </summary>
        public string ProfilePortraitURL { get; private set; }

        private string battletagUrlFriendly;
        private IBrowsingContext browsingContext;
        private IDocument userPage;
        private RegionDetectionSettings regionSettings;

        /// <summary>
        /// Construct a new Overwatch player.
        /// </summary>
        /// <param name="username">The players Battletag (SomeUser#1234) or Username for PSN/XBL</param>
        /// <param name="platform">The players platform - Defaults to "none" if a battletag is not given (use DetectPlatform() if platform is not known)</param>
        /// <param name="region">The players region (only required for PC) - Defaults to "none" (use DetectRegionPC if region is not known)</param>
        public OverwatchPlayer(string username, Platform platform = Platform.none, Region region = Region.none)
        {
            Username = username;
            Platform = platform;
            browsingContext = BrowsingContext.New(Configuration.Default.WithDefaultLoader());
            if (!IsValidBattletag(username) && platform == Platform.pc)
                throw new InvalidBattletagException();
            else if (IsValidBattletag(username))
            {
                Platform = Platform.pc;
                battletagUrlFriendly = username.Replace("#", "-");
                Region = region;
            }
            if (Region != Region.none && Platform != Platform.none)
            {
                ProfileURL = ProfileURL(Username, Region, Platform);
            }
        }

        private async Task DetectRegionAsync()
        {
            foreach(var region in regionSettings.regions)
            {
                if (await DetectRegionSpecificAsync(region))
                    return;
            }
        }   

        private async Task<bool> DetectRegionSpecificAsync(Region r)
        {
            string url = $"http://playoverwatch.com/en-gb/career/pc/{r}/{battletagUrlFriendly}";
            var rslt = await browsingContext.OpenAsync(url);
            if(rslt.StatusCode == (System.Net.HttpStatusCode)200)
            {
                Region = r;
                userPage = rslt;
                ProfileURL = url;
                return true;
            }
            return false;
        }
        
        private async Task DetectPlatformAsync()
        {
            string psnUrl = $"http://playoverwatch.com/en-gb/career/psn/{Username}";
            string xblUrl = $"http://playoverwatch.com/en-gb/career/xbl/{Username}";
            var psnRslt = await browsingContext.OpenAsync(psnUrl);
            if(psnRslt.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var xblRslt = await browsingContext.OpenAsync(xblUrl);
                if(xblRslt.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Platform = Platform.none;
                }
                else
                {
                    Platform = Platform.xbl;
                    userPage = xblRslt;
                    ProfileURL = xblUrl;
                }
            }
            else
            {
                Platform = Platform.psn;
                userPage = psnRslt;
                ProfileURL = psnUrl;
            }
        }

        /// <summary>
        /// Downloads and parses the players profile
        /// </summary>
        /// <param name="settings">The region detection settings (only required for PC) - defaults to all regions in this order: NA, EU, KR</param>
        /// <param name="useAutoDetect">Attempt to auto-detect Region/Platform</param>
        /// <returns></returns>
        public async Task UpdateStatsAsync(RegionDetectionSettings settings = null, bool useAutoDetect = true)
        {
            regionSettings = settings ?? new RegionDetectionSettings();
            if(!useAutoDetect)
            {
                if (Region == Region.none && Platform == Platform.pc)
                    throw new UserRegionNotDefinedException();
                if (Platform == Platform.none)
                    throw new UserPlatformNotDefinedException();
            }
            else
            {
                if(IsValidBattletag(Username))
                {
                    Platform = Platform.pc;
                    if (Region == Region.none)
                        await DetectRegionAsync();
                }
                else if(Platform == Platform.none)
                {
                    await DetectPlatformAsync();
                }
                ProfileURL = ProfileURL(Username, Region, Platform);            
            }
            if (userPage == null)
                userPage = await browsingContext.OpenAsync(ProfileURL);
            ParseUserPage();
        }

        private void ParseUserPage()
        {
            GetUserRanks();
            ProfilePortraitURL = userPage.QuerySelector(".player-portrait").GetAttribute("src");
            CasualStats = new OverwatchStats();
            CompetitiveStats = new OverwatchStats();
            Achievements = new OverwatchAchievements();
            Achievements.UpdateAchievementsFromPage(userPage);
            CasualStats.UpdateStatsFromPage(userPage, Mode.Casual);
            CompetitiveStats.UpdateStatsFromPage(userPage, Mode.Competitive);
            if (CompetitiveStats.Count == 0) CompetitiveStats = null;
            ProfileLastDownloaded = DateTime.UtcNow;
        }

        private void GetUserRanks()
        {
            PlayerLevel = 0;
            CompetitiveRank = 0;
            if (ushort.TryParse(userPage.QuerySelector("div.player-level div")?.TextContent, out ushort parsedPlayerLevel))
                PlayerLevel = parsedPlayerLevel;
            string playerLevelImageId = StaticVars.playerRankImageRegex.Match(userPage.QuerySelector("div.player-level")?.GetAttribute("style")).Value;
            PlayerLevel += StaticVars.prestigeDefinitions[playerLevelImageId];
            if (ushort.TryParse(userPage.QuerySelector("div.competitive-rank div")?.TextContent, out ushort parsedCompetitiveRank))
                CompetitiveRank = parsedCompetitiveRank;
            var compImg = userPage.QuerySelector("div.competitive-rank img")?.OuterHtml;
            if (!string.IsNullOrEmpty(compImg))
                CompetitiveRankImg = compImg.Replace("<img src=\"", "").Replace("\">", "");
        }

        public void Dispose()
        {
            userPage.Dispose();
        }
    }
}
