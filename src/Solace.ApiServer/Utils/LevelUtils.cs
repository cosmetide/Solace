using Microsoft.EntityFrameworkCore;
using Solace.Common;
using Solace.Db.Earth;
using Solace.Db.Earth.Models.Player;
using Solace.StaticData;

namespace Solace.ApiServer.Utils;

internal sealed partial class LevelUtils
{
#pragma warning disable IDE0060 // Remove unused parameter
    public static async Task CheckAndHandlePlayerLevelUpAsync(EarthDbContext earthDb, ResultsEF.Builder results, Guid accountId, DateTimeOffset currentTime, StaticData.StaticDataProvider staticData, CancellationToken cancellationToken = default)
#pragma warning restore IDE0060 // Remove unused parameter
    {
        var profile = await earthDb.Profiles
            .AsNoTracking()
            .Where(profile => profile.Id == accountId)
            .Select(profile => new { profile.Level, profile.Experience, })
            .FirstAsync(cancellationToken);

        var profileLevel = profile.Level;

        var changed = false;
        while (profileLevel - 1 < staticData.Levels.Levels.Length && profile.Experience >= staticData.Levels.Levels[profileLevel - 1].ExperienceRequired)
        {
            changed = true;
            profileLevel++;
            var rewards = MakeLevelRewards(staticData.Levels.Levels[profileLevel - 2]);
            await TokenUtils.AddTokenAsync(earthDb, results, new LevelUpTokenEF(accountId, profileLevel, rewards.ToDBRewardsModel()), cancellationToken);
        }

        if (changed)
        {
            await earthDb.Profiles
                .Where(profile => profile.Id == accountId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(profile => profile.Level, profileLevel), cancellationToken);

            results.Profile();
        }
    }

    public static Rewards MakeLevelRewards(PlayerLevels.Level level)
    {
        var rewards = new Rewards();
        if (level.Rubies > 0)
        {
            rewards.AddRubies(level.Rubies);
        }

        foreach (var item in level.Items)
        {
            rewards.AddItem(item.Id, item.Count);
        }

        foreach (var buildplate in level.Buildplates)
        {
            if (Guid.TryParse(buildplate, out var buildplateGuid))
            {
                rewards.AddBuildplate(buildplateGuid);
            }
            else
            {
                var logger = GlobalLoggerFactory.CreateLogger(nameof(LevelUtils));
                LogCouldNotParseLevelUpBuildplateId(logger, buildplate);
            }
        }

        return rewards;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not parse level up buildplate id '{BuildplateId}'")]
    private static partial void LogCouldNotParseLevelUpBuildplateId(ILogger logger, string BuildplateId);
}
