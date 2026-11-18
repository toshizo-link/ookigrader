using System.Text.Json;
using Microsoft.Playwright;

namespace OokiGrader.SchoolManager.Delivery;

public sealed record SchoolManagerCredentials(string Username, string Password);

public sealed record SchoolManagerDeliveryRequest(
    Uri BaseUri,
    SchoolManagerCredentials Credentials,
    string StudentNumber,
    string StudentDisplayName,
    string Title,
    string Body,
    string AttachmentName,
    byte[] AttachmentBytes,
    bool DryRun);

public sealed record SchoolManagerDeliveryResult(bool DryRun, string? ThreadId);

public sealed class SchoolManagerClientException(
    string code,
    string message,
    bool outcomeUnknown = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
}

public interface ISchoolManagerClient
{
    Task<SchoolManagerDeliveryResult> DeliverAsync(
        SchoolManagerDeliveryRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class PlaywrightSchoolManagerClient : ISchoolManagerClient
{
    private const int MaximumAttachmentBytes = 25 * 1024 * 1024;

    public Task<SchoolManagerDeliveryResult> DeliverAsync(
        SchoolManagerDeliveryRequest request,
        CancellationToken cancellationToken = default) =>
        DeliverInBrowserAsync(request, browser => browser.NewContextAsync(new BrowserNewContextOptions
        {
            AcceptDownloads = false,
            IgnoreHTTPSErrors = false,
            Locale = "ja-JP",
        }), cancellationToken);

    internal static async Task<SchoolManagerDeliveryResult> DeliverInBrowserAsync(
        SchoolManagerDeliveryRequest request,
        Func<IBrowser, Task<IBrowserContext>> openContext,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        var finalRequestStarted = false;
        var browserErrorCode = "school_manager_browser_start_failed";
        try
        {
            using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions
                {
                    Channel = "msedge",
                    Headless = true,
                    Timeout = 30_000,
                }).ConfigureAwait(false);
            var context = await openContext(browser).ConfigureAwait(false);
            var page = await context.NewPageAsync().ConfigureAwait(false);
            browserErrorCode = "school_manager_login_page_failed";
            await OpenLoginPageAsync(page, request.BaseUri, cancellationToken).ConfigureAwait(false);
            browserErrorCode = "school_manager_login_failed";
            await SubmitLoginAsync(page, request, cancellationToken).ConfigureAwait(false);
            browserErrorCode = "school_manager_recipient_page_failed";
            await SelectExactStudentAsync(page, request, cancellationToken)
                .ConfigureAwait(false);
            browserErrorCode = "school_manager_guardian_page_failed";
            await VerifyGuardianOnlyAndSetTitleAsync(page, request.Title)
                .ConfigureAwait(false);

            if (request.DryRun)
            {
                return new SchoolManagerDeliveryResult(true, null);
            }

            browserErrorCode = "school_manager_compose_page_failed";
            await page.GetByRole(AriaRole.Button, new() { Name = "次へ" })
                .ClickAsync(new LocatorClickOptions { Timeout = 15_000 })
                .ConfigureAwait(false);
            await page.WaitForURLAsync(
                    url => IsSchoolManagerOrigin(url, request.BaseUri)
                        && new Uri(url).AbsolutePath.TrimEnd('/') == "/message-thread-details/0/create",
                    new PageWaitForURLOptions { Timeout = 15_000 })
                .ConfigureAwait(false);

            var compose = page.Locator(".message-input-area:visible");
            await compose.WaitForAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);
            browserErrorCode = "school_manager_attachment_failed";
            await AttachPdfAsync(page, compose, request, cancellationToken)
                .ConfigureAwait(false);

            browserErrorCode = "school_manager_message_prepare_failed";
            await compose.Locator("ion-textarea[placeholder='メッセージを入力'] textarea:visible")
                .FillAsync(request.Body, new() { Timeout = 15_000 }).ConfigureAwait(false);
            // The normal send control is icon-only; the labelled alternative also resolves the thread.
            var send = await GetSendControlAsync(compose).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            finalRequestStarted = true;
            var sendResponse = await page.RunAndWaitForResponseAsync(
                    () => send.ClickAsync(new LocatorClickOptions { Timeout = 15_000 }),
                    response => IsApiPost(response, request.BaseUri, "/org/message-threads"),
                    new PageRunAndWaitForResponseOptions { Timeout = 30_000 })
                .ConfigureAwait(false);
            var threadId = await ReadSentThreadIdAsync(sendResponse, cancellationToken).ConfigureAwait(false);
            return new SchoolManagerDeliveryResult(false, threadId);
        }
        catch (SchoolManagerClientException)
        {
            throw;
        }
        catch (SchoolManagerRecipientException exception)
        {
            throw new SchoolManagerClientException(
                exception.Code,
                "School Manager did not provide one safe guardian recipient.",
                outcomeUnknown: false,
                exception);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            throw new SchoolManagerClientException(
                finalRequestStarted
                    ? "message_send_outcome_unknown"
                    : browserErrorCode,
                finalRequestStarted
                    ? "The browser lost confirmation after starting the final send request."
                    : "The School Manager browser operation failed before sending.",
                finalRequestStarted,
                exception);
        }
    }

    private static bool IsApiPost(IResponse response, Uri baseUri, string path) =>
        response.Request.Method == "POST"
        && IsSchoolManagerOrigin(response.Url, baseUri)
        && new Uri(response.Url).AbsolutePath.TrimEnd('/') == "/lms/v1.0" + path;

    internal static async Task AttachPdfAsync(
        IPage page, ILocator compose, SchoolManagerDeliveryRequest request, CancellationToken cancellationToken)
    {
        var rows = compose.Locator(".attached-file-table-row");
        if (await rows.CountAsync().ConfigureAwait(false) != 0)
        {
            throw new SchoolManagerClientException("attachment_upload_failed", "The compose form already contains an attachment.");
        }

        // The hidden input becomes enabled after the compose form initializes its attachment list.
        // School Manager discards earlier change events without starting an upload.
        var fileInput = compose.Locator("input.add-attached-file-input[type='file']:enabled");
        var upload = await page.RunAndWaitForResponseAsync(
            () => fileInput.SetInputFilesAsync(new FilePayload
            {
                Name = request.AttachmentName,
                MimeType = "application/pdf",
                Buffer = request.AttachmentBytes,
            }, new() { Timeout = 15_000 }),
            response => IsApiPost(response, request.BaseUri, "/org/message-threads/messages/attached-files"),
            new() { Timeout = 30_000 }).ConfigureAwait(false);
        if (upload.Status != 200 || await ReadResponseIdAsync(upload, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new SchoolManagerClientException("attachment_upload_failed", "School Manager did not confirm the PDF upload.");
        }

        // The SPA only adds the filename to this table after recording the returned attachment ID.
        await rows.First.WaitForAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);
        if (await rows.CountAsync().ConfigureAwait(false) != 1 ||
            await rows.Locator(".attached-file-table-data-1").GetAttributeAsync("title", new() { Timeout = 15_000 })
                .ConfigureAwait(false) != request.AttachmentName)
        {
            throw new SchoolManagerClientException("attachment_upload_failed", "The compose form did not confirm the expected PDF.");
        }
    }

    internal static async Task<string> ReadSentThreadIdAsync(IResponse response, CancellationToken cancellationToken)
    {
        if (response.Status != 200)
        {
            if (response.Status is 400 or 401 or 403 or 404 or 413 or 422)
            {
                throw new SchoolManagerClientException("message_send_rejected", "School Manager rejected the guardian message.");
            }
            throw new SchoolManagerClientException(
                "message_send_outcome_unknown", "School Manager did not confirm whether the message was sent.", outcomeUnknown: true);
        }

        return await ReadResponseIdAsync(response, cancellationToken).ConfigureAwait(false)
            ?? throw new SchoolManagerClientException(
                "message_send_response_invalid", "School Manager did not return a valid message thread identifier.", outcomeUnknown: true);
    }

    private static async Task<string?> ReadResponseIdAsync(IResponse response, CancellationToken cancellationToken)
    {
        JsonElement json;
        try
        {
            json = await response.JsonAsync<JsonElement>().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }

        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("id", out var id))
        {
            return null;
        }
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) && number > 0)
        {
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (id.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var value = id.GetString();
        return value is { Length: > 0 and <= 200 } && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl)
            ? value : null;
    }

    internal static async Task<ILocator> GetSendControlAsync(ILocator compose)
    {
        var send = compose.Locator("ion-button.send-message-button:visible");
        // Ionic may still be enabling the button after the body and attachment have rendered.
        try
        {
            await send.ClickAsync(new() { Trial = true, Timeout = 15_000 }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            throw new SchoolManagerClientException("send_control_invalid",
                "The School Manager send control was unavailable or ambiguous.", innerException: exception);
        }

        return send;
    }

    internal static async Task OpenLoginPageAsync(
        IPage page,
        Uri baseUri,
        CancellationToken cancellationToken,
        int timeoutMilliseconds = 30_000)
    {
        try
        {
            await LoadLoginFormAsync().ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A stalled SPA bootstrap can recover on a fresh navigation before credentials are entered.
            await LoadLoginFormAsync().ConfigureAwait(false);
        }

        async Task LoadLoginFormAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            await page.GotoAsync(baseUri.AbsoluteUri, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = timeoutMilliseconds,
            }).ConfigureAwait(false);
            // The SPA serves a shell at /, navigates to /login, then renders its inputs.
            await page.WaitForURLAsync(url => IsSchoolManagerOrigin(url, baseUri) && IsLoginRoute(url),
                new PageWaitForURLOptions { Timeout = timeoutMilliseconds }).ConfigureAwait(false);
            await page.Locator("input[type='password']:visible").WaitForAsync(
                new LocatorWaitForOptions { Timeout = timeoutMilliseconds }).ConfigureAwait(false);
        }
    }

    private static async Task SubmitLoginAsync(
        IPage page, SchoolManagerDeliveryRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSchoolManagerOrigin(page.Url, request.BaseUri) || !IsLoginRoute(page.Url))
        {
            throw new SchoolManagerClientException(
                "login_origin_changed",
                "School Manager redirected to an unexpected origin.");
        }

        var username = page.Locator("input:is(:not([type]), [type='text'], [type='email']):visible");
        var password = page.Locator("input[type='password']:visible");
        var login = page.GetByRole(AriaRole.Button, new() { Name = "ログイン", Exact = true });
        await username.FillAsync(request.Credentials.Username).ConfigureAwait(false);
        await password.FillAsync(request.Credentials.Password).ConfigureAwait(false);
        await login.ClickAsync(new LocatorClickOptions { Timeout = 15_000 }).ConfigureAwait(false);
        await page.WaitForURLAsync(url => IsSchoolManagerOrigin(url, request.BaseUri)
                && !IsLoginRoute(url), new PageWaitForURLOptions { Timeout = 30_000 })
            .ConfigureAwait(false);
    }

    private static bool IsSchoolManagerOrigin(string url, Uri baseUri) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Port == baseUri.Port
        && uri.UserInfo.Length == 0 && string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase);

    private static bool IsLoginRoute(string url) =>
        new Uri(url).AbsolutePath.TrimEnd('/').Equals("/login", StringComparison.OrdinalIgnoreCase);

    internal static async Task SelectExactStudentAsync(
        IPage page,
        SchoolManagerDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await page.GotoAsync(
                new Uri(request.BaseUri, "message-threads").AbsoluteUri,
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000,
                })
            .ConfigureAwait(false);
        // The add action initializes the compose state. Direct navigation to the
        // recipient route renders the list but leaves selecting a student inert.
        var add = page.Locator("app-message-threads:not(.ion-page-hidden):not([hidden]) ion-fab-button:visible");
        await add.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 })
            .ConfigureAwait(false);
        if (await add.CountAsync().ConfigureAwait(false) != 1)
        {
            throw new SchoolManagerClientException(
                "message_add_control_invalid",
                "The School Manager new-message control was unavailable or ambiguous.");
        }

        await add.ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => IsSchoolManagerOrigin(url, request.BaseUri)
                && new Uri(url).AbsolutePath.TrimEnd('/') == "/message-thread-target-users",
                new PageWaitForURLOptions { Timeout = 15_000 })
            .ConfigureAwait(false);
        // Ionic updates the URL before mounting this page and hiding the thread list.
        // Both pages have a search box and table rows during the transition.
        var recipients = page.Locator("app-message-thread-target-users:not(.ion-page-hidden):not([hidden])");
        var search = recipients.GetByRole(AriaRole.Searchbox);
        await search.First.WaitForAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);
        if (await search.CountAsync().ConfigureAwait(false) != 1)
        {
            throw new SchoolManagerClientException(
                "recipient_search_invalid",
                "The School Manager student search control was unavailable.");
        }

        if (string.IsNullOrWhiteSpace(request.StudentNumber)
            || string.IsNullOrWhiteSpace(request.StudentDisplayName))
        {
            throw new SchoolManagerRecipientException("recipient_not_found",
                "A student number and display name are required to select a recipient.");
        }

        await search.FillAsync(request.StudentNumber).ConfigureAwait(false);
        await search.PressAsync("Enter").ConfigureAwait(false);
        var rows = recipients.Locator(".table-row:visible");
        const string readIdentities = """
            rows => {
                const pager = rows[0]?.closest('ion-content')?.querySelector('ion-infinite-scroll');
                if (pager && pager.disabled !== true) {
                    // School Manager progressively loads longer lists on scroll.
                    // A match on a partial page cannot establish uniqueness.
                    pager.scrollIntoView({ block: 'end' });
                    return [];
                }
                return rows.map(row => [
                    row.querySelector(':scope > .table-data-2')?.innerText ?? '',
                    row.querySelector(':scope > .table-data-1 > div')?.innerText ?? ''
                ]);
            }
            """;
        // Ionic can replace or reorder rows after the search or reuse an existing
        // node for a different student. Recheck the complete matched snapshot and
        // select in the same browser event so an old index can never select a new row.
        const string selectIfUnchanged = $$"""
            (rows, { identities, index }) => {
                const current = ({{readIdentities}})(rows);
                if (current.length !== identities.length || current.some(([code, name], i) =>
                    code !== identities[i][0] || name !== identities[i][1])) return false;
                const buttons = rows[index].querySelectorAll('ion-button');
                if (buttons.length !== 1) return false;
                const button = buttons[0];
                if (button.disabled || button.matches('[disabled], [aria-disabled="true"]')
                    || button.closest('[inert]') || !button.checkVisibility({ checkVisibilityCSS: true })
                    || getComputedStyle(button).pointerEvents === 'none') return false;
                button.click();
                return true;
            }
            """;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(15);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identities = await rows.EvaluateAllAsync<string[][]>(readIdentities).ConfigureAwait(false);
            var candidates = identities.Select(identity => new SchoolManagerStudentCandidate(
                string.Empty, string.Empty, identity[0], identity[1])).ToArray();
            try
            {
                var match = SchoolManagerRecipientMatcher.Match(request.StudentNumber, request.StudentDisplayName, candidates);
                cancellationToken.ThrowIfCancellationRequested();
                if (await rows.EvaluateAllAsync<bool>(selectIfUnchanged,
                    new { identities, index = Array.IndexOf(candidates, match) }).ConfigureAwait(false))
                {
                    break;
                }
                if (elapsed.Elapsed >= timeout)
                {
                    throw new SchoolManagerClientException("recipient_search_invalid",
                        "The matched School Manager recipient could not be selected.");
                }
            }
            catch (SchoolManagerRecipientException error) when (error.Code == "recipient_not_found"
                && elapsed.Elapsed < timeout)
            {
                // The search result table is still loading.
            }
            await Task.Delay(125, cancellationToken).ConfigureAwait(false);
        }

        await page.WaitForURLAsync(
                url => IsSchoolManagerOrigin(url, request.BaseUri)
                    && new Uri(url).AbsolutePath.TrimEnd('/') == "/message-thread-members-and-title",
                new PageWaitForURLOptions { Timeout = 15_000 })
            .ConfigureAwait(false);
    }

    internal static async Task VerifyGuardianOnlyAndSetTitleAsync(
        IPage page,
        string title)
    {
        // Ionic retains previous pages in the DOM. Inspect only controls on
        // the current page, and allow the asynchronous recipient form to render.
        ILocator Control(string selector) =>
            page.Locator(selector + ":not(.ion-page-hidden *):not([hidden] *)");
        var parentsAll = Control(
            "ion-checkbox[formcontrolname='parentsAll']");
        var parents = Control(
            "[formarrayname='parents'] ion-checkbox[formcontrolname='parent']");
        // With no student-app account, Ionic renders disabled placeholders without
        // formcontrolname. Still require both controls and verify they are unchecked.
        var studentsAll = Control(
            ".captioned-checkbox:has(> .checkbox-caption:text-is('生徒')) > ion-checkbox");
        var students = Control(
            "[formarrayname='students'] ion-checkbox");
        var titleInput = Control("ion-textarea[formcontrolname='title'] textarea");
        await titleInput.First.WaitForAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);
        try
        {
            await parents.First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 })
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new SchoolManagerClientException("guardian_controls_missing",
                "The selected student has no verifiable guardian recipients.", innerException: exception);
        }
        var parentCount = await parents.CountAsync().ConfigureAwait(false);
        if (await parentsAll.CountAsync().ConfigureAwait(false) != 1
            || parentCount == 0
            || await studentsAll.CountAsync().ConfigureAwait(false) != 1
            || await students.CountAsync().ConfigureAwait(false) != 1)
        {
            throw new SchoolManagerClientException(
                "guardian_controls_missing",
                "The selected student has no verifiable guardian recipients.");
        }

        var allParentsChecked = await parentsAll
            .EvaluateAsync<bool>("element => Boolean(element.checked)")
            .ConfigureAwait(false);
        var allStudentsChecked = await studentsAll
            .EvaluateAsync<bool>("element => Boolean(element.checked)")
            .ConfigureAwait(false);
        var studentChecked = await students
            .EvaluateAsync<bool>("element => Boolean(element.checked)")
            .ConfigureAwait(false);
        var allStudentsVerifiable = await IsStudentControlVerifiableAsync(studentsAll)
            .ConfigureAwait(false);
        var studentVerifiable = await IsStudentControlVerifiableAsync(students)
            .ConfigureAwait(false);
        var everyGuardianChecked = true;
        for (var index = 0; index < parentCount; index++)
        {
            everyGuardianChecked &= await parents.Nth(index)
                .EvaluateAsync<bool>("element => Boolean(element.checked)")
                .ConfigureAwait(false);
        }

        if (!allParentsChecked
            || !everyGuardianChecked
            || allStudentsChecked
            || studentChecked
            || !allStudentsVerifiable
            || !studentVerifiable)
        {
            throw new SchoolManagerClientException(
                "guardian_only_selection_invalid",
                "School Manager did not present a guardian-only recipient selection.");
        }

        if (await titleInput.CountAsync().ConfigureAwait(false) != 1)
        {
            throw new SchoolManagerClientException(
                "message_title_invalid",
                "The School Manager message title field was unavailable.");
        }

        await titleInput.FillAsync(title).ConfigureAwait(false);
        var next = page.GetByRole(AriaRole.Button, new() { Name = "次へ", Exact = true });
        try
        {
            await next.ClickAsync(new() { Trial = true, Timeout = 15_000 }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            throw new SchoolManagerClientException(
                "guardian_selection_not_ready",
                "The guardian-only recipient selection could not be validated.", innerException: exception);
        }
    }

    private static Task<bool> IsStudentControlVerifiableAsync(ILocator control) =>
        control.EvaluateAsync<bool>("""
            element => {
                const name = element.getAttribute('formcontrolname');
                if (name === 'studentsAll' || name === 'student') {
                    // School Manager sets the individual Angular form value to
                    // null for an unchecked student and an object for a selected one.
                    return (element.checked === false || element.checked === null)
                        && element.getAttribute('aria-checked') !== 'true';
                }
                return name === null
                    && element.getAttribute('aria-checked') === 'false'
                    && element.classList.contains('checkbox-disabled')
                    && element.querySelector('input.aux-input[disabled]') !== null;
            }
            """);

    private static void ValidateRequest(SchoolManagerDeliveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BaseUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(
                request.BaseUri.Host,
                "fsm.flens.jp",
                StringComparison.OrdinalIgnoreCase)
            || request.BaseUri.Port != 443)
        {
            throw new ArgumentException(
                "School Manager delivery is restricted to https://fsm.flens.jp/.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Credentials.Username)
            || string.IsNullOrEmpty(request.Credentials.Password)
            || string.IsNullOrWhiteSpace(request.StudentNumber)
            || string.IsNullOrWhiteSpace(request.StudentDisplayName)
            || string.IsNullOrWhiteSpace(request.Title)
            || string.IsNullOrWhiteSpace(request.Body)
            || string.IsNullOrWhiteSpace(request.AttachmentName)
            || request.AttachmentBytes.Length is <= 4 or > MaximumAttachmentBytes
            || request.AttachmentBytes[0] != (byte)'%'
            || request.AttachmentBytes[1] != (byte)'P'
            || request.AttachmentBytes[2] != (byte)'D'
            || request.AttachmentBytes[3] != (byte)'F')
        {
            throw new ArgumentException(
                "The School Manager delivery request is incomplete or unsafe.",
                nameof(request));
        }
    }
}
