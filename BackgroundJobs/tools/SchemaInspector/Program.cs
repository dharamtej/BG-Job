using SchemaInspector;

var connString = Environment.GetEnvironmentVariable("CAREERPANDA_DB_CONNECTION")
    ?? args.FirstOrDefault()
    ?? throw new InvalidOperationException("Set CAREERPANDA_DB_CONNECTION or pass connection string as first argument.");

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 10; i++)
    {
        if (File.Exists(Path.Combine(dir, "CareerPanda.sln")))
            return dir;
        var parent = Directory.GetParent(dir)?.FullName;
        if (parent == null)
            break;
        dir = parent;
    }

    throw new InvalidOperationException("Could not locate CareerPanda.sln from " + AppContext.BaseDirectory);
}

var repoRoot = FindRepoRoot();
var entitiesRoot = Path.Combine(repoRoot, "DataAccess", "Entities");
var dbContextDir = Path.Combine(repoRoot, "DataAccess", "PostgreSQL");

if (args.Contains("--check-logs"))
{
    await using var conn = new Npgsql.NpgsqlConnection(connString);
    await conn.OpenAsync();
    const string sql = """
        SELECT table_schema, table_name FROM information_schema.tables
        WHERE table_name ILIKE '%application_log%' OR table_name ILIKE '%log%'
        ORDER BY 1, 2
        """;
    await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        Console.WriteLine($"{reader.GetString(0)}.{reader.GetString(1)}");
    return;
}

if (args.Contains("--list-only"))
{
    await using var conn = new Npgsql.NpgsqlConnection(connString);
    await conn.OpenAsync();
    const string listSql = """
        SELECT table_schema, table_name FROM information_schema.tables
        WHERE table_schema IN ('api','cp','md') AND table_type = 'BASE TABLE'
        ORDER BY 1, 2
        """;
    await using var cmd = new Npgsql.NpgsqlCommand(listSql, conn);
    await using var reader = await cmd.ExecuteReaderAsync();
    var count = 0;
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"{reader.GetString(0)}.{reader.GetString(1)}");
        count++;
    }
    Console.WriteLine($"Total: {count}");
    return;
}

await EntityGenerator.GenerateAsync(connString, entitiesRoot, dbContextDir);
Console.WriteLine("Done. Rebuild CareerPanda.sln");
