using Microsoft.EntityFrameworkCore;

public static class DemoDataSeeder
{
    // Latest HLTV world ranking available on 2026-11-16: November 9, 2026.
    // https://www.hltv.org/ranking/teams/2026/november/9
    private static readonly (string Name, string[] Players)[] Teams =
    [
        ("MOUZ", ["torzsi", "Spinx", "xertioN", "PR", "xelex"]),
        ("FURIA", ["FalleN", "yuurih", "YEKINDAR", "KSCERATO", "molodoy"]),
        ("Vitality", ["apEX", "ropz", "ZywOo", "flameZ", "mezii"]),
        ("Spirit", ["sh1ro", "magixx", "tN1R", "zont1x", "donk"]),
        ("Aurora", ["XANTARES", "woxic", "Jimpphat", "kyxsan", "Wicadia"]),
        ("Falcons", ["karrigan", "NiKo", "TeSeS", "m0NESY", "kyousuke"]),
        ("BETBOOM", ["Boombl4", "zorte", "S1ren", "d1Ledez", "Magnojez"]),
        ("Natus Vincere", ["Aleksib", "iM", "b1t", "w0nderful", "makazze"]),
        ("FUT", ["SunPayus", "xfl0ud", "dem0n", "Krabeni", "dziugss"]),
        ("The MongolZ", ["nin9", "bLitz", "Techno", "910", "cobrazera"])
    ];

    public static async Task EnsureAsync(PlayersDbContext db)
    {
        var profiles = await db.Profiles.ToListAsync();
        var playerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nextPlayerId = 1;

        foreach (var (_, players) in Teams)
        {
            foreach (var nickname in players)
            {
                if (playerIds.ContainsKey(nickname))
                    continue;

                var profile = profiles.FirstOrDefault(item =>
                    string.Equals(item.Nickname, nickname, StringComparison.OrdinalIgnoreCase));
                if (profile is null)
                {
                    profile = new PlayerProfile
                    {
                        UserId = CreatePlayerId(nextPlayerId++),
                        Nickname = nickname,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    db.Profiles.Add(profile);
                    profiles.Add(profile);
                }

                playerIds[nickname] = profile.UserId;
            }
        }

        for (var index = 0; index < Teams.Length; index++)
        {
            var (name, players) = Teams[index];
            var teamId = CreateTeamId(index + 1);
            var team = await db.Teams.FindAsync(teamId);
            if (team is null)
            {
                team = new PlayerTeam
                {
                    Id = teamId,
                    Name = name,
                    OwnerId = playerIds[players[0]],
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.Teams.Add(team);
            }

            foreach (var nickname in players)
            {
                var userId = playerIds[nickname];
                if (!await db.TeamMembers.AnyAsync(member => member.TeamId == teamId && member.UserId == userId))
                {
                    db.TeamMembers.Add(new TeamMember
                    {
                        TeamId = teamId,
                        UserId = userId,
                        JoinedAt = DateTimeOffset.UtcNow
                    });
                }
            }
        }

        await db.SaveChangesAsync();
    }

    private static string CreatePlayerId(int index) =>
        Guid.Parse($"00000000-0000-0000-0000-{index:D12}").ToString();

    private static Guid CreateTeamId(int index) =>
        Guid.Parse($"10000000-0000-0000-0000-{index:D12}");
}
