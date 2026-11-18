using Microsoft.Playwright;
using OokiGrader.SchoolManager.Delivery;

namespace OokiGrader.SchoolManager.Tests;

public sealed class SchoolManagerLoginTests
{
    [EdgeFact]
    public async Task ReloadsLoginPageOnceWhenFirstLoginFormNeverRenders()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge" });
        var page = await browser.NewPageAsync(new() { ServiceWorkers = ServiceWorkerPolicy.Block });
        var shellLoads = 0;
        var loginLoads = 0;
        var otherRequests = 0;
        await page.RouteAsync("**/*", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (route.Request.Method == "GET" && path == "/")
            {
                Interlocked.Increment(ref shellLoads);
                await route.FulfillAsync(new() { ContentType = "text/html",
                    Body = "<script>location.replace('/login')</script>" });
            }
            else if (route.Request.Method == "GET" && path == "/login")
            {
                var attempt = Interlocked.Increment(ref loginLoads);
                await route.FulfillAsync(new() { ContentType = "text/html", Body = attempt == 1
                    ? "<main>Loading</main>"
                    : "<form><input type='text'><input type='password'><button>Login</button></form>" });
            }
            else
            {
                Interlocked.Increment(ref otherRequests);
                await route.AbortAsync();
            }
        });

        await PlaywrightSchoolManagerClient.OpenLoginPageAsync(
            page, new Uri("https://fsm.flens.jp/"), default, timeoutMilliseconds: 2_000);

        Assert.Equal(2, shellLoads);
        Assert.Equal(2, loginLoads);
        Assert.Equal(0, otherRequests);
        Assert.Equal("", await page.Locator("input[type=text]").InputValueAsync());
        Assert.Equal("", await page.Locator("input[type=password]").InputValueAsync());
    }

    [EdgeFact]
    public async Task RejectedLoginStaysOnLoginPageAndNeverOpensRecipientSearch()
    {
        var loginAttempts = 0;
        var otherRequests = 0;
        var error = await Assert.ThrowsAsync<SchoolManagerClientException>(() =>
            PlaywrightSchoolManagerClient.DeliverInBrowserAsync(new(
                new Uri("https://fsm.flens.jp/"), new("test", "test"), "10", "テスト",
                "テスト", "テスト", "test.pdf", "%PDF-1.7"u8.ToArray(), true),
                async browser =>
                {
                    var context = await browser.NewContextAsync(new() { ServiceWorkers = ServiceWorkerPolicy.Block });
                    await context.RouteAsync("**/*", async route =>
                    {
                        switch (new Uri(route.Request.Url).AbsolutePath)
                        {
                            case "/":
                                await route.FulfillAsync(new() { ContentType = "text/html",
                                    Body = "<script>location.replace('/login')</script>" });
                                break;
                            case "/login":
                                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = """
                                    <input type="text"><input type="password">
                                    <button onclick="fetch('/test-login',{method:'POST'}).then(()=>this.textContent='ログイン失敗')">ログイン</button>
                                    """ });
                                break;
                            case "/test-login":
                                Interlocked.Increment(ref loginAttempts);
                                await route.FulfillAsync(new() { Status = 401 });
                                break;
                            default:
                                Interlocked.Increment(ref otherRequests);
                                await route.AbortAsync();
                                break;
                        }
                    });
                    return context;
                }, default));
        Assert.Equal("school_manager_login_failed", error.Code);
        Assert.False(error.OutcomeUnknown);
        Assert.Equal(1, loginAttempts);
        Assert.Equal(0, otherRequests);
    }

    // Explicitly opt in to a read-only check of the vendor's public login UI.
    [PublicLoginFact]
    public async Task PublicLoginPageRendersTheControlsUsedByTheDeliveryClient()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge" });
        await using var context = await browser.NewContextAsync(new()
        {
            Locale = "ja-JP", ServiceWorkers = ServiceWorkerPolicy.Block,
        });
        await context.RouteAsync("**/*", route => route.Request.Method is "GET" or "HEAD"
            ? route.ContinueAsync() : route.AbortAsync());
        var page = await context.NewPageAsync();
        await PlaywrightSchoolManagerClient.OpenLoginPageAsync(page, new Uri("https://fsm.flens.jp/"), default);
        Assert.Equal("https://fsm.flens.jp/login", page.Url.TrimEnd('/'));
        Assert.Equal(1, await page.Locator("input:is(:not([type]), [type='text'], [type='email']):visible").CountAsync());
        Assert.Equal(1, await page.Locator("input[type='password']:visible").CountAsync());
        Assert.Equal(1, await page.GetByRole(AriaRole.Button, new() { Name = "ログイン", Exact = true }).CountAsync());
    }
}

public sealed class PublicLoginFactAttribute : EdgeFactAttribute
{
    public PublicLoginFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OOKI_CHECK_PUBLIC_SCHOOL_MANAGER_LOGIN") != "1")
        {
            Skip = "Set OOKI_CHECK_PUBLIC_SCHOOL_MANAGER_LOGIN=1 to check the public login page.";
        }
    }
}
