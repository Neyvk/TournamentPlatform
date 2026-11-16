using Microsoft.EntityFrameworkCore;

public static class RatingDemoDataSeeder
{
    // HLTV world ranking snapshot from November 9, 2026.
    // https://www.hltv.org/ranking/teams/2026/november/9
    private static readonly (int TeamNumber, int Value)[] InitialRatings =
    [
        (1, 912), // MOUZ
        (2, 910), // FURIA
        (3, 836), // Vitality
        (4, 777), // Spirit
        (5, 552), // Aurora
        (6, 477), // Falcons
        (7, 439), // BETBOOM
        (8, 412), // Natus Vincere
        (9, 361), // FUT
        (10, 324) // The MongolZ
    ];

    public static async Task EnsureAsync(RatingDbContext db)
    {
        foreach (var (teamNumber, value) in InitialRatings)
        {
            var teamId = Guid.Parse($"10000000-0000-0000-0000-{teamNumber:D12}").ToString();
            if (await db.Ratings.FindAsync("team", teamId) is not null)
                continue;

            db.Ratings.Add(new ParticipantRating
            {
                ParticipantType = "team",
                ParticipantId = teamId,
                Value = value,
                MatchesPlayed = 0,
                UpdatedAt = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero)
            });
        }

        await db.SaveChangesAsync();
    }
}
