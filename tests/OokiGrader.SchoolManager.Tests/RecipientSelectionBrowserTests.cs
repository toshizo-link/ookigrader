using Microsoft.Playwright;
using OokiGrader.SchoolManager.Delivery;

namespace OokiGrader.SchoolManager.Tests;

public sealed class RecipientSelectionBrowserTests
{
    [EdgeFact]
    public Task WaitsForSearchControlAndDelayedResults() => CheckSelectionAsync("""
        search.hidden = true;
        setTimeout(() => search.hidden = false, 350);
        addRow('sameNumber', '1234', '別の 生徒');
        addRow('sameName', '9999', '試験 太郎');
        search.onkeydown = event => {
            if (event.key === 'Enter' && search.value === '1234')
                setTimeout(() => addRow('expected', '１２３４', '試験　太郎'), 1000);
        };
        """);

    [EdgeFact]
    public Task RechecksRowsReorderedAfterIdentityRead() => CheckSelectionAsync("""
        const target = addRow('expected', '1234', '試験 太郎');
        const other = addRow('wrong', '9999', '別の 生徒');
        afterReadingIdentity(other, () => results.prepend(other));
        target.querySelector('ion-button').addEventListener('click', () => {
            if (results.firstElementChild !== other) window.selections.push('list did not reorder');
        });
        """);

    [EdgeFact]
    public Task RejectsNormalizedDuplicateAddedAfterIdentityRead() => CheckSelectionAsync("""
        const target = addRow('expected', '1234', '試験 太郎');
        afterReadingIdentity(target, () => addRow('duplicate', '１２３４', '試験　太郎'));
        """, "recipient_ambiguous");

    [EdgeFact]
    public Task LoadsRemainingSearchResultsBeforeAcceptingAVisibleMatch() => CheckSelectionAsync("""
        const content = results.closest('ion-content');
        content.style.cssText = 'display:block;height:200px;overflow:auto';
        addRow('expected', '1234', '試験 太郎');
        for (let i = 1; i < 50; i++) addRow('other' + i, 'X' + i, '別の 生徒');
        const pager = document.createElement('ion-infinite-scroll');
        pager.textContent = '読み込み';
        pager.disabled = false;
        content.append(pager);
        content.addEventListener('scroll', () => {
            setTimeout(() => {
                addRow('duplicate', '１２３４', '試験　太郎');
                pager.disabled = true;
            }, 100);
        }, { once: true });
        """, "recipient_ambiguous");

    private static async Task CheckSelectionAsync(string setup, string? expectedError = null)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge" });
        var page = await browser.NewPageAsync(new() { ServiceWorkers = ServiceWorkerPolicy.Block });
        await page.RouteAsync("**/*", route => route.FulfillAsync(new()
        {
            ContentType = "text/html; charset=utf-8",
            Body = $$$"""
                <ion-fab-button onclick="history.pushState({}, '', '/message-thread-target-users')">+</ion-fab-button>
                <ion-content><input id="search" type="search"><div id="results"></div></ion-content>
                <script>
                const search = document.querySelector('#search');
                const results = document.querySelector('#results');
                window.selections = [];
                function addRow(id, code, name) {
                    const row = document.createElement('div');
                    row.className = 'table-row';
                    row.innerHTML = '<div class="table-data-1"><div></div></div><div class="table-data-2"></div><ion-button>選択</ion-button>';
                    row.querySelector('.table-data-1 > div').textContent = name;
                    row.querySelector('.table-data-2').textContent = code;
                    row.querySelector('ion-button').onclick = () => {
                        window.selections.push(id);
                        history.pushState({}, '', '/message-thread-members-and-title');
                    };
                    results.append(row);
                    return row;
                }
                function afterReadingIdentity(row, callback) {
                    const code = row.querySelector('.table-data-2');
                    Object.defineProperty(code, 'innerText', { configurable: true, get() {
                        delete code.innerText;
                        queueMicrotask(callback);
                        return code.innerText;
                    }});
                }
                {{{setup}}}
                </script>
                """,
        }));
        Task SelectAsync() => PlaywrightSchoolManagerClient.SelectExactStudentAsync(page, new(
            new Uri("https://fsm.flens.jp/"), new("unused", "unused"), "1234", "試験 太郎",
            "試験", "試験", "test.pdf", "%PDF-1.7"u8.ToArray(), true), default);
        if (expectedError is null)
        {
            await SelectAsync();
            Assert.EndsWith("/message-thread-members-and-title", page.Url);
            Assert.Equal("expected", Assert.Single(await page.EvaluateAsync<string[]>("window.selections")));
        }
        else
        {
            var error = await Assert.ThrowsAsync<SchoolManagerRecipientException>(SelectAsync);
            Assert.Equal(expectedError, error.Code);
            Assert.Empty(await page.EvaluateAsync<string[]>("window.selections"));
        }
    }
}
