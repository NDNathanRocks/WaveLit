namespace Wavelit.Core.Playback;

/// <summary>"Follows the sun": one wallpaper by day, another by night, switched at local sunrise and sunset.</summary>
public sealed record DayNightSchedule
{
    public bool Enabled { get; init; }
    public string? DayItemId { get; init; }
    public string? NightItemId { get; init; }
    public string Place { get; init; } = "";
    public double Latitude { get; init; }
    public double Longitude { get; init; }

    public bool IsReady => Enabled && DayItemId is not null && NightItemId is not null && Place.Length > 0;
}

public sealed record City(string Name, double Latitude, double Longitude);

/// <summary>Sunrise and sunset worked out on the PC (NOAA solar equations); no location service involved.</summary>
public static class SunTimes
{
    /// <summary>Sunrise and sunset (UTC) for a date, or null during polar day/night.</summary>
    public static (DateTime Sunrise, DateTime Sunset)? Calculate(DateOnly date, double latitude, double longitude)
    {
        double rise = Event(date, latitude, longitude, rising: true);
        double set = Event(date, latitude, longitude, rising: false);
        if (double.IsNaN(rise) || double.IsNaN(set)) return null;
        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var sunrise = midnight.AddMinutes(rise);
        var sunset = midnight.AddMinutes(set);
        if (sunset < sunrise) sunset = sunset.AddDays(1); // far east/west of UTC: sunset falls on the next UTC day
        return (sunrise, sunset);
    }

    /// <summary>True when the sun is up at <paramref name="utcNow"/> at the given place (polar cases by season).</summary>
    public static bool IsDaytime(DateTime utcNow, double latitude, double longitude)
    {
        // Use the local solar date so the right day's events are compared.
        var localDate = DateOnly.FromDateTime(utcNow.AddHours(longitude / 15));
        foreach (var d in new[] { localDate.AddDays(-1), localDate, localDate.AddDays(1) })
        {
            if (Calculate(d, latitude, longitude) is { } t && utcNow >= t.Sunrise && utcNow < t.Sunset) return true;
        }
        if (Calculate(localDate, latitude, longitude) is null)
        {
            bool northernSummer = utcNow.Month is >= 4 and <= 9;
            return latitude >= 0 ? northernSummer : !northernSummer; // midnight sun or polar night
        }
        return false;
    }

    // Minutes after UTC midnight of the event (NOAA "sunrise equation" with refraction, zenith 90.833°).
    private static double Event(DateOnly date, double lat, double lng, bool rising)
    {
        double jd = date.ToDateTime(TimeOnly.MinValue).ToOADate() + 2415018.5 + 0.5; // Julian day at UTC noon
        double t = (jd - 2451545.0) / 36525.0;
        double l0 = Mod(280.46646 + t * (36000.76983 + t * 0.0003032), 360);
        double m = 357.52911 + t * (35999.05029 - 0.0001537 * t);
        double e = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
        double c = Sin(m) * (1.914602 - t * (0.004817 + 0.000014 * t)) + Sin(2 * m) * (0.019993 - 0.000101 * t) + Sin(3 * m) * 0.000289;
        double trueLong = l0 + c;
        double omega = 125.04 - 1934.136 * t;
        double lambda = trueLong - 0.00569 - 0.00478 * Sin(omega);
        double obliq = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60 + 0.00256 * Cos(omega);
        double decl = Deg(Math.Asin(Sin(obliq) * Sin(lambda)));
        double y = Math.Pow(Math.Tan(Rad(obliq / 2)), 2);
        double eqTime = 4 * Deg(y * Sin(2 * l0) - 2 * e * Sin(m) + 4 * e * y * Sin(m) * Cos(2 * l0) - 0.5 * y * y * Sin(4 * l0) - 1.25 * e * e * Sin(2 * m));
        double cosHa = Cos(90.833) / (Cos(lat) * Cos(decl)) - Math.Tan(Rad(lat)) * Math.Tan(Rad(decl));
        if (cosHa is < -1 or > 1) return double.NaN;
        double ha = Deg(Math.Acos(cosHa));
        double noon = 720 - 4 * lng - eqTime;
        return rising ? noon - 4 * ha : noon + 4 * ha;
    }

    private static double Rad(double d) => d * Math.PI / 180;
    private static double Deg(double r) => r * 180 / Math.PI;
    private static double Sin(double d) => Math.Sin(Rad(d));
    private static double Cos(double d) => Math.Cos(Rad(d));
    private static double Mod(double a, double n) => (a % n + n) % n;

    /// <summary>Major cities for picking a location without any location service.</summary>
    public static IReadOnlyList<City> Cities { get; } =
    [
        new("Amsterdam", 52.37, 4.90), new("Athens", 37.98, 23.73), new("Atlanta", 33.75, -84.39), new("Auckland", -36.85, 174.76),
        new("Bangkok", 13.76, 100.50), new("Barcelona", 41.39, 2.17), new("Beijing", 39.90, 116.41), new("Bengaluru", 12.97, 77.59),
        new("Berlin", 52.52, 13.40), new("Bogotá", 4.71, -74.07), new("Boston", 42.36, -71.06), new("Brisbane", -27.47, 153.03),
        new("Buenos Aires", -34.60, -58.38), new("Cairo", 30.04, 31.24), new("Cape Town", -33.92, 18.42), new("Chicago", 41.88, -87.63),
        new("Copenhagen", 55.68, 12.57), new("Dallas", 32.78, -96.80), new("Delhi", 28.61, 77.21), new("Denver", 39.74, -104.99),
        new("Dhaka", 23.81, 90.41), new("Dubai", 25.20, 55.27), new("Dublin", 53.35, -6.26), new("Helsinki", 60.17, 24.94),
        new("Ho Chi Minh City", 10.82, 106.63), new("Hong Kong", 22.32, 114.17), new("Honolulu", 21.31, -157.86), new("Houston", 29.76, -95.37),
        new("Istanbul", 41.01, 28.98), new("Jakarta", -6.21, 106.85), new("Johannesburg", -26.20, 28.05), new("Karachi", 24.86, 67.01),
        new("Kolkata", 22.57, 88.36), new("Kuala Lumpur", 3.14, 101.69), new("Lagos", 6.52, 3.38), new("Lima", -12.05, -77.04),
        new("Lisbon", 38.72, -9.14), new("London", 51.51, -0.13), new("Los Angeles", 34.05, -118.24), new("Madrid", 40.42, -3.70),
        new("Manila", 14.60, 120.98), new("Melbourne", -37.81, 144.96), new("Mexico City", 19.43, -99.13), new("Miami", 25.76, -80.19),
        new("Milan", 45.46, 9.19), new("Montréal", 45.50, -73.57), new("Moscow", 55.76, 37.62), new("Mumbai", 19.08, 72.88),
        new("Nairobi", -1.29, 36.82), new("New York", 40.71, -74.01), new("Oslo", 59.91, 10.75), new("Paris", 48.86, 2.35),
        new("Perth", -31.95, 115.86), new("Phoenix", 33.45, -112.07), new("Prague", 50.08, 14.44), new("Reykjavík", 64.15, -21.94),
        new("Rio de Janeiro", -22.91, -43.17), new("Riyadh", 24.71, 46.68), new("Rome", 41.90, 12.50), new("San Francisco", 37.77, -122.42),
        new("Santiago", -33.45, -70.67), new("São Paulo", -23.55, -46.63), new("Seattle", 47.61, -122.33), new("Seoul", 37.57, 126.98),
        new("Shanghai", 31.23, 121.47), new("Singapore", 1.35, 103.82), new("Stockholm", 59.33, 18.07), new("Sydney", -33.87, 151.21),
        new("Taipei", 25.03, 121.57), new("Tehran", 35.69, 51.39), new("Tokyo", 35.68, 139.69), new("Toronto", 43.65, -79.38),
        new("Vancouver", 49.28, -123.12), new("Vienna", 48.21, 16.37), new("Warsaw", 52.23, 21.01), new("Washington, D.C.", 38.91, -77.04),
        new("Zürich", 47.38, 8.54),
    ];
}

/// <summary>Remembers wallpapers per monitor setup (docked, laptop only, TV attached…).</summary>
public static class SetupMemory
{
    public const int MaxSetups = 12;

    /// <summary>A stable key for a set of monitors and their arrangement.</summary>
    public static string Signature(IEnumerable<Desktop.MonitorInfo> monitors)
        => string.Join('|', monitors.OrderBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => $"{m.Id}@{m.Bounds.Left},{m.Bounds.Top},{m.Bounds.Width}x{m.Bounds.Height}"));
}
