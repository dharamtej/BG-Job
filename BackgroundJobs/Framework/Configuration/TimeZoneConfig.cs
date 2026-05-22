namespace CareerPanda.Framework.Configuration;

public class TimeZoneConfig
{
    public string Timezone { get; set; } = "UTC";

    public string DateFormat { get; set; } = "dd-MMM-yy";

    public string TimeFormat { get; set; } = "HH:mm:ss";
}
