using System.Text;
using Npgsql;

// Usage:
//   dotnet run -- "<conn>" --sql "<query>"
//   dotnet run -- "<conn>" --audit api.raw_jobs source     (population matrix: % non-null per col per group)
var connString = args.ElementAtOrDefault(0)
    ?? throw new InvalidOperationException("arg0 = connection string");

await using var conn = new NpgsqlConnection(connString);
await conn.OpenAsync();

if (args.Contains("--audit"))
{
    int ai = Array.IndexOf(args, "--audit");
    var table = args[ai + 1];
    var groupCol = args[ai + 2];
    var schema = table.Split('.')[0];
    var tbl = table.Split('.')[1];

    var cols = new List<string>();
    await using (var c1 = new NpgsqlCommand(
        "SELECT column_name FROM information_schema.columns WHERE table_schema=@s AND table_name=@t ORDER BY ordinal_position", conn))
    {
        c1.Parameters.AddWithValue("s", schema);
        c1.Parameters.AddWithValue("t", tbl);
        await using var r1 = await c1.ExecuteReaderAsync();
        while (await r1.ReadAsync()) cols.Add(r1.GetString(0));
    }

    var sb = new StringBuilder();
    sb.Append($"SELECT \"{groupCol}\" AS grp, count(*) AS total");
    foreach (var col in cols) sb.Append($", count(\"{col}\") AS \"{col}\"");
    sb.Append($" FROM {schema}.{tbl} GROUP BY \"{groupCol}\" ORDER BY \"{groupCol}\"");

    var sources = new List<string>();
    var totals = new Dictionary<string, long>();
    var data = new Dictionary<string, Dictionary<string, long>>();
    foreach (var col in cols) data[col] = new();

    await using (var c2 = new NpgsqlCommand(sb.ToString(), conn))
    await using (var r2 = await c2.ExecuteReaderAsync())
    {
        while (await r2.ReadAsync())
        {
            var src = r2.IsDBNull(0) ? "<null>" : r2.GetValue(0).ToString()!;
            sources.Add(src);
            totals[src] = r2.GetInt64(1);
            for (int i = 0; i < cols.Count; i++)
                data[cols[i]][src] = r2.GetInt64(2 + i);
        }
    }

    Console.WriteLine("column\t" + string.Join("\t", sources));
    Console.WriteLine("__ROWS__\t" + string.Join("\t", sources.Select(s => totals[s])));
    foreach (var col in cols)
    {
        var cells = sources.Select(s =>
        {
            long total = totals[s];
            if (total == 0) return "-";
            long nn = data[col][s];
            return ((int)Math.Round(100.0 * nn / total)).ToString();
        });
        Console.WriteLine(col + "\t" + string.Join("\t", cells));
    }
    return;
}

string sql = args.Contains("--sql")
    ? args[Array.IndexOf(args, "--sql") + 1]
    : throw new InvalidOperationException("pass --sql \"...\" or --audit <schema.table> <groupcol>");

await using var cmd = new NpgsqlCommand(sql, conn);
await using var reader = await cmd.ExecuteReaderAsync();
do
{
    if (reader.FieldCount == 0) continue;
    var headers = new string[reader.FieldCount];
    for (int i = 0; i < reader.FieldCount; i++) headers[i] = reader.GetName(i);
    Console.WriteLine(string.Join("\t", headers));
    while (await reader.ReadAsync())
    {
        var cells = new string[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (await reader.IsDBNullAsync(i)) { cells[i] = "<null>"; continue; }
            var v = reader.GetValue(i);
            cells[i] = v switch
            {
                string[] arr => "[" + string.Join(" | ", arr) + "]",
                Array a => "[" + string.Join(" | ", a.Cast<object>()) + "]",
                DateTime dt => dt.ToString("yyyy-MM-dd HH:mm"),
                _ => v.ToString() ?? ""
            };
            cells[i] = cells[i].Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            if (cells[i].Length > 140) cells[i] = cells[i][..137] + "...";
        }
        Console.WriteLine(string.Join("\t", cells));
    }
    Console.WriteLine("====");
} while (await reader.NextResultAsync());
