using GreatCrowler;
using Microsoft.Playwright;
using System.Diagnostics;
using System.Text.RegularExpressions;
using IBrowser = Microsoft.Playwright.IBrowser;

internal static class EmailSearcher {
    private const string emailPattern = @"(?:[a-z0-9!#$%&'*+\/=?^_`{|}~-]+(?:\.[a-z0-9!#$%&'*+\/=?^_`{|}~-]+)*|""""(?:[\x01-\x08\x0b\x0c\x0e-\x1f\x21\x23-\x5b\x5d-\x7f]|\\[\x01-\x09\x0b\x0c\x0e-\x7f])*"")[\s]*@[\s]*(?:(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])?|\[(?:(?:(2(5[0-5]|[0-4][0-9])|1[0-9][0-9]|[1-9]?[0-9]))\.){3}(?:(2(5[0-5]|[0-4][0-9])|1[0-9][0-9]|[1-9]?[0-9])|[a-z0-9-]*[a-z0-9]:(?:[\x01-\x08\x0b\x0c\x0e-\x1f\x21-\x5a\x53-\x7f]|\\[\x01-\x09\x0b\x0c\x0e-\x7f])+)\])";
    private static readonly Regex emailRegex = new(emailPattern);
    static readonly object lockObject = new();
    static int processed = 0;
    public static async Task<string[]> Search(string[] domains, Action<double> updateProgress) {
        using IPlaywright pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions() { Timeout = 60_000 });

        int domainsToFind = domains.Length;
        processed = 0;
        var results = new string[domainsToFind][];
        var errors = new string[domainsToFind][];
        var elapsed = new long[domainsToFind];
        long totalMs = ScrapEmails(domains, browser, results, errors, elapsed, updateProgress);

        var formattedResults = new string[domainsToFind];
        for (int i = 0; i < domainsToFind; i++) {
            if (results[i].Length == 0) { formattedResults[i] = domains[i]; continue; }
            formattedResults[i] = domains[i] + "," + results[i].Aggregate((a, b) => a + "," + b);
        }

        var formattedErrors = new string[domainsToFind];
        for (int i = 0; i < domainsToFind; i++) {
            if (errors[i].Length == 0) { formattedErrors[i] = domains[i] + " : " + elapsed[i]; continue; }
            formattedErrors[i] = domains[i] + " : " + elapsed[i] + Environment.NewLine + errors[i].Aggregate((a, b) => a + Environment.NewLine + b) + Environment.NewLine;
        }
        string logFile = $"{Path.GetTempPath()}GreatCrowler{DateTime.Now.Ticks}.txt";
        File.WriteAllText(logFile, "TOTAL : " + totalMs / 1000 + Environment.NewLine);
        File.AppendAllLines(logFile, formattedErrors);
        return formattedResults;
    }

    private static long ScrapEmails(string[] domains, IBrowser browser, string[][] results, string[][] errors, long[] elapsed, Action<double> updateProgress) {
        var totalSw = new Stopwatch();
        totalSw.Start();
        int domainsToFind = domains.Length;
        Parallel.For(0, domainsToFind, new ParallelOptions { MaxDegreeOfParallelism = 20 }, (i) => {
            if (domains[i] == null) {
                UpdateProgress(updateProgress, domainsToFind);
                return;
            }
            string[] emails = new string[0];
            errors[i] = new string[0];
            var sw = new Stopwatch();
            sw.Start();
            try {
                IPage page = browser.NewPageAsync().GetAwaiter().GetResult();
                GetUri(domains[i], out string absoluteUri, out string domain);

                emails = GetEmailsAndSort(emails, page, absoluteUri, domain);
                //if (!IsPrimaryEmailFound(domain, emails)) {
                //    emails = GetEmailsAndSort(emails, page, absoluteUri + @"contacts", domain);
                //}

                if (!IsPrimaryEmailFound(domain, emails)) {
                    GoToPage(page, absoluteUri);
                    IEnumerable<string> linktexts = GetAllLinkReferences(page)
                    .Where(linktext => linktext.ToLower().Contains("contact") || linktext.ToLower().Contains("write") 
                    || linktext.ToLower().Contains("about") || linktext.ToLower().Contains("advertise") || linktext.ToLower().Contains("with")).ToArray();
                    foreach (var linktext in linktexts) {
                        string internalAbsoluteLinkText = linktext;
                        if (!linktext.Contains(domain)) {
                            if (linktext.StartsWith("http") || linktext.StartsWith("#")) { continue; }
                            internalAbsoluteLinkText = absoluteUri + linktext;
                        }
                        GetUri(internalAbsoluteLinkText, out string linkAbsoluteUri, out string linkDomain);
                        if (linkDomain.ToLower() != domain.ToLower()) { continue; }
                        emails = GetEmailsAndSort(emails, page, internalAbsoluteLinkText, domain);
                    }
                }
                page.CloseAsync().GetAwaiter().GetResult();
            } catch (Exception e) {
                errors[i] = new string[] { domains[i], e.Message, e.StackTrace ?? "" };
            }
            sw.Stop();
            elapsed[i] = sw.ElapsedMilliseconds / 1000;
            results[i] = emails;
            UpdateProgress(updateProgress, domainsToFind);
        });
        totalSw.Stop();
        long totalMs = totalSw.ElapsedMilliseconds;
        return totalMs;
    }

    private static void UpdateProgress(Action<double> updateProgress, int domainsToFind) {
        lock (lockObject) {
            processed++;
            updateProgress(processed / (double)domainsToFind);
        }
    }

    private static void GoToPage(IPage page, string absoluteUri) {
        page.GotoAsync(absoluteUri, new PageGotoOptions() { Timeout = 60_000 }).GetAwaiter().GetResult();
    }

    private static IEnumerable<string> GetAllLinkReferences(IPage page) {
        int locatorCount = page.Locator("a:visible").CountAsync().GetAwaiter().GetResult();
        if (locatorCount > 1000) { return new[] { "" }; }
        var allLocators = page.Locator("a:visible").AllAsync().GetAwaiter().GetResult();
        return allLocators.Select(link => {
            try {
                return link.GetAttributeAsync("href", new LocatorGetAttributeOptions() { Timeout = 60_0 }).GetAwaiter().GetResult()?.ToLower() ?? "";
            } catch {
                return "";
            }
        }).Distinct();
    }

    private static bool IsPrimaryEmailFound(string domain, string[] emails) => false;// IsPrimaryEmail(domain, emails.FirstOrDefault());

    private static void GetUri(string initialUri, out string absoluteUri, out string domain) {
        Uri uri = new UriBuilder(initialUri.Trim()).Uri;
        absoluteUri = uri.AbsoluteUri;
        if (absoluteUri[..7] == @"http://") {
            absoluteUri = "https://" + absoluteUri[7..];
        }
        domain = uri.Host;
        if (domain.StartsWith("www.")) {
            domain = domain[4..];
        }
    }

    private static string[] GetEmailsAndSort(string[] emails, IPage page, string url, string domain) {
        GoToPage(page, url);
        List<string> pageEmails = page.GetByText(emailRegex).AllInnerTextsAsync().GetAwaiter().GetResult()
            .Select(email => emailRegex.Match(email).Value.Trim()).Except(new[] { "" }).ToList();
        List<string> pageEmailsFromLinks = GetAllLinkReferences(page).Select(email => emailRegex.Match(email).Value.Trim()).Except(new[] { "" }).ToList();
        pageEmails.AddRange(pageEmailsFromLinks);
        pageEmails.AddRange(emails);
        string[] res = pageEmails.Distinct().ToArray();
        Array.Sort(res, (left, right) => EmailComparer(domain, left));
        return res;
    }

    private static int EmailComparer(string domain, string email) {
        if (IsPrimaryEmail(domain, email)) return -100;
        if (IsSecondaryEmail(email)) return -10;
        return 0;
    }

    private static bool IsPrimaryEmail(string domain, string? email) => email?.EndsWith(domain) ?? false;
    private static bool IsSecondaryEmail(string? email) => email?.EndsWith("gmail.com") ?? false;
}