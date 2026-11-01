using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VlessCheck.Core;

/// <summary>Настройки в SQLite: группы, подписки, параметры проверки. Общая точка для api и checker.</summary>
public sealed partial class Store
{
    [GeneratedRegex("^[a-z0-9_-]{1,32}$")]
    public static partial Regex GroupName();

    static readonly (string Name, string Title, string[] Urls)[] DefaultGroups =
    [
        ("white", "🏳️ Белые списки",
        [
            "https://raw.githubusercontent.com/GoldCaviar/vpn-configs-for-russia/refs/heads/main/Vless-Reality-White-Lists-Rus-Mobile.txt",
            "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/refs/heads/main/Vless-Reality-White-Lists-Rus-Mobile.txt",
            "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/refs/heads/main/WHITE-CIDR-RU-all.txt",
            "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/refs/heads/main/WHITE-CIDR-RU-checked.txt",
            "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/refs/heads/main/WHITE-SNI-RU-all.txt",
        ]),
        ("black", "🏴 Чёрные списки",
        [
            "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/refs/heads/main/BLACK_VLESS_RUS.txt",
            "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/refs/heads/main/BLACK_SS+All_RUS.txt",
        ]),
    ];

    readonly string _connectionString;

    public string DataDir { get; }

    public Store(string dataDir)
    {
        DataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(DataDir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDir, "vlesscheck.db"),
            ForeignKeys = true,
            DefaultTimeout = 10,
        }.ToString();

        using var db = Open();
        Exec(db, "PRAGMA journal_mode = WAL");
        // api и checker стартуют одновременно: эксклюзивная транзакция, чтобы миграции не перетёрли друг друга
        using var tx = db.BeginTransaction(deferred: false);
        Exec(db, """
            CREATE TABLE IF NOT EXISTS groups (
                name TEXT PRIMARY KEY, title TEXT NOT NULL, top_n INTEGER NOT NULL DEFAULT 55,
                max_per_host INTEGER NOT NULL DEFAULT 2, position INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS subscriptions (
                id INTEGER PRIMARY KEY, group_name TEXT NOT NULL REFERENCES groups(name) ON DELETE CASCADE,
                url TEXT NOT NULL, created REAL NOT NULL, UNIQUE(group_name, url));
            CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
        if (Scalar(db, "SELECT COUNT(*) FROM groups") is 0L)
            for (var i = 0; i < DefaultGroups.Length; i++)
            {
                var (name, title, urls) = DefaultGroups[i];
                Exec(db, "INSERT INTO groups(name, title, position) VALUES ($n, $t, $p)", ("$n", name), ("$t", title), ("$p", i));
                foreach (var u in urls)
                    Exec(db, "INSERT INTO subscriptions(group_name, url, created) VALUES ($g, $u, $c)", ("$g", name), ("$u", u), ("$c", Now()));
            }
        if (Get(db, "settings") is null)
            SaveSettings(db, new Settings { SubToken = NewToken() });
        var current = JsonSerializer.Deserialize<Settings>(Get(db, "settings")!, Json.Options)!;
        if (current.PanelPath == "")
        {
            // секретный адрес панели: из переменной окружения при установке, иначе случайный
            current.PanelPath = Environment.GetEnvironmentVariable("VLESSCHECK_PANEL_PATH") is { Length: > 0 } p ? p : NewToken();
            SaveSettings(db, current);
        }

        // у каждой группы свой токен, чтобы менять ссылку одной группы, не трогая остальные;
        // при переходе с общего токена группы получают его — уже розданные ссылки продолжают работать
        if (Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('groups') WHERE name='token'") is 0L)
        {
            Exec(db, "ALTER TABLE groups ADD COLUMN token TEXT NOT NULL DEFAULT ''");
            var shared = JsonSerializer.Deserialize<Settings>(Get(db, "settings")!, Json.Options)!.SubToken;
            Exec(db, "UPDATE groups SET token=$t", ("$t", shared));
        }
        foreach (var name in Column(db, "SELECT name FROM groups WHERE token=''"))
            Exec(db, "UPDATE groups SET token=$t WHERE name=$n", ("$t", NewToken()), ("$n", name));
        tx.Commit();
    }

    static List<string> Column(SqliteConnection db, string sql)
    {
        using var cmd = Cmd(db, sql, []);
        using var r = cmd.ExecuteReader();
        var values = new List<string>();
        while (r.Read())
            values.Add(r.GetString(0));
        return values;
    }

    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        Exec(db, "PRAGMA busy_timeout = 10000");
        return db;
    }

    static SqliteCommand Cmd(SqliteConnection db, string sql, (string, object)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args)
            cmd.Parameters.AddWithValue(k, v);
        return cmd;
    }

    static int Exec(SqliteConnection db, string sql, params (string, object)[] args)
    {
        using var cmd = Cmd(db, sql, args);
        return cmd.ExecuteNonQuery();
    }

    static object? Scalar(SqliteConnection db, string sql, params (string, object)[] args)
    {
        using var cmd = Cmd(db, sql, args);
        return cmd.ExecuteScalar();
    }

    static string? Get(SqliteConnection db, string key) => Scalar(db, "SELECT value FROM kv WHERE key=$k", ("$k", key)) as string;

    static void Set(SqliteConnection db, string key, string value) =>
        Exec(db, "INSERT OR REPLACE INTO kv VALUES ($k, $v)", ("$k", key), ("$v", value));

    static void SaveSettings(SqliteConnection db, Settings s) => Set(db, "settings", JsonSerializer.Serialize(s, Json.Options));

    // --- настройки ---

    public Settings Settings()
    {
        using var db = Open();
        return JsonSerializer.Deserialize<Settings>(Get(db, "settings")!, Json.Options)!;
    }

    /// <summary>Частичное обновление: только известные ключи, типы проверяет десериализация.</summary>
    public Settings UpdateSettings(JsonObject patch)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        // через класс, а не сырой JSON из базы: так в current есть и настройки, добавленные в новых версиях
        var stored = JsonSerializer.Deserialize<Settings>(Get(db, "settings")!, Json.Options)!;
        var current = JsonSerializer.SerializeToNode(stored, Json.Options)!.AsObject();
        foreach (var (key, value) in patch)
        {
            if (!current.ContainsKey(key) || Core.Settings.ReadOnlyKeys.Contains(key))
                throw new ArgumentException($"неизвестная настройка {key}");
            current[key] = value?.DeepClone();
        }
        Settings s;
        try
        {
            s = current.Deserialize<Settings>(Json.Options)!;
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"неверный тип значения: {e.Path}");
        }
        s.Validate();
        SaveSettings(db, s);
        tx.Commit();
        return s;
    }

    void MutateSettings(Action<Settings> change)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var s = JsonSerializer.Deserialize<Settings>(Get(db, "settings")!, Json.Options)!;
        change(s);
        SaveSettings(db, s);
        tx.Commit();
    }

    /// <summary>Новая ссылка подписки группы: старая сразу перестаёт работать, Happ-ссылку надо создать заново.</summary>
    public void RotateGroupToken(string name)
    {
        using (var db = Open())
            Exec(db, "UPDATE groups SET token=$t WHERE name=$n", ("$t", NewToken()), ("$n", name));
        MutateSettings(s => s.HappLinks.Remove(name));
    }

    public void SetHappLink(string group, string url, string link) => MutateSettings(s => s.HappLinks[group] = new HappLink(url, link));

    public string? GetValue(string key)
    {
        using var db = Open();
        return Get(db, key);
    }

    public void SetValue(string key, string value)
    {
        using var db = Open();
        Set(db, key, value);
    }

    public void RequestRun(string group = "*")
    {
        using var db = Open();
        Set(db, "run_requested", group);
    }

    public string? PopRunRequest()
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var value = Get(db, "run_requested");
        if (value is not null)
            Exec(db, "DELETE FROM kv WHERE key='run_requested'");
        tx.Commit();
        return value;
    }

    // --- группы и подписки ---

    public List<Group> Groups()
    {
        using var db = Open();
        var groups = new List<Group>();
        using (var cmd = Cmd(db, "SELECT name, title, top_n, max_per_host, token FROM groups ORDER BY position, name", []))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                groups.Add(new Group
                {
                    Name = r.GetString(0), Title = r.GetString(1), TopN = r.GetInt32(2), MaxPerHost = r.GetInt32(3), Token = r.GetString(4),
                });
        using (var cmd = Cmd(db, "SELECT id, group_name, url, created FROM subscriptions ORDER BY id", []))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                groups.FirstOrDefault(g => g.Name == r.GetString(1))?.Subscriptions.Add(new Subscription(r.GetInt64(0), r.GetString(2), r.GetDouble(3)));
        return groups;
    }

    public void AddGroup(string name, string title)
    {
        if (!GroupName().IsMatch(name))
            throw new ArgumentException("имя группы: латиница в нижнем регистре, цифры, - и _, до 32 символов");
        using var db = Open();
        var pos = Scalar(db, "SELECT COALESCE(MAX(position), -1) + 1 FROM groups")!;
        try
        {
            Exec(db, "INSERT INTO groups(name, title, position, token) VALUES ($n, $t, $p, $k)",
                 ("$n", name), ("$t", string.IsNullOrWhiteSpace(title) ? name : title), ("$p", pos), ("$k", NewToken()));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            throw new ArgumentException("такая группа уже есть");
        }
    }

    public void UpdateGroup(string name, JsonObject patch)
    {
        using var db = Open();
        foreach (var (key, value) in patch)
        {
            switch (key)
            {
                case "title":
                    Exec(db, "UPDATE groups SET title=$v WHERE name=$n", ("$v", value!.GetValue<string>()), ("$n", name));
                    break;
                case "top_n" or "max_per_host":
                    var n = value!.GetValue<int>();
                    if (n <= 0)
                        throw new ArgumentException("числа должны быть больше нуля");
                    Exec(db, $"UPDATE groups SET {key}=$v WHERE name=$n", ("$v", n), ("$n", name));
                    break;
                default:
                    throw new ArgumentException($"неизвестное поле {key}");
            }
        }
    }

    public void DeleteGroup(string name)
    {
        using var db = Open();
        Exec(db, "DELETE FROM groups WHERE name=$n", ("$n", name));
    }

    public int AddSubscriptions(string group, IEnumerable<string> urls)
    {
        var clean = urls.Select(u => u.Trim()).Where(u => u != "").ToList();
        var bad = clean.FirstOrDefault(u => !(Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme is "http" or "https"));
        if (bad is not null)
            throw new ArgumentException($"не похоже на ссылку: {bad[..Math.Min(80, bad.Length)]}");
        using var db = Open();
        if (Scalar(db, "SELECT 1 FROM groups WHERE name=$n", ("$n", group)) is null)
            throw new KeyNotFoundException("нет такой группы");
        return clean.Sum(u => Exec(db, "INSERT OR IGNORE INTO subscriptions(group_name, url, created) VALUES ($g, $u, $c)",
                                   ("$g", group), ("$u", u), ("$c", Now())));
    }

    public void DeleteSubscription(long id)
    {
        using var db = Open();
        Exec(db, "DELETE FROM subscriptions WHERE id=$id", ("$id", id));
    }

    public string GroupDir(string name) => Path.Combine(DataDir, "groups", name);
}
