namespace CareerPanda.Framework.Configuration;

public class RedisConfig
{
    public string Connection { get; set; } = "localhost:6379";

    public string InstanceName { get; set; } = "CareerPanda:";

    public bool Enabled { get; set; } = true;
}
