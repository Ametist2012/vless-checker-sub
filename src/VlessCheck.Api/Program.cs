using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Net.Http.Headers;
using VlessCheck.Core;

// API панели и выдача подписок. Слушает только localhost — наружу его выставляет nginx.
// Панель и API — под входом (сессия в cookie); подписки — по секретному токену в ссылке.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:8787");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    o.SerializerOptions.Encoder = Json.Options.Encoder;
});
var store = new Store(Environment.GetEnvironmentVariable("VLESSCHECK_DATA") ?? "data");
builder.Services.AddSingleton(store);

var app = builder.Build();
var happHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

var auth = new Auth(store);
auth.ImportInitialPassword(Path.Combine(store.DataDir, "initial-password"));
if (!auth.HasPassword)
{
    // локальный запуск без скрипта установки: пароль генерируется и показывается один раз
    var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(15)).Replace('+', 'x').Replace('/', 'y');
    auth.SetCredentials("admin", generated);
    app.Logger.LogWarning("создан вход в панель: admin / {Password} — смените его в настройках", generated);
}

const string SessionCookie = "vc_session";
string ClientIp(HttpContext ctx) => ctx.Request.Headers["X-Real-IP"].FirstOrDefault() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
bool Authenticated(HttpContext ctx) => auth.ValidSession(ctx.Request.Cookies[SessionCookie]);

void SetSession(HttpContext ctx) => ctx.Response.Cookies.Append(SessionCookie, auth.IssueSession(), new CookieOptions
{
    HttpOnly = true,
    SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
    Secure = ctx.Request.IsHttps || ctx.Request.Headers["X-Forwarded-Proto"] == "https",
    MaxAge = Auth.SessionLifetime,
    Path = "/",
});

// Адреса (nginx отдаёт сюда всё под /checker/ без этого префикса):
//   /sub/<токен>/<группа>   — подписки, не зависят от адреса панели;
//   /<секрет>/…             — панель и API; секрет меняется в настройках и сразу применяется;
//   всё остальное           — 404, чтобы по адресу нельзя было понять, что здесь есть.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "/";
    if (path.StartsWith("/sub/"))
    {
        await next();
        return;
    }
    var secret = "/" + store.Settings().PanelPath;
    if (path == secret)
    {
        ctx.Response.Redirect(secret.TrimStart('/') + "/"); // относительный редирект — работает под любым префиксом nginx
        return;
    }
    if (!path.StartsWith(secret + "/"))
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    ctx.Request.PathBase = ctx.Request.PathBase.Add(secret);
    ctx.Request.Path = path[secret.Length..];
    await next();
});

app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    // мутации только с заголовком X-Panel: форма с чужого сайта его поставить не может (защита от CSRF)
    if (path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers["X-Panel"] != "1")
    {
        await Results.Json(new { error = "нет заголовка X-Panel" }, statusCode: 403).ExecuteAsync(ctx);
        return;
    }
    var open = path.StartsWithSegments("/api/session") || path.StartsWithSegments("/api/login");
    if (path.StartsWithSegments("/api") && !open && !Authenticated(ctx))
    {
        await Results.Json(new { error = "нужно войти" }, statusCode: 401).ExecuteAsync(ctx);
        return;
    }
    try
    {
        await next();
    }
    catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException or JsonException)
    {
        var code = e is KeyNotFoundException ? 404 : 400;
        await Results.Json(new { error = e.Message }, statusCode: code).ExecuteAsync(ctx);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl =
        c.File.Name == "index.html" ? "no-cache" : "public, max-age=31536000, immutable",
});

Group RequireGroup(string name) => store.Groups().FirstOrDefault(g => g.Name == name) ?? throw new KeyNotFoundException("нет такой группы");
object State() => new { groups = store.Groups(), settings = store.Settings() };

// маршрутизация после подмены пути — иначе эндпоинты сопоставились бы с исходным адресом
app.UseRouting();

var api = app.MapGroup("/api");

api.MapGet("/session", (HttpContext ctx) => new { authenticated = Authenticated(ctx), login = Authenticated(ctx) ? auth.Login : null });

api.MapPost("/login", (HttpContext ctx, JsonObject body) =>
{
    var ip = ClientIp(ctx);
    if (auth.LockedFor(ip) is { } wait)
        return Results.Json(new { error = $"слишком много попыток — подождите {Math.Ceiling(wait.TotalSeconds)} с" }, statusCode: 429);
    var login = body["login"]?.GetValue<string>() ?? "";
    var password = body["password"]?.GetValue<string>() ?? "";
    if (!auth.Verify(login, password))
    {
        auth.RegisterFailure(ip);
        app.Logger.LogWarning("неудачный вход в панель с {Ip}", ip);
        return Results.Json(new { error = "неверный логин или пароль" }, statusCode: 401);
    }
    auth.RegisterSuccess(ip);
    SetSession(ctx);
    return Results.Ok(new { authenticated = true, login = auth.Login });
});

api.MapPost("/logout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete(SessionCookie, new CookieOptions { Path = "/" });
    return new { authenticated = false };
});

api.MapPost("/credentials", (HttpContext ctx, JsonObject body) =>
{
    if (!auth.Verify(auth.Login, body["current_password"]?.GetValue<string>() ?? ""))
        throw new ArgumentException("текущий пароль неверный");
    auth.SetCredentials(body["login"]?.GetValue<string>() ?? auth.Login, body["new_password"]?.GetValue<string>() ?? "");
    SetSession(ctx); // ключ сессий сменился: остальные устройства вышли, это — остаётся
    return new { authenticated = true, login = auth.Login };
});

api.MapGet("/state", State);

api.MapGet("/status", () => new
{
    progress = Json.Load<JsonNode>(ProgressState.PathIn(store.DataDir)) ?? new JsonObject(),
    groups = store.Groups().ToDictionary(g => g.Name,
        g => Json.Load<JsonNode>(Path.Combine(store.GroupDir(g.Name), "status.json")) ?? new JsonObject()),
});

api.MapPatch("/settings", (JsonObject body) =>
{
    store.UpdateSettings(body);
    return State();
});

api.MapPost("/groups", (JsonObject body) =>
{
    store.AddGroup(body["name"]?.GetValue<string>().Trim() ?? "", body["title"]?.GetValue<string>().Trim() ?? "");
    return State();
});

api.MapPatch("/groups/{name}", (string name, JsonObject body) =>
{
    RequireGroup(name);
    store.UpdateGroup(name, body);
    return State();
});

api.MapDelete("/groups/{name}", (string name) =>
{
    RequireGroup(name);
    store.DeleteGroup(name);
    if (Store.GroupName().IsMatch(name) && Directory.Exists(store.GroupDir(name)))
        Directory.Delete(store.GroupDir(name), recursive: true);
    return State();
});

api.MapPost("/groups/{name}/subscriptions", (string name, JsonObject body) =>
{
    var urls = body["urls"]?.AsArray().Select(u => u?.GetValue<string>() ?? "") ?? [];
    var added = store.AddSubscriptions(name, urls);
    return new { groups = store.Groups(), settings = store.Settings(), added };
});

api.MapDelete("/subscriptions/{id:long}", (long id) =>
{
    store.DeleteSubscription(id);
    return State();
});

api.MapPost("/run", (JsonObject body) =>
{
    var group = body["group"]?.GetValue<string>() ?? "*";
    if (!store.Settings().Enabled)
        throw new InvalidOperationException("сервис выключен — сначала включите его");
    if (group != "*")
        RequireGroup(group);
    store.RequestRun(group);
    return new { ok = true };
});

api.MapPost("/groups/{name}/rotate-token", (string name) =>
{
    RequireGroup(name);
    store.RotateGroupToken(name);
    return State();
});

// зашифрованная ссылка happ://crypt5/... через официальный API Happ: в приложении адрес подписки не виден
api.MapPost("/groups/{name}/happ-link", async (string name, JsonObject body) =>
{
    var group = RequireGroup(name);
    var baseUrl = body["base"]?.GetValue<string>() ?? "";
    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Query != "" || u.Fragment != "")
        throw new ArgumentException("Happ-ссылку можно сделать только для адреса https://");
    var url = $"{baseUrl.TrimEnd('/')}/sub/{group.Token}/{name}";
    string link;
    try
    {
        using var resp = await happHttp.PostAsJsonAsync("https://crypto.happ.su/api-v2.php", new { url });
        resp.EnsureSuccessStatusCode();
        link = (await resp.Content.ReadFromJsonAsync<JsonObject>())?["encrypted_link"]?.GetValue<string>() ?? "";
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
    {
        return Results.Json(new { error = $"не удалось получить ссылку от Happ: {e.Message}" }, statusCode: 502);
    }
    if (!link.StartsWith("happ://"))
        return Results.Json(new { error = "Happ API вернул неожиданный ответ" }, statusCode: 502);
    store.SetHappLink(name, url, link);
    return Results.Ok(State());
});

api.MapFallback(() => Results.Json(new { error = "нет такого метода API" }, statusCode: 404));

// подписка: /sub/<token>/<group> (base64) и /sub/<token>/<group>/raw
IResult Subscription(HttpContext ctx, string token, string group, bool raw)
{
    var s = store.Settings();
    var g = store.Groups().FirstOrDefault(x => x.Name == group);
    if (g is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(g.Token)))
        return Results.NotFound();
    if (!s.Enabled)
        return Results.Text("subscription is disabled\n", statusCode: 503);
    if (s.HappOnly && !ctx.Request.Headers.UserAgent.ToString().Contains("happ", StringComparison.OrdinalIgnoreCase))
        return Results.Text("open this subscription in Happ\n", statusCode: 403);
    var file = Path.Combine(store.GroupDir(group), "sub_raw.txt");
    if (!File.Exists(file))
        return Results.Text("first check is still running\n", statusCode: 503);
    var body = File.ReadAllText(file);
    // маршрутизация Happ: первой строкой тела (работает при любом размере) и, если коротка, ещё заголовком
    if (s.RoutingInSubscription && s.RoutingLink != "")
    {
        body = s.RoutingLink + "\n" + body;
        if (s.RoutingLink.Length <= HappRouting.MaxHeaderLength)
            ctx.Response.Headers["routing"] = s.RoutingLink;
    }
    ctx.Response.Headers["profile-title"] = "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(g.Title));
    ctx.Response.Headers["profile-update-interval"] = "1";
    ctx.Response.Headers[HeaderNames.CacheControl] = "no-store";
    return Results.Text(raw ? body : Convert.ToBase64String(Encoding.UTF8.GetBytes(body)), "text/plain; charset=utf-8");
}

app.MapGet("/sub/{token}/{group}", (HttpContext ctx, string token, string group) => Subscription(ctx, token, group, false));
app.MapGet("/sub/{token}/{group}/raw", (HttpContext ctx, string token, string group) => Subscription(ctx, token, group, true));

app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});

app.Run();
