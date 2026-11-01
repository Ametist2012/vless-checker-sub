using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VlessCheck.Core;

/// <summary>Вход в панель: один администратор, пароль в виде PBKDF2-хеша, сессия — подписанный токен в cookie.</summary>
public sealed class Auth(Store store)
{
    const int Iterations = 210_000;
    const int MaxFailures = 5;
    static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    sealed record Credentials(string Login, string Salt, string Hash, int Iterations);

    readonly ConcurrentDictionary<string, (int Failures, DateTime Until)> _attempts = new();

    Credentials? Load() => store.GetValue("auth") is { } json ? JsonSerializer.Deserialize<Credentials>(json, Json.Options) : null;

    public bool HasPassword => Load() is not null;

    public string Login => Load()?.Login ?? "admin";

    static byte[] Hash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Новые логин и пароль. Ключ подписи сессий меняется — все старые сессии выходят.</summary>
    public void SetCredentials(string login, string password)
    {
        login = login.Trim();
        if (login.Length is 0 or > 64)
            throw new ArgumentException("логин: от 1 до 64 символов");
        if (password.Length < 8)
            throw new ArgumentException("пароль: не короче 8 символов");
        var salt = RandomNumberGenerator.GetBytes(16);
        var creds = new Credentials(login, Convert.ToBase64String(salt), Convert.ToBase64String(Hash(password, salt, Iterations)), Iterations);
        store.SetValue("auth", JsonSerializer.Serialize(creds, Json.Options));
        store.SetValue("session_secret", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public bool Verify(string login, string password)
    {
        var c = Load();
        if (c is null)
            return false;
        var actual = Hash(password, Convert.FromBase64String(c.Salt), c.Iterations);
        // сравнение логина тоже без раннего выхода, чтобы по времени ответа нельзя было подобрать логин
        var loginOk = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(login.Trim()), Encoding.UTF8.GetBytes(c.Login));
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(c.Hash)) & loginOk;
    }

    /// <summary>Пароль из файла при первом запуске (его кладёт скрипт установки); файл после этого удаляется.</summary>
    public void ImportInitialPassword(string path)
    {
        if (!File.Exists(path))
            return;
        if (!HasPassword)
            SetCredentials("admin", File.ReadAllText(path).Trim());
        File.Delete(path); // пароль открытым текстом на диске не оставляем в любом случае
    }

    // --- защита от перебора: после MaxFailures неудач с одного IP — пауза Lockout ---

    public TimeSpan? LockedFor(string ip) =>
        _attempts.TryGetValue(ip, out var a) && a.Failures >= MaxFailures && a.Until > DateTime.UtcNow ? a.Until - DateTime.UtcNow : null;

    public void RegisterFailure(string ip) => _attempts.AddOrUpdate(ip, _ => (1, DateTime.UtcNow + Lockout),
        (_, a) => a.Until < DateTime.UtcNow && a.Failures >= MaxFailures ? (1, DateTime.UtcNow + Lockout) : (a.Failures + 1, DateTime.UtcNow + Lockout));

    public void RegisterSuccess(string ip) => _attempts.TryRemove(ip, out _);

    // --- сессии ---

    byte[] Secret()
    {
        if (store.GetValue("session_secret") is { } s)
            return Convert.FromBase64String(s);
        var fresh = RandomNumberGenerator.GetBytes(32);
        store.SetValue("session_secret", Convert.ToBase64String(fresh));
        return fresh;
    }

    static string Sign(byte[] secret, string payload) =>
        Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(payload))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string IssueSession()
    {
        var payload = DateTimeOffset.UtcNow.Add(SessionLifetime).ToUnixTimeSeconds().ToString();
        return payload + "." + Sign(Secret(), payload);
    }

    public bool ValidSession(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Split('.') is not [var payload, var sig] || !long.TryParse(payload, out var exp))
            return false;
        if (exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(Secret(), payload)), Encoding.UTF8.GetBytes(sig));
    }
}
