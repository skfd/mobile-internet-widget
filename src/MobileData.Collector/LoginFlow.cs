using Microsoft.Playwright;

namespace MobileData.Collector;

/// <summary>
/// Best-effort automation of the carrier login form. Deliberately forgiving:
/// carriers change their markup and sometimes split username/password across two
/// steps, so every step is wrapped and failures fall back to manual sign-in.
/// </summary>
internal static class LoginFlow
{
    private static readonly string[] UsernameSelectors =
    {
        "input[type=email]",
        "input[name=username]",
        "input[id*=username i]",
        "input[autocomplete=username]",
        "input[name*=email i]",
        "input[type=text]"
    };

    private const string PasswordSelector = "input[type=password]";

    /// <summary>True once the dashboard's usage content is present.</summary>
    public static async Task<bool> WaitForUsageAsync(IPage page, int timeoutMs)
    {
        try
        {
            await page.WaitForFunctionAsync(
                "() => /Remaining data|remaining in bill cycle/i.test(document.body.innerText)",
                null, new PageWaitForFunctionOptions { Timeout = timeoutMs });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Fill and submit the login form. Handles single- and two-step layouts.</summary>
    public static async Task TryAutoLoginAsync(IPage page, CarrierConfig config, Credentials creds)
    {
        try
        {
            var username = await FirstVisibleAsync(page, UsernameSelectors);
            if (username is not null)
            {
                await username.FillAsync(creds.Username);

                // Two-step forms: no password field yet — advance first.
                if (!await IsVisibleAsync(page, PasswordSelector))
                {
                    await ClickSubmitAsync(page);
                    try
                    {
                        await page.WaitForSelectorAsync(PasswordSelector,
                            new() { Timeout = 10000, State = WaitForSelectorState.Visible });
                    }
                    catch { /* fall through; maybe still single-step */ }
                }
            }

            var password = await FirstVisibleAsync(page, new[] { PasswordSelector });
            if (password is not null)
            {
                await password.FillAsync(creds.Password);
                await ClickSubmitAsync(page);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(auto-login could not complete: {ex.Message})");
        }
    }

    private static async Task<ILocator?> FirstVisibleAsync(IPage page, string[] selectors)
    {
        foreach (var sel in selectors)
        {
            var loc = page.Locator(sel).First;
            try
            {
                if (await loc.IsVisibleAsync()) return loc;
            }
            catch { /* selector not present */ }
        }
        return null;
    }

    private static async Task<bool> IsVisibleAsync(IPage page, string selector)
    {
        try { return await page.Locator(selector).First.IsVisibleAsync(); }
        catch { return false; }
    }

    private static readonly string[] SubmitSelectors =
    {
        "button:has-text('Log in')",
        "button:has-text('Sign in')",
        "button:has-text('Continue')",
        "button:has-text('Next')",
        "button:has-text('Submit')",
        "button[type=submit]",
        "input[type=submit]"
    };

    private static async Task ClickSubmitAsync(IPage page)
    {
        // Playwright's :has-text is case-insensitive substring matching.
        foreach (var sel in SubmitSelectors)
        {
            try
            {
                var loc = page.Locator(sel).First;
                if (await loc.IsVisibleAsync())
                {
                    await loc.ClickAsync();
                    return;
                }
            }
            catch { /* try next */ }
        }

        try { await page.Keyboard.PressAsync("Enter"); } catch { /* nothing else to do */ }
    }
}
