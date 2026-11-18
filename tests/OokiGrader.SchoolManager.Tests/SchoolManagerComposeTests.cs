using Microsoft.Playwright;
using OokiGrader.SchoolManager.Delivery;

namespace OokiGrader.SchoolManager.Tests;

public sealed class SchoolManagerComposeTests
{
    private const string UploadPath = "/lms/v1.0/org/message-threads/messages/attached-files";
    private static readonly SchoolManagerDeliveryRequest Request = new(
        new Uri("https://fsm.flens.jp/"), new("dummy", "dummy"), "1234", "試験 太郎", "結果", "結果を送ります。",
        "数学 結果.pdf", "%PDF-1.4\nfixture"u8.ToArray(), false);

    [EdgeFact]
    public async Task UploadWaitsForTheEnabledInputAndExpectedFileInTheActiveForm()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge" });
        var page = await browser.NewPageAsync(new() { ServiceWorkers = ServiceWorkerPolicy.Block });
        var uploaded = new List<byte[]>();
        var uploadsBefore = 0;
        foreach (var scenario in new[] { "delayed", "wrong-name", "extra-file", "cached-file", "invalid-id", "server-error" })
        {
            await page.RouteAsync("**/*", async route =>
            {
                var uri = new Uri(route.Request.Url);
                if (uri.GetLeftPart(UriPartial.Authority) != Request.BaseUri.GetLeftPart(UriPartial.Authority))
                {
                    await route.AbortAsync();
                }
                else if (route.Request.Method == "GET" && uri.AbsolutePath == "/")
                {
                    await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Fixture });
                }
                else if (route.Request.Method == "POST" && uri.AbsolutePath == UploadPath)
                {
                    uploaded.Add(route.Request.PostDataBuffer!);
                    await route.FulfillAsync(new()
                    {
                        ContentType = "application/json",
                        Status = scenario == "server-error" ? 500 : 200,
                        Body = scenario == "invalid-id" ? """{"id":{}}""" : """{"id":110}""",
                    });
                }
                else
                {
                    await route.AbortAsync();
                }
            });
            await page.GotoAsync(Request.BaseUri.AbsoluteUri);
            await page.EvaluateAsync("scenario => document.body.dataset.scenario = scenario", scenario);
            var compose = page.Locator(".message-input-area:visible");
            if (scenario == "cached-file")
            {
                await compose.EvaluateAsync("form => form.insertAdjacentHTML('beforeend','<div class=attached-file-table-row>old</div>')");
            }
            Task UploadAsync() => PlaywrightSchoolManagerClient.AttachPdfAsync(page, compose, Request, default);
            if (scenario == "delayed")
            {
                await compose.Locator("input[type=file]").EvaluateAsync("""
                    input => {
                        input.disabled = true;
                        setTimeout(() => input.disabled = false, 750);
                    }
                    """);
                await UploadAsync();
                Assert.Equal(Request.AttachmentName, await compose.Locator(".attached-file-table-data-1").TextContentAsync());
                Assert.Null(await page.Locator("body").GetAttributeAsync("data-early-change"));
                Assert.Null(await page.Locator("body").GetAttributeAsync("data-hidden-upload"));
            }
            else
            {
                var error = await Assert.ThrowsAsync<SchoolManagerClientException>(UploadAsync);
                Assert.Equal("attachment_upload_failed", error.Code);
                Assert.False(error.OutcomeUnknown);
            }
            if (scenario != "cached-file")
            {
                Assert.Equal(++uploadsBefore, uploaded.Count);
                Assert.Equal(Request.AttachmentBytes, uploaded[^1]);
            }
            else
            {
                Assert.Equal(uploadsBefore, uploaded.Count);
            }
            await page.UnrouteAllAsync(new() { Behavior = UnrouteBehavior.Wait });
        }
    }

    [EdgeFact]
    public async Task FinalResponseRequiresAValidThreadIdAndTreatsServerFailureAsUnconfirmed()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge" });
        var page = await browser.NewPageAsync(new() { ServiceWorkers = ServiceWorkerPolicy.Block });
        await page.RouteAsync("**/*", route => route.FulfillAsync(new() { ContentType = "text/html", Body = "<p>Dummy</p>" }));
        await page.GotoAsync(Request.BaseUri.AbsoluteUri);
        const string endpoint = "/lms/v1.0/org/message-threads";
        var cases = new (int Status, string Json, string? Id, bool Unknown)[]
        {
            (200, """{"id":12345}""", "12345", false), (200, """{"id":"thread-1"}""", "thread-1", false),
            (200, """{"id":true}""", null, true), (200, """{"id":[]}""", null, true),
            (200, """{"id":{}}""", null, true), (200, """{"id":0}""", null, true),
            (200, """{"id":1.1}""", null, true), (200, """{"id":" "}""", null, true),
            (200, """{"id":null}""", null, true), (200, "{}", null, true), (200, "<html>Login</html>", null, true),
            (202, """{"id":12345}""", null, true), (503, "{}", null, true),
            (408, "{}", null, true), (403, "{}", null, false), (422, "{}", null, false),
        };
        foreach (var item in cases)
        {
            await page.RouteAsync("**" + endpoint, route => route.FulfillAsync(new()
            {
                Status = item.Status, ContentType = "application/json", Body = item.Json,
            }));
            var response = await page.RunAndWaitForResponseAsync(
                () => page.EvaluateAsync("url => { fetch(url, {method:'POST'}); }", endpoint),
                r => r.Url == new Uri(Request.BaseUri, endpoint).AbsoluteUri);
            Task<string> ReadAsync() => PlaywrightSchoolManagerClient.ReadSentThreadIdAsync(response, default);
            if (item.Id is not null)
            {
                Assert.Equal(item.Id, await ReadAsync());
            }
            else
            {
                var error = await Assert.ThrowsAsync<SchoolManagerClientException>(ReadAsync);
                Assert.Equal(item.Unknown, error.OutcomeUnknown);
            }
            await page.UnrouteAsync("**" + endpoint);
        }
    }

    private const string Fixture = """
        <div hidden class="message-input-area"><input type="file" class="add-attached-file-input"
            onchange="document.body.dataset.hiddenUpload='yes'"></div>
        <div class="message-input-area">
          <span>添付</span><input hidden type="file" class="add-attached-file-input">
        </div>
        <script>
        document.querySelector('div:not([hidden]) input[type=file]').onchange = async e => {
          if (e.target.disabled) {
            document.body.dataset.earlyChange = 'yes';
            e.target.value = '';
            return;
          }
          const file = e.target.files[0], form = e.target.parentElement;
          const response = await fetch('/lms/v1.0/org/message-threads/messages/attached-files?' +
            new URLSearchParams({fileName:file.name, mimeType:file.type}), {method:'POST', body:file});
          if (!response.ok || !(await response.json()).id) return;
          const scenario = document.body.dataset.scenario;
          setTimeout(() => {
            const add = name => {
              const row = document.createElement('div'), cell = document.createElement('div');
              row.className = 'attached-file-table-row'; cell.className = 'attached-file-table-data-1';
              cell.title = cell.textContent = name; row.append(cell); form.append(row);
            };
            add(scenario === 'wrong-name' ? '別の結果.pdf' : file.name);
            if (scenario === 'extra-file') add('別の結果.pdf');
          }, 200);
        };
        </script>
        """;
}
